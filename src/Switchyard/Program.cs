using System.Runtime.InteropServices;
using Switchyard;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;

// Local entry point for a set of HTTP services: each path prefix goes to the service running on this
// machine, or to the selected backup environment when it is toggled off. It also runs them: a service
// toggled local is built and started from its checkout, one toggled off is stopped.
//
//   run                          text UI; attaches to a running gateway, or starts one in-process
//   run --headless               gateway only; control it through /_gw or the commands below
//   run --no-processes           route only, start nothing (when something else runs the services)
//   run --config <file|folder>   the switchyard.json to use (default: found from the current folder up)
//   run --env Beta               ASP.NET environment for the dotnet services (default: RunAs, or $SWITCHYARD_ENV)
//   run --workspace <worktree>   default checkout for the services (saved for next time)
//   run status | start | stop | restart | build | errors | logs | setup | workspace | which ...   see Cli.cs
//   any setting from the config file can be given too, e.g. --Port=9000
var options = Flags.Parse(args);

if (options.Help)
{
    Console.WriteLine(Flags.Usage);
    return;
}

if (GatewayOptions.FindConfig(options.Config) is not { } configPath || !File.Exists(configPath))
{
    Console.Error.WriteLine(options.Config != null
        ? $"No config at {options.Config}."
        : $"No {GatewayOptions.ConfigFile} in {Directory.GetCurrentDirectory()} or above it; pass --config <file>. See README.md.");
    Environment.ExitCode = 2;
    return;
}

// The project's file, then the developer's own (not committed), then the command line. Kept apart from
// the host's configuration, which also reads environment variables (PORT would become Port).
var configDirectory = Path.GetDirectoryName(configPath)!;
var gatewayConfig = new ConfigurationBuilder()
    .AddJsonFile(configPath, optional: false)
    .AddJsonFile(Path.Combine(configDirectory, GatewayOptions.UserConfigFile), optional: true)
    .AddCommandLine(options.Rest)
    .Build();
var gateway = (gatewayConfig.Get<GatewayOptions>() ?? new GatewayOptions()).Resolve(configDirectory);
var adminUrl = $"http://localhost:{gateway.Port}/_gw/";
var runAs = options.AspNetEnvironment ?? gateway.RunAs;

if (options.Command == "which")
{
    Environment.ExitCode = await Cli.Which(configPath, gateway, new GatewayClient(adminUrl));
    return;
}

if (options.Command != null)
{
    Environment.ExitCode = await Cli.Run(options.Command, options.CommandArgs, new GatewayClient(adminUrl).As(options.As, options.Force));
    return;
}

if (!options.Headless && await GatewayClient.IsRunning(adminUrl))
{
    await Tui.Run(new GatewayClient(adminUrl).As("text UI", force: true), ownsGateway: false, CancellationToken.None);
    return;
}

// Checked before the host starts: the supervisor stops the processes in the pid file as leftovers of the
// last run, and with a gateway still running those are its live processes. Kestrel only binds after that.
// The lock is what makes it safe (held until this process exits, whatever port either gateway was given);
// the checks before it only give a better message.
if (await GatewayClient.IsRunning(adminUrl))
{
    Console.Error.WriteLine($"Switchyard is already running ({adminUrl}). Run switchyard without --headless to attach to it, or use the commands.");
    Environment.ExitCode = 1;
    return;
}
Directory.CreateDirectory(gateway.StateDirectory);
FileStream stateLock;
try
{
    stateLock = new FileStream(Path.Combine(gateway.StateDirectory, "gateway.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
}
catch (IOException)
{
    Console.Error.WriteLine($"Another switchyard gateway is running with {gateway.StateDirectory}; stop it first, or attach to it.");
    Environment.ExitCode = 1;
    return;
}
using var heldLock = stateLock;
foreach (var port in gateway.Services.Select(s => s.Port).OfType<int>().Prepend(gateway.Port).Distinct())
{
    if (await PortInUse(port))
    {
        Console.Error.WriteLine($"Port {port} is in use by another program; switchyard needs it (set Port in switchyard.user.json, or --Port=9000).");
        Environment.ExitCode = 1;
        return;
    }
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = options.Rest,
    // The binary's own appsettings.json (logging) is found wherever it is started from.
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Services.AddSingleton(Options.Create(gateway));
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(60));
builder.Services.AddSingleton(new SupervisorSettings(options.Processes, runAs));
builder.Services.AddSingleton<GatewayState>();
builder.Services.AddSingleton<Workspaces>();
builder.Services.AddSingleton<ProcessSupervisor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProcessSupervisor>());
builder.Services.AddSingleton<Claims>();
builder.Services.AddSingleton<StatusBuilder>();
builder.Services.AddHostedService<LocalHealthProbe>();
builder.Services.AddReverseProxy().LoadFromMemory([], []);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.WriteIndented = true);

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenLocalhost(gateway.Port);
    foreach (var port in gateway.Services.Select(s => s.Port).OfType<int>().Distinct())
        kestrel.ListenLocalhost(port);
});

