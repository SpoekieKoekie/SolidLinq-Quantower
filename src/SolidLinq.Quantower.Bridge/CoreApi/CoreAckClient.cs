using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SolidLinq.Quantower.CoreApi;

public sealed class CoreAckClient(HttpClient http)
{
    public Task PostExecutionAckAsync(
        System.Uri coreBase,
        string commandId,
        bool ok,
        string? status,
        double? brokerMs,
        double? totalMs,
        string? bridgeInstanceId,
        string? platform,
        CancellationToken ct)
    {
        var url = new System.Uri(coreBase, "/ack");
        var body = new System.Collections.Generic.Dictionary<string, object?>
        {
            ["ack_type"] = "execution_ack",
            ["type"] = "execution_ack",
            ["id"] = commandId,
            ["ok"] = ok,
            ["status"] = status ?? (ok ? "filled" : "error")
        };
        if (brokerMs.HasValue) body["brokerMs"] = brokerMs.Value;
        if (totalMs.HasValue) body["totalMs"] = totalMs.Value;
        if (!string.IsNullOrWhiteSpace(bridgeInstanceId)) body["bridgeInstanceId"] = bridgeInstanceId;
        if (!string.IsNullOrWhiteSpace(platform)) body["platform"] = platform;

        return PostJsonAsync(url, body, ct);
    }

    public Task PostClosedTradeAsync(System.Uri coreBase, string instanceId, object body, CancellationToken ct)
    {
        var url = new System.Uri(coreBase, $"/cbot/execution?instanceId={System.Uri.EscapeDataString(instanceId)}");
        return PostJsonAsync(url, body, ct);
    }

    private async Task PostJsonAsync(System.Uri url, object body, CancellationToken ct)
    {
        var res = await http.PostAsJsonAsync(url, body, cancellationToken: ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
    }
}
