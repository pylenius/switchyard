using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Switchyard;

/// <summary>
/// Which services run locally, which environment the rest fall back to, and which checkout each local
/// one runs from. Every change is saved to the state file and raises <see cref="Changed"/>, which
/// rebuilds the proxy routes and lets the supervisor start or stop processes.
/// </summary>
public sealed class GatewayState
{
    readonly GatewayOptions _options;
    readonly ILogger<GatewayState> _logger;
    readonly Lock _lock = new();
    readonly HashSet<string> _local;
    readonly ConcurrentDictionary<string, bool> _localUp = new();
    readonly Dictionary<string, string> _workspaceOf;
    string _backup;
    string _workspace;

    public event Action? Changed;

    public GatewayState(IOptions<GatewayOptions> options, ILogger<GatewayState> logger)
    {
        _options = options.Value;
        _logger = logger;

        var saved = Load();
        _backup = saved?.Backup is { } backup && FindEnvironment(backup) != null
            ? backup
            : _options.Environments.FirstOrDefault()?.Name ?? throw new InvalidOperationException("Gateway:Environments is empty.");
        _local = saved?.Local is { } local
            ? local.Where(name => FindService(name) != null).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : _options.Services.Where(s => s.Routed).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _workspace = saved?.Workspace ?? Workspaces.Main;
        _workspaceOf = new Dictionary<string, string>(saved?.Workspaces ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ServiceOptions> Services => _options.Services;
    public IReadOnlyList<ServiceOptions> RoutedServices => [.. _options.Services.Where(s => s.Routed)];

    public string Backup
    {
        get { lock (_lock) return _backup; }
    }

    public string DefaultWorkspace
    {
        get { lock (_lock) return _workspace; }
    }

    public bool IsLocal(string name)
    {
        lock (_lock)
            return _local.Contains(name);
    }

    public bool IsLocalUp(string name) => _localUp.GetValueOrDefault(name);

    /// <summary>The checkout a service runs from: its own choice, else the default.</summary>
    public string WorkspaceOf(string name)
    {
        lock (_lock)
            return _workspaceOf.GetValueOrDefault(name) ?? _workspace;
    }

    public bool HasOwnWorkspace(string name)
    {
        lock (_lock)
            return _workspaceOf.ContainsKey(name);
    }

    public string TargetOf(ServiceOptions service)
    {
        lock (_lock)
        {
            if (_local.Contains(service.Name))
                return service.LocalUrl;

            return FindEnvironment(_backup)!.Upstreams.TryGetValue(service.Upstream, out var url)
                ? url
                : throw new InvalidOperationException($"Environment '{_backup}' has no '{service.Upstream}' upstream.");
        }
    }

    public bool SetBackup(string environment)
    {
        if (FindEnvironment(environment) is not { } found)
            return false;

        lock (_lock)
        {
            if (_backup == found.Name)
                return true;
            _backup = found.Name;
        }
        OnChanged($"backup -> {found.Name}");
        return true;
    }

    public bool SetLocal(string name, bool local)
    {
        if (FindService(name) is not { } service)
            return false;

        bool changed;
        lock (_lock)
            changed = local ? _local.Add(service.Name) : _local.Remove(service.Name);

        if (changed)
            OnChanged($"{service.Name} -> {(local ? "local" : "backup")}");
        return true;
    }

    /// <summary>All routed services; UIs, the tunnel and databases keep their own setting.</summary>
    public void SetAllLocal(bool local)
    {
        lock (_lock)
        {
            foreach (var service in _options.Services.Where(s => s.Routed))
            {
                if (local)
                    _local.Add(service.Name);
                else
                    _local.Remove(service.Name);
            }
        }
        OnChanged(local ? "all -> local" : "all -> backup");
    }

    /// <summary>
    /// For one service: its own checkout, or null to follow the default. For none (null): the default
    /// for all, which also drops every service's own choice.
    /// </summary>
    public bool SetWorkspace(string? service, string? workspace)
    {
        lock (_lock)
        {
            if (service == null)
            {
                _workspace = workspace ?? Workspaces.Main;
                _workspaceOf.Clear();
            }
            else if (FindService(service) is not { } found)
            {
                return false;
            }
            else if (workspace == null || workspace.Equals(_workspace, StringComparison.OrdinalIgnoreCase))
            {
                _workspaceOf.Remove(found.Name);
            }
            else
            {
                _workspaceOf[found.Name] = workspace;
            }
        }
        OnChanged($"{service ?? "all"} -> checkout {workspace ?? "default"}");
        return true;
    }

    /// <summary>Health is display-only: a service toggled local stays local while it is down.</summary>
    public void SetLocalUp(string name, bool up) => _localUp[name] = up;

    public ServiceOptions? FindService(string name) =>
        _options.Services.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    EnvironmentOptions? FindEnvironment(string name) =>
        _options.Environments.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    void OnChanged(string what)
    {
        _logger.LogInformation("Changed: {Change}", what);
        Save();
        Changed?.Invoke();
    }

    SavedState? Load()
    {
        try
        {
            return File.Exists(_options.StateFilePath)
                ? JsonSerializer.Deserialize<SavedState>(File.ReadAllText(_options.StateFilePath), JsonSerializerOptions.Web)
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ignoring unreadable state file {Path}", _options.StateFilePath);
            return null;
        }
    }

    void Save()
    {
        SavedState state;
        lock (_lock)
            state = new SavedState(_backup, [.. _local.Order()], _workspace, new Dictionary<string, string>(_workspaceOf));

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_options.StateFilePath)!);
            File.WriteAllText(_options.StateFilePath, JsonSerializer.Serialize(state, JsonSerializerOptions.Web));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save state file {Path}", _options.StateFilePath);
        }
    }

    sealed record SavedState(string? Backup, List<string>? Local, string? Workspace, Dictionary<string, string>? Workspaces);
}
