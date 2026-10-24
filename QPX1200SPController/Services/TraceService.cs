using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace QPX1200SPController.Services
{
    /// <summary>
    /// Optional, time-limited capture of the raw SCPI traffic to Logs\Trace\*.log.
    /// Intended for troubleshooting only; it switches itself off when the chosen duration ends.
    /// </summary>
    public sealed class TraceService
    {
        private const long MaxFileBytes = 10L * 1024 * 1024;

        private static readonly Lazy<TraceService> _instance = new Lazy<TraceService>(() => new TraceService());
        public static TraceService Instance => _instance.Value;

        public static string TraceDirectory { get; } = Path.Combine(LoggingService.LogDirectory, "Trace");

        private readonly object _sync = new object();
        private StreamWriter _writer;
        private DateTime _enabledUntil;

        /// <summary>Raised (on any thread) when tracing is switched on or off.</summary>
        public event Action StateChanged;

        private TraceService() { }

        public bool IsEnabled
        {
            get
            {
                lock (_sync)
                    return _writer != null;
            }
        }

        public DateTime EnabledUntil
        {
            get
            {
                lock (_sync)
                    return _enabledUntil;
            }
        }

        public string CurrentFile { get; private set; }

        public void Enable(TimeSpan duration)
        {
            lock (_sync)
            {
                _enabledUntil = DateTime.Now + duration;
                if (_writer == null && !OpenNewFileUnlocked())
                    return;
            }

            LoggingService.Instance.LogInfo(
                $"Trace mode ON until {_enabledUntil:HH:mm:ss}. File: {CurrentFile}",
                nameof(TraceService), nameof(Enable));
            StateChanged?.Invoke();
        }

        public void Disable()
        {
            bool wasEnabled;
            lock (_sync)
            {
                wasEnabled = _writer != null;
                CloseUnlocked();
            }

            if (!wasEnabled)
                return;

            LoggingService.Instance.LogInfo("Trace mode OFF.", nameof(TraceService), nameof(Disable));
            StateChanged?.Invoke();
        }

        /// <summary>Switches tracing off if its duration has ended (also covers idle periods with no traffic).</summary>
        public void CheckExpiry()
        {
            bool expired;
            lock (_sync)
                expired = _writer != null && DateTime.Now >= _enabledUntil;
            if (expired)
                Disable();
        }

        /// <summary>Writes one trace line. Cheap no-op while tracing is off.</summary>
        public void Write(string direction, string text, TimeSpan? elapsed = null)
        {
            bool expired = false;
            lock (_sync)
            {
                if (_writer == null)
                    return;

                var now = DateTime.Now;
                if (now >= _enabledUntil)
                {
                    expired = true;
                }
                else
                {
                    try
                    {
                        var line = new StringBuilder()
                            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                            .Append(' ').Append(direction).Append(' ')
                            .Append(Escape(text));
                        if (elapsed.HasValue)
                            line.Append("   (").Append(elapsed.Value.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)).Append(" ms)");
                        _writer.WriteLine(line.ToString());

                        if (_writer.BaseStream.Length > MaxFileBytes)
                        {
                            CloseUnlocked();
                            OpenNewFileUnlocked();
                        }
                    }
                    catch
                    {
                        CloseUnlocked();
                        expired = true;
                    }
                }
            }

            if (expired)
                Disable();
        }

        private bool OpenNewFileUnlocked()
        {
            try
            {
                Directory.CreateDirectory(TraceDirectory);
                CurrentFile = Path.Combine(TraceDirectory,
                    $"trace_{DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.log");
                _writer = new StreamWriter(CurrentFile, true, Encoding.UTF8) { AutoFlush = true };
                return true;
            }
            catch (Exception ex)
            {
                _writer = null;
                LoggingService.Instance.LogException(ex, nameof(TraceService), nameof(OpenNewFileUnlocked));
                return false;
            }
        }

        private void CloseUnlocked()
        {
            try
            {
                _writer?.Dispose();
            }
            catch
            {
                // ignored
            }
            _writer = null;
        }

        private static string Escape(string text)
        {
            if (text == null)
                return "<null>";
            return text.Replace("\r", "\\r").Replace("\n", "\\n");
        }
    }
}
