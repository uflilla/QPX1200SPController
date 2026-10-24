using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CsvHelper;
using CsvHelper.Configuration;
using QPX1200SPController.Converters;
using QPX1200SPController.Models;
using QPX1200SPController.Services;

namespace QPX1200SPController.ViewModels
{
    public class DataRetrievalViewModel : ViewModelBase
    {
        public const int MeasurementsTab = 0;
        public const int ActivitiesTab = 1;

        private readonly DatabaseService _databaseService;
        private readonly CsvConfiguration _csvConfiguration = new CsvConfiguration(CultureInfo.InvariantCulture);
        private DateTime _startDate = DateTime.Today.AddDays(-7);
        private DateTime _endDate = DateTime.Now;
        private ObservableCollection<LogEntry> _logs = new ObservableCollection<LogEntry>();
        private ObservableCollection<ActivityRecord> _activities = new ObservableCollection<ActivityRecord>();
        private bool _isEndNow = true;
        private int _selectedTabIndex;
        private bool _isLoading;
        private string _statusMessage;

        public DateTime StartDate
        {
            get => _startDate;
            set => SetProperty(ref _startDate, value);
        }

        public DateTime EndDate
        {
            get => _endDate;
            set => SetProperty(ref _endDate, value);
        }

        /// <summary>When set, the end of the range is the moment LOAD DATA is pressed.</summary>
        public bool IsEndNow
        {
            get => _isEndNow;
            set => SetProperty(ref _isEndNow, value);
        }

        public ObservableCollection<LogEntry> Logs
        {
            get => _logs;
            private set => SetProperty(ref _logs, value);
        }

        public ObservableCollection<ActivityRecord> Activities
        {
            get => _activities;
            private set => SetProperty(ref _activities, value);
        }

        /// <summary>0 = measurements, 1 = commands &amp; events. Export uses the visible table.</summary>
        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set => SetProperty(ref _selectedTabIndex, value);
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public ICommand LoadDataCommand { get; }
        public ICommand ExportToCsvCommand { get; }

        public DataRetrievalViewModel(DatabaseService databaseService)
        {
            _databaseService = databaseService;

            LoadDataCommand = new RelayCommand(async _ => await LoadData(), _ => IsEndNow || EndDate > StartDate);
            ExportToCsvCommand = new RelayCommand(async _ => await ExportToCsv(),
                _ => SelectedTabIndex == ActivitiesTab ? Activities.Count > 0 : Logs.Count > 0);
        }

        private async Task LoadData()
        {
            try
            {
                IsLoading = true;
                StatusMessage = "Loading data...";
                if (IsEndNow)
                    EndDate = DateTime.Now;

                // Make sure the most recent samples and commands are on disk first.
                await _databaseService.FlushAsync(force: false);

                var countTask = _databaseService.CountLogsAsync(StartDate, EndDate);
                var logsTask = _databaseService.GetLogsAsync(StartDate, EndDate);
                var activitiesTask = _databaseService.GetActivitiesAsync(StartDate, EndDate);
                long total = await countTask;
                var logs = await logsTask;
                var activities = await activitiesTask;

                // Replace the collections in one step: adding rows one by one froze the UI on large ranges.
                Logs = new ObservableCollection<LogEntry>(logs);
                Activities = new ObservableCollection<ActivityRecord>(activities);

                int commands = activities.Count(a => a.Kind == ActivityRecord.KindCommand);
                int errors = activities.Count(a => a.Kind == ActivityRecord.KindCommError);
                string measurements = total > logs.Count
                    ? $"{logs.Count:N0} of {total:N0} measurements (narrow the range to see the rest)"
                    : $"{logs.Count:N0} measurements";
                StatusMessage = $"Loaded {measurements}, {commands:N0} commands, {errors:N0} communication errors, " +
                                $"{activities.Count - commands - errors:N0} connection events.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error loading data: {ex.Message}";
                LoggingService.Instance.LogException(ex, nameof(DataRetrievalViewModel), nameof(LoadData));
            }
            finally
            {
                IsLoading = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private async Task ExportToCsv()
        {
            try
            {
                bool activitiesTab = SelectedTabIndex == ActivitiesTab;
                var saveFileDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "CSV files (*.csv)|*.csv",
                    DefaultExt = "csv",
                    FileName = (activitiesTab ? "PowerSupplyCommands_" : "PowerSupplyData_") +
                               $"{DateTime.Now:yyyyMMdd_HHmmss}.csv"
                };

                if (saveFileDialog.ShowDialog() != true)
                    return;

                StatusMessage = "Exporting data to CSV...";
                var logs = Logs.ToList();
                var activities = Activities.ToList();
                string path = saveFileDialog.FileName;

                await Task.Run(() =>
                {
                    using (var writer = new StreamWriter(path))
                    using (var csv = new CsvWriter(writer, _csvConfiguration))
                    {
                        csv.Context.TypeConverterCache.AddConverter<bool>(new BoolToZeroOneConverter());
                        csv.Context.TypeConverterOptionsCache.GetOptions<DateTime>().Formats = new[] { DatabaseService.TimestampFormat };

                        if (activitiesTab)
                        {
                            csv.WriteRecords(activities.Select(a => new
                            {
                                a.Timestamp,
                                a.Kind,
                                a.Source,
                                a.Command,
                                a.Result,
                                a.Requested,
                                a.Readback,
                                a.Response,
                                a.Detail,
                                a.DurationMs
                            }));
                        }
                        else
                        {
                            csv.WriteRecords(logs);
                        }
                    }
                });

                StatusMessage = $"Data exported to {path}";
            }
            catch (Exception ex)
            {
                StatusMessage = "Error exporting data: see app logs for details";
                LoggingService.Instance.LogException(ex, nameof(DataRetrievalViewModel), nameof(ExportToCsv));
            }
        }
    }
}
