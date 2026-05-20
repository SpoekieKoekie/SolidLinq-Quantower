using System;
using System.Collections.Concurrent;

namespace SolidLinq.Quantower;

/// <summary>Ignores duplicate command keys only within a short TTL (not forever).</summary>
internal sealed class TimedCommandDedupe
{
    private readonly ConcurrentDictionary<string, long> _seenMs = new();
    private readonly long _ttlMs;

    public TimedCommandDedupe(TimeSpan ttl) => _ttlMs = (long)ttl.TotalMilliseconds;

    /// <returns>True if this key should be processed now.</returns>
    public bool TryAccept(string key)
    {
        var k = (key ?? "").Trim();
        if (k.Length == 0) return true;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        PruneStale(now);

        if (_seenMs.TryGetValue(k, out var prev) && now - prev < _ttlMs)
            return false;

        _seenMs[k] = now;
        return true;
    }

    private void PruneStale(long nowMs)
    {
        if (_seenMs.Count < 500) return;

        foreach (var pair in _seenMs)
        {
            if (nowMs - pair.Value >= _ttlMs)
                _seenMs.TryRemove(pair.Key, out _);
        }
    }
}
