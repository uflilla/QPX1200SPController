using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace QPX1200SPController.Services
{
    public class AppLogEntry
    {
        public DateTime Timestamp { get; set; }
        public string Level { get; set; } // Info, Error, etc.
        public string Message { get; set; }
        public string ClassName { get; set; }
        public string MethodName { get; set; }
    }

    public class LoggingService
    {
        private static readonly Lazy<LoggingService> _instance = new Lazy<LoggingService>(() => new LoggingService());
        public static LoggingService Instance => _instance.Value;

        private readonly ConcurrentQueue<AppLogEntry> _logs = new ConcurrentQueue<AppLogEntry>();
        public event Action<AppLogEntry> LogAdded;

        private LoggingService() { }

        public void LogInfo(string message, string className = "", string methodName = "")
        {
            AddLog("Info", message, className, methodName);
        }

        public void LogError(string message, string className = "", string methodName = "")
        {
            AddLog("Error", message, className, methodName);
        }

        public void LogException(Exception ex, string className = "", string methodName = "")
        {
            AddLog("Error", ex.Message + (ex.StackTrace != null ? "\n" + ex.StackTrace : ""), className, methodName);
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
            LogAdded?.Invoke(entry);
        }

        public IEnumerable<AppLogEntry> GetLogs()
        {
            return _logs.ToArray();
        }
    }
}