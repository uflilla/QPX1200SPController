using System.Windows.Input;
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
        private double _ovpLevel;
        private double _ocpLevel;
        // Removed boolean flags as OVP and OCP are now treated as numeric values (1-65)

        public double MinVoltage
        {
            get => _minVoltage;
            set => SetProperty(ref _minVoltage, value);
        }

        public double MaxVoltage
        {
            get => _maxVoltage;
            set => SetProperty(ref _maxVoltage, value);
        }

        public double MinCurrent
        {
            get => _minCurrent;
            set => SetProperty(ref _minCurrent, value);
        }

        public double MaxCurrent
        {
            get => _maxCurrent;
            set => SetProperty(ref _maxCurrent, value);
        }

        public double OverVoltageProtectionLevel
        {
            get => _ovpLevel;
            set => SetProperty(ref _ovpLevel, value);
        }

        public double OverCurrentProtectionLevel
        {
            get => _ocpLevel;
            set => SetProperty(ref _ocpLevel, value);
        }

        // Removed boolean properties for OVP and OCP as they are now treated as numeric values (1-65)
        // Values > 0 indicate protection is active, with the value being the threshold

        public ICommand SaveCommand { get; }

        public SettingsViewModel()
        {
            // Load settings from file
            _settings = Settings.Load();
            
            // Initialize properties from settings
            MinVoltage = _settings.MinVoltage;
            MaxVoltage = _settings.MaxVoltage;
            MinCurrent = _settings.MinCurrent;
            MaxCurrent = _settings.MaxCurrent;
            OverVoltageProtectionLevel = _settings.OverVoltageProtectionLevel;
            OverCurrentProtectionLevel = _settings.OverCurrentProtectionLevel;

            // Create save command
            SaveCommand = new RelayCommand(_ => SaveSettings());
        }

        private void SaveSettings()
        {
            // Update settings object with current values
            _settings.MinVoltage = MinVoltage;
            _settings.MaxVoltage = MaxVoltage;
            _settings.MinCurrent = MinCurrent;
            _settings.MaxCurrent = MaxCurrent;
            _settings.OverVoltageProtectionLevel = OverVoltageProtectionLevel;
            _settings.OverCurrentProtectionLevel = OverCurrentProtectionLevel;
            // Removed references to boolean properties as OVP and OCP are now treated as numeric values

            // Save to file
            _settings.Save();
        }
    }
}