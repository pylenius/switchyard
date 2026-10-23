using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Switchyard;

/// <summary>Everything a project type needs to set up, build, start or stop one process from one checkout.</summary>
public sealed class RunContext
{
    public required ServiceOptions Service { get; init; }
    public required Workspace Workspace { get; init; }
    public required string AspNetEnvironment { get; init; }
    public required LogBuffer Log { get; init; }

    public ProcessOptions Process => Service.Process!;

    public string InWorkspace(string relative) => Path.GetFullPath(Path.Combine(Workspace.Path, relative));

    /// <summary>The folder the process runs in: the project's or <see cref="ProcessOptions.Dir"/>, else the checkout root.</summary>
    public string WorkingDirectory =>
        Process.Project != null ? Path.GetDirectoryName(InWorkspace(Process.Project))!
        : Process.Dir != null ? InWorkspace(Process.Dir)
        : Workspace.Path;

    /// <summary>Runs a build or setup tool with its output in this process's log, as "build" lines.</summary>
    public Task<int> Tool(string command, IEnumerable<string> args, string workingDirectory, CancellationToken cancellationToken,
        Action<string, long>? onLine = null) =>
        ProcessTools.Run(command, args, workingDirectory, line => onLine?.Invoke(line, Log.Add("build", line)), cancellationToken);
}

/// <summary>
/// What kind of project a process is, and so how it is set up, built, started, checked and stopped.
/// New kinds are one subclass plus a case in <see cref="For"/>.
/// </summary>
public abstract class ProjectType
{
    public static ProjectType For(ProcessOptions process) => process.Type.ToLowerInvariant() switch
    {
        "dotnet" => new DotnetProject(),
        "vite" => new ViteProject(),
        "docker" => new DockerProject(),
        "command" => new CommandProject(),
        var other => throw new InvalidOperationException($"Unknown process type '{other}' (dotnet, vite, docker, command)."),
    };

    public virtual bool CanBuild => false;

    /// <summary>Build before every start, as `dotnet run` does, so a start never runs stale code.</summary>
    public virtual bool BuildOnStart => false;

    /// <summary>A successful rebuild restarts a running process (vite reloads by itself).</summary>
    public virtual bool RestartAfterBuild => false;

    /// <summary>The build cannot overwrite the files of a running process (Windows locks a loaded dll).</summary>
    public virtual bool StopBeforeBuild => false;

    public virtual bool NeedsSetup(RunContext context) => false;

    public virtual Task<bool> Setup(RunContext context, bool force, CancellationToken cancellationToken) => Task.FromResult(true);

    public virtual Task<BuildResult> Build(RunContext context, CancellationToken cancellationToken) =>
        throw new InvalidOperationException($"{context.Service.Name} has nothing to build.");

    public virtual Task BeforeStart(RunContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    public abstract Task<ProcessStartInfo> StartInfo(RunContext context, IReadOnlyDictionary<string, string> env, CancellationToken cancellationToken);

    /// <summary>Own readiness check, or null to use the default (health path, port, or simply still alive).</summary>
    public virtual Task<bool>? IsReady(RunContext context, CancellationToken cancellationToken) => null;

    /// <summary>SIGTERM to the process tree, then kill it after <see cref="ProcessOptions.ShutdownSeconds"/>.</summary>
    public virtual async Task Stop(RunContext context, Process process)
    {
        if (ProcessTools.IsWindows)
        {
            // Console processes there have no polite stop signal to send.
            context.Log.Add("sys", "killing the process tree (Windows has no SIGTERM)");
            ProcessTools.KillTree(process);
            return;
        }
        ProcessTools.SignalTree(process);
        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(context.Process.ShutdownSeconds));
        try
        {
            await process.WaitForExitAsync(grace.Token);
        }
        catch (OperationCanceledException)
        {
            context.Log.Add("sys", $"did not stop in {context.Process.ShutdownSeconds}s, killing");
        }
        ProcessTools.KillTree(process);
    }

    protected static void Apply(ProcessStartInfo info, IReadOnlyDictionary<string, string> env)
    {
        foreach (var (key, value) in env)
            info.Environment[key] = value;
    }

    protected static async Task<BuildResult> RunBuild(RunContext context, string command, IEnumerable<string> args, string workingDirectory,
        CancellationToken cancellationToken)
    {
        var started = DateTime.Now;
        var output = new BuildOutput(context.Workspace.Path, workingDirectory);
        var exit = await context.Tool(command, args, workingDirectory, cancellationToken, output.Add);
        return output.Result(exit == 0, started);
    }
}

