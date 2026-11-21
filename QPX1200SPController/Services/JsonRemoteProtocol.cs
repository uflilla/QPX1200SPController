using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace QPX1200SPController.Services
{
    /// <summary>
    /// The JSON remote protocol (docs/REMOTE_PROTOCOL.md): one JSON object per line, one reply per
    /// request, in order. The same as the CPX400DP Controller's, so one client drives both apps.
    /// No unrequested messages: housekeeping is CCSDS only.
    /// </summary>
    public sealed class JsonRemoteProtocol : IRemoteProtocol
    {
        private const int MaxLineLength = 64 * 1024;
        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly IRemoteCommands _commands;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();

        public JsonRemoteProtocol(IRemoteCommands commands)
        {
            _commands = commands;
        }

        public string Name => "JSON";

        public void PublishHousekeeping(RemoteStatus status)
        {
        }

        public async Task RefuseAsync(Stream stream, string address)
        {
            using (var writer = new StreamWriter(stream, Utf8, 1024, leaveOpen: true) { NewLine = "\n" })
            {
                await writer.WriteLineAsync(_json.Serialize(Error("Another client is connected"))).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
        }

        public async Task ServeAsync(Stream stream, string address)
        {
            using (var reader = new StreamReader(stream, Utf8, false, 1024, leaveOpen: true))
            using (var writer = new StreamWriter(stream, Utf8, 1024, leaveOpen: true) { NewLine = "\n", AutoFlush = true })
            {
                while (true)
                {
                    string line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null)
                        return;
                    if (line.Trim().Length == 0)
                        continue;

                    TraceService.Instance.Write("R<", $"{address} {line}");
                    string reply = _json.Serialize(await HandleLineAsync(line).ConfigureAwait(false));
                    TraceService.Instance.Write("R>", $"{address} {reply}");
                    await writer.WriteLineAsync(reply).ConfigureAwait(false);
                }
            }
        }

        private async Task<Dictionary<string, object>> HandleLineAsync(string line)
        {
            if (line.Length > MaxLineLength)
                return Error($"Line longer than {MaxLineLength} characters");

            Dictionary<string, object> request;
            try
            {
                request = _json.DeserializeObject(line) as Dictionary<string, object>;
            }
            catch (Exception)
            {
                request = null;
            }
            if (request == null)
                return Error("Invalid JSON: send one object per line, e.g. {\"cmd\": \"read\", \"output\": 1}");

            try
            {
                string cmd = request.TryGetValue("cmd", out object value) ? value as string : null;
                switch (cmd?.ToLowerInvariant())
                {
                    case "set": return await SetAsync(request).ConfigureAwait(false);
                    case "output": return await OutputAsync(request).ConfigureAwait(false);
                    case "read": return await ReadAsync(request).ConfigureAwait(false);
                    case null: return Error("Missing \"cmd\": use set, output or read");
                    default: return Error($"Unknown command '{cmd}': use set, output or read");
                }
            }
            catch (RemoteException ex)
            {
                return Error(ex.Message);
            }
            catch (Exception ex)
            {
                LoggingService.Instance.LogException(ex, nameof(JsonRemoteProtocol), nameof(HandleLineAsync));
                return Error("Internal error: " + ex.Message);
            }
        }

        #region Commands

        private async Task<Dictionary<string, object>> SetAsync(Dictionary<string, object> request)
        {
            CheckOutput(request);
            var set = new SetRequest
            {
                Ovp = GetNumber(request, "ovp"),
                Ocp = GetNumber(request, "ocp"),
                Current = GetNumber(request, "current"),
                Voltage = GetNumber(request, "voltage")
            };

            var result = await _commands.SetAsync(set).ConfigureAwait(false);
            var reply = new Dictionary<string, object> { ["ok"] = result.Error == RemoteError.None };
            foreach (var setting in result.Results)
            {
                if (setting.Result == RemoteResult.NotSent)
                    continue;
                reply[setting.Name] = new Dictionary<string, object>
                {
                    ["requested"] = setting.Requested,
                    ["readback"] = setting.Readback,
                    ["result"] = ResultName(setting.Result)
                };
            }
            if (result.Error != RemoteError.None)
                reply["error"] = result.Message;
            return reply;
        }

        private async Task<Dictionary<string, object>> OutputAsync(Dictionary<string, object> request)
        {
            CheckOutput(request);
            bool on = GetBool(request, "on");

            var result = await _commands.OutputAsync(on).ConfigureAwait(false);
            var reply = new Dictionary<string, object>
            {
                ["ok"] = result.Error == RemoteError.None,
                ["on"] = result.On,
                ["result"] = ResultName(result.Result)
            };
            if (result.Error != RemoteError.None)
                reply["error"] = result.Message;
            return reply;
        }

        private async Task<Dictionary<string, object>> ReadAsync(Dictionary<string, object> request)
        {
            CheckOutput(request);
            var s = await _commands.ReadAsync().ConfigureAwait(false);
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["time"] = s.Timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", Inv),
                ["output"] = 1,
                ["on"] = s.OutputOn,
                ["voltage"] = Math.Round(s.Voltage, 3),
                ["current"] = Math.Round(s.Current, 3),
                ["mode"] = RemoteStatus.ModeName(s.Mode),
                ["trip"] = RemoteStatus.TripName(s.Trip),
                ["set_voltage"] = Math.Round(s.SetVoltage, 3),
                ["set_current"] = Math.Round(s.SetCurrent, 3),
                ["ovp"] = Math.Round(s.Ovp, 2),
                ["ocp"] = Math.Round(s.Ocp, 2)
            };
        }

        /// <summary>"OK", "Mismatch" or "Failed", as in the database.</summary>
        private static string ResultName(RemoteResult result)
        {
            switch (result)
            {
                case RemoteResult.Ok: return "OK";
                case RemoteResult.Mismatch: return "Mismatch";
                default: return "Failed";
            }
        }

        #endregion

        #region Request checks

        /// <summary>"output" may be left out; if given it must be 1 (same messages as the CPX400DP's).</summary>
        private static void CheckOutput(Dictionary<string, object> request)
        {
            double? value;
            try
            {
                value = GetNumber(request, "output");
            }
            catch (RemoteException)
            {
                value = 0;
            }
            if (value.HasValue && value != 1)
                throw new RemoteException(RemoteError.InvalidParameter, "\"output\" must be 1 (the QPX1200SP has one output)");
        }

        /// <summary>The named number, or null if the request does not have it.</summary>
        private static double? GetNumber(Dictionary<string, object> request, string name)
        {
            if (!request.TryGetValue(name, out object value) || value == null)
                return null;
            if (value is int || value is long || value is decimal || value is double)
                return Convert.ToDouble(value, Inv);
            throw new RemoteException(RemoteError.InvalidParameter, $"\"{name}\" must be a number");
        }

        private static bool GetBool(Dictionary<string, object> request, string name)
        {
            if (request.TryGetValue(name, out object value) && value is bool flag)
                return flag;
            throw new RemoteException(RemoteError.InvalidParameter, $"\"{name}\" must be true or false");
        }

        public static Dictionary<string, object> Error(string message)
        {
            return new Dictionary<string, object> { ["ok"] = false, ["error"] = message };
        }

        #endregion
    }
}
