using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Switchyard;

/// <summary>Client for the /_gw admin API; the text UI and the commands go through it even when the gateway runs in-process.</summary>
public sealed class GatewayClient(string adminUrl)
{
    static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    readonly HttpClient _http = new() { BaseAddress = new Uri(adminUrl), Timeout = TimeSpan.FromSeconds(5) };

    public string AdminUrl => adminUrl;

    public static async Task<bool> IsRunning(string adminUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            return (await http.GetAsync(adminUrl + "state")).IsSuccessStatusCode;
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
        throw new GatewayException(body.Length > 0 ? body : $"{(int)response.StatusCode} {response.ReasonPhrase}");
    }
}

public sealed class GatewayException(string message) : Exception(message);
