using System;

namespace QPX1200SPController.Models
{
    /// <summary>
    /// One row of the CommandLog table: a configuration command sent to the supply,
    /// a connection event, or a communication error.
    /// </summary>
    public sealed class ActivityRecord
    {
        public const string KindCommand = "Command";
        public const string KindConnection = "Connection";
        public const string KindCommError = "CommError";
        public const string KindQuery = "Query";
        public const string KindAlarm = "Alarm";

        public const string ResultOk = "OK";
        public const string ResultFailed = "Failed";
        public const string ResultMismatch = "Mismatch";
        public const string ResultRaised = "Raised";
        public const string ResultCleared = "Cleared";

        // Configuration commands are held back briefly so the read-back can be added before the row is written.
        private static readonly TimeSpan MaxCompletionWait = TimeSpan.FromSeconds(5);

        private readonly object _sync = new object();
        private readonly DateTime _createdUtc = DateTime.UtcNow;
        private bool _isFinal;
        private string _result;
        private double? _readback;
        private string _detail;

        public ActivityRecord(string kind, string command = null)
        {
            Timestamp = DateTime.Now;
            Kind = kind;
            Command = command;
            _isFinal = kind != KindCommand;
        }

        public long Id { get; set; }
        public DateTime Timestamp { get; set; }
        public string Kind { get; set; }
        public string Source { get; set; }
        public string Command { get; set; }
        public string Response { get; set; }
        public double? Requested { get; set; }
        public double? DurationMs { get; set; }

        public string Result
        {
            get { lock (_sync) return _result; }
            set { lock (_sync) _result = value; }
        }

        public double? Readback
        {
            get { lock (_sync) return _readback; }
            set { lock (_sync) _readback = value; }
        }

        public string Detail
        {
            get { lock (_sync) return _detail; }
            set { lock (_sync) _detail = value; }
        }

        public bool Success => Result == ResultOk || Result == ResultMismatch;

        /// <summary>True once the record may be written (completed, or waited long enough).</summary>
        public bool IsReadyToWrite
        {
            get
            {
                lock (_sync)
                    return _isFinal || DateTime.UtcNow - _createdUtc > MaxCompletionWait;
            }
        }

        /// <summary>Adds the verification result and releases the record for writing.</summary>
        public void Complete(string result = null, double? readback = null, string detail = null)
        {
            lock (_sync)
            {
                if (result != null)
                    _result = result;
                if (readback.HasValue)
                    _readback = readback;
                if (detail != null)
                    _detail = detail;
                _isFinal = true;
            }
        }

        /// <summary>A multi-line reply (one line per query) as "a / b".</summary>
        private static string OneLine(string text)
        {
            return string.Join(" / ", (text ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>Text shown in the data views and on plot markers.</summary>
        public string Summary
        {
            get
            {
                switch (Kind)
                {
                    case KindCommand:
                        return Command + (Result == ResultOk ? "" : " (" + Result + ")");
                    case KindAlarm:
                        return Command + " " + (Result ?? "").ToLowerInvariant() + (string.IsNullOrEmpty(Detail) ? "" : ": " + Detail);
                    case KindQuery:
                        return Command + (Result == ResultOk ? " -> " + OneLine(Response) : " (" + Result + ")");
                    case KindConnection:
                        return Result + (string.IsNullOrEmpty(Detail) ? "" : ": " + Detail);
                    default:
                        return (Command ?? "") + " " + (Detail ?? "");
                }
            }
        }
    }
}
