using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using QPX1200SPController.Models;

namespace QPX1200SPController.Services
{
    public class PowerSupplyService
    {
        private const int ResponseTimeoutMs = 5000;
        private string ipAddress;
        private int port = 9221; // Default SCPI port for many instruments
        private TcpClient client;
        private NetworkStream stream;
        private string _rxRemainder = string.Empty;
        private readonly EventStatus _latchedEventStatus = new EventStatus();
        private bool _isBusy;

        public bool IsConnected => client != null && client.Connected;

        public async Task<bool> ConnectAsync(string ipAddress, int port = 9221)
        {
            try
            {
                this.ipAddress = ipAddress;
                this.port = port;

                Disconnect();
                client = new TcpClient();
                await client.ConnectAsync(ipAddress, port);
                stream = client.GetStream();
                _rxRemainder = string.Empty;
                ResetLatchedEventStatus();
                return client.Connected;
            }
            catch (Exception ex)
            {
                Disconnect();
                LoggingService.Instance.LogException(ex, nameof(PowerSupplyService), nameof(ConnectAsync));
                return false;
            }
        }

        public void Disconnect()
        {
            try
            {
                stream?.Close();
                client?.Close();
            }
            catch (Exception ex)
            {
                LoggingService.Instance.LogException(ex, nameof(PowerSupplyService), nameof(Disconnect));
            }
            finally
            {
                client = null;
                stream = null;
                _rxRemainder = string.Empty;
                _isBusy = false;
                ResetLatchedEventStatus();
            }
        }

        /// <summary>
        /// Clears latched protection trip indicators (OVP/OCP/Sense/Fault).
        /// Call after a successful TRIPRST (or equivalent operator acknowledge).
        /// </summary>
        public void ClearLatchedTrips()
        {
            _latchedEventStatus.OverVoltageTrip = 0;
            _latchedEventStatus.OverCurrentTrip = 0;
            _latchedEventStatus.OutputSenseTrip = 0;
            _latchedEventStatus.FaultTrip = 0;
        }

        public void ResetLatchedEventStatus()
        {
            ClearLatchedTrips();
        }

        public async Task<string> SendCommandAsync(string command)
        {
            if (!IsConnected)
            {
                var ex = new InvalidOperationException("Not connected to power supply");
                LoggingService.Instance.LogException(ex, nameof(PowerSupplyService), nameof(SendCommandAsync));
                return string.Empty;
            }

            try
            {
                byte[] data = Encoding.ASCII.GetBytes(command + "\n");
                var count = 0;
                if (_isBusy && (command.Equals(PowerSupplyCommands.GetPsStatusCommand) || command.Equals(PowerSupplyCommands.GetPsSettingStatusCommand)))
                    return string.Empty;
                while (_isBusy && count < 40)
                {
                    await Task.Delay(50);
                    count++;
                }
                if (_isBusy)
                {
                    LoggingService.Instance.LogError("No Response received from Power Supply for 2 seconds. Disconnecting...", nameof(PowerSupplyService), nameof(SendCommandAsync));
                    Disconnect();
                    return string.Empty;
                }

                _isBusy = true;
                Debug.WriteLine($"{DateTime.Now:HH.mm.ss.fff} >> command: {command}");
                await stream.WriteAsync(data, 0, data.Length);

                var trimmedCommand = command.Trim();
                if (trimmedCommand.Equals("TRIPRST", StringComparison.OrdinalIgnoreCase))
                    ClearLatchedTrips();

                string response = string.Empty;
                if (trimmedCommand.EndsWith("?"))
                {
                    int expectedLines = GetExpectedQueryLineCount(trimmedCommand);
                    response = await ReadLinesAsync(expectedLines, ResponseTimeoutMs);
                    Debug.WriteLine($"{DateTime.Now:HH.mm.ss.fff} >> Valid Response: {response}");
                    _isBusy = false;
                    return response;
                }

                Debug.WriteLine($"{DateTime.Now:HH.mm.ss.fff} >> Valid Response: {response}");
                _isBusy = false;
                return string.Empty;
            }
            catch (TimeoutException ex)
            {
                LoggingService.Instance.LogException(ex, nameof(PowerSupplyService), nameof(SendCommandAsync));
                Disconnect();
                return string.Empty;
            }
            catch (Exception ex)
            {
                LoggingService.Instance.LogException(ex, nameof(PowerSupplyService), nameof(SendCommandAsync));
                try
                {
                    FlushReceiveBuffer();
                }
                catch
                {
                    // ignored — disconnect path handles cleanup
                }
                _isBusy = false;
                return string.Empty;
            }
        }

