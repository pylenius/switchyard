using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Switchyard;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ProcessState>))]
public enum ProcessState { Stopped, Waiting, SettingUp, Building, Starting, Running, Unhealthy, Stopping, Crashed, BuildFailed, External }

/// <summary>How the gateway was started: whether it runs processes at all, and as which ASP.NET environment.</summary>
public sealed record SupervisorSettings(bool Enabled, string AspNetEnvironment);

/// <summary>One configured process and what the supervisor knows about it. Fields are written by its operations only.</summary>
public sealed class ManagedProcess(ServiceOptions service, LogBuffer log)
{
    public ServiceOptions Service { get; } = service;
    public ProjectType Type { get; } = ProjectType.For(service.Process!);
    public LogBuffer Log { get; } = log;
    public string Name => Service.Name;

    public volatile ProcessState State = ProcessState.Stopped;
    public volatile string? Detail;
    public Process? Process;
    public Workspace? RunningIn;
    public DateTime? StartedAt;
    public DateTime? ExitedAt;
    public int? ExitCode;
    public int Restarts;

    /// <summary>Not started again until asked: stopped by hand, failed to build, or out of restarts.</summary>
    public volatile bool Held;
    public volatile bool Building;
    public volatile bool StopRequested;
    public BuildResult? LastBuild;

    internal readonly SemaphoreSlim Lock = new(1, 1);
    internal int Ops;
    internal CancellationTokenSource? OpCts;
    internal string? OpKind;

    public bool Alive => IsRunning(Process);

