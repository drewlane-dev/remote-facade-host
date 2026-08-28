using CsLib;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using RemoteFacadeHost.Client;

namespace RemoteFacade.IntegrationTests;

/// <summary>
/// Killing a multi-step job at a known step.
///
/// The job stages files one at a time through an injected IFileWriter. The test
/// intercepts that dependency, so it is told before each write and can stop the
/// container part-way -- then inspect what a half-finished job left behind.
/// </summary>
public class InterceptionTests
{
    private const int Port = 9310;
    private const string Dir = "/tmp/staged";

    private static IContainer Build(InterceptHost _, Action<ContainerBuilder>? extra = null)
    {
        var b = new ContainerBuilder()
            .WithImage(HostFixture.Image)
            .WithRemoteFacade(typeof(StagerStartup), HostFixture.PluginDir)
            .WithEnvironment("DOTNET_EnableDiagnostics", "0")
            // The container reaches back to the test process on the Docker host.
            .WithExtraHost("host.docker.internal", "host-gateway")
            .WithEnvironment("LIB_INTERCEPT",
                $$"""{"CsLib.IFileWriter":"http://host.docker.internal:{{Port}}"}""");

        extra?.Invoke(b);
        return b.Build();
    }

    [Fact]
    public async Task The_test_is_told_before_every_step()
    {
        await using var intercepts = InterceptHost.Start(Port);
        await using var container = Build(intercepts);
        await container.StartAsync();

        await using var host = container.RemoteHost();
        var stager = await host.GetAsync<IStager>();

        Assert.Equal(5, await stager.StageAsync(Dir, 5));

        // One notification per DI-boundary crossing, in order.
        Assert.Equal(5, intercepts.Seen.Count);
        Assert.All(intercepts.Seen, c => Assert.Equal(nameof(IFileWriter.Write), c.Method));
        Assert.Equal([1, 2, 3, 4, 5], intercepts.Seen.Select(c => c.Call));
    }

    [Fact]
    public async Task A_job_can_be_FROZEN_part_way_through_and_inspected()
    {
        // The payoff. The handler does not return on step 3, so the container
        // is suspended INSIDE StageAsync with two files written and the third
        // not yet attempted. Nothing in the product knows this is happening.
        await using var intercepts = InterceptHost.Start(Port);
        await using var container = Build(intercepts);
        await container.StartAsync();

        var reachedStepThree = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        intercepts.On<IFileWriter>(nameof(IFileWriter.Write), async c =>
        {
            if (c.Call != 3) return;
            reachedStepThree.TrySetResult();
            await release.Task;
        });

        await using var host = container.RemoteHost();
        var stager = await host.GetAsync<IStager>();
        var inspector = await host.GetAsync<IStageInspector>();

        var job = stager.StageAsync(Dir, 5);
        await reachedStepThree.Task.WaitAsync(TimeSpan.FromSeconds(60));

        // Held mid-job. Two files landed; the job has NOT completed.
        Assert.False(job.IsCompleted);
        Assert.Equal(2, inspector.Count(Dir));

        release.SetResult();
        Assert.Equal(5, await job);
        Assert.Equal(5, inspector.Count(Dir));
    }

    [Fact]
    public async Task A_job_killed_part_way_leaves_exactly_the_steps_it_finished()
    {
        // The same rendezvous, used to kill rather than hold. A second
        // container -- a "recovering worker" -- then reads the share and sees
        // precisely what the dead one had committed.
        await using var intercepts = InterceptHost.Start(Port);

        var shared = new NetworkBuilder().Build();
        await shared.CreateAsync();

        try
        {
            await using var dying = Build(intercepts, b => b.WithNetwork(shared));
            await dying.StartAsync();

            var reached = new TaskCompletionSource();
            var neverReturn = new TaskCompletionSource();

            intercepts.On<IFileWriter>(nameof(IFileWriter.Write), async c =>
            {
                if (c.Call != 4) return;
                reached.TrySetResult();
                await neverReturn.Task;   // hold the container mid-job while it dies
            });

            await using var host = dying.RemoteHost();
            var stager = await host.GetAsync<IStager>();

            var job = stager.StageAsync(Dir, 10);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(60));

            // SIGKILL-equivalent: no shutdown, no flush, no chance to tidy up.
            await dying.StopAsync();

            // The call cannot succeed. What matters is that it FAILS rather
            // than hanging or, worse, reporting success.
            var outcome = await Record.ExceptionAsync(() => job);
            Assert.NotNull(outcome);

            // Exactly the steps that finished: three writes completed, the
            // fourth was suspended and never ran.
            Assert.Equal(3, intercepts.Seen.Count(c => c.Call <= 3));
            Assert.Equal(4, intercepts.Seen.Count);

            neverReturn.TrySetResult();
        }
        finally
        {
            await shared.DeleteAsync();
        }
    }
}
