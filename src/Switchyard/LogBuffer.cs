using System.Text.RegularExpressions;

namespace Switchyard;

/// <summary>
/// Kind: "out" / "err" (the process's stdout / stderr), "build" (build and setup tools) or "sys" (the
/// gateway's own notes: build started, exited 1, ...).
/// </summary>
public sealed record LogLine(long Seq, DateTime Time, string Kind, string Text);

/// <summary>
/// The last N lines of one process, numbered so a reader can ask for what came after the line it last saw.
/// Also appended to logs/{name}.log in the state folder, which is emptied when the gateway starts.
/// </summary>
public sealed class LogBuffer
{
    readonly int _capacity;
    readonly Queue<LogLine> _lines = new();
    readonly StreamWriter? _file;
    long _seq;

    public LogBuffer(int capacity, string? filePath)
    {
        _capacity = capacity;
        if (filePath == null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            _file = new StreamWriter(filePath, append: false) { AutoFlush = true };
        }
        catch (IOException)
        {
            // Logging to the file is a convenience; the in-memory buffer is what the UI reads.
        }
    }

    public long LastSeq
    {
        get { lock (_lines) return _seq; }
    }

    public long Add(string kind, string text)
    {
        lock (_lines)
        {
            var line = new LogLine(++_seq, DateTime.Now, kind, text);
            _lines.Enqueue(line);
            while (_lines.Count > _capacity)
                _lines.Dequeue();
            try
            {
                _file?.WriteLine($"{line.Time:HH:mm:ss.fff} {kind} {text}");
            }
            catch (IOException)
            {
            }
            return line.Seq;
        }
    }

    public void Clear()
    {
        lock (_lines)
            _lines.Clear();
    }

    /// <summary>Lines after <paramref name="after"/>, at most the last <paramref name="tail"/> of them.</summary>
    public IReadOnlyList<LogLine> Read(long after, int tail)
    {
        lock (_lines)
        {
            var matching = _lines.Where(l => l.Seq > after).ToList();
            return matching.Count > tail ? matching[^tail..] : matching;
        }
    }
}

public sealed record BuildError(string? File, int? Line, int? Column, string Code, string Message, long Seq);

public sealed record BuildResult(bool Success, DateTime FinishedAt, double Seconds, int Warnings, IReadOnlyList<BuildError> Errors);

/// <summary>
/// Collects "file(line,col): error CODE: message" from MSBuild and tsc/vue-tsc output. Paths are resolved
/// against the folder the tool ran in (tsc prints them relative to it) and shown relative to the checkout.
/// </summary>
public sealed partial class BuildOutput(string root, string workingDirectory)
{
    readonly List<BuildError> _errors = [];
    readonly HashSet<string> _seen = [];
    readonly HashSet<string> _warnings = [];

    public void Add(string line, long seq)
    {
        if (Located().Match(line) is { Success: true } m)
            Record(m.Groups["sev"].Value, Relative(m.Groups["file"].Value), int.Parse(m.Groups["line"].Value),
                int.Parse(m.Groups["col"].Value), m.Groups["code"].Value, m.Groups["msg"].Value, seq);
        else if (Unlocated().Match(line) is { Success: true } u)
            Record(u.Groups["sev"].Value, null, null, null, u.Groups["code"].Value, u.Groups["msg"].Value, seq);
    }

    // MSBuild prints every diagnostic once per project that hits it, and again in its summary.
    void Record(string severity, string? file, int? line, int? column, string code, string message, long seq)
    {
        var key = $"{file}|{line}|{column}|{code}|{message}";
        lock (_errors)
        {
            if (severity == "warning")
                _warnings.Add(key);
            else if (_seen.Add(key))
                _errors.Add(new BuildError(file, line, column, code, message.Trim(), seq));
        }
    }

    public BuildResult Result(bool exitedOk, DateTime started)
    {
        lock (_errors)
            return new(exitedOk && _errors.Count == 0, DateTime.Now, Math.Round((DateTime.Now - started).TotalSeconds, 1), _warnings.Count, [.. _errors]);
    }

    string Relative(string file)
    {
        var full = Path.IsPathRooted(file) ? file : Path.GetFullPath(Path.Combine(workingDirectory, file));
        var relative = Path.GetRelativePath(root, full);
        return relative.StartsWith("..") ? full : relative.Replace('\\', '/');
    }

    // The trailing " [project.csproj]" MSBuild appends is dropped.
    [GeneratedRegex(@"^\s*(?<file>[^\s(][^(]*?)\((?<line>\d+),(?<col>\d+)(?:,\d+,\d+)?\)\s*:\s*(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.*?)(?:\s+\[[^\]]+\])?\s*$")]
    private static partial Regex Located();

    [GeneratedRegex(@"^\s*(?:[^:]+:\s*)?(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.*?)(?:\s+\[[^\]]+\])?\s*$")]
    private static partial Regex Unlocated();
}