/// <summary>
/// An ASP.NET Core project. Built with `dotnet build`, then the built dll is run directly rather than
/// through `dotnet run`: one process to stop instead of the SDK plus the app, a start that does not
/// rebuild again, and a rebuild that can finish while the old process still serves. The launch
/// profile `dotnet run` would apply (the first "Project" profile in launchSettings.json) is applied here.
/// </summary>
public sealed class DotnetProject : ProjectType
{
    static readonly ConcurrentDictionary<string, string> TargetPaths = new();

    public override bool CanBuild => true;
    public override bool BuildOnStart => true;
    public override bool RestartAfterBuild => true;
    public override bool StopBeforeBuild => ProcessTools.IsWindows;

    string ProjectPath(RunContext context) =>
        context.InWorkspace(context.Process.Project ?? throw new InvalidOperationException($"{context.Service.Name}: Process.Project is required for dotnet."));

    public override Task<BuildResult> Build(RunContext context, CancellationToken cancellationToken) =>
        RunBuild(context, "dotnet", ["build", ProjectPath(context), "--nologo", "-v", "minimal", "-clp:NoSummary", "-p:GenerateFullPaths=true"],
            context.Workspace.Path, cancellationToken);

    public override async Task<ProcessStartInfo> StartInfo(RunContext context, IReadOnlyDictionary<string, string> env, CancellationToken cancellationToken)
    {
        var project = ProjectPath(context);
        if (!TargetPaths.TryGetValue(project, out var dll))
        {
            dll = await ProcessTools.Capture("dotnet", ["msbuild", project, "-getProperty:TargetPath", "-nologo"], context.Workspace.Path, cancellationToken)
                ?? throw new InvalidOperationException($"Could not read the TargetPath of {project}.");
            TargetPaths[project] = dll;
        }
        if (!File.Exists(dll))
            throw new InvalidOperationException($"{dll} does not exist; build it first.");

        var info = ProcessStartInfo(context, dll);
        var (urls, profileEnv) = LaunchProfile(Path.GetDirectoryName(project)!);
        Apply(info, profileEnv);
        if (urls != null)
            info.Environment["ASPNETCORE_URLS"] = urls;
        // Code that reads the variable directly (rather than IHostEnvironment) must agree with
        // --environment; the launch profile usually says Development.
        info.Environment["ASPNETCORE_ENVIRONMENT"] = context.AspNetEnvironment;
        Apply(info, env);
        return info;
    }

    static ProcessStartInfo ProcessStartInfo(RunContext context, string dll) =>
        ProcessTools.StartInfo("dotnet", [dll, "--environment", context.AspNetEnvironment, .. context.Process.Args], context.WorkingDirectory);

    static (string? Urls, Dictionary<string, string> Env) LaunchProfile(string projectDir)
    {
        var path = Path.Combine(projectDir, "Properties", "launchSettings.json");
        var env = new Dictionary<string, string>();
        if (!File.Exists(path))
            return (null, env);

        using var doc = JsonDocument.Parse(File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!doc.RootElement.TryGetProperty("profiles", out var profiles))
            return (null, env);

        foreach (var profile in profiles.EnumerateObject().Select(p => p.Value))
        {
            if (!profile.TryGetProperty("commandName", out var command) || command.GetString() != "Project")
                continue;
            if (profile.TryGetProperty("environmentVariables", out var vars))
                foreach (var v in vars.EnumerateObject())
                    env[v.Name] = v.Value.GetString() ?? "";
            return (profile.TryGetProperty("applicationUrl", out var url) ? url.GetString() : null, env);
        }
        return (null, env);
    }
}

/// <summary>A Vite frontend: `npm ci` when node_modules is missing or older than package-lock.json, `npm run dev`, vue-tsc as the build.</summary>
public sealed class ViteProject : ProjectType
{
    public override bool CanBuild => true;

    static string Dir(RunContext context) => context.WorkingDirectory;

    public override bool NeedsSetup(RunContext context)
    {
        var modules = Path.Combine(Dir(context), "node_modules");
        var lockFile = Path.Combine(Dir(context), "package-lock.json");
        var installed = Path.Combine(modules, ".package-lock.json");
        return !Directory.Exists(modules)
            || (File.Exists(lockFile) && (!File.Exists(installed) || File.GetLastWriteTimeUtc(lockFile) > File.GetLastWriteTimeUtc(installed)));
    }

