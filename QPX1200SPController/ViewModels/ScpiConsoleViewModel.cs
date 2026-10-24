using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Input;
using QPX1200SPController.Models;
using QPX1200SPController.Services;
using Settings = QPX1200SPController.Models.Settings;

namespace QPX1200SPController.ViewModels
{
    /// <summary>
    /// SCPI console: send raw commands to the supply and see the replies. Queries are always allowed;
    /// setting commands need the "Allow setting commands" switch. Everything sent is written to the
    /// CommandLog with source "SCPI console".
    /// </summary>
    public sealed class ScpiConsoleViewModel : ViewModelBase
    {
        public const string Source = "SCPI console";
        private const int MaxEntries = 1000;
        private const int MaxHistory = 100;

        private readonly PowerSupplyService _powerSupply;
        private readonly MainViewModel _main;
        private readonly List<string> _history = new List<string>();
        private int _historyIndex;
        private string _commandText = string.Empty;
        private bool _allowSetCommands;
        private bool _isBusy;

        public ScpiConsoleViewModel(PowerSupplyService powerSupply, MainViewModel main)
        {
            _powerSupply = powerSupply;
            _main = main;
            SendCommand = new RelayCommand(async _ => await SendAsync(CommandText), _ => !IsBusy && !string.IsNullOrWhiteSpace(CommandText));
            QuickQueryCommand = new RelayCommand(async p => await SendAsync(p as string), _ => !IsBusy);
            ClearCommand = new RelayCommand(_ => Entries.Clear());
        }

        public ObservableCollection<ConsoleEntry> Entries { get; } = new ObservableCollection<ConsoleEntry>();

        /// <summary>Common queries offered as buttons.</summary>
        public IReadOnlyList<string> QuickQueries { get; } = new[] { "*IDN?", "V1?", "I1?", "V1O?", "I1O?", "OP1?", "OVP1?", "OCP1?", "LSR1?", "EER?" };

        public string CommandText
        {
            get => _commandText;
            set => SetProperty(ref _commandText, value ?? string.Empty);
        }

        public bool AllowSetCommands
        {
            get => _allowSetCommands;
            set => SetProperty(ref _allowSetCommands, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetProperty(ref _isBusy, value))
                    CommandManager.InvalidateRequerySuggested();
            }
        }

        public ICommand SendCommand { get; }
        public ICommand QuickQueryCommand { get; }
        public ICommand ClearCommand { get; }

        /// <summary>Up/Down arrow in the input box: older (-1) or newer (+1) command from the history.</summary>
        public void BrowseHistory(int direction)
        {
            if (_history.Count == 0)
                return;
            _historyIndex = Math.Max(0, Math.Min(_history.Count, _historyIndex + direction));
            CommandText = _historyIndex < _history.Count ? _history[_historyIndex] : string.Empty;
        }

        private async Task SendAsync(string text)
        {
            string command = text?.Trim();
            if (string.IsNullOrEmpty(command))
                return;

            if (_history.Count == 0 || _history[_history.Count - 1] != command)
            {
                _history.Add(command);
                if (_history.Count > MaxHistory)
                    _history.RemoveAt(0);
            }
            _historyIndex = _history.Count;
            if (text == CommandText)
                CommandText = string.Empty;

            bool isQuery = PowerSupplyService.IsQuery(command);
            if (!_powerSupply.IsConnected)
            {
                Add(new ConsoleEntry(command, null, "Not connected.", null));
                return;
            }
            if (!isQuery && !AllowSetCommands)
            {
                Add(new ConsoleEntry(command, null, "Not sent: setting commands are locked. Switch on \"Allow setting commands\" first.", null));
                return;
            }
            if (!isQuery && _main.IsSequenceRunning)
            {
                Add(new ConsoleEntry(command, null, "Not sent: a sequence is running.", null));
                return;
            }
            string limitProblem = isQuery ? null : CheckSoftLimits(command);
            if (limitProblem != null)
            {
                Add(new ConsoleEntry(command, null, "Not sent: " + limitProblem, null));
                return;
            }

            IsBusy = true;
            try
            {
                var record = await _powerSupply.SendConsoleCommandAsync(command, Source);
                if (!isQuery)
                    record.Complete();
                Add(record.Result == ActivityRecord.ResultOk
                    ? new ConsoleEntry(command, isQuery ? record.Response : "(sent, no reply expected)", null, record.DurationMs)
                    : new ConsoleEntry(command, null, record.Detail ?? "failed", record.DurationMs));
            }
            catch (Exception ex)
            {
                Add(new ConsoleEntry(command, null, ex.Message, null));
                LoggingService.Instance.LogException(ex, nameof(ScpiConsoleViewModel), nameof(SendAsync));
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// The soft limits apply here too: "V1 x" and "I1 x" (also inside a ';' list) must be inside them.
        /// Returns a reason, or null when the command may be sent.
        /// </summary>
        private static string CheckSoftLimits(string command)
        {
            var settings = Settings.Current;
            var inv = CultureInfo.InvariantCulture;
            foreach (var part in command.Split(';'))
            {
                var words = part.Trim().Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length != 2)
                    continue;
                string header = words[0].ToUpperInvariant();
                if (header != "V1" && header != "I1")
                    continue;
                if (!double.TryParse(words[1].Trim(), NumberStyles.Float, inv, out double value))
                    return $"'{part.Trim()}' is not a number with '.' as decimal separator.";
                if (header == "V1" && (value < settings.MinVoltage || value > settings.MaxVoltage))
                    return string.Format(inv, "{0:F3} V is outside the soft limits ({1:F2} - {2:F2} V).", value, settings.MinVoltage, settings.MaxVoltage);
                if (header == "I1" && (value < settings.MinCurrent || value > settings.MaxCurrent))
                    return string.Format(inv, "{0:F3} A is outside the soft limits ({1:F2} - {2:F2} A).", value, settings.MinCurrent, settings.MaxCurrent);
            }
            return null;
        }

        private void Add(ConsoleEntry entry)
        {
            Entries.Add(entry);
            while (Entries.Count > MaxEntries)
                Entries.RemoveAt(0);
            EntryAdded?.Invoke(entry);
        }

        /// <summary>The view scrolls the new entry into view.</summary>
        public event Action<ConsoleEntry> EntryAdded;
    }

    public sealed class ConsoleEntry
    {
        public ConsoleEntry(string command, string response, string error, double? durationMs)
        {
            Time = DateTime.Now;
            Command = command;
            Response = response;
            Error = error;
            DurationText = durationMs.HasValue ? durationMs.Value.ToString("F0", CultureInfo.InvariantCulture) + " ms" : string.Empty;
        }

        public DateTime Time { get; }
        public string Command { get; }
        public string Response { get; }
        public string Error { get; }
        public bool IsError => Error != null;
        public string DurationText { get; }
    }
}
