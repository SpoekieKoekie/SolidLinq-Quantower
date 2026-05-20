namespace SolidLinq.Quantower.Algo;

/// <summary>Aligned with <c>WebSocketBridgeBot</c> SL mode.</summary>
public enum BridgeSlMode
{
    UseStrategySl,
    DisableSl
}

/// <summary>Aligned with <c>WebSocketBridgeBot</c> drawdown mode for overall max loss.</summary>
public enum BridgeDrawdownMode
{
    Static,
    Trailing,
    EOD
}