        private static int GetExpectedQueryLineCount(string command)
        {
            var parts = command.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            int queryCount = parts.Count(p => p.TrimEnd().EndsWith("?"));
            return Math.Max(queryCount, 1);
        }

        /// <summary>
        /// Reads from the TCP stream until <paramref name="expectedLines"/> complete lines
        /// are available (handles partial TCP reads and CRLF). Leftover bytes are kept for the next read.
        /// </summary>
        private async Task<string> ReadLinesAsync(int expectedLines, int timeoutMs)
        {
            var lines = new List<string>();
            var fragment = _rxRemainder ?? string.Empty;
            _rxRemainder = string.Empty;
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            byte[] buffer = new byte[1024];

            while (lines.Count < expectedLines)
            {
                int newlineIndex;
                while (lines.Count < expectedLines && (newlineIndex = fragment.IndexOf('\n')) >= 0)
                {
                    string line = fragment.Substring(0, newlineIndex).TrimEnd('\r');
                    fragment = fragment.Substring(newlineIndex + 1);
                    lines.Add(line);
                }

                if (lines.Count >= expectedLines)
                    break;

                int remainingMs = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remainingMs <= 0)
                {
                    _rxRemainder = fragment;
                    throw new TimeoutException($"Timed out waiting for {expectedLines} SCPI response line(s).");
                }

                int bytesRead = await ReadWithTimeoutAsync(buffer, remainingMs);
                if (bytesRead == 0)
                    throw new IOException("Connection closed by remote host while reading SCPI response.");

                fragment += Encoding.ASCII.GetString(buffer, 0, bytesRead);
            }

            _rxRemainder = fragment;
            return string.Join("\n", lines);
        }

        private async Task<int> ReadWithTimeoutAsync(byte[] buffer, int timeoutMs)
        {
            var readTask = stream.ReadAsync(buffer, 0, buffer.Length);
            var delayTask = Task.Delay(timeoutMs);
            var completed = await Task.WhenAny(readTask, delayTask);
            if (completed != readTask)
                throw new TimeoutException("Timed out reading from power supply TCP stream.");
            return await readTask;
        }

        private void FlushReceiveBuffer()
        {
            _rxRemainder = string.Empty;
            if (stream == null)
                return;

            try
            {
                byte[] buffer = new byte[1024];
                while (stream.DataAvailable)
                {
                    stream.Read(buffer, 0, buffer.Length);
                }
            }
            catch (Exception ex)
            {
                LoggingService.Instance.LogException(ex, nameof(PowerSupplyService), nameof(FlushReceiveBuffer));
            }
        }