    /// <summary>Watch disposes a process once it exits, possibly between reading <see cref="Process"/> and asking it.</summary>
    public static bool IsRunning(Process? process)
    {
        try
        {
            return process is { HasExited: false };
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
    public bool Busy => Volatile.Read(ref Ops) > 0;
}

/// <summary>
/// Keeps the processes in line with the routing: a service toggled local is built and started from its
/// checkout, one toggled to the backup environment is stopped. A process that dies is restarted up to
/// <see cref="MaxRestarts"/> times. Each process runs one operation at a time; a new command cancels the
/// running one.
/// </summary>
public sealed class ProcessSupervisor : BackgroundService
{
    const int MaxRestarts = 3;
    static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(5);

    readonly GatewayOptions _options;
    readonly GatewayState _state;
    readonly Workspaces _workspaces;
    readonly SupervisorSettings _settings;
    readonly ILogger<ProcessSupervisor> _logger;
    readonly Dictionary<string, ManagedProcess> _processes;
    readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    readonly SemaphoreSlim _builds;
    readonly ConcurrentDictionary<string, string> _commandValues = new();
    readonly HttpClient _health = new() { Timeout = TimeSpan.FromSeconds(2) };
    readonly Lock _pidLock = new();
    volatile bool _stopping;

    public ProcessSupervisor(IOptions<GatewayOptions> options, GatewayState state, Workspaces workspaces, SupervisorSettings settings,
        ILogger<ProcessSupervisor> logger)
    {
        _options = options.Value;
        _state = state;
        _workspaces = workspaces;
        _settings = settings;
        _logger = logger;
        _builds = new SemaphoreSlim(Math.Max(1, _options.ParallelBuilds));
        var logDir = Path.Combine(_options.StateDirectory, "logs");
        _processes = _options.Services
            .Where(s => s.Process != null)
            .ToDictionary(s => s.Name, s => new ManagedProcess(s, new LogBuffer(_options.LogLines, Path.Combine(logDir, $"{s.Name}.log"))),
                StringComparer.OrdinalIgnoreCase);
    }

    public bool Enabled => _settings.Enabled;
    public string AspNetEnvironment => _settings.AspNetEnvironment;

    public ManagedProcess? Find(string name) => _processes.GetValueOrDefault(name);

    string PidFile => Path.Combine(_options.StateDirectory, "pids.json");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
            return;

        await KillOrphans();
        _state.Changed += Signal;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Reconcile();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reconcile failed");
            }

            using var tick = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            tick.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await _signal.Reader.ReadAsync(tick.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        foreach (var process in _processes.Values)
            process.OpCts?.Cancel();
        var alive = _processes.Values.Where(p => p.Alive).ToList();
        if (alive.Count > 0)
            _logger.LogInformation("Stopping {Count} processes", alive.Count);
        await Task.WhenAll(alive.Select(StopCore));
        await base.StopAsync(cancellationToken);
    }

    void Signal() => _signal.Writer.TryWrite(true);

    async Task Reconcile()
    {
        if (_stopping)
            return;

        foreach (var mp in _processes.Values)
        {
            var local = _state.IsLocal(mp.Name);
            if (mp.Busy)
            {
                // Toggled off while it was still building or waiting: give up on starting it.
                if (!local && mp.OpKind == "start")
                    mp.OpCts?.Cancel();
                continue;
            }

            if (mp.Alive)
            {
                if (!local)
                    Launch(mp, "stop", _ => StopCore(mp));
                else if (WorkspaceFor(mp) is { } ws && mp.RunningIn?.Path != ws.Path)
                    Launch(mp, "start", async ct => { await StopCore(mp); await StartCore(mp, build: true, ct); });
                else if (mp.State is ProcessState.Running or ProcessState.Unhealthy && HasPort(mp))
                    // A routed service's port is probed already, for the UI's up/down.
                    mp.State = (mp.Service.Routed ? _state.IsLocalUp(mp.Name) : await PortUp(mp)) ? ProcessState.Running : ProcessState.Unhealthy;
                continue;
            }

            if (mp.State == ProcessState.External)
            {
                if (!local)
                    (mp.State, mp.Detail) = (ProcessState.Stopped, null);
                else if (!await PortUp(mp))
                    (mp.State, mp.Detail, mp.Held) = (ProcessState.Stopped, "the process the gateway did not start has stopped", true);
                continue;
            }

            if (!local || mp.Held)
                continue;

            if (mp.State == ProcessState.Crashed)
            {
                if (mp.Restarts >= MaxRestarts)
                {
                    mp.Held = true;
                    mp.Detail = $"{mp.Detail}, gave up after {MaxRestarts} restarts";
                    continue;
                }
                if (DateTime.Now - mp.ExitedAt < TimeSpan.FromSeconds(2))
                    continue;
                mp.Restarts++;
            }

            Launch(mp, "start", ct => StartCore(mp, build: true, ct));
        }
    }

    // ── Commands (the API) ────────────────────────────────────────────

    /// <summary>Makes it local (routing to it) and starts it.</summary>
    public void Start(ManagedProcess mp)
    {
        Release(mp);
        if (!_state.IsLocal(mp.Name))
            _state.SetLocal(mp.Name, true);
        else
            Signal();
    }

    /// <summary>Stops it but keeps it local, e.g. to run it from the IDE instead (it then shows as external).</summary>
    public void Stop(ManagedProcess mp)
    {
        mp.Held = true;
        Command(mp, "stop", _ => StopCore(mp));
    }

    public void Restart(ManagedProcess mp)
    {
        Release(mp);
        if (!_state.IsLocal(mp.Name))
        {
            _state.SetLocal(mp.Name, true);
            return;
        }
        Command(mp, "start", async ct => { await StopCore(mp); await StartCore(mp, build: true, ct); });
    }

    /// <summary>Builds from its checkout; on success restarts it if it runs (or should run) and its type needs that.</summary>
    public bool Build(ManagedProcess mp, bool restart)
    {
        if (!mp.Type.CanBuild)
            return false;
        Command(mp, "build", ct => RebuildCore(mp, restart, ct));
        return true;
    }

    /// <summary>Reinstalls dependencies from scratch (vite: delete node_modules, npm ci).</summary>
    public void Setup(ManagedProcess mp) => Command(mp, "setup", async ct =>
    {
        var wasAlive = mp.Alive;
        if (wasAlive)
            await StopCore(mp);
        var ws = WorkspaceFor(mp) ?? throw MissingWorkspace(mp);
        mp.State = ProcessState.SettingUp;
        var ok = await mp.Type.Setup(Context(mp, ws), force: true, ct);
        (mp.State, mp.Detail) = ok ? (ProcessState.Stopped, null) : (ProcessState.Crashed, "setup failed");
        if (ok && wasAlive)
            await StartCore(mp, build: false, ct);
    });

    static void Release(ManagedProcess mp)
    {
        mp.Held = false;
        mp.Restarts = 0;
        if (mp.State is ProcessState.Crashed or ProcessState.BuildFailed)
            mp.State = ProcessState.Stopped;
    }

    void Command(ManagedProcess mp, string kind, Func<CancellationToken, Task> work)
    {
        mp.OpCts?.Cancel();
        Launch(mp, kind, work);
    }

    void Launch(ManagedProcess mp, string kind, Func<CancellationToken, Task> work)
    {
        Interlocked.Increment(ref mp.Ops);
        _ = Task.Run(async () =>
        {
            await mp.Lock.WaitAsync();
            // Not disposed: a command may still cancel it after the operation ended, which is harmless.
            var cts = new CancellationTokenSource();
            mp.OpCts = cts;
            mp.OpKind = kind;
            try
            {
                if (!_stopping)
                    await work(cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                if (!mp.Alive && mp.State is ProcessState.Waiting or ProcessState.Building or ProcessState.SettingUp or ProcessState.Starting)
                    (mp.State, mp.Detail) = (ProcessState.Stopped, null);
                mp.Log.Add("sys", $"{kind} cancelled");
            }
            catch (Exception ex)
            {
                mp.Log.Add("sys", $"{kind} failed: {ex.Message}");
                _logger.LogWarning("{Name}: {Kind} failed: {Message}", mp.Name, kind, ex.Message);
                (mp.State, mp.Detail, mp.Held) = (ProcessState.Crashed, ex.Message, true);
            }
            finally
            {
                mp.OpCts = null;
                mp.OpKind = null;
                mp.Lock.Release();
                Interlocked.Decrement(ref mp.Ops);
                Signal();
            }
        });
    }

    // ── Operations ────────────────────────────────────────────────────

    async Task StartCore(ManagedProcess mp, bool build, CancellationToken ct)
    {
        var ws = WorkspaceFor(mp) ?? throw MissingWorkspace(mp);
        var context = Context(mp, ws);
        (mp.Detail, mp.ExitCode, mp.StopRequested) = (null, null, false);

        if (await PortUp(mp))
        {
            (mp.State, mp.Detail) = (ProcessState.External, $"port {PortOf(mp)} is in use by a process the gateway did not start");
            mp.Log.Add("sys", mp.Detail);
            return;
        }

        mp.Log.Add("sys", $"── start from {ws.Name}{(ws.Branch != null ? $" ({ws.Branch})" : "")} as {AspNetEnvironment} ──");
        if (mp.Type.NeedsSetup(context))
        {
            mp.State = ProcessState.SettingUp;
            if (!await mp.Type.Setup(context, force: false, ct))
            {
                (mp.State, mp.Detail, mp.Held) = (ProcessState.Crashed, "setup failed, see the log", true);
                return;
            }
        }

        if (build && mp.Type.BuildOnStart)
        {
            mp.State = ProcessState.Building;
            var result = await BuildCore(mp, context, ct);
            if (!result.Success)
            {
                (mp.State, mp.Detail, mp.Held) = (ProcessState.BuildFailed, ErrorsText(result), true);
                return;
            }
        }

        await WaitForDependencies(mp, ct);
        var env = await EnvironmentFor(mp, context, ct);
        await mp.Type.BeforeStart(context, ct);

        mp.State = ProcessState.Starting;
        var info = await mp.Type.StartInfo(context, env, ct);
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) mp.Log.Add("out", ProcessTools.StripAnsi(e.Data)); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) mp.Log.Add("err", ProcessTools.StripAnsi(e.Data)); };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            process.Dispose();
            throw new InvalidOperationException(ex.NativeErrorCode == 2
                ? $"{info.FileName} was not found; is it installed and on PATH?"
                : $"could not start {info.FileName}: {ex.Message}");
        }
        WindowsJob.Contain(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        (mp.Process, mp.RunningIn, mp.StartedAt) = (process, ws, DateTime.Now);
        mp.Log.Add("sys", $"pid {process.Id}: {info.FileName} {string.Join(' ', info.ArgumentList)}");
        RecordPid(mp, process);
        var exited = Watch(mp, process);

        await WaitReady(mp, context, exited, ct);
    }

    async Task Watch(ManagedProcess mp, Process process)
    {
        try
        {
            await process.WaitForExitAsync();
        }
        catch (Exception)
        {
        }
        WindowsJob.Release(process.Id);
        if (mp.Process != process)
            return;

        var code = process.ExitCode;
        (mp.Process, mp.ExitedAt, mp.ExitCode) = (null, DateTime.Now, code);
        ForgetPid(mp);
        mp.Log.Add("sys", $"exited with code {code}");
        if (mp.StopRequested || _stopping)
            (mp.State, mp.Detail) = (ProcessState.Stopped, null);
        else
        {
            (mp.State, mp.Detail) = (ProcessState.Crashed, $"exit {code}");
            _logger.LogWarning("{Name} exited with code {Code}", mp.Name, code);
        }
        process.Dispose();
        Signal();
    }

    /// <summary>
    /// Until it answers or exits. The exit is <paramref name="exited"/>, not the process: Watch disposes the
    /// process when it exits, and asking a disposed one throws (which held the service instead of restarting it).
    /// </summary>
    async Task WaitReady(ManagedProcess mp, RunContext context, Task exited, CancellationToken ct)
    {
        var deadline = DateTime.Now + ReadyTimeout;
        await Task.Delay(500, ct);
        while (!exited.IsCompleted)
        {
            if (await IsReady(mp, context, ct))
            {
                (mp.State, mp.Detail) = (ProcessState.Running, null);
                // It just answered; do not wait for the next port probe to say so.
                if (mp.Service.Routed)
                    _state.SetLocalUp(mp.Name, true);
                mp.Log.Add("sys", $"ready after {(DateTime.Now - mp.StartedAt!.Value).TotalSeconds:0.0}s");
                return;
            }
            if (DateTime.Now > deadline)
            {
                (mp.State, mp.Detail) = (ProcessState.Unhealthy, $"not ready after {ReadyTimeout.TotalMinutes:0} min");
                return;
            }
            await Task.Delay(1000, ct);
        }
    }

    async Task<bool> IsReady(ManagedProcess mp, RunContext context, CancellationToken ct)
    {
        if (mp.Type.IsReady(context, ct) is { } own)
            return await own;
        if (mp.Service is { Routed: true, Health: { } health })
        {
            try
            {
                using var response = await _health.GetAsync(mp.Service.LocalUrl.TrimEnd('/') + health, ct);
                return (int)response.StatusCode < 400;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return false;
            }
        }
        return HasPort(mp) ? await PortUp(mp) : true;
    }

    async Task StopCore(ManagedProcess mp)
    {
        if (mp.Process is not { } process || !ManagedProcess.IsRunning(process))
        {
            if (mp.State != ProcessState.External)
                mp.State = ProcessState.Stopped;
            return;
        }

        mp.StopRequested = true;
        mp.State = ProcessState.Stopping;
        mp.Log.Add("sys", "── stop ──");
        await mp.Type.Stop(Context(mp, mp.RunningIn!), process);
        for (var i = 0; i < 20 && mp.Process == process; i++)
            await Task.Delay(100);
        (mp.State, mp.Detail) = (ProcessState.Stopped, null);
    }

    async Task RebuildCore(ManagedProcess mp, bool restart, CancellationToken ct)
    {
        var ws = WorkspaceFor(mp) ?? throw MissingWorkspace(mp);
        var context = Context(mp, ws);

        if (mp.Type.NeedsSetup(context))
        {
            if (mp.Alive)
                await StopCore(mp);
            mp.State = ProcessState.SettingUp;
            if (!await mp.Type.Setup(context, force: false, ct))
            {
                (mp.State, mp.Detail) = (ProcessState.Crashed, "setup failed, see the log");
                return;
            }
            mp.State = ProcessState.Stopped;
        }

        if (mp.Alive && mp.Type.StopBeforeBuild)
            await StopCore(mp);

        var result = await BuildCore(mp, context, ct);
        if (!result.Success)
        {
            if (!mp.Alive)
                (mp.State, mp.Detail, mp.Held) = (ProcessState.BuildFailed, ErrorsText(result), true);
            return;
        }

        if (mp.State == ProcessState.BuildFailed)
            (mp.State, mp.Detail) = (ProcessState.Stopped, null);
        if (!restart || !_state.IsLocal(mp.Name))
            return;
        if (mp.Alive && !mp.Type.RestartAfterBuild)
            return;

        if (mp.Alive)
            await StopCore(mp);
        mp.Held = false;
        await StartCore(mp, build: false, ct);
    }

    async Task<BuildResult> BuildCore(ManagedProcess mp, RunContext context, CancellationToken ct)
    {
        await _builds.WaitAsync(ct);
        try
        {
            mp.Building = true;
            mp.Log.Add("sys", $"── build ({context.Workspace.Name}) ──");
            var result = await mp.Type.Build(context, ct);
            mp.LastBuild = result;
            mp.Log.Add("sys", result.Success
                ? $"build succeeded in {result.Seconds}s{(result.Warnings > 0 ? $", {result.Warnings} warnings" : "")}"
                : $"build failed in {result.Seconds}s: {ErrorsText(result)}");
            return result;
        }
        finally
        {
            mp.Building = false;
            _builds.Release();
        }
    }

    static string ErrorsText(BuildResult result) => result.Errors.Count switch
    {
        0 => "failed without a parsed error, see the log",
        1 => "1 error",
        var n => $"{n} errors",
    };

    async Task WaitForDependencies(ManagedProcess mp, CancellationToken ct)
    {
        foreach (var name in mp.Service.Process!.DependsOn)
        {
            if (Find(name) is not { } dep)
                continue;
            if (!dep.Service.Routed)
            {
                // A database or similar: nothing else serves it, so it starts along.
                if (dep.Held || !_state.IsLocal(dep.Name))
                {
                    Release(dep);
                    _state.SetLocal(dep.Name, true);
                }
            }
            else if (!_state.IsLocal(dep.Name))
            {
                continue;
            }

            while (dep.State is not (ProcessState.Running or ProcessState.External))
            {
                (mp.State, mp.Detail) = (ProcessState.Waiting, $"waiting for {dep.Name}");
                await Task.Delay(500, ct);
            }
        }
        mp.Detail = null;
    }

    async Task<Dictionary<string, string>> EnvironmentFor(ManagedProcess mp, RunContext context, CancellationToken ct)
    {
        var p = mp.Service.Process!;
        var env = new Dictionary<string, string>();
        foreach (var (key, value) in _options.ProcessEnv.Concat(p.Env))
            env[key] = ProcessTools.Expand(value, AspNetEnvironment);

        if (p.EnvFromUserSecrets.Count > 0 && p.EnvFromUserSecrets.Keys.Any(k => Environment.GetEnvironmentVariable(k) == null))
        {
            var project = context.InWorkspace(p.Project ?? throw new InvalidOperationException("EnvFromUserSecrets needs Process.Project."));
            var listed = await ProcessTools.Capture("dotnet", ["user-secrets", "list", "--project", project], context.Workspace.Path, ct) ?? "";
            var secrets = listed.Split('\n')
                .Select(line => line.Split(" = ", 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim());
            foreach (var (envName, key) in p.EnvFromUserSecrets)
            {
                if (Environment.GetEnvironmentVariable(envName) != null)
                    continue;
                if (secrets.TryGetValue(key, out var value))
                    env[envName] = value;
                else
                    mp.Log.Add("sys", $"user secret {key} is not set (dotnet user-secrets set \"{key}\" ... --project {p.Project})");
            }
        }

        foreach (var (envName, command) in p.EnvFromCommand)
        {
            if (Environment.GetEnvironmentVariable(envName) is { Length: > 0 } || command.Count == 0)
                continue;
            var key = string.Join('\0', command);
            if (!_commandValues.TryGetValue(key, out var value))
            {
                // The command line is logged, never its output: it is usually a secret.
                mp.Log.Add("sys", $"{envName} from: {command[0]} {string.Join(' ', command.Skip(1))}");
                value = await ProcessTools.Capture(command[0], command.Skip(1), context.Workspace.Path, ct)
                    ?? throw new InvalidOperationException($"{envName}: `{command[0]} ...` failed (signed in?)");
                _commandValues[key] = value;
            }
            env[envName] = value;
        }
        return env;
    }

    // ── Helpers ───────────────────────────────────────────────────────

    RunContext Context(ManagedProcess mp, Workspace ws) => new()
    {
        Service = mp.Service,
        Workspace = ws,
        AspNetEnvironment = AspNetEnvironment,
        Log = mp.Log,
    };

    public Workspace? WorkspaceFor(ManagedProcess mp) => _workspaces.Find(_state.WorkspaceOf(mp.Name));

    InvalidOperationException MissingWorkspace(ManagedProcess mp) =>
        new($"checkout '{_state.WorkspaceOf(mp.Name)}' does not exist (any more); pick another with w");

    static int? PortOf(ManagedProcess mp) =>
        mp.Service.Routed ? new Uri(mp.Service.LocalUrl).Port : mp.Service.Process!.Port;

    static bool HasPort(ManagedProcess mp) => PortOf(mp) != null;

    static Task<bool> PortUp(ManagedProcess mp) =>
        PortOf(mp) is { } port ? ProcessTools.IsListening(port, CancellationToken.None) : Task.FromResult(false);

    // ── Orphans ───────────────────────────────────────────────────────
    // A gateway that is killed (or crashes) cannot stop its children, and they keep their ports. Their
    // pids are kept on disk and anything still running from the last run is stopped on the next start.

    sealed record PidRecord(int Pid, DateTime Started, string? Container);

    void RecordPid(ManagedProcess mp, Process process)
    {
        DateTime started;
        try
        {
            started = process.StartTime.ToUniversalTime();
        }
        catch (Exception)
        {
            started = DateTime.UtcNow;
        }
        UpdatePids(pids => pids[mp.Name] = new PidRecord(process.Id, started,
            mp.Type is DockerProject ? DockerProject.ContainerName(mp.Service) : null));
    }

    void ForgetPid(ManagedProcess mp) => UpdatePids(pids => pids.Remove(mp.Name));

    void UpdatePids(Action<Dictionary<string, PidRecord>> change)
    {
        lock (_pidLock)
        {
            var pids = ReadPids();
            change(pids);
            try
            {
                Directory.CreateDirectory(_options.StateDirectory);
                File.WriteAllText(PidFile, JsonSerializer.Serialize(pids, JsonSerializerOptions.Web));
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not write {Path}", PidFile);
            }
        }
    }

    Dictionary<string, PidRecord> ReadPids()
    {
        try
        {
            return File.Exists(PidFile)
                ? JsonSerializer.Deserialize<Dictionary<string, PidRecord>>(File.ReadAllText(PidFile), JsonSerializerOptions.Web) ?? []
                : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    async Task KillOrphans()
    {
        Dictionary<string, PidRecord> pids;
        lock (_pidLock)
            pids = ReadPids();

        foreach (var (name, record) in pids)
        {
            try
            {
                using var process = Process.GetProcessById(record.Pid);
                if (Math.Abs((process.StartTime.ToUniversalTime() - record.Started).TotalSeconds) < 2)
                {
                    _logger.LogInformation("Stopping {Name} (pid {Pid}) left over from the last run", name, record.Pid);
                    ProcessTools.SignalTree(process);
                    if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
                        ProcessTools.KillTree(process);
                }
            }
            catch (Exception)
            {
                // Not running any more (or the pid was reused, which the start time rules out).
            }
            if (record.Container != null)
                await ProcessTools.Run("docker", ["rm", "-f", record.Container], _workspaces.RepoRoot, null, CancellationToken.None);
        }
        lock (_pidLock)
            File.Delete(PidFile);
    }
}
