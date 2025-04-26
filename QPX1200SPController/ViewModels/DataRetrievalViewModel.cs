using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
using QPX1200SPController.Models;
using QPX1200SPController.Services;

namespace QPX1200SPController.ViewModels
{
    public class DataRetrievalViewModel : ViewModelBase
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

        public ICommand LoadDataCommand { get; }
        public ICommand ExportToCsvCommand { get; }

        public DataRetrievalViewModel(DatabaseService databaseService)
        {
            _databaseService = databaseService;
            Logs = new ObservableCollection<LogEntry>();

            LoadDataCommand = new RelayCommand(async _ => await LoadData());
            ExportToCsvCommand = new RelayCommand(async _ => await ExportToCsv(), _ => Logs.Count > 0);
        }

        private async Task LoadData()
        {
            try
            {
                IsLoading = true;
                StatusMessage = "Loading data...";

                var logs = await _databaseService.GetLogsAsync(StartDate, EndDate);
                
                Logs.Clear();
                foreach (var log in logs)
                {
                    Logs.Add(log);
                }

                StatusMessage = $"Loaded {Logs.Count} data entries";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error loading data: {ex.Message}";
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
                var saveFileDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "CSV files (*.csv)|*.csv",
                    DefaultExt = "csv",
                    FileName = $"PowerSupplyData_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
                };

                if (saveFileDialog.ShowDialog() == true)
                {
                    StatusMessage = "Exporting data to CSV...";
                    
                    await Task.Run(() =>
                    {
                        using (var writer = new StreamWriter(saveFileDialog.FileName))
                        using (var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture)))
                        {
                            csv.WriteRecords(Logs);
                        }
                    });
                    
                    StatusMessage = $"Data exported to {saveFileDialog.FileName}";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error exporting data: {ex.Message}";
            }
        }
    }
}