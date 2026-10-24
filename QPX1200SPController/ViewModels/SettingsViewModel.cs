using System.Windows.Input;
using GalaSoft.MvvmLight.Ioc;
using QPX1200SPController.Models;

namespace QPX1200SPController.ViewModels
{
    public class SettingsViewModel : ViewModelBase
    {
        private Settings _settings;
        private double _minVoltage;
        private double _maxVoltage;
        private double _minCurrent;
        private double _maxCurrent;
        
        // Removed boolean flags as OVP and OCP are now treated as numeric values (1-65)

        public double MinVoltage
        {
            get => _minVoltage;
            set
            {
                SetProperty(ref _minVoltage, value); 
                RefreshComputedProperties();
            }
        }

        public double MaxVoltage
        {
            get => _maxVoltage;
            set { SetProperty(ref _maxVoltage, value); RefreshComputedProperties();}
        }

        public double MinCurrent
        {
            get => _minCurrent;
            set { SetProperty(ref _minCurrent, value); RefreshComputedProperties();}
        }

        public double MaxCurrent
        {
            get => _maxCurrent;
            set { SetProperty(ref _maxCurrent, value); RefreshComputedProperties();}
        }

        public double MinOvpLevel => _settings.MinOvpLevel;
        public double MaxOvpLevel => _settings.MaxOvpLevel;

        public double MinOcpLevel => _settings.MinOcpLevel;
        public double MaxOcpLevel => _settings.MaxOcpLevel;


        public ICommand SaveCommand { get; }

        public SettingsViewModel()
        {
            _settings = Settings.Current;
            
            // Initialize properties from settings
            MinVoltage = _settings.MinVoltage;
            MaxVoltage = _settings.MaxVoltage;
            MinCurrent = _settings.MinCurrent;
            MaxCurrent = _settings.MaxCurrent;
            RefreshComputedProperties();
            SimpleIoc.Default.GetInstance<MainViewModel>().SettingsUpdated(_settings);
            // Create save command
            SaveCommand = new RelayCommand(_ => SaveSettings());
        }

        private void RefreshComputedProperties()
        {
            RaisePropertyChanged(nameof(MinOvpLevel));
            RaisePropertyChanged(nameof(MaxOvpLevel));
            RaisePropertyChanged(nameof(MinOcpLevel));
            RaisePropertyChanged(nameof(MaxOcpLevel));
        }

        private void SaveSettings()
        {
            // Update settings object with current values
            _settings.MinVoltage = MinVoltage;
            _settings.MaxVoltage = MaxVoltage;
            _settings.MinCurrent = MinCurrent;
            _settings.MaxCurrent = MaxCurrent;
            RefreshComputedProperties();
            // Removed references to boolean properties as OVP and OCP are now treated as numeric values

            // Save to file
            _settings.Save();
            SimpleIoc.Default.GetInstance<MainViewModel>().SettingsUpdated(_settings);
        }
    }
}