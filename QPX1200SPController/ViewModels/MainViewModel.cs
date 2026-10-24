using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using QPX1200SPController.Models;
using QPX1200SPController.Services;
using ExitChoice = QPX1200SPController.Views.ExitChoice;
using ExitDialog = QPX1200SPController.Views.ExitDialog;
using Settings = QPX1200SPController.Models.Settings;

namespace QPX1200SPController.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        private static readonly TimeSpan SettingsPollInterval = TimeSpan.FromSeconds(3);
        private const int MaxPendingLogSamples = 100000; // ~1 day at 1 Hz if the database is unavailable

        private readonly PowerSupplyService _powerSupplyService;
        private readonly DatabaseService _databaseService;
        private readonly Settings _settings = Settings.Current;
        private readonly DispatcherTimer _pollTimer;
        private readonly DispatcherTimer _loggingTimer;
        private readonly ConcurrentQueue<PowerSupplyStatus> _powerSupplyStatusQueue = new ConcurrentQueue<PowerSupplyStatus>();
        private readonly LiveTelemetryBuffer _telemetryBuffer = new LiveTelemetryBuffer();

        private bool _pollInProgress;
        private bool _loggingInProgress;
        private bool _shutdownInProgress;
        private DateTime _nextSettingsPollUtc = DateTime.MinValue;
        private ConnectionState _appliedConnectionState = ConnectionState.Disconnected;
        private string _lastConnectError;

        private PowerSupplyStatus _status;
        private string _ipAddress;
        private string _connectionStatus = "Disconnected";
        private bool _isConnected;
        private bool _isReconnecting;
        private bool _isVoltageValid = true;
        private bool _isCurrentValid = true;
        private bool _isOvpValid = true;
        private bool _isOcpValid = true;
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
        private double _maxOcpToSet;
        private double _minOcpToSet;
        private double _ocpToSet;
        private EventStatus _eventStatus;
        private PowerSupplySettingStatus _powerSupplySettingStatus;
        private bool _isInConfigMode;
        private bool _isControlPanelVisible = true;
        private GraphTimeWindowOption _selectedGraphTimeWindow;
        private SampleRateOption _selectedSampleRate;

        public event Action GraphDataUpdated;

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

        public bool IsOvpValid
        {
            get => _isOvpValid;
            private set => SetProperty(ref _isOvpValid, value);
        }

        public bool IsOcpValid
        {
            get => _isOcpValid;
            private set => SetProperty(ref _isOcpValid, value);
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

        public bool IsReconnecting
        {
            get => _isReconnecting;
            private set => SetProperty(ref _isReconnecting, value);
        }

        public bool IsControlPanelVisible
        {
            get => _isControlPanelVisible;
            set
            {
                if (SetProperty(ref _isControlPanelVisible, value))
                    RaisePropertyChanged(nameof(ControlPanelToggleText));
            }
        }

        public string ControlPanelToggleText =>
            IsControlPanelVisible ? "Hide Controls" : "Show Controls";

        public IReadOnlyList<GraphTimeWindowOption> GraphTimeWindows { get; } = CreateGraphTimeWindows();

        public IReadOnlyList<SampleRateOption> SampleRates { get; } =
            Settings.SampleRateOptionsHz.Select(hz => new SampleRateOption(hz)).ToList();

        public IReadOnlyList<string> YAxisScaleModes { get; } = new[]
        {
            PlotYAxisSettings.AutoMode,
            PlotYAxisSettings.ManualMode
        };

        public PlotYAxisSettings VoltageYAxis { get; } = new PlotYAxisSettings(0, 60);
        public PlotYAxisSettings CurrentYAxis { get; } = new PlotYAxisSettings(0, 50);
        public PlotYAxisSettings PowerYAxis { get; } = new PlotYAxisSettings(0, 1200);

        public GraphTimeWindowOption SelectedGraphTimeWindow
        {
            get => _selectedGraphTimeWindow;
            set
            {
                if (Set(ref _selectedGraphTimeWindow, value))
                    GraphDataUpdated?.Invoke();
            }
        }

        /// <summary>Polling and database logging rate. Persisted in settings.xml.</summary>
        public SampleRateOption SelectedSampleRate
        {
            get => _selectedSampleRate;
            set
            {
                if (value == null || !Set(ref _selectedSampleRate, value))
                    return;
                _pollTimer.Interval = value.Interval;
                if (_settings.SampleRateHz != value.Hz)
                {
                    _settings.SampleRateHz = value.Hz;
                    _settings.Save();
                    LoggingService.Instance.LogInfo($"Sample rate set to {value.DisplayName}.", nameof(MainViewModel), nameof(SelectedSampleRate));
                }
            }
        }

        public ICommand ConnectCommand { get; }
        public ICommand DisconnectCommand { get; }
        public ICommand SetVoltageCommand { get; }
        public ICommand SetCurrentCommand { get; }
        public ICommand ToggleOutputCommand { get; }
        public ICommand SetOvpCommand { get; }
        public ICommand SetOcpCommand { get; }
        public ICommand SendMiscCommand { get; }
        public ICommand ToggleControlPanelCommand { get; }

        public string SupplyName
        {
            get => _supplyName;
            set => Set(ref _supplyName, value);
        }

        public void SettingsUpdated(Settings settings)
        {
            MinVoltageToSet = settings.MinVoltage;
            MaxVoltageToSet = settings.MaxVoltage;
            MinCurrentToSet = settings.MinCurrent;
            MaxCurrentToSet = settings.MaxCurrent;
            MinOvpToSet = settings.MinOvpLevel;
            MaxOvpToSet = settings.MaxOvpLevel;
            MinOcpToSet = settings.MinOcpLevel;
            MaxOcpToSet = settings.MaxOcpLevel;
            ValidateAll();
        }

        public double VoltageToSet
        {
            get => _voltageToSet;
            set
            {
                Set(ref _voltageToSet, value);
                ValidateAll();
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
            set { Set(ref _currentToSet, value); ValidateAll(); }
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
            set { Set(ref _ovpToSet, value); ValidateAll(); }
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
            set { Set(ref _ocpToSet, value); ValidateAll(); }
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
            _ipAddress = _settings.LastIpAddress;
            _selectedGraphTimeWindow = GraphTimeWindows[2]; // 15 min
            AttachYAxisSettings(VoltageYAxis);
            AttachYAxisSettings(CurrentYAxis);
            AttachYAxisSettings(PowerYAxis);

            ConnectCommand = new RelayCommand(async _ => await Connect(), _ => _powerSupplyService.State == ConnectionState.Disconnected);
            DisconnectCommand = new RelayCommand(_ => Disconnect(), _ => (IsConnected && IsInConfigMode) || IsReconnecting);
            SetVoltageCommand = new RelayCommand(async _ => await SetVoltageAsync(), _ => IsConnected && IsInConfigMode);
            SetCurrentCommand = new RelayCommand(async _ => await SetCurrentAsync(), _ => IsConnected && IsInConfigMode);
            ToggleOutputCommand = new RelayCommand(async _ => await ToggleOutput(), _ => IsConnected && IsInConfigMode);
            SetOvpCommand = new RelayCommand(async _ => await SetOvpAsync(), _ => IsConnected && IsInConfigMode);
            SetOcpCommand = new RelayCommand(async _ => await SetOcpAsync(), _ => IsConnected && IsInConfigMode);
            SendMiscCommand = new RelayCommand(async p => await SendMiscCommandAsync(p as string), _ => IsConnected && IsInConfigMode);
            ToggleControlPanelCommand = new RelayCommand(_ => IsControlPanelVisible = !IsControlPanelVisible);

            _pollTimer = new DispatcherTimer();
            _pollTimer.Tick += async (s, e) => await PollAsync();

            _loggingTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _loggingTimer.Tick += async (s, e) => await LogData();

            SelectedSampleRate = SampleRates.FirstOrDefault(r => r.Hz == _settings.SampleRateHz) ?? SampleRates[1];
            _pollTimer.Interval = SelectedSampleRate.Interval;
            SettingsUpdated(_settings);

            _powerSupplyService.ConnectionStateChanged += OnConnectionStateChanged;
        }

        private void AttachYAxisSettings(PlotYAxisSettings settings)
        {
            settings.PropertyChanged += (_, __) => GraphDataUpdated?.Invoke();
        }

        public TelemetrySample[] GetGraphSamples()
        {
            DateTime? from = null;
            if (SelectedGraphTimeWindow?.Duration != null)
                from = DateTime.Now - SelectedGraphTimeWindow.Duration.Value;
            return _telemetryBuffer.GetSamplesSince(from);
        }

        private static IReadOnlyList<GraphTimeWindowOption> CreateGraphTimeWindows()
        {
            return new List<GraphTimeWindowOption>
            {
                new GraphTimeWindowOption("5 min", TimeSpan.FromMinutes(5)),
                new GraphTimeWindowOption("10 min", TimeSpan.FromMinutes(10)),
                new GraphTimeWindowOption("15 min", TimeSpan.FromMinutes(15)),
                new GraphTimeWindowOption("30 min", TimeSpan.FromMinutes(30)),
                new GraphTimeWindowOption("1 hour", TimeSpan.FromHours(1)),
                new GraphTimeWindowOption("2 hours", TimeSpan.FromHours(2)),
                new GraphTimeWindowOption("3 hours", TimeSpan.FromHours(3)),
                new GraphTimeWindowOption("5 hours", TimeSpan.FromHours(5)),
                new GraphTimeWindowOption("12 hours", TimeSpan.FromHours(12)),
                new GraphTimeWindowOption("1 day", TimeSpan.FromHours(24)),
                new GraphTimeWindowOption("Maximum", null)
            };
        }

        #region Connection

        private async Task Connect()
        {
            _lastConnectError = null;
            string ip = IpAddress?.Trim();
            if (string.IsNullOrEmpty(ip))
            {
                ConnectionStatus = "Enter an IP address";
                return;
            }

            bool success = await _powerSupplyService.ConnectAsync(ip);
            if (success)
            {
                if (_settings.LastIpAddress != ip)
                {
                    _settings.LastIpAddress = ip;
                    _settings.Save();
                }
            }
            else
            {
                _lastConnectError = "Connection failed";
                ConnectionStatus = _lastConnectError;
                SupplyName = string.Empty;
            }
        }

        private void Disconnect()
        {
            _lastConnectError = null;
            _powerSupplyService.Disconnect();
        }

        private void OnConnectionStateChanged(ConnectionState state)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
                return;
            dispatcher.BeginInvoke(new Action(() => ApplyConnectionState(state)));
        }

        private void ApplyConnectionState(ConnectionState state)
        {
            var previous = _appliedConnectionState;
            _appliedConnectionState = state;

            IsConnected = state == ConnectionState.Connected;
            IsReconnecting = state == ConnectionState.Reconnecting;

            switch (state)
            {
                case ConnectionState.Connecting:
                    ConnectionStatus = "Connecting...";
                    break;

                case ConnectionState.Connected:
                    ConnectionStatus = "Connected";
                    if (previous != ConnectionState.Connected)
                    {
                        _nextSettingsPollUtc = DateTime.MinValue;
                        _pollTimer.Start();
                        _loggingTimer.Start();
                        _ = OnConnectedAsync(isReconnect: previous == ConnectionState.Reconnecting);
                    }
                    break;

                case ConnectionState.Reconnecting:
                    _pollTimer.Stop();
                    // Drop out of configuration mode so nothing is changed by accident when the link returns.
                    IsInConfigMode = false;
                    ConnectionStatus = _powerSupplyService.ReconnectAttempt == 0
                        ? "Link lost - reconnecting..."
                        : $"Reconnecting (attempt {_powerSupplyService.ReconnectAttempt})...";
                    break;

                case ConnectionState.Disconnected:
                    _pollTimer.Stop();
                    ConnectionStatus = _lastConnectError ?? "Disconnected";
                    Status = new PowerSupplyStatus
                    {
                        ConnectionStatus = "Disconnected",
                        Timestamp = DateTime.Now,
                        Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    };
                    SupplyName = string.Empty;
                    IsInConfigMode = false;
                    GraphDataUpdated?.Invoke();
                    _ = LogData(); // write what is still queued
                    break;
            }

            CommandManager.InvalidateRequerySuggested();
        }

        private async Task OnConnectedAsync(bool isReconnect)
        {
            string identity = await _powerSupplyService.GetIdentityAsync();
            if (!string.IsNullOrWhiteSpace(identity))
                SupplyName = identity.Trim();

            var settings = await _powerSupplyService.GetPowerSupplySettingsAsync();
            if (settings == null)
                return;

            PowerSupplySettingStatus = settings;
            if (!isReconnect)
            {
                // Start the set-point fields from what the supply is actually programmed to.
                VoltageToSet = settings.SetVoltage;
                CurrentToSet = settings.SetCurrent;
                OvpToSet = settings.SetOvpLevel;
                OcpToSet = settings.SetOcpLevel;
            }
        }

        #endregion

        #region Polling and logging

        private async Task PollAsync()
        {
            if (_pollInProgress || !_powerSupplyService.IsConnected)
                return;

            _pollInProgress = true;
            try
            {
                var status = await _powerSupplyService.GetStatusAsync();
                if (status != null)
                    ApplyStatus(status);

                if (_powerSupplyService.IsConnected && DateTime.UtcNow >= _nextSettingsPollUtc)
                {
                    _nextSettingsPollUtc = DateTime.UtcNow + SettingsPollInterval;
                    var settings = await _powerSupplyService.GetPowerSupplySettingsAsync(skipIfBusy: true);
                    if (settings != null)
                        PowerSupplySettingStatus = settings;
                }
            }
            catch (Exception ex)
            {
                LoggingService.Instance.LogException(ex, nameof(MainViewModel), nameof(PollAsync));
            }
            finally
            {
                _pollInProgress = false;
            }

            CommandManager.InvalidateRequerySuggested();
        }

        private void ApplyStatus(PowerSupplyStatus status)
        {
            Status = status;
            EventStatus = status.EventStatus;

            _powerSupplyStatusQueue.Enqueue(status);
            while (_powerSupplyStatusQueue.Count > MaxPendingLogSamples && _powerSupplyStatusQueue.TryDequeue(out _))
            {
            }

            _telemetryBuffer.Add(status.Timestamp, status.Voltage, status.Current, status.Power);
            GraphDataUpdated?.Invoke();
        }

        private async Task LogData()
        {
            if (_loggingInProgress)
                return;

            _loggingInProgress = true;
            try
            {
                while (_powerSupplyStatusQueue.TryPeek(out var status))
                {
                    if (status != null)
                        await _databaseService.LogPowerSupplyStatusAsync(status);
                    _powerSupplyStatusQueue.TryDequeue(out _);
                }
            }
            catch (Exception ex)
            {
                // Keep the sample queued and retry on the next tick.
                LoggingService.Instance.LogException(ex, nameof(MainViewModel), nameof(LogData));
            }
            finally
            {
                _loggingInProgress = false;
            }
        }

        #endregion

        #region Set commands

        private void ValidateAll()
        {
            IsVoltageValid = VoltageToSet >= MinVoltageToSet && VoltageToSet <= MaxVoltageToSet;
            IsCurrentValid = CurrentToSet >= MinCurrentToSet && CurrentToSet <= MaxCurrentToSet;
            IsOvpValid = OvpToSet >= MinOvpToSet && OvpToSet <= MaxOvpToSet;
            IsOcpValid = OcpToSet >= MinOcpToSet && OcpToSet <= MaxOcpToSet;
        }

        private async Task SetVoltageAsync()
        {
            if (!IsVoltageValid)
            {
                ShowError($"Voltage must be between {MinVoltageToSet:F3} V and {MaxVoltageToSet:F3} V.");
                return;
            }

            double requested = VoltageToSet;
            if (!await _powerSupplyService.SetVoltageAsync(requested))
            {
                ShowError("The voltage command could not be sent. See App Logs for details.");
                return;
            }
            await RefreshSettingsAndVerifyAsync("Voltage", requested, s => s.SetVoltage, "V");
        }

        private async Task SetCurrentAsync()
        {
            if (!IsCurrentValid)
            {
                ShowError($"Current must be between {MinCurrentToSet:F3} A and {MaxCurrentToSet:F3} A.");
                return;
            }

            double requested = CurrentToSet;
            if (!await _powerSupplyService.SetCurrentAsync(requested))
            {
                ShowError("The current command could not be sent. See App Logs for details.");
                return;
            }
            await RefreshSettingsAndVerifyAsync("Current", requested, s => s.SetCurrent, "A");
        }

        private async Task SetOvpAsync()
        {
            if (!IsOvpValid)
            {
                ShowError($"OVP must be between {MinOvpToSet:F3} V and {MaxOvpToSet:F3} V.");
                return;
            }

            double requested = OvpToSet;
            if (!await _powerSupplyService.SetOverVoltageProtectionAsync(requested))
            {
                ShowError("The OVP command could not be sent. See App Logs for details.");
                return;
            }
            await RefreshSettingsAndVerifyAsync("OVP", requested, s => s.SetOvpLevel, "V");
        }

        private async Task SetOcpAsync()
        {
            if (!IsOcpValid)
            {
                ShowError($"OCP must be between {MinOcpToSet:F3} A and {MaxOcpToSet:F3} A.");
                return;
            }

            double requested = OcpToSet;
            if (!await _powerSupplyService.SetOverCurrentProtectionAsync(requested))
            {
                ShowError("The OCP command could not be sent. See App Logs for details.");
                return;
            }
            await RefreshSettingsAndVerifyAsync("OCP", requested, s => s.SetOcpLevel, "A");
        }

        /// <summary>Reads the set-points back and logs a warning if the supply did not take the requested value.</summary>
        private async Task RefreshSettingsAndVerifyAsync(string name, double requested,
            Func<PowerSupplySettingStatus, double> selector, string unit)
        {
            var settings = await _powerSupplyService.GetPowerSupplySettingsAsync();
            if (settings == null)
            {
                LoggingService.Instance.LogWarning($"{name} sent ({requested:F3} {unit}) but the read-back failed.",
                    nameof(MainViewModel), nameof(RefreshSettingsAndVerifyAsync));
                return;
            }

            PowerSupplySettingStatus = settings;
            double actual = selector(settings);
            double expected = Math.Round(requested, 3);
            double tolerance = Math.Max(0.011, Math.Abs(expected) * 0.001);
            if (Math.Abs(actual - expected) > tolerance)
            {
                LoggingService.Instance.LogWarning(
                    $"{name} read-back mismatch: requested {expected:F3} {unit}, supply reports {actual:F3} {unit}.",
                    nameof(MainViewModel), nameof(RefreshSettingsAndVerifyAsync));
            }
            else
            {
                LoggingService.Instance.LogInfo($"{name} set to {actual:F3} {unit}.",
                    nameof(MainViewModel), nameof(RefreshSettingsAndVerifyAsync));
            }
        }

        private async Task ToggleOutput()
        {
            bool enable = !Status.OutputEnabled;
            if (!await _powerSupplyService.SetOutputAsync(enable))
            {
                ShowError("The output command could not be sent. See App Logs for details.");
                return;
            }

            LoggingService.Instance.LogInfo($"Output switched {(enable ? "ON" : "OFF")}.", nameof(MainViewModel), nameof(ToggleOutput));
            var status = await _powerSupplyService.GetStatusAsync(skipIfBusy: false);
            if (status != null)
                ApplyStatus(status);
            IsInConfigMode = false;
        }

        private async Task SendMiscCommandAsync(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return;
            if (await _powerSupplyService.SendCommandAsync(command) == null)
                ShowError($"'{command}' could not be sent. See App Logs for details.");
            else
                LoggingService.Instance.LogInfo($"Sent '{command}'.", nameof(MainViewModel), nameof(SendMiscCommandAsync));
        }

        private static void ShowError(string message)
        {
            Xceed.Wpf.Toolkit.MessageBox.Show(Application.Current.MainWindow, message, "Error", MessageBoxButton.OK);
        }

        #endregion

        #region Shutdown

        /// <summary>
        /// Runs the exit sequence: asks what to do with the output (if connected), optionally
        /// switches it off and verifies it, writes all queued samples and closes the link.
        /// Returns false if the user cancelled.
        /// </summary>
        public async Task<bool> PrepareShutdownAsync(Window owner)
        {
            if (_shutdownInProgress)
                return false;

            _shutdownInProgress = true;
            try
            {
                var state = _powerSupplyService.State;
                if (state != ConnectionState.Disconnected)
                {
                    bool outputOn = IsConnected && Status != null && Status.OutputEnabled;
                    var choice = ExitDialog.Ask(owner, outputOn, IsConnected);
                    if (choice == ExitChoice.Cancel)
                        return false;

                    _pollTimer.Stop();

                    if (choice == ExitChoice.TurnOutputOffAndExit)
                    {
                        bool confirmedOff = await _powerSupplyService.SetOutputAsync(false);
                        if (confirmedOff)
                        {
                            var status = await _powerSupplyService.GetStatusAsync(skipIfBusy: false);
                            confirmedOff = status != null && !status.OutputEnabled;
                            if (status != null)
                                ApplyStatus(status);
                        }

                        if (confirmedOff)
                        {
                            LoggingService.Instance.LogInfo("Output switched OFF on exit.", nameof(MainViewModel), nameof(PrepareShutdownAsync));
                        }
                        else
                        {
                            LoggingService.Instance.LogError("Could not confirm the output is OFF on exit.", nameof(MainViewModel), nameof(PrepareShutdownAsync));
                            var answer = MessageBox.Show(owner,
                                "The power supply did not confirm that the output is OFF.\n\nExit anyway?",
                                "Output state unknown", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                            if (answer != MessageBoxResult.Yes)
                            {
                                if (IsConnected)
                                    _pollTimer.Start();
                                return false;
                            }
                        }
                    }
                    else if (outputOn)
                    {
                        LoggingService.Instance.LogInfo("Exited with output left ON.", nameof(MainViewModel), nameof(PrepareShutdownAsync));
                    }
                }

                _powerSupplyService.ConnectionStateChanged -= OnConnectionStateChanged;
                _pollTimer.Stop();
                _loggingTimer.Stop();
                _powerSupplyService.Disconnect();

                // Let an in-flight logging tick finish, then write whatever is still queued.
                for (int i = 0; _loggingInProgress && i < 100; i++)
                    await Task.Delay(50);
                await LogData();
                return true;
            }
            finally
            {
                _shutdownInProgress = false;
            }
        }

        #endregion
    }

    public sealed class SampleRateOption
    {
        public SampleRateOption(double hz)
        {
            Hz = hz;
            Interval = TimeSpan.FromSeconds(1.0 / hz);
            DisplayName = hz.ToString("0.#", CultureInfo.InvariantCulture) + " Hz";
        }

        public double Hz { get; }
        public TimeSpan Interval { get; }
        public string DisplayName { get; }

        public override string ToString() => DisplayName;
    }
}
