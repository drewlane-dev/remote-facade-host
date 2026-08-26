using System.Diagnostics;

namespace RemoteFacadeHost;

public static class ShareMounter
{
    /// <summary>
    /// Mounts an SMB share if one is configured, and returns whether it did.
    ///
    /// Optional by design: a library under test may need no filesystem at all,
    /// and such a host should not require CAP_SYS_ADMIN. When a mount IS
    /// configured, failure is fatal — serving against an unmounted path would
    /// write to the container's own filesystem, contend with nothing, and make
    /// multi-instance tests pass while proving nothing. Partial configuration
    /// (only one of SMB_SERVER/SMB_SHARE set) is treated as that same kind of
    /// error rather than as "unconfigured": silently skipping the mount there
    /// reaches the exact same dangerous outcome by a different route.
    /// </summary>
    public static bool MountIfConfigured()
    {
        var server = Environment.GetEnvironmentVariable("SMB_SERVER");
        var share = Environment.GetEnvironmentVariable("SMB_SHARE");
        var serverSet = !string.IsNullOrWhiteSpace(server);
        var shareSet = !string.IsNullOrWhiteSpace(share);

        if (!serverSet && !shareSet)
        {
            Console.WriteLine("[ShareMounter] SMB_SERVER/SMB_SHARE not set; no mount attempted.");
            return false;
        }

        if (serverSet != shareSet)
        {
            throw new InvalidOperationException(
                "SMB_SERVER and SMB_SHARE must both be set to mount a share, or both left " +
                $"unset to skip mounting. Got SMB_SERVER={(serverSet ? "set" : "unset")}, " +
                $"SMB_SHARE={(shareSet ? "set" : "unset")}.");
        }

        var user = Environment.GetEnvironmentVariable("SMB_USER") ?? "azure";
        var pass = Environment.GetEnvironmentVariable("SMB_PASS") ?? "Passw0rd!";
        var mountPoint = Environment.GetEnvironmentVariable("SMB_MOUNT_POINT") ?? "/mnt/share";
        var options = Environment.GetEnvironmentVariable("SMB_MOUNT_OPTIONS")
            ?? "vers=3.1.1,uid=0,gid=0,file_mode=0777,dir_mode=0777,serverino,nosharesock,actimeo=30,mfsymlinks,seal";

        Directory.CreateDirectory(mountPoint);

        // Credentials go in a 0600 file, not the -o option string. mount -o is
        // comma-delimited, so a password containing a comma would truncate at
        // the comma and inject whatever follows as an arbitrary mount option;
        // and a value passed as a CLI argument sits in this process's own
        // argv, readable from /proc/<pid>/cmdline by anything else in the
        // container for as long as `mount` runs.
        var credsPath = Path.Combine(Path.GetTempPath(), $"smb-creds-{Guid.NewGuid():N}");
        try
        {
            WriteCredentialsFile(credsPath, user, pass);

            var psi = new ProcessStartInfo("mount")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add("cifs");
            psi.ArgumentList.Add($"//{server}/{share}");
            psi.ArgumentList.Add(mountPoint);
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add($"credentials={credsPath},{options}");

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("failed to start mount");

            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"mount -t cifs //{server}/{share} failed with exit code " +
                    $"{process.ExitCode}: {stderr.Trim()}");
            }
        }
        finally
        {
            // Best-effort: the file only needs to exist for the duration of
            // the mount call above.
            try { File.Delete(credsPath); } catch { /* nothing more useful to do */ }
        }

        // `mount` can exit 0 without the kernel actually attaching anything at
        // mountPoint in some failure modes, and SMB_MOUNT_POINT drifting apart
        // from where the library actually reads/writes (e.g. the path the startup's
        // RootPath) would otherwise go unnoticed — the host would serve from
        // an ordinary, empty, local directory instead of the share. Checking
        // /proc/mounts confirms the attach really happened at the path we
        // asked for.
        if (!IsMounted(mountPoint))
        {
            throw new InvalidOperationException(
                $"mount -t cifs //{server}/{share} reported success, but {mountPoint} " +
                "does not appear in /proc/mounts.");
        }

        Console.WriteLine($"[ShareMounter] mounted //{server}/{share} at {mountPoint}.");
        return true;
    }

    private static void WriteCredentialsFile(string path, string user, string pass)
    {
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            // Unix-only; harmless if ever run on a platform without POSIX
            // permission bits. Restricting this at creation time (rather than
            // chmod-ing afterwards) avoids a window where the file is
            // briefly readable with default permissions.
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        using var writer = new StreamWriter(stream);
        writer.Write($"username={user}\npassword={pass}\n");
    }

    private static bool IsMounted(string mountPoint)
    {
        var target = Path.TrimEndingDirectorySeparator(mountPoint);

        foreach (var line in File.ReadLines("/proc/mounts"))
        {
            // Format: <device> <mountpoint> <fstype> <options> <dump> <pass>
            var fields = line.Split(' ', 3);
            if (fields.Length >= 2 && Path.TrimEndingDirectorySeparator(fields[1]) == target)
            {
                return true;
            }
        }

        return false;
    }
}
