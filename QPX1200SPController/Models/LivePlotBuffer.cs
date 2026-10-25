using System;
using System.Collections.Generic;

namespace QPX1200SPController.Models
{
    /// <summary>
    /// One unbroken run of live samples. The plots draw straight from these lists (time as OLE
    /// date), so a new sample is drawn without rebuilding anything.
    /// </summary>
    public sealed class LiveSegment
    {
        public List<double> Times { get; } = new List<double>(1024);
        public List<double> Voltages { get; } = new List<double>(1024);
        public List<double> Currents { get; } = new List<double>(1024);

        public int Count => Times.Count;
    }

    /// <summary>A command, trip, alarm or connection event shown as a vertical marker on the live plots.</summary>
    public sealed class LiveEvent
    {
        public const string KindCommand = "Command";
        public const string KindTrip = "Trip";
        public const string KindAlarm = "Alarm";
        public const string KindConnection = "Connection";

        public LiveEvent(DateTime timestamp, string kind, string title, string text)
        {
            X = timestamp.ToOADate();
            Kind = kind;
            Title = title;
            Text = text;
        }

        /// <summary>Time as OLE date.</summary>
        public double X { get; }
        public string Kind { get; }
        public string Title { get; }
        public string Text { get; }

        /// <summary>
        /// The marker for a command, alarm or connection record; null for queries and
        /// communication errors, which are not drawn.
        /// </summary>
        public static LiveEvent FromRecord(ActivityRecord record)
        {
            if (record == null)
                return null;
            switch (record.Kind)
            {
                case ActivityRecord.KindCommand:
                    string text = $"{record.Command}  {ResultSymbol(record.Result)} {record.Result}";
                    if (!string.IsNullOrEmpty(record.Detail) && record.Result != ActivityRecord.ResultOk)
                        text += $"\n{record.Detail}";
                    return new LiveEvent(record.Timestamp, KindCommand, record.Source ?? "Command", text);
                case ActivityRecord.KindAlarm:
                    return new LiveEvent(record.Timestamp, KindAlarm,
                        record.Result == ActivityRecord.ResultCleared ? "Alarm cleared" : "Alarm",
                        string.IsNullOrEmpty(record.Detail) ? record.Command : $"{record.Command}\n{record.Detail}");
                case ActivityRecord.KindConnection:
                    return new LiveEvent(record.Timestamp, KindConnection, $"Connection ({record.Source})",
                        string.IsNullOrEmpty(record.Detail) ? record.Result : $"{record.Result}: {record.Detail}");
                default:
                    return null;
            }
        }

        private static string ResultSymbol(string result)
        {
            if (result == ActivityRecord.ResultOk)
                return "✓";
            return result == ActivityRecord.ResultMismatch ? "⚠" : "✗";
        }
    }

    /// <summary>
    /// The live samples and events of this session (UI thread only), kept for 24 hours like the
    /// old buffer so the long plot windows still work. Samples are kept in segments: a new one
    /// starts after a gap (link lost, disconnected, clock step back), so the plots draw no line
    /// across periods without data. Copied from CPX400DP LivePlotBuffer and adapted (24 h).
    /// </summary>
    public sealed class LivePlotBuffer
    {
        public static readonly TimeSpan MaxRetention = TimeSpan.FromHours(24);
        public const int MaxEvents = 1000;

        private readonly List<LiveSegment> _segments = new List<LiveSegment>();
        private readonly List<LiveEvent> _events = new List<LiveEvent>();
        private bool _breakPending;

        public IReadOnlyList<LiveSegment> Segments => _segments;
        public IReadOnlyList<LiveEvent> Events => _events;

        /// <summary>Changes when a segment or event is added or removed (the plots then rebuild their lines and markers).</summary>
        public int Version { get; private set; }

        /// <summary>Time of the newest sample, or null when there is none.</summary>
        public DateTime? Newest { get; private set; }

        /// <summary>Time of the oldest sample kept, or null when there is none.</summary>
        public DateTime? Oldest => _segments.Count > 0 && _segments[0].Count > 0
            ? DateTime.FromOADate(_segments[0].Times[0])
            : (DateTime?)null;

        /// <summary>Adds one sample. A new segment starts if more than <paramref name="maxGap"/> passed since the last one.</summary>
        public void Add(DateTime timestamp, double voltage, double current, TimeSpan maxGap)
        {
            var last = _segments.Count > 0 ? _segments[_segments.Count - 1] : null;
            // Also after a clock step backwards: each segment must stay sorted by time.
            if (last == null || _breakPending
                || (Newest.HasValue && (timestamp - Newest.Value > maxGap || timestamp < Newest.Value)))
            {
                last = new LiveSegment();
                _segments.Add(last);
                _breakPending = false;
                Version++;
            }

            last.Times.Add(timestamp.ToOADate());
            last.Voltages.Add(voltage);
            last.Currents.Add(current);
            Newest = timestamp;
            Prune(timestamp - MaxRetention);
        }

        /// <summary>The next sample starts a new segment (the link was lost or closed).</summary>
        public void Break()
        {
            _breakPending = true;
        }

        public void AddEvent(LiveEvent liveEvent)
        {
            if (liveEvent == null)
                return;
            _events.Add(liveEvent);
            if (_events.Count > MaxEvents)
                _events.RemoveRange(0, _events.Count - MaxEvents);
            Version++;
        }

        private void Prune(DateTime cutoff)
        {
            double oldest = cutoff.ToOADate();
            while (_segments.Count > 0)
            {
                var first = _segments[0];
                int remove = LowerBound(first.Times, oldest);
                if (remove == 0)
                    break;
                if (remove >= first.Count)
                {
                    _segments.RemoveAt(0);
                    Version++;
                    continue;
                }
                first.Times.RemoveRange(0, remove);
                first.Voltages.RemoveRange(0, remove);
                first.Currents.RemoveRange(0, remove);
                break;
            }

            int expired = 0;
            while (expired < _events.Count && _events[expired].X < oldest)
                expired++;
            if (expired > 0)
            {
                _events.RemoveRange(0, expired);
                Version++;
            }
        }

        /// <summary>Index of the first element at or after <paramref name="value"/> (the list is sorted).</summary>
        public static int LowerBound(List<double> sorted, double value)
        {
            int lo = 0;
            int hi = sorted.Count;
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) / 2);
                if (sorted[mid] < value)
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
