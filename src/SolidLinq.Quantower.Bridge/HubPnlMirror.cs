using System;

namespace SolidLinq.Quantower;

/// <summary>Hub WebSocket mirror for position_closed (set by <see cref="HubBridgeClient"/>).</summary>
public static class HubPnlMirror
{
    public static bool TrySend(string json) => TrySendCore?.Invoke(json) ?? false;

    internal static Func<string, bool>? TrySendCore { get; set; }
}
