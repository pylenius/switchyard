namespace Switchyard;

/// <summary>The project's switchyard.json (top-level keys), plus switchyard.user.json and the command line.</summary>
public sealed class GatewayOptions
{
    public const string ConfigFile = "switchyard.json";
    public const string UserConfigFile = "switchyard.user.json";

    /// <summary>Shown in the text UI and names the state folder. Default: the main checkout's folder name.</summary>
    public string Name { get; set; } = "";

    public int Port { get; set; } = 8088;

    /// <summary>Path a service is routed on unless it sets its own; {name} is the service's name.</summary>
    public string RoutePrefix { get; set; } = "/{name}";

    /// <summary>Remembers routing and checkouts; logs/ and pids.json live next to it. Default: ~/.config/switchyard/{Name}/state.json.</summary>
    public string StateFile { get; set; } = "";

    /// <summary>Main checkout. Empty: the git checkout the config file is in (from a worktree: its main checkout). Set when loading.</summary>
    public string RepoRoot { get; set; } = "";

    /// <summary>ASP.NET environment the dotnet processes run as, unless --env or SWITCHYARD_ENV says otherwise.</summary>
    public string RunAs { get; set; } = "Development";

    /// <summary>Environment variables given to every process the gateway starts (not to containers).</summary>
    public Dictionary<string, string> ProcessEnv { get; set; } = [];

    /// <summary>Lines of output kept in memory per process for the UI and the logs API.</summary>
    public int LogLines { get; set; } = 5000;

    /// <summary>How many builds run at once.</summary>
    public int ParallelBuilds { get; set; } = 4;

    /// <summary>A claim whose holder sends nothing for this long lapses, whatever its --for.</summary>
    public double ClaimIdleMinutes { get; set; } = 10;

    public List<EnvironmentOptions> Environments { get; set; } = [];
    public List<ServiceOptions> Services { get; set; } = [];

    /// <summary>Folder of the config file; relative paths in it are resolved against this. Set when loading.</summary>
    public string ConfigDirectory { get; set; } = "";

    public string StateFilePath => StateFile != ""
        ? Path.GetFullPath(Path.Combine(ConfigDirectory, ExpandHome(StateFile)))
        : Path.Combine(ExpandHome("~/.config/switchyard"), Name, "state.json");

    public string StateDirectory => Path.GetDirectoryName(StateFilePath)!;

    /// <summary>Fills what defaults to other settings: the name, and each service's route path.</summary>
    public GatewayOptions Resolve(string configDirectory)
    {
        ConfigDirectory = configDirectory;
        // From a worktree the main checkout, so every worktree shares the name and the state folder.
        RepoRoot = RepoRoot != ""
            ? Path.GetFullPath(Path.Combine(configDirectory, ExpandHome(RepoRoot)))
            : Workspaces.FindRepoRoot(configDirectory) ?? configDirectory;
        if (Name == "")
            Name = Path.GetFileName(RepoRoot.TrimEnd('/', '\\'));
        foreach (var service in Services)
            service.PathPrefix ??= RoutePrefix.Replace("{name}", service.Name).TrimEnd('/');
        return this;
    }

    public static string ExpandHome(string path) => path.StartsWith('~')
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..].TrimStart('/', '\\'))
        : path;

    /// <summary>--config, else SWITCHYARD_CONFIG, else switchyard.json in this folder or the nearest one above it.</summary>
    public static string? FindConfig(string? explicitPath)
    {
        if ((explicitPath ?? Environment.GetEnvironmentVariable("SWITCHYARD_CONFIG")) is { Length: > 0 } given)
            return Path.GetFullPath(Directory.Exists(given) ? Path.Combine(given, ConfigFile) : given);
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, ConfigFile);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}

public sealed class EnvironmentOptions
{
    public string Name { get; set; } = "";

