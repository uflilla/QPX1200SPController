using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using QPX1200SPController.Models;
using QPX1200SPController.Services;

namespace QPX1200SPController.ViewModels
{
    /// <summary>
    /// The three remote commands (set, output and read), shared by the JSON and the CCSDS protocol.
    /// Each runs on the UI thread through the same view model methods as the buttons, so it gets the
    /// same checks and logging; only "Enable configuration" and the confirmation dialog are left out
    /// (switching Remote control on stands in for them). Refusals throw <see cref="RemoteException"/>
    /// with an error code (CCSDS) and a message (JSON).
    /// </summary>
    public sealed class RemoteCommandHandler : IRemoteCommands
    {
        private const string Source = "Remote";
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly MainViewModel _main;

        public RemoteCommandHandler(MainViewModel main)
        {
            _main = main;
        }

        public Task<SetResult> SetAsync(SetRequest request) => OnUiThread(() => SetCoreAsync(request));
        public Task<OutputResult> OutputAsync(bool on) => OnUiThread(() => OutputCoreAsync(on));
        public Task<RemoteStatus> ReadAsync() => OnUiThread(ReadCoreAsync);

        /// <summary>Called by the protocols on their own threads.</summary>
        private static Task<T> OnUiThread<T>(Func<Task<T>> action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
            {
                var closing = new TaskCompletionSource<T>();
                closing.SetException(new RemoteException(RemoteError.AppClosing, "The app is closing"));
                return closing.Task;
            }
            return dispatcher.InvokeAsync(action).Task.Unwrap();
        }

        #region Commands

        private async Task<SetResult> SetCoreAsync(SetRequest request)
        {
            // Checked before anything is sent; sent in this order (protection first). Voltage and
            // current use the operator's soft limits, like the SET buttons.
            var settings = new List<PendingSetting>();
            Add(settings, request.Ovp, RemoteSetting.Ovp, OutputSetting.Ovp, "ovp", _main.MinOvpToSet, _main.MaxOvpToSet, null, RemoteError.OvpRange);
            Add(settings, request.Ocp, RemoteSetting.Ocp, OutputSetting.Ocp, "ocp", _main.MinOcpToSet, _main.MaxOcpToSet, null, RemoteError.OcpRange);
            Add(settings, request.Current, RemoteSetting.Current, OutputSetting.Current, "current", _main.MinCurrentToSet, _main.MaxCurrentToSet, "soft limits in the app", RemoteError.CurrentRange);
            Add(settings, request.Voltage, RemoteSetting.Voltage, OutputSetting.Voltage, "voltage", _main.MinVoltageToSet, _main.MaxVoltageToSet, "soft limits in the app", RemoteError.VoltageRange);
            if (settings.Count == 0)
                throw new RemoteException(RemoteError.NoValue, "Give at least one of voltage, current, ovp, ocp");
            CheckConnected();
            CheckNoSequence();

            var result = new SetResult();
            foreach (var setting in settings)
            {
                if (result.Error != RemoteError.None)
                {
                    // Stopped at the first value that was not OK.
                    result.Results.Add(new SettingResult { Setting = setting.Setting, Name = setting.Name, Requested = setting.Value, Result = RemoteResult.NotSent });
                    continue;
                }

                var record = await _main.SendSettingAsync(setting.OutputSetting, setting.Value, Source);
                var outcome = ResultOf(record.Result);
                result.Results.Add(new SettingResult
                {
                    Setting = setting.Setting,
                    Name = setting.Name,
                    Requested = record.Requested,
                    Readback = record.Readback,
                    Result = outcome
                });
                if (outcome != RemoteResult.Ok)
                {
                    result.Error = outcome == RemoteResult.Mismatch ? RemoteError.Mismatch : RemoteError.Failed;
                    result.Message = $"{setting.Name}: {record.Detail ?? record.Result}";
                }
            }
            return result;
        }

        private async Task<OutputResult> OutputCoreAsync(bool on)
        {
            ActivityRecord record;
            if (on)
            {
                CheckConnected();
                CheckNoSequence();
                var trip = RemoteStatus.TripOf(_main.Status?.EventStatus);
                if (trip != RemoteTrip.None)
                    throw new RemoteException(RemoteError.Tripped,
                        $"The output is tripped ({RemoteStatus.TripName(trip)}): remove the cause and press TRIP RESET in the app");
                record = await _main.TurnOnAsync(Source);
            }
            else
            {
                // Turning off is always allowed while connected, as in the window (also during a sequence).
                CheckConnected();
                record = await _main.TurnOffAsync(Source);
            }

            var outcome = ResultOf(record.Result);
            var result = new OutputResult
            {
                On = record.Readback.HasValue ? record.Readback == 1 : (bool?)null,
                Result = outcome
            };
            if (outcome != RemoteResult.Ok)
            {
                if (on && outcome == RemoteResult.Mismatch)
                {
                    result.Error = RemoteError.OutputKeptOff;
                    result.Message = "The supply kept the output off (tripped?)";
                }
                else
                {
                    result.Error = outcome == RemoteResult.Mismatch ? RemoteError.Mismatch : RemoteError.Failed;
                    result.Message = record.Detail ?? record.Result;
                }
            }
            return result;
        }

        private async Task<RemoteStatus> ReadCoreAsync()
        {
            CheckConnected();

            var status = await _main.ReadNowAsync();
            var settings = status != null ? await _main.ReadSettingsNowAsync() : null;
            if (status == null || settings == null)
                throw new RemoteException(RemoteError.NoReply, "No valid reply from the supply");

            return RemoteStatus.From(status, settings, _main.IsSequenceRunning);
        }

        private static RemoteResult ResultOf(string result)
        {
            if (result == ActivityRecord.ResultOk)
                return RemoteResult.Ok;
            return result == ActivityRecord.ResultMismatch ? RemoteResult.Mismatch : RemoteResult.Failed;
        }

        #endregion

        #region Request checks

        private void CheckConnected()
        {
            if (!_main.IsConnected)
                throw new RemoteException(RemoteError.NotConnected, "Not connected to the supply");
        }

        /// <summary>The sequencer owns the set-points while it runs; read and turning off stay allowed.</summary>
        private void CheckNoSequence()
        {
            if (_main.IsSequenceRunning)
                throw new RemoteException(RemoteError.SequenceRunning, "A sequence is running in the app");
        }

        private static void Add(List<PendingSetting> settings, double? value, RemoteSetting setting, OutputSetting outputSetting,
            string name, double min, double max, string rangeNote, RemoteError rangeError)
        {
            if (!value.HasValue)
                return;
            if (double.IsNaN(value.Value) || double.IsInfinity(value.Value))
                throw new RemoteException(RemoteError.InvalidParameter, $"\"{name}\" must be a number");
            if (value < min || value > max)
                throw new RemoteException(rangeError, string.Format(Inv, "\"{0}\" must be {1}–{2}{3}", name, min, max,
                    rangeNote == null ? "" : " (" + rangeNote + ")"));
            settings.Add(new PendingSetting { Setting = setting, OutputSetting = outputSetting, Name = name, Value = value.Value });
        }

        private sealed class PendingSetting
        {
            public RemoteSetting Setting;
            public OutputSetting OutputSetting;
            public string Name;
            public double Value;
        }

        #endregion
    }
}