// The text UI owns the terminal; console logging would scribble over it.
if (!options.Headless)
    builder.Logging.ClearProviders();

var app = builder.Build();

var state = app.Services.GetRequiredService<GatewayState>();
var workspaces = app.Services.GetRequiredService<Workspaces>();
var supervisor = app.Services.GetRequiredService<ProcessSupervisor>();
if (options.Workspace != null)
{
    if (workspaces.Find(options.Workspace) == null)
    {
        Console.Error.WriteLine($"No checkout '{options.Workspace}'. There are: {string.Join(", ", workspaces.List().Select(w => w.Name))}");
        Environment.ExitCode = 1;
        return;
    }
    state.SetWorkspace(null, options.Workspace);
}

var proxyConfig = app.Services.GetRequiredService<InMemoryConfigProvider>();
void ApplyRoutes()
{
    var (routes, clusters) = ProxyRoutes.Build(state, gateway.Port);
    proxyConfig.Update(routes, clusters);
}
ApplyRoutes();
state.Changed += ApplyRoutes;

// Routes match on the port a request came in on, not its Host header: a request through a tunnel
// (frp, ngrok, cloudflared) arrives with the tunnel's public host name and no port.
app.Use((context, next) =>
{
    context.Request.Headers[ProxyRoutes.ListenerHeader] = context.Connection.LocalPort.ToString();
    return next(context);
});
app.UseRouting();

// The admin API answers only to a localhost Host: tunneled requests also reach this port, and must
// not be able to switch a developer's routing (to prod, say) or run commands from the internet.
var admin = app.MapGroup("/_gw").RequireHost($"localhost:{gateway.Port}", $"127.0.0.1:{gateway.Port}");
var claims = app.Services.GetRequiredService<Claims>();

// While someone holds the claim, only they change anything (reads stay open); the claim itself has its own rules.
// Callers name themselves with X-Switchyard-As; X-Switchyard-Force overrides (the text UI, a person at the keyboard).
admin.AddEndpointFilter(async (context, next) =>
{
    var request = context.HttpContext.Request;
    if (HttpMethods.IsGet(request.Method) || request.Path.StartsWithSegments("/_gw/claim"))
        return await next(context);
    var caller = request.Headers[GatewayClient.AsHeader].FirstOrDefault();
    if (request.Headers.ContainsKey(GatewayClient.ForceHeader) || claims.Allows(caller))
        return await next(context);
    return Results.Conflict(claims.Current is { } held ? ClaimedMessage(held) : "Claimed.");
});

admin.MapGet("/state", (StatusBuilder status) => status.Snapshot());
admin.MapGet("/claim", () => claims.Current is { } held ? Results.Ok(held) : Results.NoContent());
admin.MapPut("/claim", (ClaimRequest request, StatusBuilder status) =>
{
    if (string.IsNullOrWhiteSpace(request.As))
        return Results.BadRequest("Name yourself: --as <name>, or set SWITCHYARD_AS.");
    var found = request.Workspace == null ? null : workspaces.Find(request.Workspace);
    if (request.Workspace != null && found == null)
        return Results.NotFound($"No checkout '{request.Workspace}'.");
    var minutes = Math.Clamp(request.Minutes ?? 30, 1, 24 * 60);
    return claims.Take(request.As.Trim(), TimeSpan.FromMinutes(minutes), found?.Name) is { } held
        ? Results.Conflict(ClaimedMessage(held))
        : Results.Ok(status.Snapshot());
});
admin.MapDelete("/claim", (string? @as, bool? force, StatusBuilder status) =>
    claims.Release(@as, force ?? false) is { } held
        ? Results.Conflict(ClaimedMessage(held, "Only they release it, or pass --force."))
        : Results.Ok(status.Snapshot()));
admin.MapPut("/backup", (BackupRequest request, StatusBuilder status) =>
    state.SetBackup(request.Env) ? Results.Ok(status.Snapshot()) : Results.NotFound($"Unknown environment '{request.Env}'."));
