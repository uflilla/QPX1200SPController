using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace QPX1200SPController.Services
{
    public class AppLogEntry
    {
        public DateTime Timestamp { get; set; }
        public string Level { get; set; } // Info, Warning, Error
        public string Message { get; set; }
        public string ClassName { get; set; }
        public string MethodName { get; set; }
    }

    /// <summary>
    /// Application log: keeps the most recent entries in memory for the App Logs tab and
    /// appends every entry to a daily file in Logs\ next to the executable.
    /// </summary>
    public class LoggingService
    {
        private const int MaxInMemoryEntries = 2000;
        private const int RetentionDays = 30;

        private static readonly Lazy<LoggingService> _instance = new Lazy<LoggingService>(() => new LoggingService());
        public static LoggingService Instance => _instance.Value;

        public static string LogDirectory { get; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");

        private readonly ConcurrentQueue<AppLogEntry> _logs = new ConcurrentQueue<AppLogEntry>();
        private readonly object _fileLock = new object();
        private bool _fileLoggingFailed;

        public event Action<AppLogEntry> LogAdded;

        private LoggingService()
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                DeleteOldLogFiles();
            }
            catch
            {
                _fileLoggingFailed = true;
            }
        }

        public void LogInfo(string message, string className = "", string methodName = "")
        {
            AddLog("Info", message, className, methodName);
        }

        public void LogWarning(string message, string className = "", string methodName = "")
        {
            AddLog("Warning", message, className, methodName);
        }

        public void LogError(string message, string className = "", string methodName = "")
        {
            AddLog("Error", message, className, methodName);
        }

        public void LogException(Exception ex, string className = "", string methodName = "")
        {
            AddLog("Error", ex.GetType().Name + ": " + ex.Message + (ex.StackTrace != null ? "\n" + ex.StackTrace : ""), className, methodName);
        }

        private void AddLog(string level, string message, string className, string methodName)
        {
            var entry = new AppLogEntry
            {
                Timestamp = DateTime.Now,
                Level = level,
                Message = message,
                ClassName = className,
                MethodName = methodName
            };

            _logs.Enqueue(entry);
            while (_logs.Count > MaxInMemoryEntries && _logs.TryDequeue(out _))
            {
            }

            WriteToFile(entry);
            LogAdded?.Invoke(entry);
        }

        private void WriteToFile(AppLogEntry entry)
        {
            if (_fileLoggingFailed)
                return;

            var line = new StringBuilder()
                .Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append(" [").Append(entry.Level).Append("] ")
                .Append(entry.ClassName);
            if (!string.IsNullOrEmpty(entry.MethodName))
                line.Append('.').Append(entry.MethodName);
            line.Append(": ").Append(entry.Message).AppendLine();

            try
            {
                lock (_fileLock)
                {
                    string path = Path.Combine(LogDirectory,
                        $"app_{entry.Timestamp.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");
                    File.AppendAllText(path, line.ToString(), Encoding.UTF8);
                }
            }
            catch
            {
                // Never let logging take the application down; keep in-memory logging only.
                _fileLoggingFailed = true;
            }
        }

        private static void DeleteOldLogFiles()
        {
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in Directory.GetFiles(LogDirectory, "app_*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
        }

        public IEnumerable<AppLogEntry> GetLogs()
        {
            return _logs.ToArray();
        }
    }
}
