using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Media;
using System.Windows;
using System.Windows.Input;
using QPX1200SPController.Models;
using QPX1200SPController.Services;

namespace QPX1200SPController.ViewModels
{
    /// <summary>
    /// Watches the live readings for threshold alarms (with a delay and hysteresis), protection trips
    /// and a lost link. Raised and cleared alarms are logged to the CommandLog (Kind "Alarm"), shown in
    /// a banner until acknowledged and, optionally, beep and switch the output off.
    /// </summary>
    public sealed class AlarmsViewModel : ViewModelBase
    {
        private const string Source = "Alarms";
        private const string LinkLostKey = "LinkLost";

        private static readonly string[] TripNames = { "OVP trip", "OCP trip", "Sense trip", "Fault trip" };

        private readonly MainViewModel _main;
        private readonly PowerSupplyService _powerSupply;
        private readonly DatabaseService _database;
        private readonly AlarmSettings _settings = Settings.Current.Alarms;
        private readonly Dictionary<string, ActiveAlarm> _active = new Dictionary<string, ActiveAlarm>();
        private readonly bool[] _tripActive = new bool[4];

        public AlarmsViewModel(MainViewModel main, PowerSupplyService powerSupply, DatabaseService database)
        {
            _main = main;
            _powerSupply = powerSupply;
            _database = database;

            Rules = new[]
            {
                new AlarmRule("VoltageHigh", "Voltage above", "V", true, s => s.Voltage, _settings.VoltageHighEnabled, _settings.VoltageHigh),
                new AlarmRule("VoltageLow", "Voltage below", "V", false, s => s.Voltage, _settings.VoltageLowEnabled, _settings.VoltageLow),
                new AlarmRule("CurrentHigh", "Current above", "A", true, s => s.Current, _settings.CurrentHighEnabled, _settings.CurrentHigh),
                new AlarmRule("CurrentLow", "Current below", "A", false, s => s.Current, _settings.CurrentLowEnabled, _settings.CurrentLow),
                new AlarmRule("PowerHigh", "Power above", "W", true, s => s.Power, _settings.PowerHighEnabled, _settings.PowerHigh)
            };
            foreach (var rule in Rules)
                rule.PropertyChanged += OnRuleChanged;

            AcknowledgeCommand = new RelayCommand(_ => Acknowledge());

            _main.PropertyChanged += OnMainPropertyChanged;
            _powerSupply.ConnectionStateChanged += OnConnectionStateChanged;
        }

        /// <summary>Raised on the UI thread when a new alarm appears (the window flashes its taskbar button).</summary>
        public event Action AlarmRaised;

        public IReadOnlyList<AlarmRule> Rules { get; }

        /// <summary>Active alarms the operator has not acknowledged yet (shown in the banner).</summary>
        public ObservableCollection<ActiveAlarm> Unacknowledged { get; } = new ObservableCollection<ActiveAlarm>();

        public bool HasBanner => Unacknowledged.Count > 0;

        public string BannerText
        {
            get
            {
                if (Unacknowledged.Count == 0)
                    return null;
                var first = Unacknowledged[0];
                string text = $"{first.Name}: {first.Message} (since {first.Since.ToString("HH:mm:ss", CultureInfo.InvariantCulture)})";
                return Unacknowledged.Count > 1 ? text + $"   +{Unacknowledged.Count - 1} more" : text;
            }
        }

        public string BannerToolTip => string.Join("\n", Unacknowledged.Select(a => a.Since.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + $"  {a.Name}: {a.Message}"));

        public ICommand AcknowledgeCommand { get; }

        public int DelaySeconds
        {
            get => _settings.DelaySeconds;
            set { if (_settings.DelaySeconds != value) { _settings.DelaySeconds = Math.Max(0, value); Save(); RaisePropertyChanged(); } }
        }

        public bool AlarmOnTrip
        {
            get => _settings.AlarmOnTrip;
            set { if (_settings.AlarmOnTrip != value) { _settings.AlarmOnTrip = value; Save(); RaisePropertyChanged(); } }
        }

        public bool AlarmOnLinkLost
        {
            get => _settings.AlarmOnLinkLost;
            set { if (_settings.AlarmOnLinkLost != value) { _settings.AlarmOnLinkLost = value; Save(); RaisePropertyChanged(); } }
        }

        public bool PlaySound
        {
            get => _settings.PlaySound;
            set { if (_settings.PlaySound != value) { _settings.PlaySound = value; Save(); RaisePropertyChanged(); } }
        }

