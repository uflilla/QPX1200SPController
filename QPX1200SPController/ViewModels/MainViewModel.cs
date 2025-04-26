using System;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using QPX1200SPController.Models;
using QPX1200SPController.Services;
using System.ComponentModel;
using System.Windows;

namespace QPX1200SPController.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        private readonly PowerSupplyService _powerSupplyService;
        private readonly DatabaseService _databaseService;
        private readonly DispatcherTimer _updateTimer;
        private readonly DispatcherTimer _loggingTimer;

        private PowerSupplyStatus _status;
        private string _ipAddress = "192.168.1.100";
        private double _setVoltage = 12.0;
        private double _setCurrent = 1.0;
        private string _connectionStatus = "Disconnected";
        private bool _isConnected;
        private double _ovpLevel = 65.0;
        private double _ocpLevel = 55.0;
        private bool _isVoltageValid = true;
        private bool _isCurrentValid = true;

        public PowerSupplyStatus Status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        public string IpAddress
        {
            get => _ipAddress;
            set => SetProperty(ref _ipAddress, value);
        }

        public double SetVoltage
        {
            get => _setVoltage;
            set 
            {
                SetProperty(ref _setVoltage, value);
                ValidateVoltage();
            }
        }

        public double SetCurrent
        {
            get => _setCurrent;
            set 
            {
                SetProperty(ref _setCurrent, value);
                ValidateCurrent();
            }
        }
        
        public bool IsVoltageValid
        {
            get => _isVoltageValid;
            private set => SetProperty(ref _isVoltageValid, value);
        }
        
        public bool IsCurrentValid
        {
            get => _isCurrentValid;
            private set => SetProperty(ref _isCurrentValid, value);
        }

        public double OvpLevel
        {
            get => _ovpLevel;
            set => SetProperty(ref _ovpLevel, value);
        }

        public double OcpLevel
        {
            get => _ocpLevel;
            set => SetProperty(ref _ocpLevel, value);
        }

        public string ConnectionStatus
        {
            get => _connectionStatus;
            set => SetProperty(ref _connectionStatus, value);
        }

        public bool IsConnected
        {
            get => _isConnected;
            set => SetProperty(ref _isConnected, value);
        }

        public ICommand ConnectCommand { get; }
        public ICommand DisconnectCommand { get; }
        public ICommand SetVoltageCommand { get; }
        public ICommand SetCurrentCommand { get; }
        public ICommand ToggleOutputCommand { get; }
        public ICommand SetOvpCommand { get; }
        public ICommand SetOcpCommand { get; }

        public MainViewModel(PowerSupplyService powerSupplyService, DatabaseService databaseService)
        {
            _powerSupplyService = powerSupplyService;
            _databaseService = databaseService;
            Status = new PowerSupplyStatus();

            // Create commands
            ConnectCommand = new RelayCommand(async _ => await Connect());
            DisconnectCommand = new RelayCommand(_ => Disconnect(), _ => IsConnected);
            SetVoltageCommand = new RelayCommand(async _ => await SetVoltageAsync(), _ => IsConnected);
            SetCurrentCommand = new RelayCommand(async _ => await SetCurrentAsync(), _ => IsConnected);
            ToggleOutputCommand = new RelayCommand(async _ => await ToggleOutput(), _ => IsConnected);
            SetOvpCommand = new RelayCommand(async _ => await SetOvpAsync(), _ => IsConnected);
            SetOcpCommand = new RelayCommand(async _ => await SetOcpAsync(), _ => IsConnected);

            // Setup timer for periodic updates
            _updateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _updateTimer.Tick += async (s, e) => await UpdateStatus();
            
            // Setup timer for logging data
            _loggingTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10) // Log every 10 seconds
            };
            _loggingTimer.Tick += async (s, e) => await LogData();
        }

        private async Task Connect()
        {
            ConnectionStatus = "Connecting...";
            bool success = await _powerSupplyService.ConnectAsync(IpAddress);

            if (success)
            {
                IsConnected = true;
                ConnectionStatus = "Connected";
                _updateTimer.Start();
                _loggingTimer.Start(); // Start logging data
                await UpdateStatus();
                
                // Query current settings from the power supply instead of sending values
                await UpdateStatus();
                
                // Get current set values from the power supply
                SetVoltage = await _powerSupplyService.GetSetVoltageAsync();
                SetCurrent = await _powerSupplyService.GetSetCurrentAsync();
                
                // Get current protection levels from the device
                OvpLevel = await _powerSupplyService.GetOverVoltageProtectionAsync();
                OcpLevel = await _powerSupplyService.GetOverCurrentProtectionAsync();
                
                // Update settings with current values from device
                var settings = Settings.Load();
                settings.OverVoltageProtectionLevel = OvpLevel;
                settings.OverCurrentProtectionLevel = OcpLevel;
                settings.Save();
            }
            else
            {
                ConnectionStatus = "Connection Failed";
                IsConnected = false;
            }
        }

        private void Disconnect()
        {
            _updateTimer.Stop();
            _loggingTimer.Stop(); // Stop logging data
            _powerSupplyService.Disconnect();
            IsConnected = false;
            ConnectionStatus = "Disconnected";
        }

        private async Task UpdateStatus()
        {
            Status = await _powerSupplyService.GetStatusAsync();
            ConnectionStatus = Status.ConnectionStatus;

            if (Status.ConnectionStatus != "Connected")
            {
                _updateTimer.Stop();
                _loggingTimer.Stop(); // Stop logging if disconnected
                IsConnected = false;
            }
            
            // Update command can execute status for validation
            CommandManager.InvalidateRequerySuggested();
        }
        
        private async Task LogData()
        {
            if (IsConnected && Status != null)
            {
                await _databaseService.LogPowerSupplyStatusAsync(Status);
            }
        }

        private void ValidateVoltage()
        {
            var settings = Settings.Load();
            IsVoltageValid = SetVoltage >= settings.MinVoltage && SetVoltage <= settings.MaxVoltage;
        }
        
        private void ValidateCurrent()
        {
            var settings = Settings.Load();
            IsCurrentValid = SetCurrent >= settings.MinCurrent && SetCurrent <= settings.MaxCurrent;
        }
        
        private async Task SetVoltageAsync()
        {
            // Get settings for validation
            var settings = Settings.Load();
            
            // Validate voltage is within limits
            if (SetVoltage < settings.MinVoltage || SetVoltage > settings.MaxVoltage)
            {
                // Don't proceed if outside limits
                return;
            }
            
            await _powerSupplyService.SetVoltageAsync(SetVoltage);
            await UpdateStatus();
        }

        private async Task SetCurrentAsync()
        {
            // Get settings for validation
            var settings = Settings.Load();
            
            // Validate current is within limits
            if (SetCurrent < settings.MinCurrent || SetCurrent > settings.MaxCurrent)
            {
                // Don't proceed if outside limits
                return;
            }
            
            await _powerSupplyService.SetCurrentAsync(SetCurrent);
            await UpdateStatus();
        }

        private async Task ToggleOutput()
        {
            await _powerSupplyService.SetOutputAsync(!Status.OutputEnabled);
            await UpdateStatus();
        }
        
        private async Task SetOvpAsync()
        {
            await _powerSupplyService.SetOverVoltageProtectionAsync(OvpLevel);
            await UpdateStatus();
            
            // Save to settings
            var settings = Settings.Load();
            settings.OverVoltageProtectionLevel = OvpLevel;
            settings.Save();
        }
        
        private async Task SetOcpAsync()
        {
            await _powerSupplyService.SetOverCurrentProtectionAsync(OcpLevel);
            await UpdateStatus();
            
            // Save to settings
            var settings = Settings.Load();
            settings.OverCurrentProtectionLevel = OcpLevel;
            settings.Save();
        }
    }
}