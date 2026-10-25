using System.Text;

namespace Switchyard;

/// <summary>
/// Full-screen text UI over the /_gw API: the process list (space toggles local, which starts or stops
/// it), a log view per process, the last build's errors, and a checkout picker. Polls the gateway, so
/// changes made through the API or the commands show up here too.
/// </summary>
public sealed class Tui
{
    const string Reset = "\e[0m", Bold = "\e[1m", Dim = "\e[2m", Inverse = "\e[7m";
    const string Green = "\e[32m", Yellow = "\e[33m", Red = "\e[31m", Cyan = "\e[36m", Magenta = "\e[35m";

    enum View { List, Logs, Errors, Picker, ConfirmQuit }

    readonly GatewayClient _client;
    readonly bool _ownsGateway;
    GatewaySnapshot? _snapshot;
    string? _error;
    string? _message;
    DateTime _messageUntil;
    View _view = View.List;
    int _selected;

    // Log view
    string? _logName;
    readonly List<LogLine> _lines = [];
    long _logAfter;
    int _scroll;
    string _filter = "";
    bool _typing;
    string _typed = "";
    DateTime _nextLogPoll;

    // Errors view
    BuildResult? _build;
    int _errorSelected;

    // Checkout picker: for one service, or for all when null
    string? _pickFor;
    int _pickSelected;

    Tui(GatewayClient client, bool ownsGateway)
    {
        _client = client;
        _ownsGateway = ownsGateway;
    }

    public static Task Run(GatewayClient client, bool ownsGateway, CancellationToken cancellationToken) =>
        new Tui(client, ownsGateway).Loop(cancellationToken);

