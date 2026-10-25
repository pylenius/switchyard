using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Switchyard;

/// <summary>Client for the /_gw admin API; the text UI and the commands go through it even when the gateway runs in-process.</summary>
public sealed class GatewayClient(string adminUrl)
{
    static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    readonly HttpClient _http = new() { BaseAddress = new Uri(adminUrl), Timeout = TimeSpan.FromSeconds(5) };

    public const string AsHeader = "X-Switchyard-As";
    public const string ForceHeader = "X-Switchyard-Force";

    public string AdminUrl => adminUrl;

    /// <summary>Who is asking (the claim's holder is let through), and whether to override someone else's claim.</summary>
    public string? Caller { get; private set; }
    public bool Force { get; private set; }

    public GatewayClient As(string? caller, bool force = false)
    {
        Caller = string.IsNullOrWhiteSpace(caller) ? null : caller;
        Force = force;
        _http.DefaultRequestHeaders.Remove(AsHeader);
        _http.DefaultRequestHeaders.Remove(ForceHeader);
        if (!string.IsNullOrWhiteSpace(caller))
            _http.DefaultRequestHeaders.Add(AsHeader, caller);
        if (force)
            _http.DefaultRequestHeaders.Add(ForceHeader, "1");
        return this;
    }

    public static async Task<bool> IsRunning(string adminUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            using var response = await http.GetAsync(adminUrl + "state");
            if (!response.IsSuccessStatusCode)
                return false;
            // Another app on the port can answer 2xx too (an SPA fallback page); only a gateway sends its state.
            using var state = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return state.RootElement.ValueKind == JsonValueKind.Object && state.RootElement.TryGetProperty("services", out _);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public Task<GatewaySnapshot?> GetState() => _http.GetFromJsonAsync<GatewaySnapshot>("state", Json);

    public Task<GatewaySnapshot?> SetBackup(string env) => Put("backup", new { env });

    public Task<GatewaySnapshot?> SetLocal(string service, bool local) => Put($"services/{service}", new { local });

    public Task<GatewaySnapshot?> SetAllLocal(bool local) => Put("services", new { local });

    /// <summary>One service's checkout (null: follow the default), or with no service the default for all.</summary>
    public Task<GatewaySnapshot?> SetWorkspace(string? service, string? workspace) =>
        Put(service == null ? "workspace" : $"services/{service}/workspace", new { workspace });

    public Task<GatewaySnapshot?> Claim(string caller, int minutes, string? workspace) =>
        Put("claim", new { @as = caller, minutes, workspace });

    public async Task<GatewaySnapshot?> Release(string? caller, bool force)
    {
        using var response = await _http.DeleteAsync($"claim?as={Uri.EscapeDataString(caller ?? "")}&force={force.ToString().ToLowerInvariant()}");
        await EnsureSuccess(response);
        return await response.Content.ReadFromJsonAsync<GatewaySnapshot>(Json);
    }

    public async Task<ProcessSnapshot?> Command(string process, string action, bool? restart = null)
    {
        using var response = await _http.PostAsync($"processes/{process}/{action}{(restart is { } r ? $"?restart={r.ToString().ToLowerInvariant()}" : "")}", null);
        await EnsureSuccess(response);
        return await response.Content.ReadFromJsonAsync<ProcessSnapshot>(Json);
    }

    public Task<ProcessSnapshot?> GetProcess(string process) => _http.GetFromJsonAsync<ProcessSnapshot>($"processes/{process}", Json);

    public async Task<BuildResult?> GetBuild(string process)
    {
        using var response = await _http.GetAsync($"processes/{process}/build");
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccess(response);
        return await response.Content.ReadFromJsonAsync<BuildResult>(Json);
    }

    public async Task<IReadOnlyList<LogLine>> GetLogs(string process, long after, int tail) =>
        await _http.GetFromJsonAsync<List<LogLine>>($"processes/{process}/logs?after={after}&tail={tail}", Json) ?? [];

    async Task<GatewaySnapshot?> Put(string path, object body)
    {
        using var response = await _http.PutAsJsonAsync(path, body, Json);
        await EnsureSuccess(response);
        return await response.Content.ReadFromJsonAsync<GatewaySnapshot>(Json);
    }

    /// <summary>The API's own message ("Unknown service 'x'.") instead of a bare status code.</summary>
    static async Task EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = (await response.Content.ReadAsStringAsync()).Trim().Trim('"');
        throw new GatewayException(body.Length > 0 ? body : $"{(int)response.StatusCode} {response.ReasonPhrase}", response.StatusCode);
    }
}

public sealed class GatewayException(string message, HttpStatusCode? status = null) : Exception(message)
{
    /// <summary>409: someone else holds the claim.</summary>
    public bool Claimed => status == HttpStatusCode.Conflict;
}
