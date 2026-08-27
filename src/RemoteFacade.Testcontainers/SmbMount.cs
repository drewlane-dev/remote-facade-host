namespace RemoteFacadeHost.Client;

/// <summary>
/// The SMB share a container should mount before serving.
///
/// Defaults match the host's own, so a fixture only states what differs.
/// </summary>
public sealed class SmbMount
{
    /// <summary>Host or container alias serving SMB.</summary>
    public required string Server { get; init; }

    /// <summary>Share name on that server.</summary>
    public required string Share { get; init; }

    public string User { get; init; } = "azure";

    public string Password { get; init; } = "Passw0rd!";

    public string MountPoint { get; init; } = "/mnt/share";

    /// <summary>
    /// Passed to <c>mount -t cifs</c> as <c>-o</c>. Null leaves the host's own
    /// default, which is tuned for Azure Files parity -- override it only to
    /// reproduce a specific platform's mount, and remember that a difference
    /// here changes what the test is actually testing.
    /// </summary>
    public string? MountOptions { get; init; }

    /// <summary>
    /// Adds <c>host.docker.internal -&gt; host-gateway</c>, so a container can
    /// reach a server published on the Docker host rather than one on its own
    /// network.
    ///
    /// On by default because that is the common shape -- a Samba container
    /// published to the host, reached by a sibling. Docker resolves
    /// <c>host-gateway</c> itself on Linux as well as Docker Desktop, so an
    /// unused entry costs nothing; turn it off if you would rather the
    /// container's hosts file say only what it needs.
    /// </summary>
    public bool AddHostGateway { get; init; } = true;
}
