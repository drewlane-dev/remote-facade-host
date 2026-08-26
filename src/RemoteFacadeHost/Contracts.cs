using System.Text.Json;

namespace RemoteFacadeHost;

public sealed record InvokeRequest(string Method, JsonElement[] Args, string? Service = null);
