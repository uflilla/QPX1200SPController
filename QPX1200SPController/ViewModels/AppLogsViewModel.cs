using System.Collections.ObjectModel;
using QPX1200SPController.Services;
using System;
using System.Linq;

namespace QPX1200SPController.ViewModels
{
    public class AppLogsViewModel : ViewModelBase
    {
        private ObservableCollection<AppLogEntry> _logs;
        public ObservableCollection<AppLogEntry> Logs
        {
            get => _logs;
            set => SetProperty(ref _logs, value);
        }

        public AppLogsViewModel()
        {
            Logs = new ObservableCollection<AppLogEntry>(LoggingService.Instance.GetLogs());
            LoggingService.Instance.LogAdded += OnLogAdded;
        }

        private void OnLogAdded(AppLogEntry entry)
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                Logs.Add(entry);
                // Optionally limit log size
                if (Logs.Count > 1000)
                    Logs.RemoveAt(0);
            });
        }
    }
}