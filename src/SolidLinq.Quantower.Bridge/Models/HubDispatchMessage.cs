using System.Text.Json.Serialization;

namespace SolidLinq.Quantower.Models;

/// <summary>JSON shape broadcast by SolidLinq-Core LinkStateHub.cbotOrderWire (order | close).</summary>
public sealed class HubDispatchMessage
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("symbol")]
    public string? Symbol { get; set; }

    [JsonPropertyName("side")]
    public string? Side { get; set; }

    [JsonPropertyName("orderType")]
    public string? OrderType { get; set; }

    [JsonPropertyName("lots")]
    public double Lots { get; set; }

    [JsonPropertyName("slPercent")]
    public double? SlPercent { get; set; }

    [JsonPropertyName("tpPercent")]
    public double? TpPercent { get; set; }

    [JsonPropertyName("strategyId")]
    public string? StrategyId { get; set; }

    [JsonPropertyName("alertId")]
    public string? AlertId { get; set; }

    [JsonPropertyName("leg")]
    public int Leg { get; set; }

    [JsonPropertyName("sentAt")]
    public long SentAt { get; set; }

    [JsonPropertyName("idempotencyKey")]
    public string? IdempotencyKey { get; set; }

    [JsonPropertyName("slPct")]
    public double? SlPct { get; set; }

    [JsonPropertyName("tpPct")]
    public double? TpPct { get; set; }
}
