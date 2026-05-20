using System.Net.Http.Headers;
using SolidLinq.Quantower.CoreApi;

namespace SolidLinq.Quantower;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var o = Cli.Parse(args);
            using var http = new HttpClient();
            var httpBearer = string.IsNullOrWhiteSpace(o.HttpBearerToken) ? o.AuthToken : o.HttpBearerToken.Trim();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", httpBearer);
            var ack = new CoreAckClient(http);
            await using var bridge = new HubBridgeClient(o, ack, new LoggingStubExecutor());
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            await bridge.RunWithReconnectAsync(cts.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}

internal static class Cli
{
    public static BridgeOptions Parse(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i] ?? "";
            if (!a.StartsWith("--", StringComparison.Ordinal)) continue;
            var key = a[2..];
            var val = "";
            if (i + 1 < args.Length && !(args[i + 1]?.StartsWith("--") ?? false))
            {
                val = args[++i] ?? "";
            }
            map[key] = val;
        }

        string req(string k) =>
            map.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)
                ? v
                : throw new ArgumentException($"Missing --{k}");

        var ws = req("ws");
        var core = req("core");
        var auth = req("auth-token");
        var worker = map.TryGetValue("worker-token", out var wt) ? wt.Trim() : "";
        return new BridgeOptions
        {
            WebSocketUrl = new Uri(ws),
            BridgeInstanceId = req("bridge-instance-id"),
            AuthToken = auth,
            CoreBaseUrl = new Uri(core.TrimEnd('/') + "/"),
            HttpBearerToken = worker,
            Platform = map.TryGetValue("platform", out var pl) && pl.Length > 0 ? pl : "quantower",
            ProtocolVersion = int.TryParse(
                map.TryGetValue("protocol-version", out var pvs) ? pvs : "2",
                out var pv)
                ? pv
                : 2
        };
    }
}
