using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using QPX1200SPController.Models;
using QPX1200SPController.Services;

namespace QPX1200SPController.ViewModels
{
    /// <summary>
    /// History tab: plots any time range from the database with command/connection/error markers.
    /// The view reports zoom/pan through <see cref="OnVisibleRangeChangedAsync"/>; the visible window
    /// is then reloaded at full detail and the statistics are recalculated for it.
    /// </summary>
    public class HistoryViewModel : ViewModelBase
    {
        private const int MaxMarkers = 5000;

        private readonly DatabaseService _databaseService;
        private DateTime _from = DateTime.Now.AddHours(-1);
        private DateTime _to = DateTime.Now;
        private bool _isLoading;
        private string _statusText = "Choose a range and press LOAD.";
        private string _cursorText = "Move the mouse over a plot to read values.";
        private bool _showCommands = true;
        private bool _showConnection = true;
        private bool _showErrors = true;
        private ActivityRecord _selectedEvent;
        private int _loadVersion;
        private IReadOnlyList<ActivityRecord> _markers = Array.Empty<ActivityRecord>();

        public HistoryViewModel(DatabaseService databaseService)
        {
            _databaseService = databaseService;
            LoadCommand = new RelayCommand(async _ => await LoadAsync(From, To, resetAxes: true), _ => To > From);
            PresetCommand = new RelayCommand(async p => await ApplyPresetAsync(p as string));
            ResetZoomCommand = new RelayCommand(async _ => await LoadAsync(From, To, resetAxes: true), _ => To > From);
            ExportPngCommand = new RelayCommand(_ => ExportPngRequested?.Invoke());
            ExportCsvCommand = new RelayCommand(async _ => await ExportCsvAsync(), _ => VisibleTo > VisibleFrom);
            _autoRefreshTimer = new DispatcherTimer { Interval = AutoRefreshInterval };
            _autoRefreshTimer.Tick += async (s, e) => await AutoRefreshAsync();
        }

        /// <summary>New data for the plots. resetAxes = true for a new range, false after a zoom/pan reload.</summary>
        public event Action<HistorySeries, IReadOnlyList<ActivityRecord>, bool> DataLoaded;

        /// <summary>Marker filters changed; the view redraws the markers it already has.</summary>
        public event Action MarkersChanged;

        /// <summary>The view should move the visible window so this time is in the middle.</summary>
        public event Action<DateTime> CenterOnRequested;

        public event Action ExportPngRequested;

        public IReadOnlyList<string> Presets { get; } = new[] { "15 min", "1 hour", "6 hours", "24 hours", "7 days", "30 days", "All" };

        public DateTime From
        {
            get => _from;
            set => SetProperty(ref _from, value);
        }

        public DateTime To
        {
            get => _to;
            set => SetProperty(ref _to, value);
        }

        /// <summary>Window currently shown in the plots (after zoom/pan).</summary>
        public DateTime VisibleFrom { get; private set; }
        public DateTime VisibleTo { get; private set; }

        public bool IsLoading
        {
            get => _isLoading;
            private set => SetProperty(ref _isLoading, value);
        }

        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        /// <summary>Values under the mouse cursor, written by the plot presenter.</summary>
        public string CursorText
        {
            get => _cursorText;
            set => SetProperty(ref _cursorText, value);
        }

        /// <summary>Shown above the event list when some markers are not drawn (null when all are).</summary>
        public string MarkerNote
        {
            get => _markerNote;
            set => SetProperty(ref _markerNote, value);
        }

        private string _markerNote;

        public ObservableCollection<StatRow> Stats { get; } = new ObservableCollection<StatRow>();

        /// <summary>Commands, connection events and errors in the visible window.</summary>
        public ObservableCollection<ActivityRecord> Events { get; } = new ObservableCollection<ActivityRecord>();

        public ActivityRecord SelectedEvent
        {
            get => _selectedEvent;
            set
            {
                if (SetProperty(ref _selectedEvent, value) && value != null && !_suppressCenter)
                    CenterOnRequested?.Invoke(value.Timestamp);
            }
        }

        private bool _suppressCenter;

        /// <summary>Selects an event without moving the plots (used when a marker is clicked).</summary>
        public void SelectEventFromPlot(ActivityRecord record)
        {
            _suppressCenter = true;
            try
            {
                SelectedEvent = Events.FirstOrDefault(e => e.Id == record.Id) ?? record;
            }
            finally
            {
                _suppressCenter = false;
            }
        }

        public bool ShowCommands
        {
            get => _showCommands;
            set { if (SetProperty(ref _showCommands, value)) MarkersChanged?.Invoke(); }
        }

        public bool ShowConnection
        {
            get => _showConnection;
            set { if (SetProperty(ref _showConnection, value)) MarkersChanged?.Invoke(); }
        }

