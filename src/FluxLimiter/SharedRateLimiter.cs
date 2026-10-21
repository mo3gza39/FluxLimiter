namespace FluxLimiter;

// Pure, deterministic scheduler core. It does NOT intercept or throttle OS traffic.
// All byte budgets are binary KB/s (1 KB = 1024 bytes).
public sealed class SharedRateLimiter
{
    private sealed class Bucket
    {
        public double Tokens;
        public double LastSeconds;
        public double Capacity;
        public double Rate;
    }

    private readonly Dictionary<string, Bucket> buckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();

    public bool TryConsume(string identity, int limitKBps, int bytes, double monotonicSeconds)
    {
        if (string.IsNullOrWhiteSpace(identity)) throw new ArgumentException("Identity is required.", nameof(identity));
        if (limitKBps <= 0) throw new ArgumentOutOfRangeException(nameof(limitKBps));
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        if (double.IsNaN(monotonicSeconds) || double.IsInfinity(monotonicSeconds) || monotonicSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(monotonicSeconds));
        double rate = limitKBps * 1024.0;
        lock (gate)
        {
            if (!buckets.TryGetValue(identity, out var bucket))
            {
                bucket = new Bucket { Rate = rate, Capacity = rate, Tokens = rate, LastSeconds = monotonicSeconds };
                buckets.Add(identity, bucket);
            }
            else
            {
                double elapsed = Math.Max(0, monotonicSeconds - bucket.LastSeconds);
                bucket.Tokens = Math.Min(bucket.Capacity, bucket.Tokens + elapsed * bucket.Rate);
                bucket.LastSeconds = Math.Max(bucket.LastSeconds, monotonicSeconds);
                if (bucket.Rate != rate)
                {
                    bucket.Rate = rate;
                    bucket.Capacity = rate;
                    bucket.Tokens = Math.Min(bucket.Tokens, rate);
                }
            }
            if (bytes > bucket.Tokens) return false;
            bucket.Tokens -= bytes;
            return true;
        }
    }

    public void Remove(string identity)
    {
        lock (gate) buckets.Remove(identity);
    }
}
