using System;

namespace SolidLinq.Quantower;

/// <summary>Configuration for the SolidLinq hub WebSocket + HTTP callbacks.</summary>
public sealed class BridgeOptions
{
    public Uri WebSocketUrl { get; set; } = default!;
    public string BridgeInstanceId { get; set; } = "";
    public string AuthToken { get; set; } = "";
    public Uri CoreBaseUrl { get; set; } = default!;
    public string WorkerApiToken { get; set; } = "";
    public string Platform { get; set; } = "quantower";
    /// <summary>WebSocket protocol version negotiated with LinkStateHub (1 or 2).</summary>
    public int ProtocolVersion { get; set; } = 2;
}
