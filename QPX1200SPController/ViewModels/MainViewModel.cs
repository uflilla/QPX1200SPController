using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using QPX1200SPController.Models;
using QPX1200SPController.Services;
using System.ComponentModel;
using System.Windows;
using GalaSoft.MvvmLight.CommandWpf;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.TaskbarClock;
using System.Threading;

namespace QPX1200SPController.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        private readonly PowerSupplyService _powerSupplyService;
        private readonly DatabaseService _databaseService;
        private readonly DispatcherTimer _updateTimer;
        private readonly DispatcherTimer _loggingTimer;
        private readonly ConcurrentQueue<PowerSupplyStatus> _powerSupplyStatusQueue=new ConcurrentQueue<PowerSupplyStatus>();
        private uint skipCounter;
        private PowerSupplyStatus _status;
        private string _ipAddress = "170.170.170.120";
        private double _setVoltage = 12.0;
        private double _setCurrent = 1.0;
        private string _connectionStatus = "Disconnected";
        private bool _isConnected;
        private double _ovpLevel = 65.0;
        private double _ocpLevel = 55.0;
        private bool _isVoltageValid = true;
        private bool _isCurrentValid = true;
        private string _supplyName;
        private double _voltageToSet;
        private double _minVoltageToSet;
        private double _maxVoltageToSet;
        private double _currentToSet;
        private double _maxCurrentToSet;
        private double _minCurrentToSet;
        private double _maxOvpToSet;
        private double _minOvpToSet;
        private double _ovpToSet;
        private double _isOvpValid;
        private double _maxOcpToSet;
        private double _minOcpToSet;
        private double _ocpToSet;
        private double _isOcpValid;
        private EventStatus _eventStatus;
        private PowerSupplySettingStatus _powerSupplySettingStatus;
        private bool _isInConfigMode;

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

        public PowerSupplySettingStatus PowerSupplySettingStatus
        {
            get => _powerSupplySettingStatus;
            set => Set(ref _powerSupplySettingStatus, value);
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
        public ICommand SendMiscCommand { get; }

        public string SupplyName
        {
            get => _supplyName;
            set => Set(ref _supplyName, value);
        }
        public void SettingsUpdated(Settings settings)
        {
            MinVoltageToSet = settings.MinVoltage;
            MaxVoltageToSet = settings.MaxVoltage;
            MinCurrentToSet=settings.MinCurrent;
            MaxCurrentToSet=settings.MaxCurrent;
            MinOvpToSet = settings.MinOvpLevel;
            MaxOvpToSet=settings.MaxOvpLevel;
            MinOcpToSet=settings.MinOcpLevel;
            MaxOcpToSet=settings.MaxOcpLevel;
        }
        public double VoltageToSet
        {
            get => _voltageToSet;
            set
            {
                Set(ref _voltageToSet, value); 
                ValidateVoltage();
            }
        }

        public double MinVoltageToSet
        {
            get => _minVoltageToSet;
            set => Set(ref _minVoltageToSet, value);
        }

        public double MaxVoltageToSet
        {
            get => _maxVoltageToSet;
            set => Set(ref _maxVoltageToSet, value);
        }

        public double MinCurrentToSet
        {
            get => _minCurrentToSet;
            set => Set(ref _minCurrentToSet, value);
        }

        public double MaxCurrentToSet
        {
            get => _maxCurrentToSet;
            set => Set(ref _maxCurrentToSet, value);
        }

        public double CurrentToSet
        {
            get => _currentToSet;
            set { Set(ref _currentToSet, value); ValidateCurrent();}
        }

        public double MaxOvpToSet
        {
            get => _maxOvpToSet;
            set => Set(ref _maxOvpToSet, value);
        }

        public double MinOvpToSet
        {
            get => _minOvpToSet;
            set => Set(ref _minOvpToSet, value);
        }

        public double OvpToSet
        {
            get => _ovpToSet;
            set => Set(ref _ovpToSet, value);
        }

        public double IsOvpValid
        {
            get => _isOvpValid;
            set => Set(ref _isOvpValid, value);
        }

        public double MaxOcpToSet
        {
            get => _maxOcpToSet;
            set => Set(ref _maxOcpToSet, value);
        }

        public double MinOcpToSet
        {
            get => _minOcpToSet;
            set => Set(ref _minOcpToSet, value);
        }

        public double OcpToSet
        {
            get => _ocpToSet;
            set => Set(ref _ocpToSet, value);
        }

        public double IsOcpValid
        {
            get => _isOcpValid;
            set => Set(ref _isOcpValid, value);
        }

        public EventStatus EventStatus
        {
            get => _eventStatus;
            set => Set(ref _eventStatus, value);
        }

        public bool IsInConfigMode
        {
            get => _isInConfigMode;
            set => Set(ref _isInConfigMode, value);
        }

        public MainViewModel(PowerSupplyService powerSupplyService, DatabaseService databaseService)
        {
            _powerSupplyService = powerSupplyService;
            _databaseService = databaseService;
            Status = new PowerSupplyStatus();

            // Create commands
            ConnectCommand = new RelayCommand(async _ => await Connect());
            DisconnectCommand = new RelayCommand(_ => Disconnect(), _ => IsConnected && IsInConfigMode);
            SetVoltageCommand = new RelayCommand(async _ => await SetVoltageAsync(), _ => IsConnected && IsInConfigMode);
            SetCurrentCommand = new RelayCommand(async _ => await SetCurrentAsync(), _ => IsConnected && IsInConfigMode);
            ToggleOutputCommand = new RelayCommand(async _ => await ToggleOutput(), _ => IsConnected && IsInConfigMode);
            SetOvpCommand = new RelayCommand(async _ => await SetOvpAsync(), _ => IsConnected && IsInConfigMode);
            SetOcpCommand = new RelayCommand(async _ => await SetOcpAsync(), _ => IsConnected && IsInConfigMode);
            SendMiscCommand = new RelayCommand<string>( SendMiscCommandExecute, _ => IsConnected && IsInConfigMode);

            // Setup timer for periodic updates
            _updateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _updateTimer.Tick += async (s, e) => await UpdateStatus();

            // Setup timer for logging data
            _loggingTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5) // Log every 10 seconds
            };
            _loggingTimer.Tick += async (s, e) => await LogData();
        }

        private async void SendMiscCommandExecute(string command)
        {
            await _powerSupplyService.SendCommandAsync(command);
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
                _loggingTimer.Start();
                SupplyName = await _powerSupplyService.SendCommandAsync(PowerSupplyCommands.GetPsIdentityCommand);
                PowerSupplySettingStatus = await _powerSupplyService.GetPowerSupplySettingsAsync();
            }
            else
            {
                ConnectionStatus = "Connection Failed";
                IsConnected = false;
                SupplyName = string.Empty;
            }
        }

        private void Disconnect()
        {
            _updateTimer.Stop();
            _loggingTimer.Stop(); // Stop logging data
            _powerSupplyService.Disconnect();
            IsConnected = false;
            ConnectionStatus = "Disconnected";
            Status = new PowerSupplyStatus()
            {
                ConnectionStatus = "Disconnected", Current = 0, OutputEnabled = false, Power = 0, Voltage = 0,
                Time = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}"
            };
            SupplyName = string.Empty;
            IsInConfigMode = false;
        }

        private async Task UpdateStatus()
        {
            var status = await _powerSupplyService.GetStatusAsync();
            if (status is null)
            {
                // Transient skip (e.g. bus busy) — keep last UI values; do not treat as disconnect.
                return;
            }

            Status = status;
            EventStatus = status.EventStatus;
            ConnectionStatus = Status.ConnectionStatus;

            if (status.ConnectionStatus != "Connected")
            {
                _updateTimer.Stop();
                _loggingTimer.Stop();
                IsConnected = false;
                return;
            }

            _powerSupplyStatusQueue.Enqueue(status);

            if (skipCounter++ % 3 == 0)
            {
                var settings = await _powerSupplyService.GetPowerSupplySettingsAsync();
                if (settings != null)
                    PowerSupplySettingStatus = settings;
            }

            CommandManager.InvalidateRequerySuggested();
        }

        private async Task LogData()
        {
            while (!_powerSupplyStatusQueue.IsEmpty && _powerSupplyStatusQueue.TryDequeue(out var status) && status != null)
            {
                await _databaseService.LogPowerSupplyStatusAsync(status);
            }
        }

        private void ValidateVoltage()
        {
            IsVoltageValid = VoltageToSet >= MinVoltageToSet && VoltageToSet <= MaxVoltageToSet;
        }

        private void ValidateCurrent()
        {
            IsCurrentValid = CurrentToSet >= MinCurrentToSet && CurrentToSet <= MaxCurrentToSet;
        }

        private async Task SetVoltageAsync()
        {
            

            // Validate voltage is within limits
            if (VoltageToSet < MinVoltageToSet || VoltageToSet > MaxVoltageToSet)
            {
                Xceed.Wpf.Toolkit.MessageBox.Show(Application.Current.MainWindow, "Voltage out of range.", "Error",
                    MessageBoxButton.OK);
                // Don't proceed if outside limits
                return;
            }

            await _powerSupplyService.SetVoltageAsync(VoltageToSet);
            PowerSupplySettingStatus = await _powerSupplyService.GetPowerSupplySettingsAsync();
        }

        private async Task SetCurrentAsync()
        {
            

            // Validate current is within limits
            if (CurrentToSet < MinCurrentToSet || CurrentToSet > MaxCurrentToSet)
            {
                Xceed.Wpf.Toolkit.MessageBox.Show(Application.Current.MainWindow, "Current out of range.", "Error",
                    MessageBoxButton.OK);
                // Don't proceed if outside limits
                return;
            }

            await _powerSupplyService.SetCurrentAsync(CurrentToSet);
            PowerSupplySettingStatus = await _powerSupplyService.GetPowerSupplySettingsAsync();
        }

        private async Task ToggleOutput()
        {
            await _powerSupplyService.SetOutputAsync(!Status.OutputEnabled);
            await UpdateStatus();
            IsInConfigMode = false;
        }

        private async Task SetOvpAsync()
        {
            if (OvpToSet < MinOvpToSet || OvpToSet > MaxOvpToSet)
            {
                Xceed.Wpf.Toolkit.MessageBox.Show(Application.Current.MainWindow, "OVP out of range.", "Error",
                    MessageBoxButton.OK);
                // Don't proceed if outside limits
                return;
            }
            await _powerSupplyService.SetOverVoltageProtectionAsync(OvpToSet);
            PowerSupplySettingStatus = await _powerSupplyService.GetPowerSupplySettingsAsync();

            // Save to settings
            //var settings = Settings.Load();
            //settings.OverVoltageProtectionLevel = OvpLevel;
            //settings.Save();
        }

        private async Task SetOcpAsync()
        {
            if (OcpToSet < MinOcpToSet || OcpToSet > MaxOcpToSet)
            {
                Xceed.Wpf.Toolkit.MessageBox.Show(Application.Current.MainWindow, "OCP out of range.", "Error",
                    MessageBoxButton.OK);
                // Don't proceed if outside limits
                return;
            }
            await _powerSupplyService.SetOverCurrentProtectionAsync(OcpToSet);
            PowerSupplySettingStatus = await _powerSupplyService.GetPowerSupplySettingsAsync();

            // Save to settings
            //var settings = Settings.Load();
            //settings.OverCurrentProtectionLevel = OcpLevel;
            //settings.Save();
        }


        public void OnClosingWindow(MainWindow mainWindow, CancelEventArgs cancelEventArgs)
        {
            if (!IsConnected)
            {
                cancelEventArgs.Cancel = false;
                return;
            }
            var result = MessageBox.Show("Are you sure to close?", "Warning", MessageBoxButton.YesNo);
            if (result == MessageBoxResult.No)
            {
                cancelEventArgs.Cancel = true;
                return;
            }
            _powerSupplyService.Disconnect();
            Thread.Sleep(500);
            cancelEventArgs.Cancel = false;
        }
    }
}