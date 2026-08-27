using Microsoft.Extensions.Configuration;

// Deliberately NOT RemoteFacadeHost.Client. This extends IServiceCollection and
// is called from a startup, which already has this using -- putting it here is
// what lets the startup read as ordinary wiring instead of advertising that it
// is under test. It is the convention every Microsoft.Extensions.* package
// follows, for the same reason.
namespace Microsoft.Extensions.DependencyInjection;

public static class RemoteOptionsServiceCollectionExtensions
{
    /// <summary>
    /// Binds <typeparamref name="T"/> from environment variables written by
    /// <c>RemoteHostEnvironment.For(...).WithOptions(...)</c>.
    ///
    /// This is sugar. The equivalent stock .NET is two lines:
    ///
    /// <code>
    /// var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
    /// services.Configure&lt;T&gt;(config.GetSection(nameof(T)));
    /// </code>
    ///
    /// That is not free, though, and it is worth knowing which way round the
    /// cost falls. A plugin that does not already reference the ASP.NET Core
    /// shared framework needs TWO packages for those two lines --
    /// Microsoft.Extensions.Configuration.EnvironmentVariables and
    /// Microsoft.Extensions.Options.ConfigurationExtensions -- whereas this
    /// package brings both in through its framework reference. So for a plain
    /// class library the sugar is the SMALLER dependency, not the larger one.
    ///
    /// What the sugar adds is the guard. By default a section the fixture never
    /// set is a startup FAILURE rather than a silent fall back to defaults --
    /// the failure mode that made LIB_OPTIONS dangerous, where a typo produced
    /// a green suite testing the wrong configuration. It fails in the container
    /// at boot, where the message is in the log, instead of as a puzzling
    /// assertion later.
    /// </summary>
    /// <param name="section">Defaults to the type's short name.</param>
    /// <param name="optional">
    /// When true, a missing section binds nothing and leaves
    /// <typeparamref name="T"/>'s own defaults in place.
    /// </param>
    public static IServiceCollection BindOptions<T>(
        this IServiceCollection services, string? section = null, bool optional = false)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(services);

        var name = string.IsNullOrWhiteSpace(section) ? typeof(T).Name : section;
        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var found = config.GetSection(name);

        // Exists() is true for a section with a value OR children, so it
        // separates "the fixture set this" from "nothing here" -- which a null
        // check on a single key could not.
        if (!found.Exists() && !optional)
        {
            throw new InvalidOperationException(
                $"no configuration for '{name}' ({typeof(T).FullName}). The container's " +
                $"environment carries no {name}__* variables. Set them from the test with " +
                $"RemoteHostEnvironment.For(typeof(YourStartup)).WithOptions(new {typeof(T).Name} {{ ... }}), " +
                $"or pass optional: true to accept the type's own defaults. " +
                $"Sections present: {Present(config)}");
        }

        services.Configure<T>(found);
        return services;
    }

    /// <summary>
    /// Section-shaped keys actually present, for the message above.
    ///
    /// Filtered to entries WITH children, because a container's environment is
    /// mostly PATH, HOSTNAME and DOTNET_* -- listing all of it would bury the
    /// one line that matters, and every section this package writes has
    /// children by construction. A near-miss like "StoreOption" shows up here
    /// next to the "StoreOptions" that was asked for, which is the whole point.
    /// </summary>
    private static string Present(IConfiguration config)
    {
        var sections = config.GetChildren()
            .Where(c => c.GetChildren().Any())
            .Select(c => c.Key)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return sections.Count == 0 ? "(none)" : string.Join(", ", sections);
    }
}