        /*public async Task<string> SendCommandAsync(string command)
        {
            if (!IsConnected)
            {
                var ex = new InvalidOperationException("Not connected to power supply");
                QPX1200SPController.Services.LoggingService.Instance.LogException(ex, nameof(PowerSupplyService), nameof(SendCommandAsync));
                return string.Empty;
            }

            try
            {
                byte[] data = Encoding.ASCII.GetBytes(command + "\n");
                await stream.WriteAsync(data, 0, data.Length);

                // If command is a query (ends with ?), read response
                if (command.TrimEnd().EndsWith("?"))
                {
                    // Extract the command prefix for verification (e.g., "V1" from "V1?") 
                    string commandPrefix = ExtractCommandPrefix(command);
                    byte[] buffer = new byte[1024];
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    string response = Encoding.ASCII.GetString(buffer, 0, bytesRead).Trim();
                    int retryCount = 0;
                    const int maxRetries = 1;
                    while (retryCount < maxRetries && !IsValidResponseForCommand(response, commandPrefix))
                    {
                        await Task.Delay(100);
                        if (stream?.DataAvailable ?? false)
                        {
                            buffer = new byte[1024];
                            await stream.ReadAsync(buffer, 0, buffer.Length);
                        }
                        await stream.WriteAsync(data, 0, data.Length);
                        buffer = new byte[1024];
                        bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                        response = Encoding.ASCII.GetString(buffer, 0, bytesRead).Trim();
                        retryCount++;
                    }
                    return response;
                }
                return string.Empty;
            }
            catch (Exception ex)
            {
                QPX1200SPController.Services.LoggingService.Instance.LogException(ex, nameof(PowerSupplyService), nameof(SendCommandAsync));
                return string.Empty;
            }
        }*/

        /// <summary>
        /// Extracts the command prefix from a query command
        /// </summary>
        /// <param name="command">The command string (e.g., "V1?", "OVP1?")</param>
        /// <returns>The command prefix (e.g., "V1", "OVP1")</returns>
        private string ExtractCommandPrefix(string command)
        {
            // Remove any whitespace and the question mark
            string trimmedCommand = command.Trim();
            if (trimmedCommand.EndsWith("?"))
            {
                trimmedCommand = trimmedCommand.Substring(0, trimmedCommand.Length - 1);
            }
            
            // For commands with parameters, extract just the command part
            int spaceIndex = trimmedCommand.IndexOf(' ');
            if (spaceIndex > 0)
            {
                trimmedCommand = trimmedCommand.Substring(0, spaceIndex);
            }
            
            return trimmedCommand;
        }
        
        /// <summary>
        /// Checks if a response is valid for a given command
        /// </summary>
        /// <param name="response">The response from the power supply</param>
        /// <param name="commandPrefix">The command prefix to check against</param>
        /// <returns>True if the response is valid for the command</returns>
        private bool IsValidResponseForCommand(string response, string commandPrefix)
        {
            if (string.IsNullOrWhiteSpace(response))
                return false;
                
            // For simple numeric responses, we can't verify the command
            //if (response.Length < 3)
            //    return true;
                
            // Special case for output state query
            if (commandPrefix == "OP1" && (response == "0" || response == "1"))
                return true;
                
            // Handle specific response formats based on the manual
            switch (commandPrefix)
            {
                case "V1":
                    return response.StartsWith("V1 ", StringComparison.OrdinalIgnoreCase);
                    
                case "I1":
                    return response.StartsWith("I1 ", StringComparison.OrdinalIgnoreCase);
                    
                case "OVP1":
                    return response.StartsWith("VP1 ", StringComparison.OrdinalIgnoreCase);
                    
                case "OCP1":
                    return response.StartsWith("CP1 ", StringComparison.OrdinalIgnoreCase);
                    
                case "V1O":
                    // V1O? returns <NR2>V format
                    return response.EndsWith("V", StringComparison.OrdinalIgnoreCase);
                    
                case "I1O":
                    // I1O? returns <NR2>A format
                    return response.EndsWith("A", StringComparison.OrdinalIgnoreCase);
                case "*IDN":
                    // I1O? returns <NR2>A format
                    return true;
                default:
                    // Fallback for other commands
                    string altPrefix = commandPrefix.Replace("1", "P1");
                    return response.StartsWith(commandPrefix, StringComparison.OrdinalIgnoreCase) ||
                           response.StartsWith(altPrefix, StringComparison.OrdinalIgnoreCase);
            }
        }

