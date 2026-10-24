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
        public ICommand FirstPageCommand { get; }
        public ICommand PreviousPageCommand { get; }
        public ICommand NextPageCommand { get; }
        public ICommand LastPageCommand { get; }

        public const int PageSize = 1000;

        private DateTime _loadedFrom;
        private DateTime _loadedTo;
        private long _totalCount;
        private long _currentPage;

        /// <summary>Measurements in the loaded range (all pages).</summary>
        public long TotalCount
        {
            get => _totalCount;
            private set { if (SetProperty(ref _totalCount, value)) RaisePagingChanged(); }
        }

        /// <summary>1-based page shown in the measurements table.</summary>
        public long CurrentPage
        {
            get => _currentPage;
            private set { if (SetProperty(ref _currentPage, value)) RaisePagingChanged(); }
        }

        public long PageCount => TotalCount == 0 ? 0 : (TotalCount + PageSize - 1) / PageSize;

        public string PageText => TotalCount == 0
            ? "No measurements"
            : $"Page {CurrentPage:N0} of {PageCount:N0}   (rows {(CurrentPage - 1) * PageSize + 1:N0} - {Math.Min(CurrentPage * PageSize, TotalCount):N0} of {TotalCount:N0})";

        public DataRetrievalViewModel(DatabaseService databaseService)
        {
            _databaseService = databaseService;

            LoadDataCommand = new RelayCommand(async _ => await LoadData(), _ => IsEndNow || EndDate > StartDate);
            ExportToCsvCommand = new RelayCommand(async _ => await ExportToCsv(),
                _ => SelectedTabIndex == ActivitiesTab ? Activities.Count > 0 : TotalCount > 0);
            FirstPageCommand = new RelayCommand(async _ => await LoadPageAsync(1), _ => CurrentPage > 1);
            PreviousPageCommand = new RelayCommand(async _ => await LoadPageAsync(CurrentPage - 1), _ => CurrentPage > 1);
            NextPageCommand = new RelayCommand(async _ => await LoadPageAsync(CurrentPage + 1), _ => CurrentPage < PageCount);
            LastPageCommand = new RelayCommand(async _ => await LoadPageAsync(PageCount), _ => CurrentPage < PageCount);
        }

        private void RaisePagingChanged()
        {
            RaisePropertyChanged(nameof(PageCount));
            RaisePropertyChanged(nameof(PageText));
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

                _loadedFrom = StartDate;
                _loadedTo = EndDate;
                var countTask = _databaseService.CountLogsAsync(_loadedFrom, _loadedTo);
                var activitiesTask = _databaseService.GetActivitiesAsync(_loadedFrom, _loadedTo);
                TotalCount = await countTask;
                var activities = await activitiesTask;
                Activities = new ObservableCollection<ActivityRecord>(activities);
                await LoadPageAsync(1);

                int commands = activities.Count(a => a.Kind == ActivityRecord.KindCommand);
                int errors = activities.Count(a => a.Kind == ActivityRecord.KindCommError);
                StatusMessage = $"Loaded {TotalCount:N0} measurements, {commands:N0} commands, {errors:N0} communication errors, " +
                                $"{activities.Count - commands - errors:N0} connection events." +
                                (activities.Count >= DatabaseService.MaxRowsPerQuery ? " (Event list limited to the first 200,000.)" : "");
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

        private async Task LoadPageAsync(long page)
        {
            if (TotalCount == 0)
            {
                Logs = new ObservableCollection<LogEntry>();
                CurrentPage = 0;
                return;
            }

            page = Math.Max(1, Math.Min(page, PageCount));
            var rows = await _databaseService.GetLogsAsync(_loadedFrom, _loadedTo, PageSize, (page - 1) * PageSize);
            // Replace the collection in one step: adding rows one by one froze the UI.
            Logs = new ObservableCollection<LogEntry>(rows);
            CurrentPage = page;
            CommandManager.InvalidateRequerySuggested();
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
                string path = saveFileDialog.FileName;

                if (!activitiesTab)
                {
                    // Every page of the loaded range, streamed straight from the database.
                    long rows = await _databaseService.ExportLogsToCsvAsync(_loadedFrom, _loadedTo, path);
                    StatusMessage = $"Exported {rows:N0} measurements to {path}";
                    return;
                }

                var activities = Activities.ToList();
                await Task.Run(() =>
                {
                    using (var writer = new StreamWriter(path))
                    using (var csv = new CsvWriter(writer, _csvConfiguration))
                    {
                        csv.Context.TypeConverterCache.AddConverter<bool>(new BoolToZeroOneConverter());
                        csv.Context.TypeConverterOptionsCache.GetOptions<DateTime>().Formats = new[] { DatabaseService.TimestampFormat };

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
