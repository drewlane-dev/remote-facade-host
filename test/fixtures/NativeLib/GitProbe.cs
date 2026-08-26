using LibGit2Sharp;

namespace NativeLib;

/// <summary>
/// Drives libgit2 for real. Every method here touches the native library, so
/// none of them can pass while native asset resolution is broken.
/// </summary>
public sealed class GitProbe : IGitProbe
{
    /// <summary>
    /// Initialises a repository, stages a file, and commits it, returning the
    /// commit SHA.
    ///
    /// A real commit rather than a version string, because a version string
    /// can be answered from managed metadata on some packages and would let
    /// the test pass without libgit2 ever loading. A SHA cannot: producing it
    /// requires the native object database to hash and write the objects.
    /// </summary>
    public string InitAndCommit(string dir)
    {
        Directory.CreateDirectory(dir);
        Repository.Init(dir);

        using var repo = new Repository(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "hello");
        Commands.Stage(repo, "a.txt");

        // Identity passed explicitly: the container has no git config, and
        // falling back to one would fail for a reason unrelated to this test.
        var who = new Signature("probe", "probe@example.com", DateTimeOffset.UnixEpoch);
        return repo.Commit("first", who, who).Sha;
    }
}

public interface IGitProbe
{
    string InitAndCommit(string dir);
}
