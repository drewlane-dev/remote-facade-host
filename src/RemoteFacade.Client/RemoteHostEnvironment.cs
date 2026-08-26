namespace RemoteFacadeHost.Client;

/// <summary>
/// Builds the container's environment from the startup TYPE, so a fixture
/// cannot typo a name the compiler could have checked. A misspelled
/// LIB_REGISTRAR otherwise fails at container start with a runtime error.
///
/// Takes a <see cref="Type"/>, not a generic type parameter -- deliberately.
/// A startup is a holder for a static registration method, so it is itself
/// almost always declared "static class", and C# refuses a static type as a
/// generic type argument (CS0718). A generic For&lt;TStartup&gt;() therefore
/// could never be called with the shape this helper exists to serve.
/// typeof(RemoteStartup) is exactly as compile-time-checked as a type
/// argument -- the type must exist and be spelled correctly -- without that
/// exclusion.
/// </summary>
public static class RemoteHostEnvironment
{
    /// <param name="startupType">The startup type, typically a static class.</param>
    /// <param name="methodName">Defaults to "Configure".</param>
    public static IDictionary<string, string> For(Type startupType, string? methodName = null)
    {
        var method = methodName ?? "Configure";
        var found = startupType.GetMethod(method);

        // Checked separately from "found the method at all": an INSTANCE
        // method of the right name would otherwise pass this check and only
        // fail later, confusingly, when the container tries to invoke
        // LIB_REGISTRAR with no instance to call it on.
        if (found is null || !found.IsStatic)
        {
            throw new ArgumentException(
                $"{startupType.FullName} has no static method '{method}'. The startup must " +
                $"expose a static {method}(IServiceCollection).", nameof(methodName));
        }

        return new Dictionary<string, string>
        {
            ["LIB_ASSEMBLY"] = startupType.Assembly.GetName().Name + ".dll",
            ["LIB_REGISTRAR"] = $"{startupType.FullName}.{method}",
        };
    }
}