        public async Task<PowerSupplyStatus> GetStatusAsync()
        {
            if (!IsConnected)
            {
                LoggingService.Instance.LogError("Disconnected", nameof(PowerSupplyService), nameof(GetStatusAsync));
                return new PowerSupplyStatus { ConnectionStatus = "Disconnected" };
            }

            try
            {
                string statusResponse = await SendCommandAsync(PowerSupplyCommands.GetPsStatusCommand);

                // SendCommandAsync may have disconnected on timeout/error while we were awaiting.
                if (!IsConnected)
                    return new PowerSupplyStatus { ConnectionStatus = "Disconnected" };

                var status = ParsePsStatus(statusResponse);
                if (status == null && !IsConnected)
                    return new PowerSupplyStatus { ConnectionStatus = "Disconnected" };

                return status;
            }
            catch (Exception ex)
            {
                Disconnect();
                LoggingService.Instance.LogException(ex, nameof(PowerSupplyService), nameof(GetStatusAsync));
                return new PowerSupplyStatus { ConnectionStatus = "Error" };
            }
        }

        private PowerSupplyStatus ParsePsStatus(string statusResponse)
        {
            if (string.IsNullOrWhiteSpace(statusResponse))
                return null;

            var tcParts = PowerSupplyCommands.GetPsStatusCommand.Split(';');
            var responseParts = statusResponse
                .Split(new[] { '\n' }, StringSplitOptions.None)
                .Select(p => p.Trim())
                .ToArray();

            if (tcParts.Length != responseParts.Length)
            {
                LoggingService.Instance.LogError(
                    $"Status response line count mismatch. Expected {tcParts.Length}, got {responseParts.Length}. Flushing RX buffer.",
                    nameof(PowerSupplyService),
                    nameof(ParsePsStatus));
                FlushReceiveBuffer();
                return null;
            }

            var freshEvents = ParseEventStatus(responseParts[3]);
            var status = new PowerSupplyStatus
            {
                Voltage = ParseResponseValue(responseParts[0], "V1O"),
                Current = ParseResponseValue(responseParts[1], "I1O"),
                OutputEnabled = responseParts[2] == "1",
                EventStatus = MergeLatchedEventStatus(freshEvents),
                ConnectionStatus = "Connected",
                Time = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}"
            };
            status.Power = status.Voltage * status.Current;
            return status;
        }

        /// <summary>
        /// LSR1? clears the instrument register on every read.
        /// Mode bits (CV/CC/Unreg) are passed through as reported this poll (same as before).
        /// Trip bits are latched in software until TRIPRST, so brief trips remain visible.
        /// </summary>
        private EventStatus MergeLatchedEventStatus(EventStatus fresh)
        {
            if (fresh == null)
            {
                return new EventStatus
                {
                    OverVoltageTrip = _latchedEventStatus.OverVoltageTrip,
                    OverCurrentTrip = _latchedEventStatus.OverCurrentTrip,
                    OutputSenseTrip = _latchedEventStatus.OutputSenseTrip,
                    FaultTrip = _latchedEventStatus.FaultTrip
                };
            }

            if (fresh.OverVoltageTrip == 1)
                _latchedEventStatus.OverVoltageTrip = 1;
            if (fresh.OverCurrentTrip == 1)
                _latchedEventStatus.OverCurrentTrip = 1;
            if (fresh.OutputSenseTrip == 1)
                _latchedEventStatus.OutputSenseTrip = 1;
            if (fresh.FaultTrip == 1)
                _latchedEventStatus.FaultTrip = 1;

            return new EventStatus
            {
                // Live (entry-since-last-read) mode bits — do not sticky-latch
                ConstantVoltageMode = fresh.ConstantVoltageMode,
                ConstantCurrentMode = fresh.ConstantCurrentMode,
                UnregulatedMode = fresh.UnregulatedMode,
                // Latched trips until TRIPRST
                OverVoltageTrip = _latchedEventStatus.OverVoltageTrip,
                OverCurrentTrip = _latchedEventStatus.OverCurrentTrip,
                OutputSenseTrip = _latchedEventStatus.OutputSenseTrip,
                FaultTrip = _latchedEventStatus.FaultTrip
            };
        }

        private EventStatus ParseEventStatus(string responsePart)
        {
            if (!byte.TryParse(responsePart?.Trim(), out byte eventRegister))
            {
                LoggingService.Instance.LogError(
                    $"Failed to parse LSR1 value: '{responsePart}'",
                    nameof(PowerSupplyService),
                    nameof(ParseEventStatus));
                return null;
            }

            var bits = Convert.ToString(eventRegister, 2).PadLeft(8, '0').Reverse().ToArray();
            return new EventStatus
            {
                ConstantVoltageMode = bits[0] == '1' ? 1 : 0,
                ConstantCurrentMode = bits[1] == '1' ? 1 : 0,
                UnregulatedMode = bits[2] == '1' ? 1 : 0,
                OverVoltageTrip = bits[3] == '1' ? 1 : 0,
                OverCurrentTrip = bits[4] == '1' ? 1 : 0,
                OutputSenseTrip = bits[5] == '1' ? 1 : 0,
                FaultTrip = bits[6] == '1' ? 1 : 0
            };
        }

        public async Task SetVoltageAsync(double voltage)
        {
            await SendCommandAsync($"V1 {voltage:F3}");
        }

        public async Task SetCurrentAsync(double current)
        {
            await SendCommandAsync($"I1 {current:F3}");
        }

        public async Task SetOutputAsync(bool enabled)
        {
            await SendCommandAsync($"OP1 {(enabled ? "1" : "0")}");
        }

        public async Task SetOverVoltageProtectionAsync(double voltage)
        {
            // Set OVP level (1-65V)
            // The protection is always enabled on QPX1200SP, only the level can be set
            await SendCommandAsync($"OVP1 {voltage:F3}");
        }

        public async Task SetOverCurrentProtectionAsync(double current)
        {
            // Set OCP level (1-65A)
            // The protection is always enabled on QPX1200SP, only the level can be set
            await SendCommandAsync($"OCP1 {current:F3}");
        }

        /*public async Task<double> GetOverVoltageProtectionAsync()
        {
            string level = await SendCommandAsync("OVP1?");
            return ParseResponseValue(level, "OVP1");
        }

        public async Task<double> GetOverCurrentProtectionAsync()
        {
            string level = await SendCommandAsync("OCP1?");
            return ParseResponseValue(level, "OCP1");
        }

        public async Task<double> GetSetVoltageAsync()
        {
            // V1O? queries the set voltage value from the power supply
            string voltage = await SendCommandAsync("V1?");
            return ParseResponseValue(voltage, "V1");
        }*/

        public async Task<PowerSupplySettingStatus> GetPowerSupplySettingsAsync()
        {
            var settings = await SendCommandAsync(PowerSupplyCommands.GetPsSettingStatusCommand);
            return ParsePsSettingStatus(settings);
        }

        private PowerSupplySettingStatus ParsePsSettingStatus(string settings)
        {
            if (string.IsNullOrWhiteSpace(settings))
                return null;

            var tcParts = PowerSupplyCommands.GetPsSettingStatusCommand.Split(';');
            var responseParts = settings
                .Split(new[] { '\n' }, StringSplitOptions.None)
                .Select(p => p.Trim())
                .ToArray();

            if (tcParts.Length != responseParts.Length)
            {
                LoggingService.Instance.LogError(
                    $"Settings response line count mismatch. Expected {tcParts.Length}, got {responseParts.Length}. Flushing RX buffer.",
                    nameof(PowerSupplyService),
                    nameof(ParsePsSettingStatus));
                FlushReceiveBuffer();
                return null;
            }

            return new PowerSupplySettingStatus
            {
                SetVoltage = ParseResponseValue(responseParts[0], "V1"),
                SetCurrent = ParseResponseValue(responseParts[1], "I1"),
                SetOvpLevel = ParseResponseValue(responseParts[2], "VP1"),
                SetOcpLevel = ParseResponseValue(responseParts[3], "CP1")
            };
        }

        public async Task<double> GetSetCurrentAsync()
        {
            // I1O? queries the set current value from the power supply
            string current = await SendCommandAsync("I1?");
            return ParseResponseValue(current, "I1");
        }
        /// <summary>
        /// Parses a response value from the power supply, handling various formats
        /// and verifying that the response matches the expected command.
        /// </summary>
        /// <param name="response">The response string from the power supply</param>
        /// <param name="expectedPrefix">The expected command prefix (e.g., "V1", "I1", "OVP1")</param>
        /// <returns>The parsed double value</returns>
        private double ParseResponseValue(string response, string expectedPrefix)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(response))
                    throw new InvalidOperationException("Empty response from power supply");
                response = response.Trim();
                switch (expectedPrefix)
                {
                    case "V1":
                        if (response.StartsWith("V1 ", StringComparison.OrdinalIgnoreCase))
                        {
                            string numericPart = response.Substring(3).Trim();
                            if (double.TryParse(numericPart, out double result))
                                return result;
                        }
                        break;
                    case "I1":
                        if (response.StartsWith("I1 ", StringComparison.OrdinalIgnoreCase))
                        {
                            string numericPart = response.Substring(3).Trim();
                            if (double.TryParse(numericPart, out double result))
                                return result;
                        }
                        break;
                    case "OVP1":
                        if (response.StartsWith("VP1 ", StringComparison.OrdinalIgnoreCase))
                        {
                            string numericPart = response.Substring(4).Trim();
                            if (double.TryParse(numericPart, out double result))
                                return result;
                        }
                        break;
                    case "OCP1":
                        if (response.StartsWith("CP1 ", StringComparison.OrdinalIgnoreCase))
                        {
                            string numericPart = response.Substring(4).Trim();
                            if (double.TryParse(numericPart, out double result))
                                return result;
                        }
                        break;
                    case "V1O":
                        if (response.EndsWith("V", StringComparison.OrdinalIgnoreCase))
                        {
                            string numericPart = response.Substring(0, response.Length - 1).Trim();
                            if (double.TryParse(numericPart, out double result))
                                return result;
                        }
                        break;
                    case "I1O":
                        if (response.EndsWith("A", StringComparison.OrdinalIgnoreCase))
                        {
                            string numericPart = response.Substring(0, response.Length - 1).Trim();
                            if (double.TryParse(numericPart, out double result))
                                return result;
                        }
                        break;
                }
                string genericNumericPart = "";
                bool decimalPointFound = false;
                if (response.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
                    response.StartsWith(expectedPrefix.Replace("1", "P1"), StringComparison.OrdinalIgnoreCase))
                {
                    int prefixEndIndex = response.IndexOf(' ');
                    if (prefixEndIndex > 0 && prefixEndIndex < response.Length - 1)
                    {
                        response = response.Substring(prefixEndIndex).Trim();
                    }
                }
                foreach (char c in response)
                {
                    if (char.IsDigit(c))
                    {
                        genericNumericPart += c;
                    }
                    else if (c == '.' && !decimalPointFound)
                    {
                        genericNumericPart += c;
                        decimalPointFound = true;
                    }
                    else if ((c == '-' || c == '+') && genericNumericPart.Length == 0)
                    {
                        genericNumericPart += c;
                    }
                    else if (char.IsLetter(c) || c == 'V' || c == 'A')
                    {
                        break;
                    }
                }
                if (string.IsNullOrWhiteSpace(genericNumericPart))
                    throw new FormatException($"Could not extract numeric value from response: {response}");
                if (double.TryParse(genericNumericPart, out double genericResult))
                    return genericResult;
                else
                    throw new FormatException($"Failed to parse numeric value: {genericNumericPart} from response: {response}");
            }
            catch (Exception ex)
            {
                QPX1200SPController.Services.LoggingService.Instance.LogException(ex, nameof(PowerSupplyService), nameof(ParseResponseValue));
                return 0.0;
            }
        }
    }
}