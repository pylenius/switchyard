using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace Switchyard;

/// <summary>One route and one single-destination cluster per service, pointing at its current target.</summary>
public static class ProxyRoutes
{
    /// <summary>Set by Program to the local port a request arrived on; routes match on it and strip it.</summary>
    public const string ListenerHeader = "X-Switchyard-Port";

    // MCP streams (SSE) stay open far longer than YARP's 100 s default.
    static readonly ForwarderRequestConfig LongRequests = new() { ActivityTimeout = TimeSpan.FromMinutes(30) };

    static RouteMatch OnPort(int port, string path) => new()
    {
        Path = path,
        Headers = [new RouteHeader { Name = ListenerHeader, Values = [port.ToString()], Mode = HeaderMatchMode.ExactHeader }],
    };

    public static (IReadOnlyList<RouteConfig> Routes, IReadOnlyList<ClusterConfig> Clusters) Build(GatewayState state, int port)
    {
        var routes = new List<RouteConfig>();
        var clusters = new List<ClusterConfig>();

        foreach (var service in state.RoutedServices)
        {
            var clusterId = $"svc-{service.Name}";
            routes.Add(new RouteConfig
            {
                RouteId = service.Name,
                ClusterId = clusterId,
                Match = service.Port is { } ownPort
                    ? OnPort(ownPort, "/{**rest}")
                    : OnPort(port, $"{service.PathPrefix}/{{**rest}}"),
                Transforms = [new Dictionary<string, string> { ["RequestHeaderRemove"] = ListenerHeader }],
            });
            clusters.Add(new ClusterConfig
            {
                ClusterId = clusterId,
                HttpRequest = LongRequests,
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    ["target"] = new() { Address = state.TargetOf(service) },
                },
            });
        }

        return (routes, clusters);
    }
}

/// <summary>
/// Probes each routed service's local port so the TUI can show which ones are running. Every probe is a
/// connection the service logs at Debug level, so one the gateway itself runs and saw healthy is
/// probed only every <see cref="OwnInterval"/>: often enough to notice it hanging, rarely enough to keep
/// its log readable. The others are probed every tick, to notice one started from the IDE.
/// </summary>
public sealed class LocalHealthProbe(GatewayState state, ProcessSupervisor supervisor) : BackgroundService
{
    static readonly TimeSpan OwnInterval = TimeSpan.FromSeconds(10);

    readonly Dictionary<string, DateTime> _lastProbe = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            var due = state.RoutedServices.Where(service =>
                supervisor.Find(service.Name) is not { Alive: true, State: ProcessState.Running }
                || DateTime.UtcNow - _lastProbe.GetValueOrDefault(service.Name) >= OwnInterval).ToList();
            foreach (var service in due)
                _lastProbe[service.Name] = DateTime.UtcNow;
            await Task.WhenAll(due.Select(async service =>
                state.SetLocalUp(service.Name, await ProcessTools.IsListening(new Uri(service.LocalUrl).Port, stoppingToken))));
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
