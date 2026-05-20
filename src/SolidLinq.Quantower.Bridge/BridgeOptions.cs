using System;

namespace SolidLinq.Quantower;

/// <summary>Configuration for the SolidLinq hub WebSocket + HTTP callbacks.</summary>
public sealed class BridgeOptions
{
    public Uri WebSocketUrl { get; set; } = default!;
    public string BridgeInstanceId { get; set; } = "";
    public string AuthToken { get; set; } = "";
    public Uri CoreBaseUrl { get; set; } = default!;
    /// <summary>Optional REST bearer override (e.g. console <c>--worker-token</c>). When empty, <see cref="AuthToken"/> is used for HTTP.</summary>
    public string HttpBearerToken { get; set; } = "";
    public string Platform { get; set; } = "quantower";
    /// <summary>WebSocket protocol version negotiated with LinkStateHub (1 or 2).</summary>
    public int ProtocolVersion { get; set; } = 2;
}
