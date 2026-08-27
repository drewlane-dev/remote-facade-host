using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RemoteFacadeHost.Client;

/// <summary>
/// Pushes a typed options object into a container as environment variables.
///
/// v3 removed LIB_OPTIONS, which left configuration stringly-typed at both
/// ends: the fixture wrote "STORE_ROOT" and the startup read "STORE_ROOT", and
/// nothing checked the two agreed. A typo meant the startup silently used its
/// default -- green tests, wrong behaviour, which is the exact failure
/// LIB_OPTIONS itself was eventually guarded against.
///
/// This closes it by making the OPTIONS TYPE the only shared symbol. Rename it
/// and the compiler breaks both ends; add a property and the fixture sets a
/// property rather than inventing a string.
///
/// The wire format is the one <c>AddEnvironmentVariables()</c> already reads --
/// section, double underscore, path -- so the startup binds with stock
/// <c>Configure&lt;T&gt;(config.GetSection(...))</c> and needs nothing from this
/// package at all. <see cref="Microsoft.Extensions.DependencyInjection.RemoteOptionsServiceCollectionExtensions.BindOptions{T}"/>
/// is sugar over that, not a requirement.
/// </summary>
public static class RemoteOptions
{
    // Enums by NAME. IConfiguration binds either, but a number is opaque in
    // `docker inspect`, which is where anyone debugging a container looks first.
    private static readonly JsonSerializerOptions Shape = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Flattens <paramref name="options"/> into environment variables and adds
    /// them to <paramref name="environment"/>, which is returned so calls chain.
    /// </summary>
    /// <param name="section">
    /// Defaults to the type's short name, so <c>StoreOptions</c> becomes
    /// <c>StoreOptions__RootPath</c>. Pass one explicitly if two options types
    /// share a short name, or if the startup reads a section named differently
    /// from its type.
    /// </param>
    /// <remarks>
    /// A null property emits NOTHING rather than an empty value. Environment
    /// variables cannot distinguish "absent" from "empty string", and an empty
    /// string would overwrite the startup's own default with "" -- so null
    /// means "leave whatever the startup decided", which is the only one of the
    /// two that is ever useful.
    /// </remarks>
    public static IDictionary<string, string> WithOptions<T>(
        this IDictionary<string, string> environment, T options, string? section = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(options);

        var prefix = string.IsNullOrWhiteSpace(section) ? typeof(T).Name : section;

        Flatten(JsonSerializer.SerializeToNode(options, Shape), prefix, environment);

        return environment;
    }

    private static void Flatten(JsonNode? node, string path, IDictionary<string, string> into)
    {
        switch (node)
        {
            // Null and absent are the same thing here; see the remarks above.
            case null:
                return;

            case JsonObject obj:
                foreach (var (key, child) in obj)
                {
                    Flatten(child, $"{path}__{key}", into);
                }

                return;

            // Index, not a key: "Tags__0" is how IConfiguration represents a
            // collection element, and it binds back to a List<T> in order.
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    Flatten(array[i], $"{path}__{i}", into);
                }

                return;

            default:
                // ToJsonString() would wrap a string in quotes and they would
                // survive into the bound value. GetValue<string>() on a
                // non-string throws, so the JsonValue is asked for its text
                // through the node's own conversion instead.
                into[path] = node.GetValueKind() == JsonValueKind.String
                    ? node.GetValue<string>()
                    : node.ToJsonString();
                return;
        }
    }
}
