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
    /// The three remote commands (docs/REMOTE_PROTOCOL.md): set, output and read. Each runs on the UI
    /// thread through the same view model methods as the buttons, so it gets the same checks and
    /// logging; only "Enable configuration" and the confirmation dialog are left out (switching
    /// Remote control on stands in for them). Copied from CPX400DP and adapted to one output.
    /// </summary>
    public sealed class RemoteCommandHandler
    {
        private const string Source = "Remote";
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly MainViewModel _main;

        public RemoteCommandHandler(MainViewModel main)
        {
            _main = main;
        }

        /// <summary>Called by <see cref="RemoteServer"/> on its own thread.</summary>
        public Task<Dictionary<string, object>> HandleAsync(Dictionary<string, object> request)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
                return Task.FromResult(RemoteServer.Error("The app is closing"));
            return dispatcher.InvokeAsync(() => RunAsync(request)).Task.Unwrap();
        }

        private async Task<Dictionary<string, object>> RunAsync(Dictionary<string, object> request)
        {
            try
            {
                string cmd = request.TryGetValue("cmd", out object value) ? value as string : null;
                switch (cmd?.ToLowerInvariant())
                {
                    case "set": return await SetAsync(request);
                    case "output": return await OutputAsync(request);
                    case "read": return await ReadAsync(request);
                    case null: return RemoteServer.Error("Missing \"cmd\": use set, output or read");
                    default: return RemoteServer.Error($"Unknown command '{cmd}': use set, output or read");
                }
            }
            catch (RequestException ex)
            {
                return RemoteServer.Error(ex.Message);
            }
        }

        #region Commands

        private async Task<Dictionary<string, object>> SetAsync(Dictionary<string, object> request)
        {
            CheckOutput(request);

            // Checked before anything is sent; sent in this order (protection first). Voltage and
            // current use the operator's soft limits, like the SET buttons.
            var settings = new List<Tuple<string, OutputSetting, double>>();
            AddSetting(settings, request, "ovp", OutputSetting.Ovp, _main.MinOvpToSet, _main.MaxOvpToSet, null);
            AddSetting(settings, request, "ocp", OutputSetting.Ocp, _main.MinOcpToSet, _main.MaxOcpToSet, null);
            AddSetting(settings, request, "current", OutputSetting.Current, _main.MinCurrentToSet, _main.MaxCurrentToSet, "soft limits in the app");
            AddSetting(settings, request, "voltage", OutputSetting.Voltage, _main.MinVoltageToSet, _main.MaxVoltageToSet, "soft limits in the app");
            if (settings.Count == 0)
                throw new RequestException("Give at least one of voltage, current, ovp, ocp");
            CheckConnected();
            CheckNoSequence();

            var reply = new Dictionary<string, object> { ["ok"] = true };
            foreach (var setting in settings)
            {
                var record = await _main.SendSettingAsync(setting.Item2, setting.Item3, Source);
                reply[setting.Item1] = new Dictionary<string, object>
                {
                    ["requested"] = record.Requested,
                    ["readback"] = record.Readback,
                    ["result"] = record.Result
                };
                if (record.Result != ActivityRecord.ResultOk)
                {
                    reply["ok"] = false;
                    reply["error"] = $"{setting.Item1}: {record.Detail ?? record.Result}";
                    break;
                }
            }
            return reply;
        }

        private async Task<Dictionary<string, object>> OutputAsync(Dictionary<string, object> request)
        {
            CheckOutput(request);
            bool on = GetBool(request, "on");

            ActivityRecord record;
            if (on)
            {
                CheckConnected();
                CheckNoSequence();
                string trip = TripOf(_main.Status?.EventStatus);
                if (trip != null)
                    throw new RequestException($"The output is tripped ({trip}): remove the cause and press TRIP RESET in the app");
                record = await _main.TurnOnAsync(Source);
            }
            else
            {
                // Turning off is always allowed while connected, as in the window (also during a sequence).
                CheckConnected();
                record = await _main.TurnOffAsync(Source);
            }

            bool ok = record.Result == ActivityRecord.ResultOk;
            var reply = new Dictionary<string, object>
            {
                ["ok"] = ok,
                ["on"] = record.Readback.HasValue ? record.Readback == 1 : (bool?)null,
                ["result"] = record.Result
            };
            if (!ok)
            {
                reply["error"] = on && record.Result == ActivityRecord.ResultMismatch
                    ? "The supply kept the output off (tripped?)"
                    : record.Detail ?? record.Result;
            }
            return reply;
        }

        private async Task<Dictionary<string, object>> ReadAsync(Dictionary<string, object> request)
        {
            CheckOutput(request);
            CheckConnected();

            var status = await _main.ReadNowAsync();
            var settings = status != null ? await _main.ReadSettingsNowAsync() : null;
            if (status == null || settings == null)
                throw new RequestException("No valid reply from the supply");

            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["time"] = status.Timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", Inv),
                ["output"] = 1,
                ["on"] = status.OutputEnabled,
                ["voltage"] = Math.Round(status.Voltage, 3),
                ["current"] = Math.Round(status.Current, 3),
                ["mode"] = ModeOf(status),
                ["trip"] = TripOf(status.EventStatus),
                ["set_voltage"] = Math.Round(settings.SetVoltage, 3),
                ["set_current"] = Math.Round(settings.SetCurrent, 3),
                ["ovp"] = Math.Round(settings.SetOvpLevel, 2),
                ["ocp"] = Math.Round(settings.SetOcpLevel, 2)
            };
        }

        private static string ModeOf(PowerSupplyStatus status)
        {
            var e = status.EventStatus;
            if (!status.OutputEnabled)
                return "OFF";
            if (e != null && e.UnregulatedMode == 1)
                return "UNREG";
            return e != null && e.ConstantCurrentMode == 1 ? "CC" : "CV";
        }

        /// <summary>The first latched trip (the app keeps them until TRIP RESET), or null.</summary>
        private static string TripOf(EventStatus e)
        {
            if (e == null)
                return null;
            if (e.OverVoltageTrip == 1)
                return "OVP";
            if (e.OverCurrentTrip == 1)
                return "OCP";
            if (e.OutputSenseTrip == 1)
                return "SENSE";
            return e.FaultTrip == 1 ? "FAULT" : null;
        }

        #endregion

        #region Request checks

        private void CheckConnected()
        {
            if (!_main.IsConnected)
                throw new RequestException("Not connected to the supply");
        }

        /// <summary>The sequencer owns the set-points while it runs; read and turning off stay allowed.</summary>
        private void CheckNoSequence()
        {
            if (_main.IsSequenceRunning)
                throw new RequestException("A sequence is running in the app");
        }

        /// <summary>"output" may be left out; if given it must be 1 (same messages as the CPX400DP's).</summary>
        private static void CheckOutput(Dictionary<string, object> request)
        {
            double? value;
            try
            {
                value = GetNumber(request, "output");
            }
            catch (RequestException)
            {
                value = 0;
            }
            if (value.HasValue && value != 1)
                throw new RequestException("\"output\" must be 1 (the QPX1200SP has one output)");
        }

        private static void AddSetting(List<Tuple<string, OutputSetting, double>> settings, Dictionary<string, object> request,
            string name, OutputSetting setting, double min, double max, string rangeNote)
        {
            double? value = GetNumber(request, name);
            if (!value.HasValue)
                return;
            if (value < min || value > max)
                throw new RequestException(string.Format(Inv, "\"{0}\" must be {1}–{2}{3}", name, min, max,
                    rangeNote == null ? "" : " (" + rangeNote + ")"));
            settings.Add(Tuple.Create(name, setting, value.Value));
        }

        /// <summary>The named number, or null if the request does not have it.</summary>
        private static double? GetNumber(Dictionary<string, object> request, string name)
        {
            if (!request.TryGetValue(name, out object value) || value == null)
                return null;
            if (value is int || value is long || value is decimal || value is double)
                return Convert.ToDouble(value, Inv);
            throw new RequestException($"\"{name}\" must be a number");
        }

        private static bool GetBool(Dictionary<string, object> request, string name)
        {
            if (request.TryGetValue(name, out object value) && value is bool flag)
                return flag;
            throw new RequestException($"\"{name}\" must be true or false");
        }

        /// <summary>A request the app refuses; the message goes back to the client.</summary>
        private sealed class RequestException : Exception
        {
            public RequestException(string message) : base(message)
            {
            }
        }

        #endregion
    }
}
