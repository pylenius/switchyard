namespace Switchyard;

/// <summary>
/// Commands against a running gateway, for scripts and for Claude:
///
///   run status                        every process: local/backup, state, build, checkout
///   run start|stop|restart <name...>  start makes it local; stop keeps it local but stopped (e.g. to debug it in the IDE)
///   run build <name...|all>           build, print the output and errors, restart if it runs; exit 1 on errors
///             --no-restart            only build
///   run errors <name>                 the last build's errors as file:line:col
///   run logs <name> [-n 200] [-f]     the last lines, -f follows
///   run setup <name>                  reinstall dependencies (vite: rm -rf node_modules, npm ci)
///   run workspace                     list the checkouts
///   run workspace <checkout> [name...]  default checkout for all, or for the named services ("default" follows the default)
/// </summary>
public static class Cli
{
    public static readonly HashSet<string> Commands = ["status", "start", "stop", "restart", "build", "errors", "logs", "setup", "workspace"];

    public static async Task<int> Run(string command, string[] args, GatewayClient client)
    {
        if (!await GatewayClient.IsRunning(client.AdminUrl))
        {
            Console.Error.WriteLine($"Switchyard is not running ({client.AdminUrl}). Start it first: switchyard (no command).");
            return 2;
        }

        try
        {
            return command switch
            {
                "status" => await Status(client),
                "start" or "stop" or "restart" or "setup" => await Simple(client, command, args),
                "build" => await Build(client, args),
                "errors" => await Errors(client, args),
                "logs" => await Logs(client, args),
                "workspace" => await Workspace(client, args),
                _ => 2,
            };
        }
        catch (GatewayException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    static async Task<int> Status(GatewayClient client)
    {
        var state = await client.GetState() ?? throw new GatewayException("No state.");
        Console.WriteLine($"backup {state.Backup}   environment {state.AspNetEnvironment}   default checkout {state.Workspace}" +
            (state.Supervising ? "" : "   (--no-processes: starts nothing)"));
        Console.WriteLine($"{"NAME",-11} {"GOES TO",-8} {"STATE",-12} {"BUILD",-14} {"CHECKOUT",-28} DETAIL");
        foreach (var s in state.Services)
        {
            var p = s.Process;
            var goesTo = s.Route == "" ? (s.Local ? "on" : "off") : s.Local ? "local" : state.Backup;
            var build = p?.Building == true ? "building" : p?.LastBuild is { } b ? (b.Success ? $"ok {b.Seconds}s" : $"{b.Errors} errors") : "";
            Console.WriteLine($"{s.Name,-11} {goesTo,-8} {(p?.State.ToString() ?? (s.LocalUp ? "up" : "down")),-12} {build,-14} {s.Workspace,-28} {p?.Detail}");
        }
        return 0;
    }

    static async Task<int> Simple(GatewayClient client, string command, string[] names)
    {
        if (names.Length == 0)
            throw new GatewayException($"Usage: run {command} <name...>");
        foreach (var name in names)
        {
            var p = await client.Command(name, command);
            Console.WriteLine($"{name}: {command} requested ({p?.State})");
        }
        return 0;
    }

    /// <summary>Starts the builds, streams their output while they run, then prints every error.</summary>
    static async Task<int> Build(GatewayClient client, string[] args)
    {
        var restart = !args.Contains("--no-restart");
        var names = args.Where(a => !a.StartsWith("--")).ToList();
        if (names is ["all"])
        {
            var state = await client.GetState() ?? throw new GatewayException("No state.");
            names = state.Services.Where(s => s.Local && s.Process?.CanBuild == true).Select(s => s.Name).ToList();
        }
        if (names.Count == 0)
            throw new GatewayException("Usage: run build <name...|all> [--no-restart]");

        var seen = new Dictionary<string, long>();
        var started = new Dictionary<string, DateTime?>();
        foreach (var name in names)
        {
            var before = await client.GetProcess(name) ?? throw new GatewayException($"No process '{name}'.");
            seen[name] = before.LogSeq;
            started[name] = before.LastBuild?.FinishedAt;
            await client.Command(name, "build", restart);
        }

        var prefix = names.Count > 1;
        var pending = new HashSet<string>(names);
        var idle = names.ToDictionary(n => n, _ => 0);
        while (pending.Count > 0)
        {
            await Task.Delay(500);
            foreach (var name in pending.ToList())
            {
                // Only the build's own output: the running process keeps logging meanwhile.
                foreach (var line in await client.GetLogs(name, seen[name], 10_000))
                {
                    if (line.Kind is "build" or "sys")
                        Console.WriteLine(prefix ? $"[{name}] {line.Text}" : line.Text);
                    seen[name] = line.Seq;
                }
                var p = await client.GetProcess(name);
                if (p is { Building: false } && p.LastBuild?.FinishedAt != started[name])
                    pending.Remove(name);
                // A build that never began (no checkout, a failed setup) would otherwise be waited for forever.
                else if (p is { Building: false, State: not (ProcessState.SettingUp or ProcessState.Stopping) } && ++idle[name] > 20)
                {
                    Console.WriteLine($"[{name}] the build did not run: {p.Detail ?? p.State.ToString()}");
                    pending.Remove(name);
                }
                else if (p is { Building: true } or { State: ProcessState.SettingUp or ProcessState.Stopping })
                    idle[name] = 0;
            }
        }

        // A successful build restarts a running service; wait for that too, so the next command meets it running.
        if (restart)
        {
            foreach (var name in names)
            {
                if ((await client.GetBuild(name))?.Success != true)
                    continue;
                for (var i = 0; i < 240; i++)
                {
                    var p = await client.GetProcess(name);
                    if (p is null || !p.Busy)
                    {
                        if (p?.Pid != null)
                            Console.WriteLine($"[{name}] {p.State.ToString().ToLowerInvariant()}, pid {p.Pid}");
                        break;
                    }
                    await Task.Delay(500);
                }
            }
        }

        var failed = 0;
        Console.WriteLine();
        foreach (var name in names)
        {
            var build = await client.GetBuild(name);
            if (build == null)
                continue;
            Console.WriteLine($"{name}: {(build.Success ? "build succeeded" : "BUILD FAILED")} in {build.Seconds}s" +
                $"{Count(build.Errors.Count, "error")}{Count(build.Warnings, "warning")}");
            PrintErrors(build);
            if (!build.Success)
                failed++;
        }
        return failed > 0 ? 1 : 0;
    }

    static string Count(int n, string what) => n switch { 0 => "", 1 => $", 1 {what}", _ => $", {n} {what}s" };

    static async Task<int> Errors(GatewayClient client, string[] names)
    {
        if (names.Length != 1)
            throw new GatewayException("Usage: run errors <name>");
        var build = await client.GetBuild(names[0]) ?? throw new GatewayException($"{names[0]} has not been built since the gateway started.");
        PrintErrors(build);
        return build.Success ? 0 : 1;
    }

    static void PrintErrors(BuildResult build)
    {
        foreach (var e in build.Errors)
            Console.WriteLine(e.File != null ? $"  {e.File}:{e.Line}:{e.Column}: {e.Code}: {e.Message}" : $"  {e.Code}: {e.Message}");
    }

    static async Task<int> Logs(GatewayClient client, string[] args)
    {
        var follow = args.Contains("-f");
        var tail = 200;
        string? name = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "-n" && i + 1 < args.Length && int.TryParse(args[++i], out var n))
                tail = n;
            else if (args[i] != "-f")
                name = args[i];
        }
        if (name == null)
            throw new GatewayException("Usage: run logs <name> [-n 200] [-f]");

        long after = 0;
        do
        {
            foreach (var line in await client.GetLogs(name, after, tail))
            {
                Console.WriteLine($"{line.Time:HH:mm:ss} {(line.Kind == "sys" ? "»" : " ")} {line.Text}");
                after = line.Seq;
            }
            tail = 10_000;
            if (follow)
                await Task.Delay(500);
        }
        while (follow);
        return 0;
    }

    static async Task<int> Workspace(GatewayClient client, string[] args)
    {
        if (args.Length == 0)
        {
            var state = await client.GetState() ?? throw new GatewayException("No state.");
            foreach (var w in state.Workspaces ?? [])
                Console.WriteLine($"{(w.Name == state.Workspace ? "*" : " ")} {w.Name,-34} {w.Branch,-40} {w.Path}");
            return 0;
        }

        var workspace = args[0] == "default" ? null : args[0];
        if (args.Length == 1)
        {
            await client.SetWorkspace(null, workspace);
            Console.WriteLine($"default checkout: {workspace ?? Workspaces.Main}");
        }
        foreach (var name in args[1..])
        {
            await client.SetWorkspace(name, workspace);
            Console.WriteLine($"{name}: {workspace ?? "follows the default"}");
        }
        return 0;
    }
}
