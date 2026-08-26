using Microsoft.Extensions.DependencyInjection;
using RemoteFacadeHost.Client;

namespace RemoteFacade.UnitTests;

/// <summary>
/// Deriving container environment from a startup TYPE, so a fixture cannot
/// typo a name the compiler could have checked.
/// </summary>
public class RemoteHostEnvironmentTests
{
    public static class GoodStartup
    {
        public static void Configure(IServiceCollection services) { }
        public static void Custom(IServiceCollection services) { }
    }

    // Not a static class, because the thing under test is a startup that
    // declares Configure as an INSTANCE method -- named right, callable wrong.
    public class InstanceOnlyStartup
    {
        public void Configure(IServiceCollection services) { }
    }

    [Fact]
    public void The_assembly_and_registrar_are_derived_from_the_type()
    {
        var env = RemoteHostEnvironment.For(typeof(GoodStartup));

        Assert.Equal(typeof(GoodStartup).Assembly.GetName().Name + ".dll", env["LIB_ASSEMBLY"]);
        Assert.Equal($"{typeof(GoodStartup).FullName}.Configure", env["LIB_REGISTRAR"]);
    }

    [Fact]
    public void A_custom_method_name_is_honoured()
    {
        var env = RemoteHostEnvironment.For(typeof(GoodStartup), "Custom");
        Assert.EndsWith(".Custom", env["LIB_REGISTRAR"]);
    }

    [Fact]
    public void A_missing_method_is_refused_at_the_call_site_naming_the_shape_expected()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => RemoteHostEnvironment.For(typeof(GoodStartup), "NoSuchMethod"));

        Assert.Contains("NoSuchMethod", ex.Message);
        Assert.Contains("IServiceCollection", ex.Message);
    }

    [Fact]
    public void An_INSTANCE_method_of_the_right_name_is_still_refused()
    {
        // Otherwise it passes here and fails later, confusingly, when the
        // container tries to invoke a registrar with no instance to call it on.
        var ex = Assert.Throws<ArgumentException>(
            () => RemoteHostEnvironment.For(typeof(InstanceOnlyStartup)));

        Assert.Contains("static", ex.Message);
    }
}
