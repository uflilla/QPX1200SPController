using System;
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
        private const string CommandSource = "Control panel"; // Source column of the command log

        private readonly PowerSupplyService _powerSupplyService;
        private readonly DatabaseService _databaseService;
        private readonly Settings _settings = Settings.Current;
        private readonly DispatcherTimer _pollTimer;
        private readonly DispatcherTimer _statusBarTimer;
        private readonly LiveTelemetryBuffer _telemetryBuffer = new LiveTelemetryBuffer();

        private bool _pollInProgress;
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
        private PowerSupplySettingStatus _powerSupplySettingStatus;
        private bool _isInConfigMode;
        private bool _isControlPanelVisible = true;
        private bool _isFollowingLive = true;
        private GraphTimeWindowOption _selectedGraphTimeWindow;
        private SampleRateOption _selectedSampleRate;
        private string _lastReadingText = "No readings yet";
        private string _databaseStatusText;
        private bool _isDatabaseFailing;
        private string _traceStatusText;
        private bool _isTraceOn;
        private double _limitMinVoltage;
        private double _limitMaxVoltage;
        private double _limitMinCurrent;
        private double _limitMaxCurrent;
        private int _limitRetentionDays;
        private string _limitsStatusText;

        /// <summary>New samples arrived or the time window changed: redraw the live plots.</summary>
        public event Action GraphDataUpdated;

        /// <summary>A Y-axis mode or range changed: re-apply the Y limits of the live plots.</summary>
        public event Action GraphYAxisChanged;

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
                if (!Set(ref _selectedGraphTimeWindow, value))
                    return;
                // Picking a window means "show me that window": go back to following the newest data.
                if (!IsFollowingLive)
                    IsFollowingLive = true;
                else
                    GraphDataUpdated?.Invoke();
            }
        }

        /// <summary>
        /// True: the live plots scroll with the newest data. Zooming or panning a plot switches it off
        /// so the view stays where the user put it.
        /// </summary>
        public bool IsFollowingLive
        {
            get => _isFollowingLive;
            set
            {
                if (SetProperty(ref _isFollowingLive, value) && value)
                    GraphDataUpdated?.Invoke();
            }
        }

        public bool IsDarkTheme
        {
            get => ThemeService.Instance.IsDark;
            set
            {
                if (value == ThemeService.Instance.IsDark)
                    return;
                ThemeService.Instance.Apply(value);
                _settings.DarkTheme = value;
                _settings.Save();
                RaisePropertyChanged();
            }
        }

        #region Read-outs and status bar

        /// <summary>CV / CC / UNREG and the protection trips, as indicator chips.</summary>
        public IReadOnlyList<StatusFlag> StatusFlags { get; } = new[]
        {
            new StatusFlag("CV", StatusFlagKind.Good, "Constant voltage regulation"),
            new StatusFlag("CC", StatusFlagKind.Notice, "Constant current regulation (current limit reached)"),
            new StatusFlag("UNREG", StatusFlagKind.Notice, "Output unregulated"),
            new StatusFlag("OVP", StatusFlagKind.Trip, "Over-voltage protection tripped (use TRIP RESET)"),
            new StatusFlag("OCP", StatusFlagKind.Trip, "Over-current protection tripped (use TRIP RESET)"),
            new StatusFlag("SENSE", StatusFlagKind.Trip, "Output sense trip (use TRIP RESET)"),
            new StatusFlag("FAULT", StatusFlagKind.Trip, "Fault trip (use TRIP RESET)")
        };

        public string RegulationText
        {
            get
            {
                var e = Status?.EventStatus;
                if (!IsConnected || e == null)
                    return "Mode ---";
                if (e.ConstantCurrentMode == 1)
                    return "Mode CC";
                if (e.UnregulatedMode == 1)
                    return "Mode UNREG";
                return e.ConstantVoltageMode == 1 ? "Mode CV" : "Mode ---";
            }
        }

        /// <summary>Load resistance seen by the supply (V / I), shown while current flows.</summary>
        public string LoadText
        {
            get
            {
                var s = Status;
                if (!IsConnected || s == null || !s.OutputEnabled || s.Current < 0.005)
                    return string.Empty;
                return string.Format(CultureInfo.InvariantCulture, "Load {0:0.###} Ω", s.Voltage / s.Current);
            }
        }

        public string LastReadingText
        {
            get => _lastReadingText;
            private set => SetProperty(ref _lastReadingText, value);
        }

        public string DatabaseStatusText
        {
            get => _databaseStatusText;
            private set => SetProperty(ref _databaseStatusText, value);
        }

        public bool IsDatabaseFailing
        {
            get => _isDatabaseFailing;
            private set => SetProperty(ref _isDatabaseFailing, value);
        }

        public string TraceStatusText
        {
            get => _traceStatusText;
            private set => SetProperty(ref _traceStatusText, value);
        }

        public bool IsTraceOn
        {
            get => _isTraceOn;
            private set => SetProperty(ref _isTraceOn, value);
        }

        public string VersionText { get; } = "v" + typeof(MainViewModel).Assembly.GetName().Version.ToString(3);

        private void RefreshStatusBar()
        {
            if (!_databaseService.IsAvailable)
            {
                DatabaseStatusText = "Database unavailable - see App Logs";
                IsDatabaseFailing = true;
            }
            else if (_databaseService.IsWriteFailing)
            {
                DatabaseStatusText = "Database writes failing - data kept in memory and retried";
                IsDatabaseFailing = true;
            }
            else
            {
                DatabaseStatusText = "Logging to database";
                IsDatabaseFailing = false;
            }

            TraceService.Instance.CheckExpiry();
            IsTraceOn = TraceService.Instance.IsEnabled;
            TraceStatusText = IsTraceOn ? $"Trace ON until {TraceService.Instance.EnabledUntil:HH:mm}" : null;
        }

        #endregion

        #region Soft limits and storage

        public double InstrumentMaxVoltage => Settings.InstrumentMaxVoltage;
        public double InstrumentMaxCurrent => Settings.InstrumentMaxCurrent;

        public double LimitMinVoltage
        {
            get => _limitMinVoltage;
            set => SetProperty(ref _limitMinVoltage, value);
        }

        public double LimitMaxVoltage
        {
            get => _limitMaxVoltage;
            set => SetProperty(ref _limitMaxVoltage, value);
        }

        public double LimitMinCurrent
        {
            get => _limitMinCurrent;
            set => SetProperty(ref _limitMinCurrent, value);
        }

        public double LimitMaxCurrent
        {
            get => _limitMaxCurrent;
            set => SetProperty(ref _limitMaxCurrent, value);
        }

        /// <summary>Database rows older than this many days are deleted; 0 keeps everything.</summary>
        public int LimitRetentionDays
        {
            get => _limitRetentionDays;
            set => SetProperty(ref _limitRetentionDays, value);
        }

        public string LimitsStatusText
        {
            get => _limitsStatusText;
            private set => SetProperty(ref _limitsStatusText, value);
        }

        public ICommand SaveLimitsCommand { get; }
        public ICommand RevertLimitsCommand { get; }

        private void LoadLimitFields()
        {
            LimitMinVoltage = _settings.MinVoltage;
            LimitMaxVoltage = _settings.MaxVoltage;
            LimitMinCurrent = _settings.MinCurrent;
            LimitMaxCurrent = _settings.MaxCurrent;
            LimitRetentionDays = _settings.RetentionDays;
        }

        private void SaveLimits()
        {
            string error = null;
            if (LimitMinVoltage < 0 || LimitMaxVoltage > Settings.InstrumentMaxVoltage || LimitMinVoltage >= LimitMaxVoltage)
                error = $"Voltage limits must satisfy 0 ≤ min < max ≤ {Settings.InstrumentMaxVoltage:F0} V.";
            else if (LimitMinCurrent < 0 || LimitMaxCurrent > Settings.InstrumentMaxCurrent || LimitMinCurrent >= LimitMaxCurrent)
                error = $"Current limits must satisfy 0 ≤ min < max ≤ {Settings.InstrumentMaxCurrent:F0} A.";
            else if (LimitRetentionDays < 0)
                error = "Days to keep must be 0 (keep everything) or more.";

            if (error != null)
            {
                ShowError(error);
                return;
            }

            _settings.MinVoltage = LimitMinVoltage;
            _settings.MaxVoltage = LimitMaxVoltage;
            _settings.MinCurrent = LimitMinCurrent;
            _settings.MaxCurrent = LimitMaxCurrent;
            _settings.RetentionDays = LimitRetentionDays;
            _settings.Save();
            SettingsUpdated(_settings);
            LoadLimitFields();

            var inv = CultureInfo.InvariantCulture;
            LoggingService.Instance.LogInfo(string.Format(inv,
                    "Soft limits saved: {0:F3}-{1:F3} V, {2:F3}-{3:F3} A; keep data {4}.",
                    _settings.MinVoltage, _settings.MaxVoltage, _settings.MinCurrent, _settings.MaxCurrent,
                    _settings.RetentionDays == 0 ? "forever" : _settings.RetentionDays + " days"),
                nameof(MainViewModel), nameof(SaveLimits));
            LimitsStatusText = $"Saved at {DateTime.Now:HH:mm:ss}";
        }

        #endregion

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
            ToggleThemeCommand = new RelayCommand(_ => IsDarkTheme = !IsDarkTheme);
            SaveLimitsCommand = new RelayCommand(_ => SaveLimits());
            RevertLimitsCommand = new RelayCommand(_ => { LoadLimitFields(); LimitsStatusText = null; });

            _pollTimer = new DispatcherTimer();
            _pollTimer.Tick += async (s, e) => await PollAsync();

            SelectedSampleRate = SampleRates.FirstOrDefault(r => r.Hz == _settings.SampleRateHz) ?? SampleRates[1];
            _pollTimer.Interval = SelectedSampleRate.Interval;
            SettingsUpdated(_settings);
            LoadLimitFields();

            _statusBarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _statusBarTimer.Tick += (s, e) => RefreshStatusBar();
            _statusBarTimer.Start();
            RefreshStatusBar();

            _powerSupplyService.ConnectionStateChanged += OnConnectionStateChanged;
        }

        public ICommand ToggleThemeCommand { get; }

        private void AttachYAxisSettings(PlotYAxisSettings settings)
        {
            settings.PropertyChanged += (_, __) => GraphYAxisChanged?.Invoke();
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
                    foreach (var flag in StatusFlags)
                        flag.IsActive = false;
                    GraphDataUpdated?.Invoke();
                    break;
            }

            RaisePropertyChanged(nameof(RegulationText));
            RaisePropertyChanged(nameof(LoadText));
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
            var e = status.EventStatus ?? new EventStatus();
            int[] bits = { e.ConstantVoltageMode, e.ConstantCurrentMode, e.UnregulatedMode,
                           e.OverVoltageTrip, e.OverCurrentTrip, e.OutputSenseTrip, e.FaultTrip };
            for (int k = 0; k < StatusFlags.Count; k++)
                StatusFlags[k].IsActive = bits[k] == 1;
            RaisePropertyChanged(nameof(RegulationText));
            RaisePropertyChanged(nameof(LoadText));
            LastReadingText = "Last reading " + status.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

            _databaseService.EnqueueSample(status);
            _telemetryBuffer.Add(status.Timestamp, status.Voltage, status.Current, status.Power);
            GraphDataUpdated?.Invoke();
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

            var record = await _powerSupplyService.SetVoltageAsync(VoltageToSet, CommandSource);
            if (!record.Success)
            {
                ShowError("The voltage command could not be sent. See App Logs for details.");
                return;
            }
            await RefreshSettingsAndVerifyAsync(record, "Voltage", s => s.SetVoltage, "V");
        }

        private async Task SetCurrentAsync()
        {
            if (!IsCurrentValid)
            {
                ShowError($"Current must be between {MinCurrentToSet:F3} A and {MaxCurrentToSet:F3} A.");
                return;
            }

            var record = await _powerSupplyService.SetCurrentAsync(CurrentToSet, CommandSource);
            if (!record.Success)
            {
                ShowError("The current command could not be sent. See App Logs for details.");
                return;
            }
            await RefreshSettingsAndVerifyAsync(record, "Current", s => s.SetCurrent, "A");
        }

        private async Task SetOvpAsync()
        {
            if (!IsOvpValid)
            {
                ShowError($"OVP must be between {MinOvpToSet:F3} V and {MaxOvpToSet:F3} V.");
                return;
            }

            var record = await _powerSupplyService.SetOverVoltageProtectionAsync(OvpToSet, CommandSource);
            if (!record.Success)
            {
                ShowError("The OVP command could not be sent. See App Logs for details.");
                return;
            }
            await RefreshSettingsAndVerifyAsync(record, "OVP", s => s.SetOvpLevel, "V");
        }

        private async Task SetOcpAsync()
        {
            if (!IsOcpValid)
            {
                ShowError($"OCP must be between {MinOcpToSet:F3} A and {MaxOcpToSet:F3} A.");
                return;
            }

            var record = await _powerSupplyService.SetOverCurrentProtectionAsync(OcpToSet, CommandSource);
            if (!record.Success)
            {
                ShowError("The OCP command could not be sent. See App Logs for details.");
                return;
            }
            await RefreshSettingsAndVerifyAsync(record, "OCP", s => s.SetOcpLevel, "A");
        }

        /// <summary>
        /// Reads the set-points back, records the read-back on the command's log row and
        /// logs a warning if the supply did not take the requested value.
        /// </summary>
        private async Task RefreshSettingsAndVerifyAsync(ActivityRecord record, string name,
            Func<PowerSupplySettingStatus, double> selector, string unit)
        {
            double expected = record.Requested ?? 0;
            var settings = await _powerSupplyService.GetPowerSupplySettingsAsync();
            if (settings == null)
            {
                record.Complete(detail: "read-back failed");
                LoggingService.Instance.LogWarning($"{name} sent ({expected:F3} {unit}) but the read-back failed.",
                    nameof(MainViewModel), nameof(RefreshSettingsAndVerifyAsync));
                return;
            }

            PowerSupplySettingStatus = settings;
            double actual = selector(settings);
            double tolerance = Math.Max(0.011, Math.Abs(expected) * 0.001);
            if (Math.Abs(actual - expected) > tolerance)
            {
                string message = $"{name} read-back mismatch: requested {expected:F3} {unit}, supply reports {actual:F3} {unit}.";
                record.Complete(ActivityRecord.ResultMismatch, actual, message);
                LoggingService.Instance.LogWarning(message, nameof(MainViewModel), nameof(RefreshSettingsAndVerifyAsync));
            }
            else
            {
                record.Complete(ActivityRecord.ResultOk, actual);
                LoggingService.Instance.LogInfo($"{name} set to {actual:F3} {unit}.",
                    nameof(MainViewModel), nameof(RefreshSettingsAndVerifyAsync));
            }
        }

        private async Task ToggleOutput()
        {
            bool enable = !Status.OutputEnabled;
            var record = await _powerSupplyService.SetOutputAsync(enable, CommandSource);
            if (!record.Success)
            {
                ShowError("The output command could not be sent. See App Logs for details.");
                return;
            }

            await VerifyOutputAsync(record, enable);
            IsInConfigMode = false;
        }

        /// <summary>Reads the output state back onto the command's log row. Returns true if it matches.</summary>
        private async Task<bool> VerifyOutputAsync(ActivityRecord record, bool expectedOn)
        {
            var status = await _powerSupplyService.GetStatusAsync(skipIfBusy: false);
            if (status == null)
            {
                record.Complete(detail: "read-back failed");
                LoggingService.Instance.LogWarning($"Output {(expectedOn ? "ON" : "OFF")} sent but the read-back failed.",
                    nameof(MainViewModel), nameof(VerifyOutputAsync));
                return false;
            }

            ApplyStatus(status);
            bool matches = status.OutputEnabled == expectedOn;
            if (matches)
            {
                record.Complete(ActivityRecord.ResultOk, status.OutputEnabled ? 1 : 0);
                LoggingService.Instance.LogInfo($"Output switched {(expectedOn ? "ON" : "OFF")}.", nameof(MainViewModel), nameof(VerifyOutputAsync));
            }
            else
            {
                string message = $"Output {(expectedOn ? "ON" : "OFF")} requested but the supply reports {(status.OutputEnabled ? "ON" : "OFF")}" +
                                 (expectedOn && HasTrip(status.EventStatus) ? " (trip active - use TRIP Reset)." : ".");
                record.Complete(ActivityRecord.ResultMismatch, status.OutputEnabled ? 1 : 0, message);
                LoggingService.Instance.LogWarning(message, nameof(MainViewModel), nameof(VerifyOutputAsync));
            }
            return matches;
        }

        private static bool HasTrip(EventStatus e)
        {
            return e != null && (e.OverVoltageTrip == 1 || e.OverCurrentTrip == 1 || e.OutputSenseTrip == 1 || e.FaultTrip == 1);
        }

        private async Task SendMiscCommandAsync(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return;

            var record = await _powerSupplyService.SendConfigCommandAsync(command, CommandSource);
            record.Complete();
            if (!record.Success)
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
                        var record = await _powerSupplyService.SetOutputAsync(false, "Exit");
                        bool confirmedOff = record.Success && await VerifyOutputAsync(record, expectedOn: false);

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
                _powerSupplyService.Disconnect();

                // Write every queued sample and log row before the process ends.
                await _databaseService.FlushAsync(force: true);
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
