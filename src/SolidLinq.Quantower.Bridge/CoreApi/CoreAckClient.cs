using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SolidLinq.Quantower.CoreApi;

public sealed class CoreAckClient(HttpClient http)
{
    private const string DefaultWebhookAckOrigin = "https://webhook.solidlinq.com";

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

        return PostJsonAsync(url, body, authToken: null, ct);
    }

    public Task PostClosedTradeAsync(
        System.Uri coreBase,
        string instanceId,
        object body,
        string? authToken,
        CancellationToken ct)
    {
        var url = new System.Uri(coreBase, $"/cbot/execution?instanceId={System.Uri.EscapeDataString(instanceId)}");
        return PostJsonAsync(url, body, authToken, ct);
    }

    /// <summary>POST /ack position_closed to Core (always). Webhook is best-effort and never throws.</summary>
    public async Task PostPositionClosedAckAsync(
        System.Uri coreBase,
        object body,
        string? authToken,
        CancellationToken ct)
    {
        await PostJsonAsync(new System.Uri(coreBase, "/ack"), body, authToken: null, ct).ConfigureAwait(false);
        await TryPostWebhookAckAsync(body, authToken, ct).ConfigureAwait(false);
    }

    /// <summary>Webhook /ack mirror — best-effort, never throws.</summary>
    public async Task TryPostWebhookAckAsync(object body, string? authToken, CancellationToken ct)
    {
        var tok = (authToken ?? "").Trim();
        if (tok.Length == 0) return;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, new System.Uri($"{DefaultWebhookAckOrigin.TrimEnd('/')}/ack"))
            {
                Content = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(body),
                    Encoding.UTF8,
                    "application/json")
            };
            req.Headers.TryAddWithoutValidation("x-webhook-token", tok);

            using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                var errBody = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine(
                    $"[SolidLinq] webhook /ack {(int)res.StatusCode}: {Truncate(errBody, 200)}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SolidLinq] webhook /ack error: {ex.Message}");
        }
    }

    private async Task PostJsonAsync(System.Uri url, object body, string? authToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };

        var tok = (authToken ?? "").Trim();
        if (tok.Length > 0)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tok);

        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (res.IsSuccessStatusCode) return;

        var errBody = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new HttpRequestException(
            $"POST {url} failed {(int)res.StatusCode}: {Truncate(errBody, 300)}");
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max];
}
