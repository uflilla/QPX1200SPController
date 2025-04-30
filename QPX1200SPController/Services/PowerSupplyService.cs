using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using QPX1200SPController.Models;

namespace QPX1200SPController.Services
{
    public class PowerSupplyService
    {
        private string ipAddress;
        private int port = 9221; // Default SCPI port for many instruments
        private TcpClient client;
        private NetworkStream stream;

        public bool IsConnected => client != null && client.Connected;

        public async Task<bool> ConnectAsync(string ipAddress, int port = 9221)
        {
            try
            {
                this.ipAddress = ipAddress;
                this.port = port;

                client = new TcpClient();
                await client.ConnectAsync(ipAddress, port);
                stream = client.GetStream();
                return client.Connected;
                // Test connection with IDN query
                string response = await SendCommandAsync("*IDN?");
                return response.Contains("QPX1200SP");
            }
            catch (Exception ex)
            {
                Disconnect();
                QPX1200SPController.Services.LoggingService.Instance.LogException(ex, nameof(PowerSupplyService), nameof(ConnectAsync));
                return false;
            }
        }

        public void Disconnect()
        {
            stream?.Close();
            client?.Close();
            client = null;
            stream = null;
        }

        public async Task<string> SendCommandAsync(string command)
        {
            if (!IsConnected)
            {
                var ex = new InvalidOperationException("Not connected to power supply");
                QPX1200SPController.Services.LoggingService.Instance.LogException(ex, nameof(PowerSupplyService), nameof(SendCommandAsync));
                return string.Empty;
            }
            Debug.WriteLine($"command: {command}");
            try
            {
                byte[] data = Encoding.ASCII.GetBytes(command + "\n");
                await stream.WriteAsync(data, 0, data.Length);
                var response=string.Empty;
                // If command is a query (ends with ?), read response
                if (command.TrimEnd().EndsWith("?"))
                {
                    // Extract the command prefix for verification (e.g., "V1" from "V1?") 
                    string commandPrefix = ExtractCommandPrefix(command);
                    byte[] buffer = new byte[1024];
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    response = Encoding.ASCII.GetString(buffer, 0, bytesRead).Trim();
                    //Debug.WriteLine($"Response: {response}");
                    //var validResponse = IsValidResponseForCommand(response, commandPrefix);
                    //var responseValidated= validResponse ? response :string.Empty;
                    //Debug.WriteLine($"Validated Response: {responseValidated}");
                    //return responseValidated;
                    return response;
                }
                Debug.WriteLine($"Valid Response: {response}");
                return string.Empty;
            }
            catch (Exception ex)
            {
                QPX1200SPController.Services.LoggingService.Instance.LogException(ex, nameof(PowerSupplyService), nameof(SendCommandAsync));
                return string.Empty;
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

        private bool _isGettingStatus;
        public async Task<PowerSupplyStatus> GetStatusAsync()
        {
            
            if (!IsConnected)
            {
                QPX1200SPController.Services.LoggingService.Instance.LogError("Disconnected", nameof(PowerSupplyService), nameof(GetStatusAsync));
                return new PowerSupplyStatus { ConnectionStatus = "Disconnected" };
            }
            var count = 0;
            while (_isGettingStatus && count<20)
            {
                await Task.Delay(50);
                count++;
            }
            if (_isGettingStatus)
            {
                
                LoggingService.Instance.LogError("Already Getting status", nameof(PowerSupplyService), nameof(GetStatusAsync));
                return new PowerSupplyStatus { ConnectionStatus = "Busy"};
            }
            try
            {
                _isGettingStatus = true;
                string statusResponse = await SendCommandAsync(PowerSupplyCommands.GetPsStatusCommand);
                var status = ParsePsStatus(statusResponse);
                _isGettingStatus=false;
                return status;
            }
            catch (Exception ex)
            {
                Disconnect();
                QPX1200SPController.Services.LoggingService.Instance.LogException(ex, nameof(PowerSupplyService), nameof(GetStatusAsync));
                _isGettingStatus = false;
                return new PowerSupplyStatus { ConnectionStatus = "Error" };
            }
        }

        private PowerSupplyStatus ParsePsStatus(string statusResponse)
        {
            var tcParts = PowerSupplyCommands.GetPsStatusCommand.Split(';');
            var responseParts = statusResponse.Split('\n');
            if (tcParts.Length != responseParts.Length) return default;
            var status = new PowerSupplyStatus();

            status.Voltage = ParseResponseValue(responseParts[0], "V1O");
            status.Current = ParseResponseValue(responseParts[1], "I1O");
            status.OutputEnabled = responseParts[2].Trim()=="1";
            status.EventStatus=ParseEventStatus(responseParts[3]);
            status.Power=status.Voltage * status.Current;
            status.ConnectionStatus = "Connected";
            status.Time = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            return status;
        }

        private EventStatus ParseEventStatus(string responsePart)
        {
            var eventRegister= byte.Parse(responsePart);
            var bits = Convert.ToString(eventRegister, 2).PadLeft(8,'0').Reverse().ToArray();
            var result= new EventStatus();
            result.ConstantVoltageMode = bits[0]=='1'? 1:0;
            result.ConstantCurrentMode = bits[1]=='1'? 1:0;
            result.UnregulatedMode = bits[2]=='1'? 1:0;
            result.OverVoltageTrip = bits[3]=='1'? 1:0;
            result.OverCurrentTrip = bits[4]=='1'? 1:0;
            result.OutputSenseTrip = bits[5]=='1'? 1:0;
            result.FaultTrip = bits[6]=='1'? 1:0;
            return result;
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

        public async Task<double> GetOverVoltageProtectionAsync()
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
        }

        public async Task<PowerSupplySettingStatus> GetPowerSupplySettingsAsync()
        {
            var settings = await SendCommandAsync(PowerSupplyCommands.GetPsSettingStatusCommand);
            return ParsePsSettingStatus(settings);
        }

        private PowerSupplySettingStatus ParsePsSettingStatus(string settings)
        {
            var tcParts = PowerSupplyCommands.GetPsSettingStatusCommand.Split(';');
            var responseParts = settings.Split('\n');
            if (tcParts.Length != responseParts.Length) return default;
            var status = new PowerSupplySettingStatus();

            status.SetVoltage = ParseResponseValue(responseParts[0], "V1");
            status.SetCurrent = ParseResponseValue(responseParts[1], "I1");
            status.SetOvpLevel = ParseResponseValue(responseParts[2], "VP1");
            status.SetOcpLevel = ParseResponseValue(responseParts[3], "CP1");
            return status;
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