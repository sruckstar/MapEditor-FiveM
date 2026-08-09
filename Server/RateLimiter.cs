using System;
using System.Collections.Generic;

namespace MapEditor.Server
{
    /// <summary>
    /// A token bucket per player.
    ///
    /// Every event handler here is reachable by any connected client as often as it likes, and two of them
    /// touch the disk. Without this, one player holding down a key — or one deliberately hostile client —
    /// is a loop writing files as fast as the server will take them. §5.9 of the port plan asks for it by
    /// name.
    ///
    /// Tokens refill continuously rather than in windows, so a player who has been idle gets their whole
    /// burst back and one who is hammering gets a steady trickle, which is the shape that matches how the
    /// editor is actually used: a flurry of saves while building, then nothing for an hour.
    /// </summary>
    public sealed class RateLimiter
    {
        private sealed class Bucket
        {
            public double Tokens;
            public int LastTicks;
        }

        private readonly double _capacity;
        private readonly double _refillPerSecond;
        private readonly Dictionary<string, Bucket> _buckets = new Dictionary<string, Bucket>();

        /// <param name="capacity">How many requests may arrive back to back.</param>
        /// <param name="refillPerSecond">How quickly the allowance comes back afterwards.</param>
        public RateLimiter(double capacity, double refillPerSecond)
        {
            _capacity = capacity;
            _refillPerSecond = refillPerSecond;
        }

        public bool Allow(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;

            var now = Environment.TickCount;

            Bucket bucket;
            if (!_buckets.TryGetValue(key, out bucket))
            {
                bucket = new Bucket { Tokens = _capacity, LastTicks = now };
                _buckets[key] = bucket;
            }
            else
            {
                // Unchecked: TickCount wraps every 49 days, and a wrapped difference read as a huge
                // positive number would hand out a full bucket to whoever happened to ask at that moment.
                var elapsed = unchecked(now - bucket.LastTicks);
                if (elapsed < 0) elapsed = 0;

                bucket.Tokens = Math.Min(_capacity, bucket.Tokens + elapsed / 1000.0 * _refillPerSecond);
                bucket.LastTicks = now;
            }

            if (bucket.Tokens < 1.0) return false;

            bucket.Tokens -= 1.0;
            return true;
        }

        /// <summary>Called when a player drops, so that the table does not grow for the server's uptime.</summary>
        public void Forget(string key)
        {
            if (key != null) _buckets.Remove(key);
        }
    }
}
