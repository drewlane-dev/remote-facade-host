using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace RemoteFacadeHost.Client;

/// <summary>How the plugin directory reaches the container.</summary>
public enum PluginTransport
{
    /// <summary>
    /// Bind mount. Fast, and the default, because the common case is a test
    /// process running on the Docker host.
    /// </summary>
    BindMount,

    /// <summary>
    /// Copy the directory in over the Docker API.
    ///
    /// Slower for a large plugin, but it works when the TEST ITSELF runs in a
    /// container -- a bind mount there names a path on the Docker host, which
    /// is not the path the test can see, so the container silently gets an
    /// empty directory. Use this for a containerised test runner, or CI that
    /// mounts the workspace.
    /// </summary>
    Copy,
}

/// <summary>
/// Fluent setup for a remote-facade-host container.
///
/// These exist because the alternative is a page of builder calls in every
/// fixture, where the parts most easily got wrong -- the capability set a cifs
/// mount needs, the wait strategy that actually means "ready" -- are the parts
/// least likely to be reviewed.
/// </summary>
public static class ContainerBuilderExtensions
{
    /// <summary>The port the host listens on unless LIB_PORT says otherwise.</summary>
    public const int Port = 8080;

    /// <summary>
    /// How long to wait for a container to become healthy before giving up.
    ///
    /// Testcontainers' own default is an hour, which is the wrong shape for
    /// this: a facade container either comes up in seconds or is misconfigured
    /// and never will. Measured -- a container missing the capabilities its
    /// cifs mount needs took 1h 00m 02s to fail a test that could have failed
    /// in two minutes, and the log said what was wrong the whole time.
    /// </summary>
    public static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Mounts a published plugin directory and points the host at its startup:
    /// the bind mount, LIB_DIR, LIB_ASSEMBLY, LIB_REGISTRAR, a randomised port
    /// binding, and a wait strategy.
    /// </summary>
    /// <param name="startup">
    /// The type declaring the static registration method. LIB_ASSEMBLY and
    /// LIB_REGISTRAR are derived from it, so a rename is a compile error rather
    /// than a container that starts and cannot find what it was told to serve.
    /// </param>
    /// <param name="pluginDirectory">A <c>dotnet publish</c> output directory.</param>
    /// <param name="registrarMethod">Defaults to <c>Configure</c>.</param>
    /// <param name="transport">
    /// How the directory reaches the container. Switch to
    /// <see cref="PluginTransport.Copy"/> when the test process is itself
    /// containerised.
    /// </param>
    public static ContainerBuilder WithRemoteFacade(
        this ContainerBuilder builder, Type startup, string pluginDirectory,
        string? registrarMethod = null, PluginTransport transport = PluginTransport.BindMount)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(startup);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);

        // Checked here rather than left to the container. A missing directory
        // bind-mounts as an EMPTY one, so the container starts and fails with
        // "assembly not found" -- naming the file, but not the fact that the
        // path on THIS side never existed, which is the actual mistake.
        if (!Directory.Exists(pluginDirectory))
        {
            throw new DirectoryNotFoundException(
                $"plugin directory not found: {pluginDirectory}. This is the host path that gets " +
                "bind-mounted, so it must exist before the container starts. Publish the plugin " +
                "project to it first.");
        }

        var full = Path.GetFullPath(pluginDirectory);

        builder = (transport == PluginTransport.Copy
                ? builder.WithResourceMapping(new DirectoryInfo(full), "/plugin")
                : builder.WithBindMount(full, "/plugin", AccessMode.ReadOnly))
            .WithEnvironment("LIB_DIR", "/plugin")
            .WithEnvironment(new Dictionary<string, string>(
                RemoteHostEnvironment.For(startup, registrarMethod)))
            .WithPortBinding(Port, assignRandomHostPort: true);

        // /health, not UntilPortIsAvailable. Kestrel binds the port before the
        // service graph is built, so a port check can hand back a container
        // that is listening and cannot yet answer anything -- which surfaces
        // later as a confusing first-call failure instead of a startup one.
        return builder.WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(
                r => r.ForPath("/health").ForPort(Port),
                w => w.WithTimeout(StartupTimeout)));
    }

    /// <summary>
    /// Pushes a typed options object in as environment variables, the same way
    /// <see cref="RemoteOptions.WithOptions{T}"/> does for a dictionary. Bind
    /// it in the startup with <c>services.BindOptions&lt;T&gt;()</c>.
    /// </summary>
    public static ContainerBuilder WithOptions<T>(
        this ContainerBuilder builder, T options, string? section = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(builder);

        var env = new Dictionary<string, string>().WithOptions(options, section);
        return builder.WithEnvironment(new Dictionary<string, string>(env));
    }

    /// <summary>
    /// Configures the container to mount an SMB share: the credentials, and
    /// the privileges a cifs mount needs.
    ///
    /// The privileges are the reason this is worth having. A cifs mount needs
    /// CAP_SYS_ADMIN and CAP_DAC_READ_SEARCH, and -- under Docker's default
    /// AppArmor profile -- an unconfined security option as well. Getting that
    /// set wrong produces a mount failure whose message says nothing about
    /// capabilities, so it is exactly the kind of thing worth writing once.
    /// </summary>
    public static ContainerBuilder WithSmbMount(this ContainerBuilder builder, SmbMount smb)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(smb);

        builder = builder
            .WithCreateParameterModifier(p =>
            {
                p.HostConfig.CapAdd = ["SYS_ADMIN", "DAC_READ_SEARCH"];
                p.HostConfig.SecurityOpt = ["apparmor=unconfined"];
            })
            .WithEnvironment("SMB_SERVER", smb.Server)
            .WithEnvironment("SMB_SHARE", smb.Share)
            .WithEnvironment("SMB_USER", smb.User)
            .WithEnvironment("SMB_PASS", smb.Password)
            .WithEnvironment("SMB_MOUNT_POINT", smb.MountPoint);

        // Only when set: the host's own default is tuned for Azure Files
        // parity, and sending an empty string would replace it with nothing.
        if (!string.IsNullOrWhiteSpace(smb.MountOptions))
        {
            builder = builder.WithEnvironment("SMB_MOUNT_OPTIONS", smb.MountOptions);
        }

        return smb.AddHostGateway
            ? builder.WithExtraHost("host.docker.internal", "host-gateway")
            : builder;
    }
}

public static class ContainerExtensions
{
    /// <summary>
    /// A client pointed at this container, with the mapped port resolved.
    ///
    /// Saves interpolating hostname and port by hand, which is easy to get
    /// subtly wrong -- the container's own port is not the one to connect to.
    /// </summary>
    public static RemoteHost RemoteHost(this IContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);

        return Client.RemoteHost.At(
            $"http://{container.Hostname}:{container.GetMappedPublicPort(ContainerBuilderExtensions.Port)}");
    }
}