    public override async Task<bool> Setup(RunContext context, bool force, CancellationToken cancellationToken)
    {
        var modules = Path.Combine(Dir(context), "node_modules");
        if (force && Directory.Exists(modules))
        {
            context.Log.Add("sys", "removing node_modules");
            Directory.Delete(modules, recursive: true);
        }
        var hasLock = File.Exists(Path.Combine(Dir(context), "package-lock.json"));
        context.Log.Add("sys", hasLock ? "npm ci" : "npm install");
        return await context.Tool("npm", [hasLock ? "ci" : "install", "--no-audit", "--no-fund"], Dir(context), cancellationToken) == 0;
    }

    // Type check only: the dev server compiles by itself, and `vite build` is a release concern.
    public override Task<BuildResult> Build(RunContext context, CancellationToken cancellationToken)
    {
        var checker = File.Exists(Path.Combine(Dir(context), "node_modules", ".bin", "vue-tsc")) ? "vue-tsc" : "tsc";
        return RunBuild(context, "npx", ["--no", "--", checker, "--noEmit", "--pretty", "false"], Dir(context), cancellationToken);
    }

    public override Task<ProcessStartInfo> StartInfo(RunContext context, IReadOnlyDictionary<string, string> env, CancellationToken cancellationToken)
    {
        var info = ProcessTools.StartInfo("npm", ["run", "dev", .. context.Process.Args], Dir(context));
        info.Environment["BROWSER"] = "none";
        Apply(info, env);
        return Task.FromResult(info);
    }
}

/// <summary>
/// A container run in the foreground with --rm. A container left over from a gateway that was killed is
/// removed first, and it is stopped with `docker stop`: killing the docker CLI leaves the container running.
/// </summary>
public sealed class DockerProject : ProjectType
{
    public static string ContainerName(ServiceOptions service) => service.Process!.Container ?? $"switchyard-{service.Name}";

    static string Container(RunContext context) => ContainerName(context.Service);

    public override Task BeforeStart(RunContext context, CancellationToken cancellationToken) =>
        ProcessTools.Run("docker", ["rm", "-f", Container(context)], context.Workspace.Path, null, cancellationToken);

    public override Task<ProcessStartInfo> StartInfo(RunContext context, IReadOnlyDictionary<string, string> env, CancellationToken cancellationToken)
    {
        var p = context.Process;
        var args = new List<string> { "run", "--rm", "--name", Container(context) };
        foreach (var port in p.Ports)
            args.AddRange(["-p", port]);
        foreach (var volume in p.Volumes)
            args.AddRange(["-v", volume]);
        // Only the container's own variables; the gateway-wide ProcessEnv is for the services.
        foreach (var key in p.Env.Keys)
            args.AddRange(["-e", $"{key}={env[key]}"]);
        args.Add(p.Image ?? throw new InvalidOperationException($"{context.Service.Name}: Process.Image is required for docker."));
        args.AddRange(p.Args);
        return Task.FromResult(ProcessTools.StartInfo("docker", args, context.Workspace.Path));
    }

    public override Task<bool>? IsReady(RunContext context, CancellationToken cancellationToken) =>
        context.Process.ReadyExec.Count == 0
            ? null
            : ProcessTools.Run("docker", ["exec", Container(context), .. context.Process.ReadyExec], context.Workspace.Path, null, cancellationToken)
                .ContinueWith(t => t.IsCompletedSuccessfully && t.Result == 0, TaskScheduler.Default);

    public override async Task Stop(RunContext context, Process process)
    {
        await ProcessTools.Run("docker", ["stop", "-t", context.Process.ShutdownSeconds.ToString(), Container(context)],
            context.Workspace.Path, null, CancellationToken.None);
        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(grace.Token);
        }
        catch (OperationCanceledException)
        {
        }
        ProcessTools.KillTree(process);
    }
}

/// <summary>Any other program: a tunnel client, a queue emulator, a script.</summary>
public sealed class CommandProject : ProjectType
{
    public override Task<ProcessStartInfo> StartInfo(RunContext context, IReadOnlyDictionary<string, string> env, CancellationToken cancellationToken)
    {
        var info = ProcessTools.StartInfo(
            context.Process.Command ?? throw new InvalidOperationException($"{context.Service.Name}: Process.Command is required."),
            context.Process.Args, context.WorkingDirectory);
        Apply(info, env);
        return Task.FromResult(info);
    }
}
