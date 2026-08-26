using System.Text.Json;

namespace RemoteFacadeHost.Client;

/// <summary>
/// A container hosting a composition root. Ask it for the services its startup
/// registered.
/// </summary>
public sealed class RemoteHost : IAsyncDisposable
{
    private readonly HttpClient _http;

    // Private, deliberately: At(string) is the only supported way in. A
    // public primary constructor would be a second, undocumented entry point
    // that bypasses it -- and DisposeAsync below unconditionally disposes
    // whatever client it holds, with no ownership flag. A consumer sharing
    // one HttpClient across several RemoteHost instances (reasonable, e.g.
    // for a shared handler) would have it silently disposed by the first
    // host to go out of scope, and every later call would fail far from the
    // cause. This freezes at v1.1, so it is fixed now rather than left for a
    // breaking change later.
    private RemoteHost(HttpClient http) => _http = http;

    public static RemoteHost At(string baseUrl) =>
        new(new HttpClient { BaseAddress = new Uri(baseUrl) });

    /// <summary>
    /// Resolves the service in the container and returns a typed proxy.
    ///
    /// The round trip is deliberate. A missing registration fails HERE, naming
    /// the interface and listing what IS registered, rather than surfacing
    /// later as a confusing failure at the first method call.
    /// </summary>
    public async Task<T> GetAsync<T>() where T : class
    {
        if (!typeof(T).IsInterface)
        {
            throw new ArgumentException(
                $"{typeof(T).FullName} is not an interface. A remote proxy can only " +
                "implement an interface.", nameof(T));
        }

        var name = typeof(T).FullName!;
        var response = await _http.GetAsync("/services");
        var text = await response.Content.ReadAsStringAsync();

        // Named the same way RemoteFacade.CallAsync names a bad /invoke
        // response -- the URL, what was being asked for, the status and the
        // body -- rather than a bare EnsureSuccessStatusCode() naming none
        // of them.
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"call to {_http.BaseAddress} for GET /services returned " +
                $"{(int)response.StatusCode} {response.ReasonPhrase}, not the service " +
                $"list. Body: {text}");
        }

        var registered = JsonSerializer.Deserialize<string[]>(text) ?? [];

        if (!registered.Contains(name))
        {
            throw new InvalidOperationException(
                $"the host has no service '{name}'. Registered services: " +
                string.Join(", ", registered));
        }

        return RemoteFacade.ForService<T>(_http, name);
    }

    /// <summary>Rebuilds the container's provider. Existing proxies stay valid.</summary>
    public async Task ResetAsync()
    {
        var response = await _http.DeleteAsync("/instance");

        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"call to {_http.BaseAddress} for DELETE /instance returned " +
                $"{(int)response.StatusCode} {response.ReasonPhrase}, not 204. " +
                $"Body: {text}");
        }
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
