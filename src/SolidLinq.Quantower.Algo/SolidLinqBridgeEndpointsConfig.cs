using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SolidLinq.Quantower.Algo;

/// <summary>URLs loaded from JSON so they are not hard-coded in strategy inputs.</summary>
internal sealed class SolidLinqBridgeEndpointsConfig
{
    [JsonPropertyName("webSocketUrl")]
    public string? WebSocketUrl { get; set; }

    [JsonPropertyName("coreBaseUrl")]
    public string? CoreBaseUrl { get; set; }

    internal static string DefaultConfigPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolidLinq",
            "quantower-bridge.json");

    internal static SolidLinqBridgeEndpointsConfig? TryLoad(string? explicitPath, out string resolvedPath,
        out string? error)
    {
        error = null;
        resolvedPath = string.IsNullOrWhiteSpace(explicitPath)
            ? DefaultConfigPath()
            : Environment.ExpandEnvironmentVariables(explicitPath.Trim());

        try
        {
            if (!File.Exists(resolvedPath))
            {
                error = $"Config file not found: {resolvedPath}";
                return null;
            }

            var json = File.ReadAllText(resolvedPath);
            var cfg = JsonSerializer.Deserialize<SolidLinqBridgeEndpointsConfig>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (cfg == null)
            {
                error = "Invalid JSON in bridge config.";
                return null;
            }

            return cfg;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }
}
