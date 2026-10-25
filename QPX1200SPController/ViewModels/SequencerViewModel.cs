using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using QPX1200SPController.Helpers;
using QPX1200SPController.Models;
using QPX1200SPController.Services;
using Settings = QPX1200SPController.Models.Settings;

namespace QPX1200SPController.ViewModels
{
    /// <summary>
    /// Runs a list of voltage/current steps (each held for a duration, optionally ramping the voltage
    /// from the previous value), repeated for a number of cycles. Every command goes through the normal
    /// command log with source "Sequencer". The run stops when a command fails, the link drops or the
    /// output is switched off by anything else (operator, alarm, trip).
    /// </summary>
    public sealed class SequencerViewModel : ViewModelBase
    {
        public const string Source = "Sequencer";
        private const double RampIntervalSeconds = 1.0;
        private const double MaxStepSeconds = 7 * 24 * 3600;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly PowerSupplyService _powerSupply;
        private readonly MainViewModel _main;
        private readonly Settings _settings = Settings.Current;
        private CancellationTokenSource _cts;
        private SequenceStep _selectedStep;
        private bool _isRunning;
        private bool _isPaused;
        private double _progress;
        private string _statusText = "Edit the steps, then press START.";
        private bool _loading;
        private bool _stoppingForExit;

        public SequencerViewModel(PowerSupplyService powerSupply, MainViewModel main)
        {
            _powerSupply = powerSupply;
            _main = main;

            _loading = true;
            var saved = _settings.SequenceSteps.Count > 0
                ? _settings.SequenceSteps
                : new List<SequenceStepData>
                {
                    new SequenceStepData { Voltage = 5, Current = 1, DurationSeconds = 10 },
                    new SequenceStepData { Voltage = 12, Current = 1, DurationSeconds = 30, Ramp = true },
                    new SequenceStepData { Voltage = 12, Current = 1, DurationSeconds = 20 }
                };
            foreach (var data in saved)
                AddStep(SequenceStep.From(data), Steps.Count);
            _loading = false;
            Steps.CollectionChanged += OnStepsChanged;
            Recalculate();

            AddCommand = new RelayCommand(_ => AddAfterSelected(), _ => !IsRunning);
            RemoveCommand = new RelayCommand(_ => Steps.Remove(SelectedStep), _ => !IsRunning && SelectedStep != null);
            MoveUpCommand = new RelayCommand(_ => Move(-1), _ => !IsRunning && SelectedStep != null && Steps.IndexOf(SelectedStep) > 0);
            MoveDownCommand = new RelayCommand(_ => Move(+1), _ => !IsRunning && SelectedStep != null && Steps.IndexOf(SelectedStep) < Steps.Count - 1);
            StartCommand = new RelayCommand(async _ => await RunAsync(), _ => !IsRunning && Steps.Count > 0 && _powerSupply.IsConnected);
            PauseCommand = new RelayCommand(_ => IsPaused = !IsPaused, _ => IsRunning);
            StopCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsRunning);
            SaveFileCommand = new RelayCommand(_ => SaveToFile(), _ => Steps.Count > 0);
            LoadFileCommand = new RelayCommand(_ => LoadFromFile(), _ => !IsRunning);
            // On exit the exit dialog decides about the output, not the sequence settings.
            _main.StopSequenceRequested += () => { _stoppingForExit = true; _cts?.Cancel(); };
        }

        public ObservableCollection<SequenceStep> Steps { get; } = new ObservableCollection<SequenceStep>();

        public SequenceStep SelectedStep
        {
            get => _selectedStep;
            set => SetProperty(ref _selectedStep, value);
        }

        public int Cycles
        {
            get => _settings.SequenceCycles;
            set
            {
                value = Math.Max(1, value);
                if (_settings.SequenceCycles == value)
                    return;
                _settings.SequenceCycles = value;
                _settings.Save();
                RaisePropertyChanged();
                Recalculate();
            }
        }

        public bool OutputOnAtStart
        {
            get => _settings.SequenceOutputOnAtStart;
            set { if (_settings.SequenceOutputOnAtStart != value) { _settings.SequenceOutputOnAtStart = value; _settings.Save(); RaisePropertyChanged(); } }
        }

