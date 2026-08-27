using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RemoteFacadeHost.Client;

namespace RemoteFacade.UnitTests;

public enum Speed { Slow, Fast }

public sealed class SampleOptions
{
    public string RootPath { get; set; } = "/tmp";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    public int Retries { get; set; } = 3;
    public bool Enabled { get; set; }
    public Speed Speed { get; set; }
    public List<string> Tags { get; set; } = [];
    public string? Absent { get; set; }
    public NestedOptions Nested { get; set; } = new();
}

public sealed class NestedOptions
{
    public string Name { get; set; } = "";
}

/// <summary>
/// The flattening half. What matters is not that it produces SOME environment
/// variables, but that it produces exactly the ones IConfiguration reads back
/// -- so every case here asserts the key, and the round-trip test below binds
/// them for real rather than trusting the shape.
/// </summary>
[Collection("environment")]
public class RemoteOptionsTests
{
    private static IDictionary<string, string> Flatten(SampleOptions o, string? section = null) =>
        new Dictionary<string, string>().WithOptions(o, section);

    [Fact]
    public void The_section_defaults_to_the_type_s_short_name()
    {
        var env = Flatten(new SampleOptions { RootPath = "/mnt/share" });
        Assert.Equal("/mnt/share", env["SampleOptions__RootPath"]);
    }

    [Fact]
    public void An_explicit_section_overrides_the_type_name()
    {
        var env = Flatten(new SampleOptions { RootPath = "/x" }, "Store");
        Assert.Equal("/x", env["Store__RootPath"]);
        Assert.DoesNotContain(env, e => e.Key.StartsWith("SampleOptions"));
    }

    [Fact]
    public void A_string_is_written_unquoted()
    {
        // ToJsonString() would emit "\"/mnt/share\"" and the quotes would
        // survive binding, giving a path that does not exist.
        Assert.Equal("/mnt/share", Flatten(new SampleOptions { RootPath = "/mnt/share" })["SampleOptions__RootPath"]);
    }

    [Fact]
    public void An_enum_is_written_by_NAME_not_number()
    {
        // IConfiguration binds either, but "1" tells nobody anything in
        // `docker inspect`, which is where a container is debugged from.
        Assert.Equal("Fast", Flatten(new SampleOptions { Speed = Speed.Fast })["SampleOptions__Speed"]);
    }

    [Fact]
    public void A_collection_is_written_with_INDEXED_keys()
    {
        var env = Flatten(new SampleOptions { Tags = ["a", "b"] });
        Assert.Equal("a", env["SampleOptions__Tags__0"]);
        Assert.Equal("b", env["SampleOptions__Tags__1"]);
    }

    [Fact]
    public void A_nested_object_keeps_nesting_in_the_key()
    {
        var env = Flatten(new SampleOptions { Nested = new NestedOptions { Name = "inner" } });
        Assert.Equal("inner", env["SampleOptions__Nested__Name"]);
    }

    [Fact]
    public void A_null_property_emits_NOTHING()
    {
        // Not an empty string. "" would overwrite the startup's own default
        // with blank, where absent leaves it alone -- and absent is the only
        // one of the two that is ever what someone wanted.
        Assert.DoesNotContain(Flatten(new SampleOptions { Absent = null }), e => e.Key.EndsWith("__Absent"));
    }

    [Fact]
    public void Existing_entries_survive_and_the_same_dictionary_comes_back()
    {
        // The fixture builds one dictionary from RemoteHostEnvironment.For and
        // chains onto it, so clobbering LIB_REGISTRAR would break the container.
        var env = new Dictionary<string, string> { ["LIB_REGISTRAR"] = "X.Y.Z" };
        var returned = env.WithOptions(new SampleOptions());

        Assert.Same(env, returned);
        Assert.Equal("X.Y.Z", env["LIB_REGISTRAR"]);
    }

    [Fact]
    public void Two_options_types_coexist_without_colliding()
    {
        var env = new Dictionary<string, string>()
            .WithOptions(new SampleOptions { RootPath = "/a" })
            .WithOptions(new NestedOptions { Name = "n" });

        Assert.Equal("/a", env["SampleOptions__RootPath"]);
        Assert.Equal("n", env["NestedOptions__Name"]);
    }

    [Fact]
    public void What_is_written_is_what_IConfiguration_binds_back()
    {
        // The test that actually matters. Every assertion above pins a KEY;
        // this one proves those keys reconstitute the object, which is the
        // only property anyone cares about. Driven through the real
        // AddEnvironmentVariables provider, not a hand-built dictionary.
        var original = new SampleOptions
        {
            RootPath = "/mnt/share",
            Timeout = TimeSpan.FromSeconds(45),
            Retries = 5,
            Enabled = true,
            Speed = Speed.Fast,
            Tags = ["x", "y"],
            Nested = new NestedOptions { Name = "inner" },
        };

        var bound = BindThroughEnvironment(original);

        Assert.Equal(original.RootPath, bound.RootPath);
        Assert.Equal(original.Timeout, bound.Timeout);
        Assert.Equal(original.Retries, bound.Retries);
        Assert.Equal(original.Enabled, bound.Enabled);
        Assert.Equal(original.Speed, bound.Speed);
        Assert.Equal(original.Tags, bound.Tags);
        Assert.Equal(original.Nested.Name, bound.Nested.Name);
    }

    [Fact]
    public void A_null_property_leaves_the_startup_s_default_standing()
    {
        // The consequence of "null emits nothing", asserted rather than
        // assumed: the default survives instead of being blanked.
        var bound = BindThroughEnvironment(new SampleOptions { Absent = null });
        Assert.Null(bound.Absent);

        var withDefault = BindThroughEnvironment(new SampleOptions { RootPath = "/set" });
        Assert.Equal(3, withDefault.Retries);
    }

    /// <summary>
    /// Writes the flattened pairs into the REAL process environment, binds them
    /// back through the real provider, then removes them. Anything less would
    /// be testing this test's own idea of the format.
    /// </summary>
    private static SampleOptions BindThroughEnvironment(SampleOptions original)
    {
        var env = Flatten(original);
        try
        {
            foreach (var (k, v) in env) Environment.SetEnvironmentVariable(k, v);

            var services = new ServiceCollection();
            services.BindOptions<SampleOptions>();
            return services.BuildServiceProvider().GetRequiredService<IOptions<SampleOptions>>().Value;
        }
        finally
        {
            foreach (var k in env.Keys) Environment.SetEnvironmentVariable(k, null);
        }
    }
}
