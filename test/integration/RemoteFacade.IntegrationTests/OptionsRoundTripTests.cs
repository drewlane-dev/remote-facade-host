using CsLib;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using RemoteFacadeHost.Client;

namespace RemoteFacade.IntegrationTests;

/// <summary>
/// Options written on the test side, read inside a real container.
///
/// The unit tests pin the flattening and bind it back in this process. That
/// leaves one thing unproven: that the pairs actually survive Docker's
/// environment and arrive as the same values. Only a container can show that.
/// </summary>
public class OptionsRoundTripTests
{
    private static readonly EchoOptions Written = new()
    {
        RootPath = "/mnt/share",
        Timeout = TimeSpan.FromSeconds(45),
        Retries = 5,
        Enabled = true,
        Tags = ["alpha", "beta"],
    };

    // What OptionsEcho.Describe() must return if every value survived. The
    // fixture's defaults are deliberately absurd (NEVER-SET, -1), so a graph
    // that fell back to them cannot produce this string by accident.
    private const string Expected = "/mnt/share|00:00:45|5|True|alpha,beta";

    private static async Task<string> DescribeAsync(Type startup, bool withOptions)
    {
        var env = RemoteHostEnvironment.For(startup);
        if (withOptions) env.WithOptions(Written);

        var builder = new ContainerBuilder()
            .WithImage(HostFixture.Image)
            .WithBindMount(HostFixture.PluginDir, "/plugin", AccessMode.ReadOnly)
            .WithEnvironment("LIB_DIR", "/plugin")
            .WithEnvironment(new Dictionary<string, string>(env))
            .WithEnvironment("DOTNET_EnableDiagnostics", "0")
            .WithPortBinding(8080, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPath("/health").ForPort(8080)));

        await using var container = builder.Build();
        await container.StartAsync();

        await using var host = RemoteHost.At(
            $"http://{container.Hostname}:{container.GetMappedPublicPort(8080)}");

        return (await host.GetAsync<IOptionsEcho>()).Describe();
    }

    [Fact]
    public async Task Every_value_survives_the_trip_into_the_container()
    {
        Assert.Equal(Expected, await DescribeAsync(typeof(EchoStartup), withOptions: true));
    }

    [Fact]
    public async Task The_stock_two_line_pattern_binds_identically()
    {
        // Same options, same assertion, a startup that never calls BindOptions
        // and uses only ConfigurationBuilder + Configure<T>. If these two ever
        // diverge, the documented alternative has stopped being equivalent.
        Assert.Equal(Expected, await DescribeAsync(typeof(EchoStockStartup), withOptions: true));
    }

    [Fact]
    public async Task A_startup_whose_options_were_never_set_refuses_to_START()
    {
        // The guard, end to end. Without it this container would come up
        // happily and serve NEVER-SET, which is the silent-default failure the
        // whole design exists to prevent -- and it is what makes the two cases
        // above non-vacuous.
        //
        // No health wait strategy here on purpose: the container is EXPECTED
        // never to become healthy, so waiting for it would burn the whole
        // timeout to learn something the logs say immediately.
        await using var container = new ContainerBuilder()
            .WithImage(HostFixture.Image)
            .WithBindMount(HostFixture.PluginDir, "/plugin", AccessMode.ReadOnly)
            .WithEnvironment("LIB_DIR", "/plugin")
            .WithEnvironment(new Dictionary<string, string>(
                RemoteHostEnvironment.For(typeof(EchoStartup))))
            .WithEnvironment("DOTNET_EnableDiagnostics", "0")
            .Build();

        // The container is expected to die during startup, but NOT instantly:
        // StartAsync returns as soon as Docker reports it running, which is
        // well before the .NET host has loaded the plugin and thrown. Reading
        // the logs at that moment returns "" and the assertion fails against
        // an empty string -- which is exactly what the first version of this
        // test did, at 288ms.
        try { await container.StartAsync(); } catch { /* may fail outright */ }

        var deadline = DateTime.UtcNow.AddSeconds(30);
        var log = "";

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var (stdout, stderr) = await container.GetLogsAsync();
                log = stdout + stderr;
            }
            catch { /* container may already be gone */ }

            if (log.Contains("no configuration for")) break;
            await Task.Delay(250);
        }

        Assert.Contains("no configuration for 'EchoOptions'", log);
        Assert.Contains("WithOptions", log);
    }
}