admin.MapPut("/services", (LocalRequest request, StatusBuilder status) =>
{
    state.SetAllLocal(request.Local);
    return Results.Ok(status.Snapshot());
});
admin.MapPut("/services/{name}", (string name, LocalRequest request, StatusBuilder status) =>
{
    if (state.FindService(name) == null)
        return Results.NotFound($"Unknown service '{name}'.");
    // Toggling also releases a process stopped by hand, so local means running again.
    if (request.Local && supervisor.Find(name) is { } mp)
        supervisor.Start(mp);
    else
        state.SetLocal(name, request.Local);
    return Results.Ok(status.Snapshot());
});
// A null workspace means the default checkout (for one service) or main (for all).
admin.MapPut("/workspace", (WorkspaceRequest request, StatusBuilder status) =>
{
    var found = request.Workspace == null ? null : workspaces.Find(request.Workspace);
    if (request.Workspace != null && found == null)
        return Results.NotFound($"No checkout '{request.Workspace}'.");
    state.SetWorkspace(null, found?.Name);
    return Results.Ok(status.Snapshot());
});
admin.MapPut("/services/{name}/workspace", (string name, WorkspaceRequest request, StatusBuilder status) =>
{
    var found = request.Workspace == null ? null : workspaces.Find(request.Workspace);
    if (request.Workspace != null && found == null)
        return Results.NotFound($"No checkout '{request.Workspace}'.");
    return state.SetWorkspace(name, found?.Name) ? Results.Ok(status.Snapshot()) : Results.NotFound($"Unknown service '{name}'.");
});

var processes = admin.MapGroup("/processes");
processes.MapGet("/{name}", (string name) =>
    supervisor.Find(name) is { } mp ? Results.Ok(StatusBuilder.Process(mp)) : Results.NotFound($"No process '{name}'."));
processes.MapGet("/{name}/build", (string name) =>
    supervisor.Find(name) is not { } mp ? Results.NotFound($"No process '{name}'.")
    : mp.LastBuild is { } build ? Results.Ok(build)
    : Results.NotFound($"{name} has not been built since the gateway started."));
processes.MapGet("/{name}/logs", (string name, long? after, int? tail) =>
    supervisor.Find(name) is { } mp
        ? Results.Ok(mp.Log.Read(after ?? 0, Math.Clamp(tail ?? 200, 1, gateway.LogLines)))
        : Results.NotFound($"No process '{name}'."));
processes.MapPost("/{name}/{action}", (string name, string action, bool? restart) =>
{
    if (!supervisor.Enabled)
        return Results.Conflict("The gateway runs with --no-processes and starts nothing.");
    if (supervisor.Find(name) is not { } mp)
        return Results.NotFound($"No process '{name}'.");
    switch (action)
    {
        case "start": supervisor.Start(mp); break;
        case "stop": supervisor.Stop(mp); break;
        case "restart": supervisor.Restart(mp); break;
        case "setup": supervisor.Setup(mp); break;
        case "build":
            if (!supervisor.Build(mp, restart ?? true))
                return Results.BadRequest($"{name} is a {mp.Service.Process!.Type} process and has nothing to build.");
            break;
        default: return Results.NotFound($"Unknown action '{action}' (start, stop, restart, build, setup).");
    }
    return Results.Accepted(value: StatusBuilder.Process(mp));
});

app.MapReverseProxy();

// Closing the terminal window sends SIGHUP; stop the children instead of leaving them behind.
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
using var hangup = OperatingSystem.IsWindows()
    ? null
    : PosixSignalRegistration.Create(PosixSignal.SIGHUP, context =>
    {
        context.Cancel = true;
        lifetime.StopApplication();
    });

if (options.Headless)
{
    await app.RunAsync();
    return;
}

await app.StartAsync();
await Tui.Run(new GatewayClient(adminUrl).As("text UI", force: true), ownsGateway: true, lifetime.ApplicationStopping);
try
{
    Console.WriteLine("Stopping switchyard and its processes...");
}
catch (IOException)
{
}
await app.StopAsync();

static string ClaimedMessage(ClaimSnapshot held, string hint = "Wait for it: switchyard claim --as <you> --wait. Override: --force.") =>
    $"Claimed by {held.Holder} since {held.Since.ToLocalTime():HH:mm}, until {held.Until.ToLocalTime():HH:mm}" +
    (held.Workspace != null ? $", running checkout {held.Workspace}" : "") + $". {hint}";