        public bool TurnOutputOff
        {
            get => _settings.TurnOutputOff;
            set { if (_settings.TurnOutputOff != value) { _settings.TurnOutputOff = value; Save(); RaisePropertyChanged(); } }
        }

        #region Evaluation

        private void OnMainPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.Status) && _main.IsConnected && _main.Status != null)
                Evaluate(_main.Status);
        }

        private void Evaluate(PowerSupplyStatus status)
        {
            DateTime now = status.Timestamp;
            bool outputOn = status.OutputEnabled;
            var delay = TimeSpan.FromSeconds(_settings.DelaySeconds);

            foreach (var rule in Rules)
            {
                double value = rule.Selector(status);
                bool active = _active.ContainsKey(rule.Key);

                if (!rule.IsEnabled || !outputOn)
                {
                    rule.ConditionSince = null;
                    if (active)
                        Clear(rule.Key, rule.IsEnabled ? "output off" : "alarm disabled");
                    continue;
                }

                // A little hysteresis so a value sitting on the threshold does not raise and clear repeatedly.
                double hysteresis = Math.Max(0.005, Math.Abs(rule.Limit) * 0.01);
                bool beyond = rule.IsHigh ? value > rule.Limit : value < rule.Limit;
                bool backInside = rule.IsHigh ? value < rule.Limit - hysteresis : value > rule.Limit + hysteresis;

                if (beyond)
                {
                    if (!rule.ConditionSince.HasValue)
                        rule.ConditionSince = now;
                    if (!active && now - rule.ConditionSince.Value >= delay)
                    {
                        Raise(rule.Key, rule.Name.Replace(" above", " high").Replace(" below", " low"),
                            string.Format(CultureInfo.InvariantCulture, "{0:F3} {1} {2} {3:F3} {1}",
                                value, rule.Unit, rule.IsHigh ? ">" : "<", rule.Limit),
                            isThreshold: true);
                    }
                }
                else
                {
                    rule.ConditionSince = null;
                    if (active && backInside)
                        Clear(rule.Key, string.Format(CultureInfo.InvariantCulture, "{0:F3} {1}", value, rule.Unit));
                }
            }

            // Protection trips (latched in PowerSupplyService until TRIPRST).
            var e = status.EventStatus ?? new EventStatus();
            int[] trips = { e.OverVoltageTrip, e.OverCurrentTrip, e.OutputSenseTrip, e.FaultTrip };
            for (int k = 0; k < trips.Length; k++)
            {
                bool tripped = trips[k] == 1;
                if (tripped == _tripActive[k])
                    continue;
                _tripActive[k] = tripped;
                if (tripped && _settings.AlarmOnTrip)
                    Raise(TripNames[k], TripNames[k], "protection tripped, output switched off by the supply - use TRIP RESET", isThreshold: false);
                else if (!tripped && _active.ContainsKey(TripNames[k]))
                    Clear(TripNames[k], "trip reset");
            }
        }

        private void OnConnectionStateChanged(ConnectionState state)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
                return;
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (state == ConnectionState.Reconnecting && _settings.AlarmOnLinkLost && !_active.ContainsKey(LinkLostKey))
                    Raise(LinkLostKey, "Link lost", "no response from the supply - reconnecting", isThreshold: false);
                else if (state == ConnectionState.Connected || state == ConnectionState.Disconnected)
                {
                    if (_active.ContainsKey(LinkLostKey))
                        Clear(LinkLostKey, state == ConnectionState.Connected ? "reconnected" : "disconnected");
                    if (state == ConnectionState.Disconnected)
                        ClearAll("disconnected");
                }
            }));
        }

        #endregion

        #region Raise / clear

        private void Raise(string key, string name, string message, bool isThreshold)
        {
            var alarm = new ActiveAlarm(key, name, message, DateTime.Now);
            _active[key] = alarm;
            Unacknowledged.Add(alarm);
            RaiseBannerChanged();

            Log(name, ActivityRecord.ResultRaised, message);
            LoggingService.Instance.LogWarning($"Alarm: {name} - {message}", nameof(AlarmsViewModel), nameof(Raise));
            if (_settings.PlaySound)
                SystemSounds.Exclamation.Play();
            AlarmRaised?.Invoke();

            if (isThreshold && _settings.TurnOutputOff && _main.Status != null && _main.Status.OutputEnabled)
                SwitchOutputOff(name);
        }

        private async void SwitchOutputOff(string alarmName)
        {
            try
            {
                var record = await _powerSupply.SetOutputAsync(false, Source);
                record.Complete(detail: "alarm: " + alarmName);
                LoggingService.Instance.LogWarning(
                    record.Success ? $"Output switched OFF by alarm '{alarmName}'." : $"Alarm '{alarmName}' could not switch the output off.",
                    nameof(AlarmsViewModel), nameof(SwitchOutputOff));
            }
            catch (Exception ex)
            {
                LoggingService.Instance.LogException(ex, nameof(AlarmsViewModel), nameof(SwitchOutputOff));
            }
        }

        private void Clear(string key, string reason)
        {
            if (!_active.TryGetValue(key, out var alarm))
                return;
            _active.Remove(key);
            Unacknowledged.Remove(alarm);
            RaiseBannerChanged();
            Log(alarm.Name, ActivityRecord.ResultCleared, reason);
            LoggingService.Instance.LogInfo($"Alarm cleared: {alarm.Name} ({reason})", nameof(AlarmsViewModel), nameof(Clear));
        }

        private void ClearAll(string reason)
        {
            foreach (var key in _active.Keys.ToList())
                Clear(key, reason);
            foreach (var rule in Rules)
                rule.ConditionSince = null;
        }

        private void Acknowledge()
        {
            Unacknowledged.Clear();
            RaiseBannerChanged();
        }

        private void Log(string name, string result, string detail)
        {
            var record = new ActivityRecord(ActivityRecord.KindAlarm, name)
            {
                Source = Source,
                Result = result,
                Detail = detail
            };
            _database.EnqueueActivity(record);
        }

        private void RaiseBannerChanged()
        {
            RaisePropertyChanged(nameof(HasBanner));
            RaisePropertyChanged(nameof(BannerText));
            RaisePropertyChanged(nameof(BannerToolTip));
        }

        #endregion

        private void OnRuleChanged(object sender, PropertyChangedEventArgs e)
        {
            var rule = (AlarmRule)sender;
            if (e.PropertyName != nameof(AlarmRule.IsEnabled) && e.PropertyName != nameof(AlarmRule.Limit))
                return;

            switch (rule.Key)
            {
                case "VoltageHigh": _settings.VoltageHighEnabled = rule.IsEnabled; _settings.VoltageHigh = rule.Limit; break;
                case "VoltageLow": _settings.VoltageLowEnabled = rule.IsEnabled; _settings.VoltageLow = rule.Limit; break;
                case "CurrentHigh": _settings.CurrentHighEnabled = rule.IsEnabled; _settings.CurrentHigh = rule.Limit; break;
                case "CurrentLow": _settings.CurrentLowEnabled = rule.IsEnabled; _settings.CurrentLow = rule.Limit; break;
                case "PowerHigh": _settings.PowerHighEnabled = rule.IsEnabled; _settings.PowerHigh = rule.Limit; break;
            }
            Save();
        }

        private static void Save()
        {
            Settings.Current.Save();
        }
    }

    /// <summary>One threshold alarm (e.g. "Voltage above 13 V").</summary>
    public sealed class AlarmRule : ViewModelBase
    {
        private bool _isEnabled;
        private double _limit;

        public AlarmRule(string key, string name, string unit, bool isHigh, Func<PowerSupplyStatus, double> selector,
            bool isEnabled, double limit)
        {
            Key = key;
            Name = name;
            Unit = unit;
            IsHigh = isHigh;
            Selector = selector;
            _isEnabled = isEnabled;
            _limit = limit;
        }

        public string Key { get; }
        public string Name { get; }
        public string Unit { get; }
        public bool IsHigh { get; }
        public Func<PowerSupplyStatus, double> Selector { get; }

        /// <summary>When the value first went beyond the limit (for the delay).</summary>
        public DateTime? ConditionSince { get; set; }

        public bool IsEnabled
        {
            get => _isEnabled;
            set { if (SetProperty(ref _isEnabled, value)) ConditionSince = null; }
        }

        public double Limit
        {
            get => _limit;
            set { if (SetProperty(ref _limit, value)) ConditionSince = null; }
        }
    }

    public sealed class ActiveAlarm
    {
        public ActiveAlarm(string key, string name, string message, DateTime since)
        {
            Key = key;
            Name = name;
            Message = message;
            Since = since;
        }

        public string Key { get; }
        public string Name { get; }
        public string Message { get; }
        public DateTime Since { get; }
    }
}
