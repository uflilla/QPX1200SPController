using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using QPX1200SPController.Services;

namespace QPX1200SPController.ViewModels
{
    public class AppLogsViewModel : ViewModelBase
    {
        private const int MaxVisibleEntries = 1000;

        private readonly DispatcherTimer _traceTimer;
        private ObservableCollection<AppLogEntry> _logs;
        private TraceDurationOption _selectedTraceDuration;
        private string _traceStatusText;

        public ObservableCollection<AppLogEntry> Logs
        {
            get => _logs;
            set => SetProperty(ref _logs, value);
        }

        public IReadOnlyList<TraceDurationOption> TraceDurations { get; } = new[]
        {
            new TraceDurationOption("15 min", TimeSpan.FromMinutes(15)),
            new TraceDurationOption("1 hour", TimeSpan.FromHours(1)),
            new TraceDurationOption("4 hours", TimeSpan.FromHours(4)),
            new TraceDurationOption("12 hours", TimeSpan.FromHours(12))
        };

        public TraceDurationOption SelectedTraceDuration
        {
            get => _selectedTraceDuration;
            set => SetProperty(ref _selectedTraceDuration, value);
        }

        /// <summary>Bound to the trace toggle; switching on starts a new time-limited capture.</summary>
        public bool IsTraceEnabled
        {
            get => TraceService.Instance.IsEnabled;
            set
            {
                if (value)
                    TraceService.Instance.Enable((SelectedTraceDuration ?? TraceDurations[0]).Duration);
                else
                    TraceService.Instance.Disable();
                RefreshTraceState();
            }
        }

        public string TraceStatusText
        {
            get => _traceStatusText;
            private set => SetProperty(ref _traceStatusText, value);
        }

        public ICommand OpenLogFolderCommand { get; }

        public AppLogsViewModel()
        {
            Logs = new ObservableCollection<AppLogEntry>(LoggingService.Instance.GetLogs());
            while (Logs.Count > MaxVisibleEntries)
                Logs.RemoveAt(0);
            LoggingService.Instance.LogAdded += OnLogAdded;

            _selectedTraceDuration = TraceDurations[0];
            OpenLogFolderCommand = new RelayCommand(_ => OpenLogFolder());

            TraceService.Instance.StateChanged += OnTraceStateChanged;
            _traceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _traceTimer.Tick += (s, e) =>
            {
                TraceService.Instance.CheckExpiry();
                RefreshTraceState();
            };
            _traceTimer.Start();
            RefreshTraceState();
        }

        private void OnLogAdded(AppLogEntry entry)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
                return;

            // BeginInvoke (not Invoke) so a background logger never blocks on the UI thread.
            dispatcher.BeginInvoke(new Action(() =>
            {
                Logs.Add(entry);
                if (Logs.Count > MaxVisibleEntries)
                    Logs.RemoveAt(0);
            }));
        }

        private void OnTraceStateChanged()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
                return;
            dispatcher.BeginInvoke(new Action(RefreshTraceState));
        }

        private void RefreshTraceState()
        {
            var trace = TraceService.Instance;
            TraceStatusText = trace.IsEnabled
                ? $"Tracing all traffic until {trace.EnabledUntil:HH:mm} → {Path.GetFileName(trace.CurrentFile)}"
                : "Trace off. Only failures are logged.";
            RaisePropertyChanged(nameof(IsTraceEnabled));
        }

        private static void OpenLogFolder()
        {
            try
            {
                Directory.CreateDirectory(LoggingService.LogDirectory);
                Process.Start("explorer.exe", "\"" + LoggingService.LogDirectory + "\"");
            }
            catch (Exception ex)
            {
                LoggingService.Instance.LogException(ex, nameof(AppLogsViewModel), nameof(OpenLogFolder));
            }
        }
    }

    public sealed class TraceDurationOption
    {
        public TraceDurationOption(string displayName, TimeSpan duration)
        {
            DisplayName = displayName;
            Duration = duration;
        }

        public string DisplayName { get; }
        public TimeSpan Duration { get; }

        public override string ToString() => DisplayName;
    }
}