        public bool ShowErrors
        {
            get => _showErrors;
            set { if (SetProperty(ref _showErrors, value)) MarkersChanged?.Invoke(); }
        }

        public ICommand LoadCommand { get; }
        public ICommand PresetCommand { get; }
        public ICommand ResetZoomCommand { get; }
        public ICommand ExportPngCommand { get; }
        public ICommand ExportCsvCommand { get; }

        public bool IsMarkerVisible(ActivityRecord record)
        {
            if (IsError(record) || record.Kind == ActivityRecord.KindAlarm)
                return ShowErrors;
            return record.Kind == ActivityRecord.KindConnection ? ShowConnection : ShowCommands;
        }

        /// <summary>Errors and raised alarms (red markers; counted under the "Errors" filter).</summary>
        public static bool IsError(ActivityRecord record)
        {
            return record.Kind == ActivityRecord.KindCommError
                   || (record.Kind == ActivityRecord.KindAlarm && record.Result == ActivityRecord.ResultRaised)
                   || record.Result == ActivityRecord.ResultFailed
                   || record.Result == ActivityRecord.ResultMismatch
                   || record.Result == "Lost"
                   || record.Result == "Connect failed";
        }

        public IReadOnlyList<ActivityRecord> Markers => _markers;

        private async Task ApplyPresetAsync(string preset)
        {
            var now = DateTime.Now;
            switch (preset)
            {
                case "15 min": From = now.AddMinutes(-15); break;
                case "1 hour": From = now.AddHours(-1); break;
                case "6 hours": From = now.AddHours(-6); break;
                case "24 hours": From = now.AddDays(-1); break;
                case "7 days": From = now.AddDays(-7); break;
                case "30 days": From = now.AddDays(-30); break;
                case "All":
                    var extent = await _databaseService.GetDataExtentAsync();
                    if (extent == null)
                    {
                        StatusText = "The database has no measurements yet.";
                        return;
                    }
                    // First to last sample, with a little room on both sides.
                    var margin = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(5).Ticks, (extent[1] - extent[0]).Ticks / 100));
                    From = extent[0] - margin;
                    now = extent[1] + margin;
                    break;
                default:
                    return;
            }
            To = now;
            await LoadAsync(From, To, resetAxes: true);
        }

        /// <summary>Called (debounced) by the view after the user zoomed or panned.</summary>
        public Task OnVisibleRangeChangedAsync(DateTime from, DateTime to)
        {
            // The user is looking at a particular moment: stop sliding the window to "now".
            IsAutoRefresh = false;
            return LoadAsync(from, to, resetAxes: false);
        }

        /// <summary>
        /// Every <see cref="AutoRefreshInterval"/> the shown window (same length) is moved to end now,
        /// so the History tab can be left open as a long-term live view.
        /// </summary>
        public bool IsAutoRefresh
        {
            get => _isAutoRefresh;
            set
            {
                if (!SetProperty(ref _isAutoRefresh, value))
                    return;
                if (value)
                {
                    _autoRefreshTimer.Start();
                    _ = AutoRefreshAsync();
                }
                else
                {
                    _autoRefreshTimer.Stop();
                }
            }
        }

        private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(5);
        private bool _isAutoRefresh;
        private readonly DispatcherTimer _autoRefreshTimer;

        private async Task AutoRefreshAsync()
        {
            if (!IsAutoRefresh || IsLoading)
                return;
            var span = VisibleTo > VisibleFrom ? VisibleTo - VisibleFrom : To - From;
            if (span <= TimeSpan.Zero)
                span = TimeSpan.FromHours(1);
            To = DateTime.Now;
            From = To - span;
            await LoadAsync(From, To, resetAxes: true);
        }

        private async Task LoadAsync(DateTime from, DateTime to, bool resetAxes)
        {
            if (to <= from)
                return;

            int version = ++_loadVersion;
            IsLoading = true;
            StatusText = "Loading...";
            try
            {
                // Make sure the latest samples are on disk.
                await _databaseService.FlushAsync(force: false);

                var seriesTask = _databaseService.GetHistorySeriesAsync(from, to);
                var markersTask = _databaseService.GetActivitiesAsync(from, to, MaxMarkers);
                var series = await seriesTask;
                var markers = await markersTask;
                if (version != _loadVersion)
                    return; // a newer request superseded this one

                VisibleFrom = from;
                VisibleTo = to;
                _markers = markers;
                Events.Clear();
                foreach (var m in markers)
                    Events.Add(m);
                DataLoaded?.Invoke(series, markers, resetAxes);

                StatusText = series.SourceRows == 0
                    ? $"No measurements between {from:yyyy-MM-dd HH:mm:ss} and {to:yyyy-MM-dd HH:mm:ss}."
                    : $"{FormatRange(from, to)}: {series.SourceRows:N0} samples" +
                      (series.IsDownsampled ? $" shown as a {series.Xs.Length:N0}-point min/max envelope (zoom in for every sample)" : "") +
                      $", {markers.Count:N0} events" + (markers.Count >= MaxMarkers ? " (first 5,000)" : "") + ".";

                var stats = await _databaseService.GetStatsAsync(from, to);
                if (version != _loadVersion)
                    return;
                ShowStats(stats);
            }
            catch (Exception ex)
            {
                StatusText = "Error loading history: " + ex.Message;
                LoggingService.Instance.LogException(ex, nameof(HistoryViewModel), nameof(LoadAsync));
            }
            finally
            {
                if (version == _loadVersion)
                    IsLoading = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void ShowStats(HistoryStats s)
        {
            Stats.Clear();
            if (s.Samples == 0)
            {
                Stats.Add(new StatRow("Samples", "0"));
                return;
            }

            var inv = CultureInfo.InvariantCulture;
            Stats.Add(new StatRow("Samples", s.Samples.ToString("N0")));
            Stats.Add(new StatRow("First", s.First?.ToString("yyyy-MM-dd HH:mm:ss", inv)));
            Stats.Add(new StatRow("Last", s.Last?.ToString("yyyy-MM-dd HH:mm:ss", inv)));
            Stats.Add(new StatRow("Voltage", $"{s.VoltageMin:F3} / {s.VoltageAvg:F3} / {s.VoltageMax:F3} V", "min / avg / max"));
            Stats.Add(new StatRow("Current", $"{s.CurrentMin:F3} / {s.CurrentAvg:F3} / {s.CurrentMax:F3} A", "min / avg / max"));
            Stats.Add(new StatRow("Power", $"{s.PowerMin:F2} / {s.PowerAvg:F2} / {s.PowerMax:F2} W", "min / avg / max"));
            Stats.Add(new StatRow("Energy", s.EnergyWh >= 1000 ? $"{s.EnergyWh / 1000:F3} kWh" : $"{s.EnergyWh:F3} Wh"));
            Stats.Add(new StatRow("Charge", $"{s.ChargeAh:F3} Ah"));
            Stats.Add(new StatRow("Output ON", FormatDuration(TimeSpan.FromSeconds(s.OutputOnSeconds))));
            Stats.Add(new StatRow("Commands", s.CommandCount.ToString("N0")));
            Stats.Add(new StatRow("Errors", s.ErrorCount.ToString("N0"), "communication errors, failed or mismatched commands, lost links"));
            Stats.Add(new StatRow("Alarms", s.AlarmCount.ToString("N0"), "alarms raised in this window"));
        }

        private async Task ExportCsvAsync()
        {
            var from = VisibleFrom;
            var to = VisibleTo;
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv",
                DefaultExt = "csv",
                FileName = $"PowerSupplyHistory_{from:yyyyMMdd_HHmmss}-{to:yyyyMMdd_HHmmss}.csv"
            };
            if (dialog.ShowDialog() != true)
                return;

            try
            {
                IsLoading = true;
                StatusText = "Exporting...";
                long rows = await _databaseService.ExportLogsToCsvAsync(from, to, dialog.FileName);
                StatusText = $"Exported {rows:N0} samples ({FormatRange(from, to)}) to {dialog.FileName}";
            }
            catch (Exception ex)
            {
                StatusText = "Export failed: " + ex.Message;
                LoggingService.Instance.LogException(ex, nameof(HistoryViewModel), nameof(ExportCsvAsync));
            }
            finally
            {
                IsLoading = false;
            }
        }

        public void ReportPngExported(string path, Exception error)
        {
            StatusText = error == null ? $"Plot image saved to {path}" : "Saving the image failed: " + error.Message;
            if (error != null)
                LoggingService.Instance.LogException(error, nameof(HistoryViewModel), nameof(ReportPngExported));
        }

        private static string FormatRange(DateTime from, DateTime to)
        {
            return $"{from:yyyy-MM-dd HH:mm:ss} - {to:yyyy-MM-dd HH:mm:ss} ({FormatDuration(to - from)})";
        }

        private static string FormatDuration(TimeSpan span)
        {
            if (span.TotalDays >= 1)
                return $"{(int)span.TotalDays} d {span.Hours} h {span.Minutes} min";
            if (span.TotalHours >= 1)
                return $"{(int)span.TotalHours} h {span.Minutes} min";
            if (span.TotalMinutes >= 1)
                return $"{(int)span.TotalMinutes} min {span.Seconds} s";
            return $"{span.TotalSeconds:F1} s";
        }
    }

    public sealed class StatRow
    {
        public StatRow(string label, string value, string hint = null)
        {
            Label = label;
            Value = value;
            Hint = hint;
        }

        public string Label { get; }
        public string Value { get; }
        public string Hint { get; }
    }
}
