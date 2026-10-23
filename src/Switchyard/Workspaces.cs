using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Switchyard;

/// <summary>A checkout a service can run from: the main one ("main") or a git worktree, named after its folder.</summary>
public sealed record Workspace(string Name, string Path, string? Branch);

/// <summary>The main checkout and its git worktrees, wherever they are (`git worktree list`).</summary>
public sealed class Workspaces(IOptions<GatewayOptions> options)
{
    public const string Main = "main";
    static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(3);

    readonly Lock _lock = new();
    IReadOnlyList<Workspace>? _cached;
    DateTime _cachedAt;

    public string RepoRoot => options.Value.RepoRoot;

    /// <summary>Re-read every few seconds: worktrees come and go while the gateway runs.</summary>
    public IReadOnlyList<Workspace> List()
    {
        lock (_lock)
        {
            if (_cached == null || DateTime.UtcNow - _cachedAt > CacheFor)
            {
                _cached = Read();
                _cachedAt = DateTime.UtcNow;
            }
            return _cached;
        }
    }

    public Workspace? Find(string? name) =>
        List().FirstOrDefault(w => w.Name.Equals(name ?? Main, StringComparison.OrdinalIgnoreCase));

    IReadOnlyList<Workspace> Read()
    {
        var list = new List<Workspace>();
        string? path = null, branch = null;
        foreach (var line in Git(RepoRoot, "worktree", "list", "--porcelain").Append(""))
        {
            if (line.StartsWith("worktree "))
                path = System.IO.Path.GetFullPath(line["worktree ".Length..]);
            else if (line.StartsWith("branch "))
                branch = line["branch ".Length..].Replace("refs/heads/", "");
            else if (line.StartsWith("HEAD ") && branch == null)
                branch = line["HEAD ".Length..][..Math.Min(9, line.Length - 5)];
            else if (line == "" && path != null)
            {
                // The first entry is the main checkout. A worktree whose folder was deleted is skipped.
                if (list.Count == 0)
                    list.Add(new Workspace(Main, path, branch));
                else if (Directory.Exists(path))
                    list.Add(new Workspace(System.IO.Path.GetFileName(path.TrimEnd('/', '\\')), path, branch));
                (path, branch) = (null, null);
            }
        }
        if (list.Count == 0)
            list.Add(new Workspace(Main, RepoRoot, null));
        return [list[0], .. list.Skip(1).OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase)];
    }

    static List<string> Git(string dir, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args)
                info.ArgumentList.Add(arg);
            using var git = Process.Start(info)!;
            var output = git.StandardOutput.ReadToEnd();
            git.WaitForExit();
            return git.ExitCode == 0 ? [.. output.Split('\n').Select(l => l.TrimEnd('\r'))] : [];
        }
        catch (Exception)
        {
            return []; // No git: only the main checkout.
        }
    }

    /// <summary>The nearest folder with a .git; a worktree's .git file points into the main checkout's .git/worktrees/.</summary>
    public static string? FindRepoRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
        {
            var git = System.IO.Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git))
                return dir.FullName;
            if (!File.Exists(git))
                continue;
            try
            {
                var line = File.ReadLines(git).FirstOrDefault() ?? "";
                if (!line.StartsWith("gitdir:"))
                    return dir.FullName;
                var gitDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir.FullName, line["gitdir:".Length..].Trim()));
                var marker = $"{System.IO.Path.DirectorySeparatorChar}.git{System.IO.Path.DirectorySeparatorChar}worktrees";
                var at = gitDir.IndexOf(marker, StringComparison.Ordinal);
                return at > 0 ? gitDir[..at] : dir.FullName;
            }
            catch (IOException)
            {
                return dir.FullName;
            }
        }
        return null;
    }
}
