using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Input;
using QPX1200SPController.Models;
using QPX1200SPController.Services;

namespace QPX1200SPController.ViewModels
{
    /*public class LogViewModel : ViewModelBase
    {
        private readonly DatabaseService _databaseService;
        private DateTime _startDate = DateTime.Today.AddDays(-7);
        private DateTime _endDate = DateTime.Now;
        private ObservableCollection<LogEntry> _logs;
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

        public ObservableCollection<LogEntry> Logs
        {
            get => _logs;
            set => SetProperty(ref _logs, value);
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

        public ICommand LoadLogsCommand { get; }
        public ICommand ExportToCsvCommand { get; }

        public LogViewModel(DatabaseService databaseService)
        {
            _databaseService = databaseService;
            Logs = new ObservableCollection<LogEntry>();

            LoadLogsCommand = new RelayCommand(async _ => await LoadLogs());
            ExportToCsvCommand = new RelayCommand(async _ => await ExportToCsv(), _ => Logs.Count > 0);

            // Load logs on startup
            Task.Run(async () => await LoadLogs());
        }

        private async Task LoadLogs()
        {
            try
            {
                IsLoading = true;
                StatusMessage = "Loading logs...";

                var logs = await _databaseService.GetLogsAsync(StartDate, EndDate);
                
                Logs.Clear();
                foreach (var log in logs)
                {
                    Logs.Add(log);
                }

                StatusMessage = $"Loaded {Logs.Count} log entries";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error loading logs: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task ExportToCsv()
        {
            try
            {
                var saveFileDialog = new SaveFileDialog
                {
                    Filter = "CSV files (*.csv)|*.csv",
                    DefaultExt = "csv",
                    FileName = $"PowerSupplyLogs_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
                };

                if (saveFileDialog.ShowDialog() == DialogResult.OK)
                {
                    StatusMessage = "Exporting to CSV...";
                    bool success = await _databaseService.ExportToCsvAsync(new System.Collections.Generic.List<LogEntry>(Logs), saveFileDialog.FileName);
                    
                    if (success)
                    {
                        StatusMessage = $"Successfully exported to {saveFileDialog.FileName}";
                    }
                    else
                    {
                        StatusMessage = "Failed to export data";
                    }
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error exporting to CSV: {ex.Message}";
            }
        }
    }*/
}