using System;
using System.Collections.Generic;

namespace FluxLimiter;

/// <summary>
/// Deterministic simulation-only byte queue. It does not intercept operating-system traffic.
/// Each identity and direction has an independent token bucket.
/// </summary>
public sealed class TrafficPacer
{
    private sealed class Budget
    {
        public double Tokens;
        public double UpdatedAt;
        public double Rate;
    }

    private readonly Dictionary<string, Budget> budgets = new(StringComparer.OrdinalIgnoreCase);
    private readonly object sync = new();

    /// <summary>Returns the earliest permitted monotonic time, in seconds, for a byte chunk.</summary>
    public double Schedule(string identity, bool download, int limitKBps, int bytes, double nowSeconds)
    {
        if (string.IsNullOrWhiteSpace(identity)) throw new ArgumentException("Required", nameof(identity));
        if (limitKBps <= 0) throw new ArgumentOutOfRangeException(nameof(limitKBps));
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        if (!double.IsFinite(nowSeconds) || nowSeconds < 0) throw new ArgumentOutOfRangeException(nameof(nowSeconds));
        double rate = limitKBps * 1024.0;
        string key = SharedRateLimiter.DirectionKey(identity, download);
        lock (sync)
        {
            if (!budgets.TryGetValue(key, out var budget))
            {
                budget = new Budget { Tokens = rate, Rate = rate, UpdatedAt = nowSeconds };
                budgets.Add(key, budget);
            }
            double effectiveNow = Math.Max(nowSeconds, budget.UpdatedAt);
            if (budget.Rate != rate)
            {
                budget.Rate = rate;
                budget.Tokens = Math.Min(budget.Tokens, rate);
            }
            budget.Tokens = Math.Min(rate, budget.Tokens + (effectiveNow - budget.UpdatedAt) * rate);
            budget.UpdatedAt = effectiveNow;
            // Negative tokens represent queued work. Large requests are paced rather than denied forever.
            budget.Tokens -= bytes;
            return budget.Tokens >= 0 ? effectiveNow : effectiveNow - budget.Tokens / rate;
        }
    }

    public void Clear(string identity)
    {
        lock (sync)
        {
            budgets.Remove(SharedRateLimiter.DirectionKey(identity, true));
            budgets.Remove(SharedRateLimiter.DirectionKey(identity, false));
        }
    }
}
