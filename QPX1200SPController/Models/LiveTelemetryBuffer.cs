using System;
using System.Collections.Generic;

namespace QPX1200SPController.Models
{
    public readonly struct TelemetrySample
    {
        public TelemetrySample(DateTime timestamp, double voltage, double current, double power)
        {
            Timestamp = timestamp;
            Voltage = voltage;
            Current = current;
            Power = power;
        }

        public DateTime Timestamp { get; }
        public double Voltage { get; }
        public double Current { get; }
        public double Power { get; }
    }

    /// <summary>
    /// Rolling in-memory buffer capped at 24 hours of live telemetry samples.
    /// </summary>
    public sealed class LiveTelemetryBuffer
    {
        public static readonly TimeSpan MaxRetention = TimeSpan.FromHours(24);

        private readonly object _sync = new object();
        private readonly List<TelemetrySample> _samples = new List<TelemetrySample>(4096);

        public int Count
        {
            get { lock (_sync) return _samples.Count; }
        }

        public void Add(DateTime timestamp, double voltage, double current, double power)
        {
            lock (_sync)
            {
                _samples.Add(new TelemetrySample(timestamp, voltage, current, power));
                PruneUnlocked(timestamp - MaxRetention);
            }
        }

        public void Clear()
        {
            lock (_sync)
            {
                _samples.Clear();
            }
        }

        /// <summary>
        /// Returns samples at or after <paramref name="fromInclusive"/>.
        /// Pass null to return all retained samples (up to 24h).
        /// </summary>
        public TelemetrySample[] GetSamplesSince(DateTime? fromInclusive)
        {
            lock (_sync)
            {
                if (_samples.Count == 0)
                    return Array.Empty<TelemetrySample>();

                if (fromInclusive == null)
                    return _samples.ToArray();

                int start = LowerBoundUnlocked(fromInclusive.Value);
                int count = _samples.Count - start;
                if (count <= 0)
                    return Array.Empty<TelemetrySample>();

                var result = new TelemetrySample[count];
                _samples.CopyTo(start, result, 0, count);
                return result;
            }
        }

        private void PruneUnlocked(DateTime cutoff)
        {
            if (_samples.Count == 0)
                return;

            int keepFrom = LowerBoundUnlocked(cutoff);
            if (keepFrom > 0)
                _samples.RemoveRange(0, keepFrom);
        }

        private int LowerBoundUnlocked(DateTime cutoff)
        {
            int lo = 0;
            int hi = _samples.Count;
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) / 2);
                if (_samples[mid].Timestamp < cutoff)
                    lo = mid + 1;
                else
                    hi = mid;
            }
            return lo;
        }
    }

    public sealed class GraphTimeWindowOption
    {
        public GraphTimeWindowOption(string displayName, TimeSpan? duration)
        {
            DisplayName = displayName;
            Duration = duration;
        }

        public string DisplayName { get; }

        /// <summary>Null means Maximum (all retained in-memory data).</summary>
        public TimeSpan? Duration { get; }

        public override string ToString() => DisplayName;
    }
}
