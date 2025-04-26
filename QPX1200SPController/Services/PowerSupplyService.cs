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
                
            // Check if the response contains the command prefix or a variation of it
            // Some devices respond with VP1 instead of V1, etc.
            string altPrefix = commandPrefix.Replace("1", "P1");
            
            return response.StartsWith(commandPrefix, StringComparison.OrdinalIgnoreCase) ||
                   response.StartsWith(altPrefix, StringComparison.OrdinalIgnoreCase) ||
                   response.Contains(commandPrefix + " ") ||
                   response.Contains(altPrefix + " ");
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

            // Check if the response contains the expected prefix
            // Some responses might be prefixed with the command (e.g., "V1 12.345V" or "VP1 12.345V")
            if (response.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
                response.StartsWith(expectedPrefix.Replace("1", "P1"), StringComparison.OrdinalIgnoreCase))
            {
                // Extract the part after the prefix
                int prefixEndIndex = response.IndexOf(' ');
                if (prefixEndIndex > 0 && prefixEndIndex < response.Length - 1)
                {
                    response = response.Substring(prefixEndIndex).Trim();
                }
                else
                {
                    throw new FormatException($"Invalid response format: {response}");
                }
            }

            // Remove any units (V, A, etc.) and other non-numeric characters
            string numericPart = "";
            bool decimalPointFound = false;

            foreach (char c in response)
            {
                if (char.IsDigit(c))
                {
                    numericPart += c;
                }
                else if (c == '.' && !decimalPointFound)
                {
                    numericPart += c;
                    decimalPointFound = true;
                }
                else if ((c == '-' || c == '+') && numericPart.Length == 0)
                {
                    // Allow negative or positive sign at the beginning
                    numericPart += c;
                }
                else if (char.IsLetter(c) || c == 'V' || c == 'A')
                {
                    // Stop parsing when we hit a letter (likely a unit)
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(numericPart))
                throw new FormatException($"Could not extract numeric value from response: {response}");

            if (double.TryParse(numericPart, out double result))
                return result;
            else
                throw new FormatException($"Failed to parse numeric value: {numericPart} from response: {response}");
        }
    }
}