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
///   run which                         the config, state folder and ports this folder uses; needs no running gateway
///   run claim [--for 30m] [--wait] [--no-switch]  hold the gateway for a test, from this folder's checkout (see Claims)
///   run release                       give it back
///   --as <name> (or $SWITCHYARD_AS) names the caller; --force overrides someone else's claim
/// </summary>
public static class Cli
{
    public static readonly HashSet<string> Commands = ["status", "start", "stop", "restart", "build", "errors", "logs", "setup", "workspace", "which", "claim", "release"];

    /// <summary>Which gateway this folder belongs to: with several projects each on its own port, the one the commands here talk to.</summary>
    public static async Task<int> Which(string configPath, GatewayOptions gateway, GatewayClient client)
    {
        // The port may be another project's gateway (two configs with the same Port): say whose it is.
        var answering = await GatewayClient.IsRunning(client.AdminUrl) ? (await client.GetState())?.Name : null;
        var running = answering == null ? "not running"
            : string.Equals(answering, gateway.Name, StringComparison.OrdinalIgnoreCase) ? "running"
            : $"in use by the gateway of '{answering}'";
        Console.WriteLine($"config   {configPath}");
        Console.WriteLine($"name     {gateway.Name}");
        Console.WriteLine($"state    {gateway.StateDirectory}");
        Console.WriteLine($"gateway  http://localhost:{gateway.Port}  {running}");
        foreach (var service in gateway.Services)
        {
            var ports = new[] { service.Port, service.Process?.Port }.OfType<int>().Distinct().Select(p => $":{p}");
            var url = service.Routed ? service.LocalUrl : "";
            Console.WriteLine($"  {service.Name,-16} {url,-28} {string.Join(" ", ports)}".TrimEnd());
        }
        return 0;
    }

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
                "claim" => await Claim(client, args),
                "release" => await Release(client),
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
        Console.WriteLine(state.Claim is { } claim
            ? $"claimed by {claim.Holder} since {claim.Since.ToLocalTime():HH:mm}, until {claim.Until.ToLocalTime():HH:mm}" +
              (claim.Workspace != null ? $", checkout {claim.Workspace}" : "")
            : "not claimed");
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

