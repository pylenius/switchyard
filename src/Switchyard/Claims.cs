using Microsoft.Extensions.Options;

namespace Switchyard;

/// <summary>Who holds the gateway: the agent or person whose test runs on it, until when, from which checkout.</summary>
public sealed record ClaimSnapshot(string Holder, DateTime Since, DateTime Until, string? Workspace);

/// <summary>
/// One holder at a time for the whole gateway. Several agents in their own worktrees share one gateway,
/// one set of ports and one routing, so a test only means something while nobody else switches, builds
/// or restarts. While a claim is held the admin API refuses changes from anyone else (unless forced);
/// reads stay open. The holder's own changes renew it; one that is not renewed lapses, and so does one whose
/// holder sends nothing for ClaimIdleMinutes (10), so an agent that crashed, forgot or went quiet cannot block the gateway. Taking a claim from a worktree moves the services to that
/// checkout (those built from code: dotnet and vite); releasing it, or its lapsing, puts the checkouts back as
/// they were.
/// </summary>
public sealed class Claims(GatewayState state, IOptions<GatewayOptions> options, ILogger<Claims> logger)
{
    /// <summary>A holder that does nothing this long loses it, whatever --for said: an agent that went quiet holds up the others.</summary>
    TimeSpan Idle => TimeSpan.FromMinutes(options.Value.ClaimIdleMinutes);

    sealed record Held(string Holder, DateTime Since, DateTime Until, TimeSpan For, string? Workspace, GatewayState.CheckoutLayout? Before, DateTime LastSeen);

    /// <summary>The earlier of its end and its idle lapse.</summary>
    DateTime Ends(Held h) => h.LastSeen + Idle < h.Until ? h.LastSeen + Idle : h.Until;

    readonly Lock _lock = new();
    Held? _held;

    public ClaimSnapshot? Current => Get() is { } h ? Snapshot(h) : null;

    ClaimSnapshot Snapshot(Held h) => new(h.Holder, h.Since, Ends(h), h.Workspace);

    /// <summary>Takes or renews it; the current claim when someone else holds it.</summary>
    public ClaimSnapshot? Take(string holder, TimeSpan duration, string? workspace)
    {
        var current = Get();
        if (current != null && !Same(current.Holder, holder))
            return Snapshot(current);
        var now = DateTime.UtcNow;
        var before = current?.Before ?? (workspace != null ? state.Checkouts() : null);
        lock (_lock)
            _held = new(holder, current?.Since ?? now, now + duration, duration, workspace ?? current?.Workspace, before, now);
        // Only what is built from the checkout's code: a database or a tunnel would only be restarted for nothing.
        // A service already running from that checkout is left alone, and a renewal changes nothing.
        if (workspace != null && (current == null || !Same(current.Workspace, workspace)))
            foreach (var service in state.Services.Where(BuiltFromCheckout))
                state.SetWorkspace(service.Name, workspace);
        if (current == null)
            logger.LogInformation("Claimed by {Holder} for {Minutes:0} min{From}", holder, duration.TotalMinutes, workspace != null ? $", checkout {workspace}" : "");
        return null;
    }

    /// <summary>Gives it up (the holder, or anyone with force); the claim that stays otherwise.</summary>
    public ClaimSnapshot? Release(string? holder, bool force)
    {
        var current = Get();
        if (current == null)
            return null;
        if (!force && !Same(current.Holder, holder))
            return Snapshot(current);
        End(current, "released");
        return null;
    }

    /// <summary>Whether a change from this caller goes through; the holder's renews the claim.</summary>
    public bool Allows(string? caller)
    {
        var current = Get();
        if (current == null)
            return true;
        if (!Same(current.Holder, caller))
            return false;
        lock (_lock)
            if (_held == current)
                _held = current with { Until = DateTime.UtcNow + current.For, LastSeen = DateTime.UtcNow };
        return true;
    }

    /// <summary>A read by the holder (status, logs): it is still there, so the idle lapse moves on.</summary>
    public void Touch(string? caller)
    {
        var current = Get();
        if (current == null || !Same(current.Holder, caller))
            return;
        lock (_lock)
            if (_held == current)
                _held = current with { LastSeen = DateTime.UtcNow };
    }

    Held? Get()
    {
        Held? lapsed;
        lock (_lock)
        {
            if (_held == null || Ends(_held) > DateTime.UtcNow)
                return _held;
            lapsed = _held;
        }
        End(lapsed, "lapsed");
        return null;
    }

    void End(Held claim, string how)
    {
        lock (_lock)
        {
            if (_held != claim)
                return;
            _held = null;
        }
        if (claim.Before is { } before)
            state.RestoreCheckouts(before);
        logger.LogInformation("Claim of {Holder} {How}", claim.Holder, how);
    }

    public static bool BuiltFromCheckout(ServiceOptions service) => service.Process?.Type is "dotnet" or "vite";

    static bool Same(string? a, string? b) => a != null && b != null && a.Equals(b, StringComparison.OrdinalIgnoreCase);
}
