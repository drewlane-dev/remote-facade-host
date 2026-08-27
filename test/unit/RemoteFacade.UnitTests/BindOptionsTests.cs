using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RemoteFacadeHost.Client;

namespace RemoteFacade.UnitTests;

/// <summary>
/// The guard. Its whole reason to exist is that a section the fixture forgot
/// to set must not silently fall back to defaults -- that is what made
/// LIB_OPTIONS dangerous, and re-creating it here with nicer syntax would be
/// no improvement at all.
/// </summary>
[Collection("environment")]
public class BindOptionsTests : IDisposable
{
    private readonly List<string> _set = [];

    private void Set(string key, string value)
    {
        _set.Add(key);
        Environment.SetEnvironmentVariable(key, value);
    }

    public void Dispose()
    {
        foreach (var k in _set) Environment.SetEnvironmentVariable(k, null);
    }

    [Fact]
    public void A_missing_section_is_a_failure_not_a_silent_default()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().BindOptions<SampleOptions>());

        Assert.Contains("SampleOptions", ex.Message);
        Assert.Contains(typeof(SampleOptions).FullName!, ex.Message);
    }

    [Fact]
    public void The_failure_lists_the_sections_that_ARE_present()
    {
        // A near miss is the likely mistake, so the message has to put the
        // typo next to the name that was asked for. Listing every environment
        // variable would bury it; only section-shaped entries are shown.
        Set("SampleOption__RootPath", "/typo");

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().BindOptions<SampleOptions>());

        Assert.Contains("SampleOption", ex.Message);
        Assert.DoesNotContain("PATH,", ex.Message);
    }

    [Fact]
    public void The_failure_says_how_to_fix_it()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().BindOptions<SampleOptions>());

        Assert.Contains("WithOptions", ex.Message);
        Assert.Contains("optional: true", ex.Message);
    }

    [Fact]
    public void optional_true_accepts_a_missing_section_and_keeps_the_defaults()
    {
        var services = new ServiceCollection();
        services.BindOptions<SampleOptions>(optional: true);

        var bound = services.BuildServiceProvider().GetRequiredService<IOptions<SampleOptions>>().Value;

        Assert.Equal("/tmp", bound.RootPath);
        Assert.Equal(3, bound.Retries);
    }

    [Fact]
    public void A_present_section_binds_and_does_not_throw()
    {
        Set("SampleOptions__RootPath", "/mnt/share");

        var services = new ServiceCollection();
        services.BindOptions<SampleOptions>();

        Assert.Equal("/mnt/share",
            services.BuildServiceProvider().GetRequiredService<IOptions<SampleOptions>>().Value.RootPath);
    }

    [Fact]
    public void An_explicit_section_name_is_honoured_on_both_halves()
    {
        var env = new Dictionary<string, string>().WithOptions(new SampleOptions { RootPath = "/x" }, "Store");
        foreach (var (k, v) in env) Set(k, v);

        var services = new ServiceCollection();
        services.BindOptions<SampleOptions>("Store");

        Assert.Equal("/x",
            services.BuildServiceProvider().GetRequiredService<IOptions<SampleOptions>>().Value.RootPath);
    }
}