    async Task Loop(CancellationToken cancellationToken)
    {
        var lastFrame = "";
        var nextPoll = DateTime.MinValue;

        Console.TreatControlCAsInput = true;
        Console.Write("\e[?1049h\e[?25l"); // alternate screen, hide cursor
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                while (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    try
                    {
                        if (!await Handle(key))
                            return;
                    }
                    catch (Exception ex)
                    {
                        Say(ex.Message, error: true);
                    }
                    nextPoll = DateTime.MinValue;
                }

                if (DateTime.UtcNow >= nextPoll)
                {
                    nextPoll = DateTime.UtcNow.AddSeconds(1);
                    try
                    {
                        _snapshot = await _client.GetState();
                        _error = null;
                    }
                    catch (Exception ex)
                    {
                        _error = $"Gateway not reachable at {_client.AdminUrl}: {ex.Message}";
                    }
                }
                if (_view == View.Logs && DateTime.UtcNow >= _nextLogPoll)
                    await PollLogs();

                var frame = Render();
                if (frame != lastFrame)
                {
                    Console.Write(frame);
                    lastFrame = frame;
                }
                await Task.Delay(40, CancellationToken.None);
            }
        }
        finally
        {
            Console.Write("\e[?25h\e[?1049l");
            Console.TreatControlCAsInput = false;
        }
    }

    void Say(string message, bool error = false)
    {
        _message = error ? $"{Red}{message}{Reset}" : message;
        _messageUntil = DateTime.UtcNow.AddSeconds(error ? 8 : 4);
    }

    /// <summary>Grouped, groups in the order they first appear in the config, services in config order within them.</summary>
    List<ServiceSnapshot> Ordered()
    {
        if (_snapshot == null)
            return [];
        var groups = _snapshot.Services.Select(s => s.Group).Distinct().ToList();
        return [.. _snapshot.Services.Select((s, i) => (s, i)).OrderBy(x => groups.IndexOf(x.s.Group)).ThenBy(x => x.i).Select(x => x.s)];
    }

    string Title => _snapshot is { Name: { Length: > 0 } name } ? $"{name} · switchyard" : "switchyard";

    ServiceSnapshot? Selected => Ordered() is { Count: > 0 } list ? list[Math.Clamp(_selected, 0, list.Count - 1)] : null;

    // ── Keys ──────────────────────────────────────────────────────────

    /// <returns>false to quit.</returns>
    async Task<bool> Handle(ConsoleKeyInfo key)
    {
        if (_snapshot == null)
            return !IsQuit(key);

        switch (_view)
        {
            case View.ConfirmQuit:
                if (key.Key == ConsoleKey.Y)
                    return false;
                _view = View.List;
                return true;
            case View.Logs:
                await HandleLogs(key);
                return true;
            case View.Errors:
                await HandleErrors(key);
                return true;
            case View.Picker:
                await HandlePicker(key);
                return true;
        }

        if (IsQuit(key))
        {
            var running = _snapshot.Services.Count(s => s.Process?.Pid != null);
            if (!_ownsGateway || running == 0)
                return false;
            _view = View.ConfirmQuit;
            return true;
        }

        var list = Ordered();
        var s = Selected!;
        var env = _snapshot.Environments.ToList().IndexOf(_snapshot.Backup);
        switch (key.Key)
        {
            case ConsoleKey.UpArrow or ConsoleKey.K:
                _selected = (_selected - 1 + list.Count) % list.Count;
                break;
            case ConsoleKey.DownArrow or ConsoleKey.J:
                _selected = (_selected + 1) % list.Count;
                break;
            case ConsoleKey.Spacebar:
                _snapshot = await _client.SetLocal(s.Name, !s.Local);
                break;
            case ConsoleKey.LeftArrow:
                _snapshot = await _client.SetBackup(_snapshot.Environments[(env - 1 + _snapshot.Environments.Count) % _snapshot.Environments.Count]);
                break;
            case ConsoleKey.RightArrow:
                _snapshot = await _client.SetBackup(_snapshot.Environments[(env + 1) % _snapshot.Environments.Count]);
                break;
            case ConsoleKey.A:
                _snapshot = await _client.SetAllLocal(true);
                break;
            case ConsoleKey.N:
                _snapshot = await _client.SetAllLocal(false);
                break;
            case ConsoleKey.Enter or ConsoleKey.L:
                OpenLogs(s.Name);
                break;
            case ConsoleKey.E:
                await OpenErrors(s.Name);
                break;
            case ConsoleKey.W:
                _pickFor = key.KeyChar == 'W' ? null : s.Name;
                var current = _pickFor == null ? _snapshot.Workspace : s.Workspace;
                _pickSelected = Math.Max(0, PickerItems().FindIndex(w => w == current));
                _view = View.Picker;
                break;
            case ConsoleKey.B when key.KeyChar == 'B':
                foreach (var target in list.Where(x => x.Local && x.Process?.CanBuild == true))
                    await _client.Command(target.Name, "build", restart: true);
                Say("building every local service");
                break;
            case ConsoleKey.B:
            case ConsoleKey.R:
            case ConsoleKey.S:
            case ConsoleKey.X:
                await ProcessKey(key, s);
                break;
        }
        return true;
    }

    static bool IsQuit(ConsoleKeyInfo key) =>
        key.Key is ConsoleKey.Q or ConsoleKey.Escape || key is { Key: ConsoleKey.C, Modifiers: ConsoleModifiers.Control };

    /// <summary>b build, r restart, s start, x stop: the same in the list and the log view.</summary>
    async Task ProcessKey(ConsoleKeyInfo key, ServiceSnapshot s)
    {
        if (s.Process == null)
        {
            Say($"{s.Name} is only routed; the gateway does not run it");
            return;
        }
        if (_snapshot is { Supervising: false })
        {
            Say("the gateway runs with --no-processes and starts nothing", error: true);
            return;
        }
        switch (key.Key)
        {
            case ConsoleKey.B when !s.Process.CanBuild:
                Say($"{s.Name} has nothing to build");
                break;
            case ConsoleKey.B:
                await _client.Command(s.Name, "build", restart: true);
                Say($"building {s.Name}");
                break;
            case ConsoleKey.R:
                await _client.Command(s.Name, "restart");
                Say($"restarting {s.Name}");
                break;
            case ConsoleKey.S:
                await _client.Command(s.Name, "start");
                break;
            case ConsoleKey.X:
                await _client.Command(s.Name, "stop");
                Say($"stopping {s.Name}; it stays local (space routes it to {_snapshot?.Backup})");
                break;
        }
    }

    void OpenLogs(string name, long? jumpTo = null)
    {
        if (_logName != name)
        {
            _lines.Clear();
            _logAfter = 0;
            _filter = "";
        }
        _logName = name;
        _scroll = 0;
        _jumpTo = jumpTo;
        _highlight = jumpTo;
        _nextLogPoll = DateTime.MinValue;
        _view = View.Logs;
    }

    long? _jumpTo;

    async Task PollLogs()
    {
        _nextLogPoll = DateTime.UtcNow.AddMilliseconds(300);
        if (_logName == null)
            return;
        try
        {
            var lines = await _client.GetLogs(_logName, _logAfter, _logAfter == 0 ? 5000 : 10_000);
            if (lines.Count == 0)
                return;
            _lines.AddRange(lines);
            _logAfter = lines[^1].Seq;
            if (_lines.Count > 5000)
                _lines.RemoveRange(0, _lines.Count - 5000);
            // Scrolled up: keep the same lines on screen while new ones arrive below.
            if (_scroll > 0)
                _scroll += lines.Count(Shown);
        }
        catch (Exception ex)
        {
            Say(ex.Message, error: true);
        }
    }

    List<LogLine> VisibleLines() => _filter == "" && !_hideDebug ? _lines : [.. _lines.Where(Shown)];

    bool Shown(LogLine line) =>
        (_filter == "" || line.Text.Contains(_filter, StringComparison.OrdinalIgnoreCase)) && !(_hideDebug && IsDebug(line.Text));

    // Serilog's "[12:00:00 DBG]" and Microsoft's "dbug:" console format.
    static bool IsDebug(string text) =>
        text.Contains(" DBG] ", StringComparison.Ordinal) || text.Contains(" VRB] ", StringComparison.Ordinal)
        || text.StartsWith("dbug:", StringComparison.Ordinal) || text.StartsWith("trce:", StringComparison.Ordinal);

    bool _hideDebug = true;

    async Task HandleLogs(ConsoleKeyInfo key)
    {
        if (_typing)
        {
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    (_filter, _typing, _scroll) = (_typed, false, 0);
                    break;
                case ConsoleKey.Escape:
                    _typing = false;
                    break;
                case ConsoleKey.Backspace:
                    _typed = _typed.Length > 0 ? _typed[..^1] : "";
                    break;
                default:
                    if (!char.IsControl(key.KeyChar))
                        _typed += key.KeyChar;
                    break;
            }
            return;
        }

        var page = Math.Max(1, Height() - 5);
        var max = Math.Max(0, VisibleLines().Count - page);
        switch (key.Key)
        {
            case ConsoleKey.Escape or ConsoleKey.Q:
                if (_filter != "")
                    _filter = "";
                else
                    _view = View.List;
                break;
            case ConsoleKey.UpArrow or ConsoleKey.K:
                _scroll = Math.Min(max, _scroll + 1);
                break;
            case ConsoleKey.DownArrow or ConsoleKey.J:
                _scroll = Math.Max(0, _scroll - 1);
                break;
            case ConsoleKey.PageUp:
                _scroll = Math.Min(max, _scroll + page);
                break;
            case ConsoleKey.PageDown or ConsoleKey.Spacebar:
                _scroll = Math.Max(0, _scroll - page);
                break;
            case ConsoleKey.Home or ConsoleKey.G when key.KeyChar != 'G':
                _scroll = max;
                break;
            case ConsoleKey.End or ConsoleKey.F or ConsoleKey.G:
                _scroll = 0;
                break;
            case ConsoleKey.C:
                _lines.Clear();
                _scroll = 0;
                break;
            case ConsoleKey.D:
                _hideDebug = !_hideDebug;
                _scroll = 0;
                break;
            case ConsoleKey.E:
                await OpenErrors(_logName!);
                break;
            case ConsoleKey.B or ConsoleKey.R or ConsoleKey.S or ConsoleKey.X:
                if (_snapshot?.Services.FirstOrDefault(s => s.Name == _logName) is { } service)
                    await ProcessKey(key, service);
                _scroll = 0;
                break;
            default:
                if (key.KeyChar == '/')
                    (_typing, _typed) = (true, _filter);
                break;
        }
    }

    async Task OpenErrors(string name)
    {
        _build = await _client.GetBuild(name);
        if (_build == null)
        {
            Say($"{name} has not been built since the gateway started");
            return;
        }
        _logName = name == _logName ? _logName : null;
        _logName ??= name;
        _errorSelected = 0;
        _view = View.Errors;
    }

    async Task HandleErrors(ConsoleKeyInfo key)
    {
        var count = _build?.Errors.Count ?? 0;
        switch (key.Key)
        {
            case ConsoleKey.Escape or ConsoleKey.Q:
                _view = View.List;
                break;
            case ConsoleKey.UpArrow or ConsoleKey.K when count > 0:
                _errorSelected = (_errorSelected - 1 + count) % count;
                break;
            case ConsoleKey.DownArrow or ConsoleKey.J when count > 0:
                _errorSelected = (_errorSelected + 1) % count;
                break;
            case ConsoleKey.Enter or ConsoleKey.L:
                var name = _logName!;
                _logName = null; // reload: the error's line may be older than what the log view holds
                OpenLogs(name, count > 0 ? _build!.Errors[_errorSelected].Seq : null);
                break;
            case ConsoleKey.B:
                if (_snapshot?.Services.FirstOrDefault(s => s.Name == _logName) is { } service)
                    await ProcessKey(key, service);
                OpenLogs(_logName!);
                break;
        }
    }

    /// <summary>Checkout names; for one service the first entry (null) follows the default.</summary>
    List<string?> PickerItems() =>
        [.. (_pickFor != null ? new string?[] { null } : []), .. (_snapshot?.Workspaces ?? []).Select(w => (string?)w.Name)];

    async Task HandlePicker(ConsoleKeyInfo key)
    {
        var items = PickerItems();
        switch (key.Key)
        {
            case ConsoleKey.Escape or ConsoleKey.Q:
                _view = View.List;
                break;
            case ConsoleKey.UpArrow or ConsoleKey.K:
                _pickSelected = (_pickSelected - 1 + items.Count) % items.Count;
                break;
            case ConsoleKey.DownArrow or ConsoleKey.J:
                _pickSelected = (_pickSelected + 1) % items.Count;
                break;
            case ConsoleKey.Enter or ConsoleKey.Spacebar:
                var picked = items[_pickSelected];
                _snapshot = await _client.SetWorkspace(_pickFor, picked);
                Say(_pickFor == null
                    ? $"every service now runs from {picked}; running ones restart"
                    : $"{_pickFor} runs from {picked ?? "the default checkout"}{(Selected?.Process?.Pid != null ? "; restarting" : "")}");
                _view = View.List;
                break;
        }
    }

    // ── Rendering ─────────────────────────────────────────────────────

    static int Width()
    {
        try { return Math.Max(60, Console.WindowWidth); } catch (IOException) { return 120; }
    }

    static int Height()
    {
        try { return Math.Max(15, Console.WindowHeight); } catch (IOException) { return 40; }
    }

    string Render()
    {
        var width = Width();
        var sb = new StringBuilder("\e[H");
        var lines = 0;
        void Line(string text = "")
        {
            sb.Append(Fit(text, width - 1)).Append("\e[K\n");
            lines++;
        }

        if (_snapshot == null)
        {
            Line();
            Line($"  {Bold}switchyard{Reset}");
            Line();
            Line($"  {Red}{_error ?? "Connecting..."}{Reset}");
            return sb.Append("\e[J").ToString();
        }

        switch (_view)
        {
            case View.Logs:
                RenderLogs(Line);
                break;
            case View.Errors:
                RenderErrors(Line);
                break;
            case View.Picker:
                RenderPicker(Line);
                break;
            default:
                RenderList(Line);
                break;
        }
        return sb.Append("\e[J").ToString();
    }

    void Header(Action<string> line)
    {
        var s = _snapshot!;
        var envs = string.Join(" ", s.Environments.Select(e =>
            e == s.Backup ? $"{Inverse}{EnvColor(e)} {e} {Reset}" : $"{Dim} {e} {Reset}"));
        line("");
        line($"  {Bold}{Title}{Reset}  {Cyan}http://localhost:{s.Port}{Reset}   as {Bold}{s.AspNetEnvironment}{Reset}" +
             $"   checkout {Magenta}{s.Workspace}{Reset}   backup {envs}" +
             (s.Supervising ? "" : $"   {Yellow}--no-processes{Reset}"));
        // Your keys still work while it is held (the text UI overrides claims), but they cut into that test.
        if (s.Claim is { } claim)
            line($"  {Yellow}claimed by {Bold}{claim.Holder}{Reset}{Yellow} since {claim.Since.ToLocalTime():HH:mm}, until {claim.Until.ToLocalTime():HH:mm}" +
                 (claim.Workspace != null ? $", checkout {claim.Workspace}" : "") + $" (a test is running; your changes cut into it){Reset}");
        line("");
    }

    void Footer(Action<string> line, string keys)
    {
        line(_error != null ? $"  {Red}{_error}{Reset}" : _message != null && DateTime.UtcNow < _messageUntil ? $"  {_message}" : "");
        line($"  {Dim}{keys}{Reset}");
    }

    void RenderList(Action<string> line)
    {
        var s = _snapshot!;
        Header(line);
        line($"  {Dim}    {"NAME",-10} {"ROUTE",-11} {"GOES TO",-7}  {"STATE",-24} {"BUILD",-13} CHECKOUT{Reset}");

        var list = Ordered();
        string? group = null;
        for (var i = 0; i < list.Count; i++)
        {
            var svc = list[i];
            if (svc.Group != group)
            {
                group = svc.Group;
                line($"  {Dim}{group}{Reset}");
            }
            var selected = i == Math.Clamp(_selected, 0, list.Count - 1);
            var cursor = selected ? $"{Bold}>{Reset}" : " ";
            var box = svc.Local ? $"{Green}[x]{Reset}" : "[ ]";
            var name = selected ? $"{Bold}{svc.Name,-10}{Reset}" : $"{svc.Name,-10}";
            var goesTo = svc.Route == "" ? $"{Dim}{"-",-7}{Reset}"
                : svc.Local ? $"{Green}{"local",-7}{Reset}" : $"{EnvColor(s.Backup)}{s.Backup,-7}{Reset}";
            var checkout = svc.Process == null ? "" : svc.Local ? CheckoutText(svc) : $"{Dim}{svc.Workspace}{Reset}";
            line($"  {cursor} {box} {name} {svc.Route,-11} {goesTo}  {StateText(svc)} {BuildText(svc)} {checkout}");
        }

        line("");
        if (s.EnvironmentDetails?.FirstOrDefault(e => e.Name == s.Backup) is { Warn: true })
            line($"  {Red}{Bold}The backup is {s.Backup}: toggled-off services read and write its live data.{Reset}");
        if (Selected is { } sel)
            line($"  {Dim}{Describe(sel)}{Reset}");
        Footer(line, "↑↓ move  space local on/off  enter logs  e errors  b build  B build all local  r restart  s start  x stop\n" +
                     "  w checkout  W checkout for all  ←→ backup env  a all local  n all backup  q quit");
        if (_view == View.ConfirmQuit)
        {
            var running = s.Services.Count(x => x.Process?.Pid != null);
            line("");
            line($"  {Yellow}{Bold}Stop {running} running process{(running == 1 ? "" : "es")} and quit?  y / n{Reset}");
        }
    }

    string Describe(ServiceSnapshot svc)
    {
        var parts = new List<string>();
        if (svc.Target != "")
            parts.Add($"{svc.Name} → {svc.Target}");
        if (svc.Process is { } p)
        {
            var ws = _snapshot!.Workspaces?.FirstOrDefault(w => w.Name == svc.Workspace);
            parts.Add($"{p.Type} from {svc.Workspace}{(ws?.Branch != null ? $" ({ws.Branch})" : "")}");
            if (p.Pid != null)
                parts.Add($"pid {p.Pid}");
            if (p.Restarts > 0)
                parts.Add($"restarted {p.Restarts}x");
            if (p.Detail != null)
                parts.Add(p.Detail);
        }
        return string.Join("  ·  ", parts);
    }

    string CheckoutText(ServiceSnapshot svc)
    {
        var text = svc.Process?.RunningIn is { } runningIn && runningIn != svc.Workspace
            ? $"{runningIn} → {svc.Workspace}"
            : svc.Workspace;
        return svc.OwnWorkspace ? $"{Magenta}{Bold}{text}{Reset}" : svc.Workspace == Workspaces.Main ? $"{Dim}{text}{Reset}" : $"{Magenta}{text}{Reset}";
    }

    static string StateText(ServiceSnapshot svc)
    {
        const int w = 24;
        if (svc.Process is not { } p)
        {
            var port = svc.LocalUrl != "" ? new Uri(svc.LocalUrl).Port.ToString() : "";
            return svc.LocalUp
                ? $"{Green}{$"● up {port}",-w}{Reset}"
                : $"{(svc.Local ? Red : Dim)}{$"○ down {port}",-w}{Reset}";
        }

        var (text, color) = p.State switch
        {
            ProcessState.Running => ($"● running {Uptime(p.StartedAt)}", Green),
            ProcessState.Unhealthy => ("● not answering", Red),
            ProcessState.Starting => ($"◌ starting {Uptime(p.StartedAt)}", Yellow),
            ProcessState.Building => ("◌ building", Yellow),
            ProcessState.SettingUp => ("◌ installing packages", Yellow),
            ProcessState.Waiting => ($"◌ {p.Detail ?? "waiting"}", Yellow),
            ProcessState.Stopping => ("◌ stopping", Yellow),
            ProcessState.Crashed => ($"✗ {(p.Detail is { Length: < 20 } d ? d : "failed")}", Red),
            ProcessState.BuildFailed => ("✗ build failed", Red),
            ProcessState.External => ("● external", Cyan),
            _ when svc.Local && p.Held => ("○ stopped (s starts)", Yellow),
            _ when svc.Local => ("○ stopped", Yellow),
            _ => ("○ off", Dim),
        };
        return $"{color}{Truncate(text, w),-w}{Reset}";
    }

    static string BuildText(ServiceSnapshot svc)
    {
        const int w = 13;
        return svc.Process switch
        {
            { Building: true } => $"{Yellow}{"building…",-w}{Reset}",
            { LastBuild: { Success: true } b } => $"{Dim}{$"✓ {b.Seconds:0.#}s",-w}{Reset}",
            { LastBuild: { } b } => $"{Red}{$"✗ {b.Errors} error{(b.Errors == 1 ? "" : "s")}",-w}{Reset}",
            _ => new string(' ', w),
        };
    }

    static string Uptime(DateTime? since)
    {
        if (since == null)
            return "";
        var t = DateTime.Now - since.Value;
        return t.TotalSeconds < 60 ? $"{t.TotalSeconds:0}s" : t.TotalMinutes < 60 ? $"{t.TotalMinutes:0}m" : $"{(int)t.TotalHours}h{t.Minutes:00}";
    }

    void RenderLogs(Action<string> line)
    {
        var svc = _snapshot!.Services.FirstOrDefault(s => s.Name == _logName);
        var visible = VisibleLines();
        var height = Math.Max(3, Height() - 5);

        if (_jumpTo is { } seq && visible.FindIndex(l => l.Seq >= seq) is var at and >= 0)
        {
            _scroll = Math.Max(0, visible.Count - at - height / 2);
            _jumpTo = null;
        }
        _scroll = Math.Min(_scroll, Math.Max(0, visible.Count - height));
        var end = visible.Count - _scroll;
        var start = Math.Max(0, end - height);

        var status = svc == null ? "" : StateText(svc).TrimEnd();
        var mode = _scroll == 0 ? $"{Green}following{Reset}" : $"{Yellow}scrolled ({_scroll} lines up, f follows){Reset}";
        var debug = _hideDebug ? $"   {Dim}debug hidden (d){Reset}" : "";
        var filter = _typing ? $"   filter: {Bold}{_typed}▏{Reset}" : _filter != "" ? $"   filter {Bold}{_filter}{Reset} ({visible.Count} lines, esc clears)" : "";
        line($" {Bold}{_logName}{Reset}  {status}  {mode}{debug}{filter}");

        for (var i = start; i < end; i++)
        {
            var l = visible[i];
            var highlight = l.Seq == _highlight ? Inverse : "";
            var text = l.Kind == "sys" ? $"{Cyan}» {l.Text}{Reset}"
                : l.Kind == "build" && !l.Text.Contains(": error ", StringComparison.Ordinal) && !l.Text.Contains(": warning ", StringComparison.Ordinal)
                    ? $"{Dim}{l.Text}{Reset}"
                : l.Text.Contains(": error ", StringComparison.Ordinal) || l.Text.Contains("fail:", StringComparison.Ordinal) ? $"{Red}{l.Text}{Reset}"
                : l.Text.Contains(": warning ", StringComparison.Ordinal) || l.Text.Contains("warn:", StringComparison.Ordinal) ? $"{Yellow}{l.Text}{Reset}"
                : l.Text;
            line($"{highlight}{Dim}{l.Time:HH:mm:ss}{Reset}{highlight} {text}");
        }
        for (var i = end - start; i < height; i++)
            line("");

        Footer(line, "↑↓ PgUp PgDn scroll  f/End follow  Home top  / filter  d debug lines  c clear  e errors  b build  r restart  s start  x stop  esc back");
    }

    long? _highlight;

    void RenderErrors(Action<string> line)
    {
        var build = _build!;
        line("");
        line($"  {Bold}{_logName}{Reset}  last build {(build.Success ? $"{Green}succeeded{Reset}" : $"{Red}failed{Reset}")} at {build.FinishedAt:HH:mm:ss} in {build.Seconds}s" +
             $", {build.Errors.Count} error{(build.Errors.Count == 1 ? "" : "s")}, {build.Warnings} warning{(build.Warnings == 1 ? "" : "s")}");
        line("");
        if (build.Errors.Count == 0)
            line(build.Success ? $"  {Green}No errors.{Reset}" : $"  {Yellow}The build failed without an error line the gateway recognises; enter shows the log.{Reset}");

        var height = Math.Max(3, Height() - 7);
        var first = Math.Clamp(_errorSelected - height / 2, 0, Math.Max(0, build.Errors.Count - height));
        for (var i = first; i < Math.Min(build.Errors.Count, first + height); i++)
        {
            var e = build.Errors[i];
            var cursor = i == _errorSelected ? $"{Bold}>{Reset}" : " ";
            var where = e.File != null ? $"{e.File}:{e.Line}:{e.Column}" : "";
            line($"  {cursor} {Red}{e.Code}{Reset} {Cyan}{where}{Reset}  {e.Message}");
        }
        line("");
        Footer(line, "↑↓ select  enter show in the log  b build again  esc back");
        _highlight = build.Errors.Count > 0 ? build.Errors[Math.Clamp(_errorSelected, 0, build.Errors.Count - 1)].Seq : null;
    }

    void RenderPicker(Action<string> line)
    {
        Header(line);
        line(_pickFor == null
            ? $"  {Bold}Checkout for every service{Reset} {Dim}(also clears each service's own choice){Reset}"
            : $"  {Bold}Checkout for {_pickFor}{Reset}");
        line("");
        var items = PickerItems();
        for (var i = 0; i < items.Count; i++)
        {
            var cursor = i == _pickSelected ? $"{Bold}>{Reset}" : " ";
            if (items[i] is not { } name)
            {
                line($"  {cursor} {Dim}follow the default ({_snapshot!.Workspace}){Reset}");
                continue;
            }
            var ws = _snapshot!.Workspaces!.First(w => w.Name == name);
            line($"  {cursor} {Magenta}{name,-36}{Reset} {ws.Branch,-44} {Dim}{ws.Path}{Reset}");
        }
        line("");
        Footer(line, "↑↓ select  enter use it (a running service restarts from there)  esc back");
    }

    string EnvColor(string env) => (_snapshot?.EnvironmentDetails?.FirstOrDefault(e => e.Name == env)?.Color ?? "cyan") switch
    {
        "red" => Red,
        "yellow" => Yellow,
        "green" => Green,
        "magenta" => Magenta,
        _ => Cyan,
    };

    static string Truncate(string text, int width) => text.Length <= width ? text : text[..(width - 1)] + "…";

    /// <summary>Cuts a line with ANSI colours to the terminal width, counting only visible characters.</summary>
    static string Fit(string text, int width)
    {
        if (text.Contains('\n'))
            return string.Join("\e[K\n", text.Split('\n').Select(part => Fit(part, width)));

        var sb = new StringBuilder();
        var visible = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\e')
            {
                var end = text.IndexOf('m', i);
                if (end < 0)
                    break;
                sb.Append(text, i, end - i + 1);
                i = end;
                continue;
            }
            if (visible >= width)
            {
                sb.Append(Reset);
                break;
            }
            sb.Append(text[i]);
            visible++;
        }
        return sb.ToString();
    }
}