// A connect, not a test bind: a listener on the IPv6 wildcard leaves 127.0.0.1 bindable on macOS.
static async Task<bool> PortInUse(int port)
{
    foreach (var address in new[] { System.Net.IPAddress.Loopback, System.Net.IPAddress.IPv6Loopback })
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient(address.AddressFamily);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await client.ConnectAsync(address, port, timeout.Token);
            return true;
        }
        catch (Exception)
        {
            // Refused or unreachable: nothing listens there.
        }
    }
    return false;
}

record BackupRequest(string Env);
record LocalRequest(bool Local);
record WorkspaceRequest(string? Workspace);
record ClaimRequest(string? As, int? Minutes, string? Workspace);

/// <summary>The gateway's own flags, taken out before the rest reaches ASP.NET configuration.</summary>
sealed record Flags(
    bool Headless, bool Processes, string? AspNetEnvironment, string? Workspace, string? Config,
    string? Command, string[] CommandArgs, string[] Rest, bool Help = false, string? As = null, bool Force = false)
{
    public const string Usage = """
        switchyard                          text UI; attaches to a running gateway, or starts one in-process
        switchyard --headless               gateway only; control it through /_gw or the commands below
        switchyard --no-processes           route only, start nothing (when something else runs the services)
        switchyard --config <file|folder>   the switchyard.json to use (default: found from the current folder up)
        switchyard --env Beta               ASP.NET environment for the dotnet services (default: RunAs, or $SWITCHYARD_ENV)
        switchyard --workspace <worktree>   default checkout for the services (saved for next time)
        any setting from the config file can be given too, e.g. --Port=9000

        Commands, against a running gateway:
          status                            every process: local/backup, state, build, checkout
          start|stop|restart <name...>      start makes it local; stop keeps it local but stopped
          build <name...|all> [--no-restart]  build, print the output and errors, restart if it runs; exit 1 on errors
          errors <name>                     the last build's errors as file:line:col
          logs <name> [-n 200] [-f]         the last lines, -f follows
          setup <name>                      reinstall dependencies (vite: rm -rf node_modules, npm ci)
          workspace [<checkout> [name...]]  list the checkouts, or set the default one
          which                             the config, state folder and ports this folder uses (no gateway needed)
          claim [--for 30m] [--wait] [--no-switch]   hold the gateway for a test; dotnet and vite services run from this folder's checkout
          release                           give it back; the checkouts go back as they were
        Name yourself for claims with --as <name> or $SWITCHYARD_AS; --force overrides someone else's claim.
        """;

    public static Flags Parse(string[] args)
    {
        var headless = Console.IsInputRedirected || Console.IsOutputRedirected;
        var processes = true;
        var env = Environment.GetEnvironmentVariable("SWITCHYARD_ENV") is { Length: > 0 } fromEnv ? fromEnv : null;
        string? workspace = null, config = null, command = null;
        var caller = Environment.GetEnvironmentVariable("SWITCHYARD_AS") is { Length: > 0 } fromEnvAs ? fromEnvAs : null;
        var force = false;
        var commandArgs = new List<string>();
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (command != null)
            {
                // Commands take their own arguments; --config and settings (--Port=9000) are the gateway's.
                if (args[i] == "--config" && i + 1 < args.Length)
                    config = args[++i];
                else if (args[i] == "--as" && i + 1 < args.Length)
                    caller = args[++i];
                else if (args[i] == "--force")
                    force = true;
                else if (args[i].StartsWith("--") && args[i].Contains('='))
                    rest.Add(args[i]);
                else
                    commandArgs.Add(args[i]);
                continue;
            }
            switch (args[i])
            {
                case "--help" or "-h" or "help" when rest.Count == 0:
                    return new Flags(headless, processes, env, workspace, config, null, [], [], Help: true);
                case "--headless": headless = true; break;
                case "--no-processes": processes = false; break;
                case "--env" when i + 1 < args.Length: env = args[++i]; break;
                case "--workspace" when i + 1 < args.Length: workspace = args[++i]; break;
                case "--config" when i + 1 < args.Length: config = args[++i]; break;
                case "--as" when i + 1 < args.Length: caller = args[++i]; break;
                case "--force": force = true; break;
                case var verb when rest.Count == 0 && Cli.Commands.Contains(verb): command = verb; headless = true; break;
                default: rest.Add(args[i]); break;
            }
        }
        return new Flags(headless, processes, env, workspace, config, command, [.. commandArgs], [.. rest], As: caller, Force: force);
    }
}
