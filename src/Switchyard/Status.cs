using Microsoft.Extensions.Options;

namespace Switchyard;

public sealed record GatewaySnapshot(
    int Port,
    string Backup,
    IReadOnlyList<string> Environments,
    IReadOnlyList<ServiceSnapshot> Services,
    string AspNetEnvironment = "",
    bool Supervising = false,
    string Workspace = Workspaces.Main,
    IReadOnlyList<Workspace>? Workspaces = null,
    string Name = "",
    IReadOnlyList<EnvironmentSnapshot>? EnvironmentDetails = null,
    ClaimSnapshot? Claim = null);

public sealed record EnvironmentSnapshot(string Name, string Color, bool Warn);

/// <summary>Route, LocalUrl and Target are empty for a process that is not routed (a UI, the tunnel).</summary>
public sealed record ServiceSnapshot(
    string Name,
    string Route,
    bool Local,
    bool LocalUp,
    string LocalUrl,
    string Target,
    string Group = "services",
    string Workspace = Workspaces.Main,
    bool OwnWorkspace = false,
    ProcessSnapshot? Process = null);

public sealed record ProcessSnapshot(
    string Type,
    ProcessState State,
    string? Detail,
    int? Pid,
    DateTime? StartedAt,
    string? RunningIn,
    int Restarts,
    bool Held,
    bool Building,
    bool CanBuild,
    BuildSummary? LastBuild,
    long LogSeq,
    bool Busy = false);

public sealed record BuildSummary(bool Success, DateTime FinishedAt, double Seconds, int Warnings, int Errors);

public sealed class StatusBuilder(IOptions<GatewayOptions> options, GatewayState state, ProcessSupervisor supervisor, Workspaces workspaces, Claims claims)
{
    public GatewaySnapshot Snapshot() => new(
        options.Value.Port,
        state.Backup,
        options.Value.Environments.Select(e => e.Name).ToList(),
        state.Services.Select(Service).ToList(),
        supervisor.AspNetEnvironment,
        supervisor.Enabled,
        state.DefaultWorkspace,
        workspaces.List(),
        options.Value.Name,
        options.Value.Environments.Select(e => new EnvironmentSnapshot(e.Name, e.Color, e.Warn)).ToList(),
        claims.Current);

    ServiceSnapshot Service(ServiceOptions s) => new(
        s.Name,
        s.Route,
        state.IsLocal(s.Name),
        s.Routed ? state.IsLocalUp(s.Name) : supervisor.Find(s.Name)?.State == ProcessState.Running,
        s.LocalUrl,
        s.Routed ? state.TargetOf(s) : "",
        s.Group,
        state.WorkspaceOf(s.Name),
        state.HasOwnWorkspace(s.Name),
        supervisor.Find(s.Name) is { } mp ? Process(mp) : null);

    public static ProcessSnapshot Process(ManagedProcess mp)
    {
        int? pid = null;
        try
        {
            if (mp.Process is { HasExited: false } p)
                pid = p.Id;
        }
        catch (InvalidOperationException)
        {
        }
        return new ProcessSnapshot(
            mp.Service.Process!.Type,
            mp.State,
            mp.Detail,
            pid,
            pid != null ? mp.StartedAt : null,
            pid != null ? mp.RunningIn?.Name : null,
            mp.Restarts,
            mp.Held,
            mp.Building,
            mp.Type.CanBuild,
            mp.LastBuild is { } b ? new BuildSummary(b.Success, b.FinishedAt, b.Seconds, b.Warnings, b.Errors.Count) : null,
            mp.Log.LastSeq,
            mp.Busy);
    }
}
