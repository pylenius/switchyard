using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Switchyard;

/// <summary>Starting, signalling and probing child processes, the same way on macOS and Windows.</summary>
public static partial class ProcessTools
{
    public static readonly bool IsWindows = OperatingSystem.IsWindows();

    /// <summary>
    /// A start info with every stream redirected, so a child never touches the terminal the text UI owns.
    /// On Windows a .cmd shim (npm, npx, az) cannot be started directly and goes through cmd.exe.
    /// </summary>
    public static ProcessStartInfo StartInfo(string command, IEnumerable<string> args, string workingDirectory)
    {
        var info = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (IsWindows && FindOnPath(command) is { } found
            && (found.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || found.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            info.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            info.ArgumentList.Add("/d");
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add(found);
        }
        else
        {
            info.FileName = command;
        }
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        return info;
    }

    static string? FindOnPath(string command)
    {
        if (Path.IsPathRooted(command) || Path.HasExtension(command))
            return command;
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        foreach (var extension in extensions)
        {
            var candidate = Path.Combine(dir, command + extension.ToLowerInvariant());
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>Runs a tool to completion, handing each output line (stdout and stderr) to <paramref name="onLine"/>.</summary>
    public static async Task<int> Run(
        string command, IEnumerable<string> args, string workingDirectory, Action<string>? onLine,
        CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? env = null)
    {
        var info = StartInfo(command, args, workingDirectory);
        foreach (var (key, value) in env ?? new Dictionary<string, string>())
            info.Environment[key] = value;

        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) onLine?.Invoke(StripAnsi(e.Data)); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine?.Invoke(StripAnsi(e.Data)); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            throw;
        }
        return process.ExitCode;
    }

    /// <summary>Runs a tool and returns its trimmed stdout, or null when it fails.</summary>
    public static async Task<string?> Capture(string command, IEnumerable<string> args, string workingDirectory, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        var exit = await Run(command, args, workingDirectory, line => { lock (lines) lines.Add(line); }, cancellationToken);
        return exit == 0 ? string.Join('\n', lines).Trim() : null;
    }

    /// <summary>
    /// Asks the process and everything it started to exit (SIGTERM). `npm run dev` is npm → sh → node, so
    /// signalling npm alone leaves vite holding the port. Windows has no such signal for console
    /// processes; there the tree is killed after the grace period instead.
    /// </summary>
    public static void SignalTree(Process process)
    {
        if (IsWindows)
            return;
        try
        {
            foreach (var pid in Descendants(process.Id).Prepend(process.Id))
                _ = kill(pid, SIGTERM);
        }
        catch (Exception)
        {
            // Already gone.
        }
    }

    public static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone.
        }
    }

    static List<int> Descendants(int root)
    {
        var children = new Dictionary<int, List<int>>();
        using (var ps = Process.Start(new ProcessStartInfo("ps", ["-A", "-o", "pid=,ppid="]) { RedirectStandardOutput = true })!)
        {
            foreach (var line in ps.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && int.TryParse(parts[0], out var pid) && int.TryParse(parts[1], out var parent))
                {
                    if (!children.TryGetValue(parent, out var list))
                        children[parent] = list = [];
                    list.Add(pid);
                }
            }
            ps.WaitForExit();
        }

        var result = new List<int>();
        var queue = new Queue<int>([root]);
        while (queue.TryDequeue(out var pid))
        {
            foreach (var child in children.GetValueOrDefault(pid) ?? [])
            {
                result.Add(child);
                queue.Enqueue(child);
            }
        }
        return result;
    }

    const int SIGTERM = 15;

    [DllImport("libc", SetLastError = true)]
    static extern int kill(int pid, int sig);

    public static async Task<bool> IsListening(int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("localhost", port, timeout.Token);
            return true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public static string StripAnsi(string text) => text.Contains('\e') ? AnsiPattern().Replace(text, "") : text;

    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*\x07|[@-Z\\-_])")]
    private static partial Regex AnsiPattern();

    /// <summary>{env} and {user}, then ${VAR} and ${VAR:-default} from the gateway's environment.</summary>
    public static string Expand(string value, string aspNetEnvironment)
    {
        // Tokens first, so they can be a default: ${TUNNEL_NAME:-{user}}.
        var withTokens = value
            .Replace("{env}", aspNetEnvironment)
            .Replace("{user}", Environment.UserName.ToLowerInvariant());
        return VariablePattern().Replace(withTokens, m =>
            Environment.GetEnvironmentVariable(m.Groups[1].Value) is { Length: > 0 } set ? set : m.Groups[2].Value);
    }

    [GeneratedRegex(@"\$\{(\w+)(?::-([^}]*))?\}")]
    private static partial Regex VariablePattern();
}