    /// <summary>
    /// Takes the gateway for a test (or renews it): with --wait behind whoever holds it. From a worktree every
    /// service then runs from it; this waits until they do, so what is tested is this checkout's code.
    /// </summary>
    static async Task<int> Claim(GatewayClient client, string[] all)
    {
        var dashes = Array.IndexOf(all, "--");
        var args = dashes < 0 ? all : all[..dashes];
        var run = dashes < 0 ? [] : all[(dashes + 1)..];
        var caller = client.Caller ?? throw new GatewayException("Name yourself: switchyard claim --as <name> (or set SWITCHYARD_AS), so others can see who holds it.");
        var minutes = 30;
        if (Array.IndexOf(args, "--for") is var at and >= 0)
            minutes = at + 1 < args.Length ? ParseMinutes(args[at + 1]) : throw new GatewayException("--for takes a duration: 30m, 2h, 45.");
        var wait = args.Contains("--wait");

        var state = await client.GetState() ?? throw new GatewayException("No state.");
        var checkout = args.Contains("--no-switch") ? null : CheckoutOf(Directory.GetCurrentDirectory(), state);

        var waitUntil = DateTime.UtcNow.AddHours(2);
        var said = false;
        while (true)
        {
            try
            {
                state = await client.Claim(caller, minutes, checkout?.Name) ?? throw new GatewayException("No state.");
                break;
            }
            catch (GatewayException ex) when (ex.Claimed && wait && DateTime.UtcNow < waitUntil)
            {
                if (!said)
                    Console.WriteLine($"{ex.Message.Split(". Wait")[0]}; waiting.");
                said = true;
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }

        var held = state.Claim!;
        Console.WriteLine($"claimed by {held.Holder} until {held.Until.ToLocalTime():HH:mm}" +
            (checkout != null ? $"; dotnet and vite services run from checkout {checkout.Name}" : ""));
        var ready = checkout == null ? 0 : await WaitForCheckout(client, checkout.Name);
        if (run.Length == 0)
            return ready;
        try
        {
            return ready != 0 ? ready : await RunHolding(client, caller, minutes, checkout?.Name, run);
        }
        finally
        {
            // Also when the command failed, or the services never came up: the next agent is waiting.
            await client.Release(caller, force: false);
            Console.WriteLine("released");
        }
    }

    /// <summary>
    /// Runs the command in the foreground (same terminal, same exit code) and renews the claim while it runs.
    /// Ctrl-C and SIGTERM reach the command; the claim is released once it has exited.
    /// </summary>
    static async Task<int> RunHolding(GatewayClient client, string caller, int minutes, string? checkout, string[] run)
    {
        var info = new System.Diagnostics.ProcessStartInfo(run[0]) { UseShellExecute = false };
        foreach (var arg in run[1..])
            info.ArgumentList.Add(arg);
        using var child = System.Diagnostics.Process.Start(info) ?? throw new GatewayException($"could not start {run[0]}");

        // The terminal sends Ctrl-C to the whole process group, the command included: only wait for it here.
        ConsoleCancelEventHandler onCancel = (_, e) => e.Cancel = true;
        Console.CancelKeyPress += onCancel;
        using var term = OperatingSystem.IsWindows() ? null : System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            try { child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        });

        using var renew = new CancellationTokenSource();
        var renewing = Task.Run(async () =>
        {
            while (!renew.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(20), renew.Token);
                    await client.Claim(caller, minutes, checkout);
                }
                catch (OperationCanceledException) { }
                catch (GatewayException ex) { Console.Error.WriteLine($"renewing the claim: {ex.Message}"); }
            }
        });
        try
        {
            await child.WaitForExitAsync();
            return child.ExitCode;
        }
        finally
        {
            renew.Cancel();
            await renewing;
            Console.CancelKeyPress -= onCancel;
        }
    }

    static async Task<int> Release(GatewayClient client)
    {
        var state = await client.Release(client.Caller, client.Force) ?? throw new GatewayException("No state.");
        Console.WriteLine($"released; default checkout {state.Workspace}");
        return 0;
    }

    /// <summary>Until every local service runs (or failed to) from the checkout: a switch rebuilds and restarts them.</summary>
    static async Task<int> WaitForCheckout(GatewayClient client, string checkout)
    {
        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < deadline)
        {
            var state = await client.GetState() ?? throw new GatewayException("No state.");
            var local = state.Services.Where(s => s.Local && s.Process != null && s.Workspace.Equals(checkout, StringComparison.OrdinalIgnoreCase)).ToList();
            var failed = local.Where(s => s.Process!.State is ProcessState.BuildFailed or ProcessState.Crashed && !s.Process.Busy).ToList();
            if (failed.Count > 0)
            {
                foreach (var s in failed)
                    Console.Error.WriteLine($"{s.Name}: {s.Process!.State} in {checkout}: {s.Process.Detail}");
                return 1;
            }
            if (local.All(s => s.Process!.State is ProcessState.Running or ProcessState.External && !s.Process.Busy
                               && (s.Process.RunningIn == null || s.Process.RunningIn.Equals(checkout, StringComparison.OrdinalIgnoreCase))))
            {
                if (local.Count > 0)
                    Console.WriteLine($"running from {checkout}: {string.Join(", ", local.Select(s => s.Name))}");
                return 0;
            }
            await Task.Delay(1000);
        }
        Console.Error.WriteLine($"not all services run from {checkout} after 10 min; see switchyard status");
        return 1;
    }

    /// <summary>The checkout a folder is in: the deepest one containing it (a worktree may sit inside the main checkout).</summary>
    static Workspace? CheckoutOf(string folder, GatewaySnapshot state)
    {
        var full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return (state.Workspaces ?? [])
            .Where(w => full.StartsWith(Path.GetFullPath(w.Path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .MaxBy(w => w.Path.Length);
    }

    static int ParseMinutes(string text) =>
        text.EndsWith('h') && double.TryParse(text[..^1], System.Globalization.CultureInfo.InvariantCulture, out var hours) ? (int)Math.Ceiling(hours * 60)
        : double.TryParse(text.TrimEnd('m'), System.Globalization.CultureInfo.InvariantCulture, out var minutes) ? (int)Math.Ceiling(minutes)
        : throw new GatewayException($"Not a duration: {text} (30m, 2h, 45).");

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
