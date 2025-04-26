using System;
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

                // Test connection with IDN query
                string response = await SendCommandAsync("*IDN?");
                return response.Contains("QPX1200SP");
            }
            catch (Exception)
            {
                Disconnect();
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
                throw new InvalidOperationException("Not connected to power supply");

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
                
                // Add retry logic for potentially incomplete or incorrect responses
                int retryCount = 0;
                const int maxRetries = 3;
                
                while (retryCount < maxRetries && !IsValidResponseForCommand(response, commandPrefix))
                {
                    // Wait a bit before retrying
                    await Task.Delay(100);
                    
                    // Clear any pending data
                    if (stream.DataAvailable)
                    {
                        buffer = new byte[1024];
                        await stream.ReadAsync(buffer, 0, buffer.Length);
                    }
                    
                    // Resend the command
                    await stream.WriteAsync(data, 0, data.Length);
                    
                    // Read the response again
                    buffer = new byte[1024];
                    bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    response = Encoding.ASCII.GetString(buffer, 0, bytesRead).Trim();
                    
                    retryCount++;
                }
                
                return response;
            }

            return string.Empty;
        }
        
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
            if (response.Length < 3)
                return true;
                
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
                return new PowerSupplyStatus { ConnectionStatus = "Disconnected" };

            try
            {
                var status = new PowerSupplyStatus();

                // Get voltage
                string voltageStr = await SendCommandAsync("V1?");
                status.Voltage = ParseResponseValue(voltageStr, "V1");

                // Get current
                string currentStr = await SendCommandAsync("I1?");
                status.Current = ParseResponseValue(currentStr, "I1");

                // Calculate power
                status.Power = status.Voltage * status.Current;

                // Get output state
                string outputStr = await SendCommandAsync("OP1?");
                status.OutputEnabled = outputStr.Trim() == "1";

                // Get protection values
                string ovpStr = await SendCommandAsync("OVP1?");
                status.OverVoltageProtection = ParseResponseValue(ovpStr, "OVP1");

                string ocpStr = await SendCommandAsync("OCP1?");
                status.OverCurrentProtection = ParseResponseValue(ocpStr, "OCP1");

                status.ConnectionStatus = "Connected";
                return status;
            }
            catch (Exception)
            {
                Disconnect();
                return new PowerSupplyStatus { ConnectionStatus = "Error" };
            }
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
            string voltage = await SendCommandAsync("V1O?");
            return ParseResponseValue(voltage, "V1O");
        }

        public async Task<double> GetSetCurrentAsync()
        {
            // I1O? queries the set current value from the power supply
            string current = await SendCommandAsync("I1O?");
            return ParseResponseValue(current, "I1O");
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
            if (string.IsNullOrWhiteSpace(response))
                throw new InvalidOperationException("Empty response from power supply");

            // Trim any whitespace
            response = response.Trim();
            
            // Handle specific response formats based on the command prefix
            switch (expectedPrefix)
            {
                case "V1":
                    // Format: V1 <NR2><RMT> where <NR2> is in Volts
                    if (response.StartsWith("V1 ", StringComparison.OrdinalIgnoreCase))
                    {
                        string numericPart = response.Substring(3).Trim();
                        if (double.TryParse(numericPart, out double result))
                            return result;
                    }
                    break;
                    
                case "I1":
                    // Format: I1 <NR2><RMT> where <NR2> is in Amps
                    if (response.StartsWith("I1 ", StringComparison.OrdinalIgnoreCase))
                    {
                        string numericPart = response.Substring(3).Trim();
                        if (double.TryParse(numericPart, out double result))
                            return result;
                    }
                    break;
                    
                case "OVP1":
                    // Format: VP1 <NR2><RMT> where <NR2> is in Volts
                    if (response.StartsWith("VP1 ", StringComparison.OrdinalIgnoreCase))
                    {
                        string numericPart = response.Substring(4).Trim();
                        if (double.TryParse(numericPart, out double result))
                            return result;
                    }
                    break;
                    
                case "OCP1":
                    // Format: CP1 <NR2><RMT> where <NR2> is in Amps
                    if (response.StartsWith("CP1 ", StringComparison.OrdinalIgnoreCase))
                    {
                        string numericPart = response.Substring(4).Trim();
                        if (double.TryParse(numericPart, out double result))
                            return result;
                    }
                    break;
                    
                case "V1O":
                    // Format: <NR2>V<RMT> where <NR2> is in Volts
                    if (response.EndsWith("V", StringComparison.OrdinalIgnoreCase))
                    {
                        string numericPart = response.Substring(0, response.Length - 1).Trim();
                        if (double.TryParse(numericPart, out double result))
                            return result;
                    }
                    break;
                    
                case "I1O":
                    // Format: <NR2>A<RMT> where <NR2> is in Amps
                    if (response.EndsWith("A", StringComparison.OrdinalIgnoreCase))
                    {
                        string numericPart = response.Substring(0, response.Length - 1).Trim();
                        if (double.TryParse(numericPart, out double result))
                            return result;
                    }
                    break;
            }
            
            // Fallback to generic parsing for other commands or if specific parsing failed
            // Remove any units (V, A, etc.) and other non-numeric characters
            string genericNumericPart = "";
            bool decimalPointFound = false;

            // Check if the response contains the expected prefix
            // Some responses might be prefixed with the command
            if (response.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
                response.StartsWith(expectedPrefix.Replace("1", "P1"), StringComparison.OrdinalIgnoreCase))
            {
                // Extract the part after the prefix
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
                    // Allow negative or positive sign at the beginning
                    genericNumericPart += c;
                }
                else if (char.IsLetter(c) || c == 'V' || c == 'A')
                {
                    // Stop parsing when we hit a letter (likely a unit)
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
    }
}