        public bool OutputOffAtEnd
        {
            get => _settings.SequenceOutputOffAtEnd;
            set { if (_settings.SequenceOutputOffAtEnd != value) { _settings.SequenceOutputOffAtEnd = value; _settings.Save(); RaisePropertyChanged(); } }
        }

        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (SetProperty(ref _isRunning, value))
                {
                    RaisePropertyChanged(nameof(IsEditable));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool IsEditable => !IsRunning;

        public bool IsPaused
        {
            get => _isPaused;
            set
            {
                if (SetProperty(ref _isPaused, value))
                    RaisePropertyChanged(nameof(PauseButtonText));
            }
        }

        public string PauseButtonText => IsPaused ? "RESUME" : "PAUSE";

        /// <summary>0..100 over the whole run.</summary>
        public double Progress
        {
            get => _progress;
            private set => SetProperty(ref _progress, value);
        }

        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        public string TotalDurationText { get; private set; }

        public ICommand AddCommand { get; }
        public ICommand RemoveCommand { get; }
        public ICommand MoveUpCommand { get; }
        public ICommand MoveDownCommand { get; }
        public ICommand StartCommand { get; }
        public ICommand PauseCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand SaveFileCommand { get; }
        public ICommand LoadFileCommand { get; }

        #region Editing

        private void AddStep(SequenceStep step, int index)
        {
            step.PropertyChanged += OnStepPropertyChanged;
            Steps.Insert(index, step);
        }

        private void AddAfterSelected()
        {
            var template = SelectedStep ?? Steps.LastOrDefault();
            var step = template != null
                ? new SequenceStep { Voltage = template.Voltage, Current = template.Current, DurationSeconds = template.DurationSeconds }
                : new SequenceStep { Voltage = Math.Max(_settings.MinVoltage, 5), Current = Math.Max(_settings.MinCurrent, 1), DurationSeconds = 10 };
            int index = SelectedStep != null ? Steps.IndexOf(SelectedStep) + 1 : Steps.Count;
            AddStep(step, index);
            SelectedStep = step;
        }

        private void Move(int direction)
        {
            var step = SelectedStep;
            int index = Steps.IndexOf(step);
            Steps.Move(index, index + direction);
            SelectedStep = step;
        }

        private void OnStepsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null && e.Action == NotifyCollectionChangedAction.Remove)
                foreach (SequenceStep step in e.OldItems)
                    step.PropertyChanged -= OnStepPropertyChanged;
            Persist();
            Recalculate();
        }

        private void OnStepPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SequenceStep.IsActive) || e.PropertyName == nameof(SequenceStep.Number)
                || e.PropertyName == nameof(SequenceStep.StartsAtText))
                return;
            Persist();
            Recalculate();
        }

        private void Persist()
        {
            if (_loading)
                return;
            _settings.SequenceSteps = Steps.Select(s => s.ToData()).ToList();
            _settings.Save();
        }

        private void Recalculate()
        {
            double t = 0;
            for (int k = 0; k < Steps.Count; k++)
            {
                Steps[k].Number = k + 1;
                Steps[k].StartsAtText = FormatDuration(t);
                t += Math.Max(0, Steps[k].DurationSeconds);
            }
            TotalDurationText = Steps.Count == 0
                ? "No steps"
                : $"{Steps.Count} steps, {FormatDuration(t)} per cycle" + (Cycles > 1 ? $", {FormatDuration(t * Cycles)} in total" : "");
            RaisePropertyChanged(nameof(TotalDurationText));
        }

        #endregion

        #region Running

        private async Task RunAsync()
        {
            var problems = Validate();
            if (problems.Count > 0)
            {
                await Dialogs.InfoAsync("The sequence cannot start", "Fix these first:", isError: true,
                    details: string.Join("\n", problems));
                return;
            }

            var steps = Steps.ToList();
            int cycles = Cycles;
            double cycleSeconds = steps.Sum(s => s.DurationSeconds);
            double totalSeconds = cycleSeconds * cycles;

            var details = new StringBuilder();
            details.AppendLine(TotalDurationText + ".");
            details.AppendLine(OutputOnAtStart ? "Output is switched ON at the start." : "Output state is not changed at the start.");
            details.Append(OutputOffAtEnd ? "Output is switched OFF at the end or when stopped." : "Output is left as it is at the end.");
            foreach (var warning in Warnings())
                details.Append("\n" + warning);
            var answer = await Dialogs.ConfirmAsync("Start sequence?", "The sequencer will take control of the voltage and current set-points.",
                "START", MaterialDesignThemes.Wpf.PackIconKind.PlaylistPlay, details.ToString(), DialogTone.Go);
            if (!answer.Confirmed)
                return;

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            IsPaused = false;
            IsRunning = true;
            _main.IsSequenceRunning = true;
            Progress = 0;
            string endReason = "completed";
            bool failed = false;
            LoggingService.Instance.LogInfo($"Sequence started: {TotalDurationText}.", nameof(SequencerViewModel), nameof(RunAsync));

            bool outputSeenOn = false;
            try
            {
                var present = await _powerSupply.GetPowerSupplySettingsAsync();
                double lastVoltage = present?.SetVoltage ?? steps[0].Voltage;

                // Program the first step before switching on, so the output starts at the right level.
                await SendAsync(_powerSupply.SetCurrentAsync(steps[0].Current, Source));
                if (!steps[0].Ramp)
                {
                    await SendAsync(_powerSupply.SetVoltageAsync(steps[0].Voltage, Source));
                    lastVoltage = steps[0].Voltage;
                }
                if (OutputOnAtStart && !(_main.Status?.OutputEnabled ?? false))
                    await SendAsync(_powerSupply.SetOutputAsync(true, Source));

                double doneSeconds = 0;
                for (int cycle = 1; cycle <= cycles; cycle++)
                {
                    for (int index = 0; index < steps.Count; index++)
                    {
                        var step = steps[index];
                        step.IsActive = true;
                        try
                        {
                            if (cycle > 1 || index > 0)
                            {
                                await SendAsync(_powerSupply.SetCurrentAsync(step.Current, Source));
                                if (!step.Ramp)
                                {
                                    await SendAsync(_powerSupply.SetVoltageAsync(step.Voltage, Source));
                                    lastVoltage = step.Voltage;
                                }
                            }

                            double rampFrom = lastVoltage;
                            double lastSent = lastVoltage;
                            double nextRampAt = 0;
                            var clock = Stopwatch.StartNew();
                            while (clock.Elapsed.TotalSeconds < step.DurationSeconds)
                            {
                                await Task.Delay(100, token);

                                if (!_powerSupply.IsConnected)
                                    throw new SequenceAbortException("the link to the supply was lost");
                                bool outputOn = _main.Status?.OutputEnabled ?? false;
                                if (outputOn)
                                    outputSeenOn = true;
                                else if (outputSeenOn)
                                    throw new SequenceAbortException("the output was switched off");

                                if (IsPaused)
                                {
                                    clock.Stop();
                                    UpdateProgress(cycle, cycles, index, steps.Count, doneSeconds + clock.Elapsed.TotalSeconds, totalSeconds, paused: true);
                                    continue;
                                }
                                clock.Start();

                                double elapsed = clock.Elapsed.TotalSeconds;
                                if (step.Ramp && elapsed >= nextRampAt)
                                {
                                    double v = rampFrom + (step.Voltage - rampFrom) * Math.Min(1, elapsed / step.DurationSeconds);
                                    if (Math.Abs(v - lastSent) >= 0.0005)
                                    {
                                        await SendAsync(_powerSupply.SetVoltageAsync(v, Source));
                                        lastSent = v;
                                    }
                                    nextRampAt += RampIntervalSeconds;
                                }
                                UpdateProgress(cycle, cycles, index, steps.Count, doneSeconds + elapsed, totalSeconds, paused: false);
                            }

                            if (step.Ramp && Math.Abs(step.Voltage - lastSent) >= 0.0005)
                                await SendAsync(_powerSupply.SetVoltageAsync(step.Voltage, Source));
                            lastVoltage = step.Voltage;
                            doneSeconds += step.DurationSeconds;
                        }
                        finally
                        {
                            step.IsActive = false;
                        }
                    }
                }
                Progress = 100;
            }
            catch (OperationCanceledException)
            {
                endReason = "stopped by the operator";
            }
            catch (SequenceAbortException ex)
            {
                endReason = "aborted: " + ex.Message;
                failed = true;
            }
            catch (Exception ex)
            {
                endReason = "aborted: " + ex.Message;
                failed = true;
                LoggingService.Instance.LogException(ex, nameof(SequencerViewModel), nameof(RunAsync));
            }
            finally
            {
                if (OutputOffAtEnd && !_stoppingForExit && _powerSupply.IsConnected)
                {
                    var record = await _powerSupply.SetOutputAsync(false, Source);
                    record.Complete(detail: "sequence end");
                }

                IsPaused = false;
                IsRunning = false;
                _main.IsSequenceRunning = false;
                _main.SequenceStatusText = null;
                _cts.Dispose();
                _cts = null;
            }

            StatusText = $"Sequence {endReason} at {DateTime.Now.ToString("HH:mm:ss", Inv)}" +
                         (OutputOffAtEnd ? " - output switched OFF." : ".");
            if (failed)
                LoggingService.Instance.LogWarning("Sequence " + endReason, nameof(SequencerViewModel), nameof(RunAsync));
            else
                LoggingService.Instance.LogInfo("Sequence " + endReason + ".", nameof(SequencerViewModel), nameof(RunAsync));
        }

        private static async Task SendAsync(Task<ActivityRecord> command)
        {
            var record = await command;
            record.Complete();
            if (!record.Success)
                throw new SequenceAbortException($"'{record.Command}' failed ({record.Detail ?? "no detail"})");
        }

        private void UpdateProgress(int cycle, int cycles, int index, int count, double done, double total, bool paused)
        {
            Progress = total > 0 ? Math.Min(100, done / total * 100) : 0;
            string where = $"step {index + 1}/{count}" + (cycles > 1 ? $", cycle {cycle}/{cycles}" : "");
            string left = FormatDuration(Math.Ceiling(Math.Max(0, total - done))) + " left";
            StatusText = (paused ? "Paused at " : "Running ") + where + " - " + left;
            _main.SequenceStatusText = "Sequence " + (paused ? "paused, " : "") + where + ", " + left;
        }

        private List<string> Validate()
        {
            var problems = new List<string>();
            if (!_powerSupply.IsConnected)
                problems.Add("Not connected to the supply.");
            if (Steps.Count == 0)
                problems.Add("There are no steps.");

            foreach (var s in Steps)
            {
                if (s.Voltage < _settings.MinVoltage || s.Voltage > _settings.MaxVoltage)
                    problems.Add(string.Format(Inv, "Step {0}: {1:F3} V is outside the soft limits ({2:F2} - {3:F2} V).", s.Number, s.Voltage, _settings.MinVoltage, _settings.MaxVoltage));
                if (s.Current < _settings.MinCurrent || s.Current > _settings.MaxCurrent)
                    problems.Add(string.Format(Inv, "Step {0}: {1:F3} A is outside the soft limits ({2:F2} - {3:F2} A).", s.Number, s.Current, _settings.MinCurrent, _settings.MaxCurrent));
                if (s.DurationSeconds < 0.5 || s.DurationSeconds > MaxStepSeconds)
                    problems.Add($"Step {s.Number}: the duration must be between 0.5 s and 7 days.");
                var ps = _main.PowerSupplySettingStatus;
                if (ps != null && s.Voltage >= ps.SetOvpLevel)
                    problems.Add(string.Format(Inv, "Step {0}: {1:F3} V would trip the over-voltage protection (OVP {2:F2} V).", s.Number, s.Voltage, ps.SetOvpLevel));
            }
            return problems;
        }

        private IEnumerable<string> Warnings()
        {
            var ps = _main.PowerSupplySettingStatus;
            if (ps == null)
                yield break;
            foreach (var s in Steps.Where(s => s.Current > ps.SetOcpLevel))
                yield return string.Format(Inv, "Note: step {0} current {1:F3} A is above OCP {2:F2} A.", s.Number, s.Current, ps.SetOcpLevel);
        }

        #endregion

        #region Files

        private void SaveToFile()
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Sequence (*.csv)|*.csv",
                DefaultExt = "csv",
                FileName = "Sequence.csv"
            };
            if (dialog.ShowDialog() != true)
                return;

            try
            {
                var sb = new StringBuilder("Voltage,Current,DurationSeconds,Ramp\r\n");
                foreach (var s in Steps)
                    sb.AppendFormat(Inv, "{0:0.###},{1:0.###},{2:0.###},{3}\r\n", s.Voltage, s.Current, s.DurationSeconds, s.Ramp ? 1 : 0);
                File.WriteAllText(dialog.FileName, sb.ToString());
                StatusText = "Sequence saved to " + dialog.FileName;
            }
            catch (Exception ex)
            {
                StatusText = "Saving failed: " + ex.Message;
                LoggingService.Instance.LogException(ex, nameof(SequencerViewModel), nameof(SaveToFile));
            }
        }

        private void LoadFromFile()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Sequence (*.csv)|*.csv|All files (*.*)|*.*" };
            if (dialog.ShowDialog() != true)
                return;

            try
            {
                var loaded = new List<SequenceStep>();
                int lineNumber = 0;
                foreach (var raw in File.ReadAllLines(dialog.FileName))
                {
                    lineNumber++;
                    string line = raw.Trim();
                    if (line.Length == 0 || char.IsLetter(line[0]))
                        continue; // header or blank
                    var f = line.Split(',', ';', '\t');
                    if (f.Length < 3
                        || !double.TryParse(f[0], NumberStyles.Float, Inv, out double v)
                        || !double.TryParse(f[1], NumberStyles.Float, Inv, out double i)
                        || !double.TryParse(f[2], NumberStyles.Float, Inv, out double d))
                        throw new FormatException($"line {lineNumber} is not 'Voltage,Current,DurationSeconds[,Ramp]' with '.' decimals");
                    bool ramp = f.Length > 3 && (f[3].Trim() == "1" || f[3].Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
                    loaded.Add(new SequenceStep { Voltage = v, Current = i, DurationSeconds = d, Ramp = ramp });
                }
                if (loaded.Count == 0)
                    throw new FormatException("the file has no steps");

                _loading = true;
                Steps.Clear();
                foreach (var step in loaded)
                    AddStep(step, Steps.Count);
                _loading = false;
                Persist();
                Recalculate();
                StatusText = $"Loaded {loaded.Count} steps from {dialog.FileName}";
            }
            catch (Exception ex)
            {
                _loading = false;
                StatusText = "Loading failed: " + ex.Message;
                LoggingService.Instance.LogException(ex, nameof(SequencerViewModel), nameof(LoadFromFile));
            }
        }

        #endregion

        private static string FormatDuration(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Round(seconds, 1));
            return t.TotalHours >= 24
                ? $"{(int)t.TotalDays}d {t.ToString("hh\\:mm\\:ss", Inv)}"
                : t.ToString(t.Milliseconds != 0 ? "hh\\:mm\\:ss\\.f" : "hh\\:mm\\:ss", Inv);
        }

        private sealed class SequenceAbortException : Exception
        {
            public SequenceAbortException(string message) : base(message) { }
        }
    }

    public sealed class SequenceStep : ViewModelBase
    {
        private double _voltage;
        private double _current;
        private double _durationSeconds;
        private bool _ramp;
        private bool _isActive;
        private int _number;
        private string _startsAtText;

        public double Voltage { get => _voltage; set => SetProperty(ref _voltage, value); }
        public double Current { get => _current; set => SetProperty(ref _current, value); }
        public double DurationSeconds { get => _durationSeconds; set => SetProperty(ref _durationSeconds, value); }

        /// <summary>Ramp the voltage from the previous step's value over this step instead of jumping.</summary>
        public bool Ramp { get => _ramp; set => SetProperty(ref _ramp, value); }

        /// <summary>True while this step is being executed.</summary>
        public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }

        public int Number { get => _number; set => SetProperty(ref _number, value); }
        public string StartsAtText { get => _startsAtText; set => SetProperty(ref _startsAtText, value); }

        public static SequenceStep From(SequenceStepData d)
        {
            return new SequenceStep { Voltage = d.Voltage, Current = d.Current, DurationSeconds = d.DurationSeconds, Ramp = d.Ramp };
        }

        public SequenceStepData ToData()
        {
            return new SequenceStepData { Voltage = Voltage, Current = Current, DurationSeconds = DurationSeconds, Ramp = Ramp };
        }
    }
}