    /// <summary>Base URL per upstream kind ("api", "mcp"), matched against <see cref="ServiceOptions.Upstream"/>.</summary>
    public Dictionary<string, string> Upstreams { get; set; } = [];

    /// <summary>Text UI colour: cyan, green, yellow, red, magenta.</summary>
    public string Color { get; set; } = "cyan";

    /// <summary>Show a warning while this is the backup environment (production: toggled-off services use live data).</summary>
    public bool Warn { get; set; }
}

public sealed class ServiceOptions
{
    public string Name { get; set; } = "";

    /// <summary>The text UI groups by it, in the order the groups first appear.</summary>
    public string Group { get; set; } = "services";

    /// <summary>The service's local http address. Empty for a process the gateway runs but does not route (a UI, the tunnel).</summary>
    public string LocalUrl { get; set; } = "";

    /// <summary>Own listener port. When set the service gets every path on that port instead of a path prefix.</summary>
    public int? Port { get; set; }

    public string Upstream { get; set; } = "api";

    /// <summary>Path on <see cref="LocalUrl"/> that answers 2xx once the service is ready; without it an open port is enough.</summary>
    public string? Health { get; set; }

    /// <summary>How the gateway starts and builds it. Without it the gateway only routes to whatever runs on the port.</summary>
    public ProcessOptions? Process { get; set; }

    /// <summary>Path it is routed on; default from <see cref="GatewayOptions.RoutePrefix"/>.</summary>
    public string? PathPrefix { get; set; }

    public bool Routed => LocalUrl != "";
    public string Route => !Routed ? "" : Port is { } port ? $":{port}" : PathPrefix ?? "";
}

public sealed class ProcessOptions
{
    /// <summary>dotnet, vite, docker or command; see ProjectTypes.cs.</summary>
    public string Type { get; set; } = "command";

    /// <summary>dotnet: the web project's .csproj, relative to the checkout.</summary>
    public string? Project { get; set; }

    /// <summary>vite: the folder with package.json; command: the working directory. Relative to the checkout.</summary>
    public string? Dir { get; set; }

    /// <summary>command: the program to run.</summary>
    public string? Command { get; set; }

    /// <summary>Extra arguments (dotnet: after the app's own; docker: after the image).</summary>
    public List<string> Args { get; set; } = [];

    /// <summary>Process environment (docker: the container's). Values expand ${VAR}, ${VAR:-default}, {env} and {user}.</summary>
    public Dictionary<string, string> Env { get; set; } = [];

    /// <summary>Env var name → user-secrets key of <see cref="Project"/>, read at start unless the variable is already set.</summary>
    public Dictionary<string, string> EnvFromUserSecrets { get; set; } = [];

    /// <summary>
    /// Env var name → a command (program and arguments) whose trimmed output is the value, run at start
    /// unless the variable is already set, and remembered while the gateway runs. For secrets kept in a
    /// vault: ["az", "keyvault", "secret", "show", ...], ["op", "read", "op://..."], ["aws", "secretsmanager", ...].
    /// </summary>
    public Dictionary<string, List<string>> EnvFromCommand { get; set; } = [];

    /// <summary>
    /// Started first. A routed dependency is waited for only while it is local (otherwise the backup
    /// environment serves it); an unrouted one (a database) is always started along.
    /// </summary>
    public List<string> DependsOn { get; set; } = [];

    /// <summary>Seconds between the polite stop signal and killing the process tree.</summary>
    public int ShutdownSeconds { get; set; } = 10;

    /// <summary>Unrouted processes: a port that answers once the process is ready (vite's dev server).</summary>
    public int? Port { get; set; }

    /// <summary>docker: image, container name, "host:container" ports, "volume:path" mounts, readiness command run with docker exec.</summary>
    public string? Image { get; set; }
    public string? Container { get; set; }
    public List<string> Ports { get; set; } = [];
    public List<string> Volumes { get; set; } = [];
    public List<string> ReadyExec { get; set; } = [];
}
