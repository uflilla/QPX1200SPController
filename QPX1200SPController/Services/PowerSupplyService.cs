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
                byte[] buffer = new byte[1024];
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                return Encoding.ASCII.GetString(buffer, 0, bytesRead).Trim();
            }

            return string.Empty;
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
                status.Voltage = double.Parse(voltageStr);

                // Get current
                string currentStr = await SendCommandAsync("I1?");
                status.Current = double.Parse(currentStr);

                // Calculate power
                status.Power = status.Voltage * status.Current;

                // Get output state
                string outputStr = await SendCommandAsync("OP1?");
                status.OutputEnabled = outputStr.Trim() == "1";

                // Get protection values
                string ovpStr = await SendCommandAsync("OVP1?");
                status.OverVoltageProtection = double.Parse(ovpStr);

                string ocpStr = await SendCommandAsync("OCP1?");
                status.OverCurrentProtection = double.Parse(ocpStr);

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
            return double.Parse(level);
        }

        public async Task<double> GetOverCurrentProtectionAsync()
        {
            string level = await SendCommandAsync("OCP1?");
            return double.Parse(level);
        }

        public async Task<double> GetSetVoltageAsync()
        {
            // V1O? queries the set voltage value from the power supply
            string voltage = await SendCommandAsync("V1O?");
            return double.Parse(voltage);
        }

        public async Task<double> GetSetCurrentAsync()
        {
            // I1O? queries the set current value from the power supply
            string current = await SendCommandAsync("I1O?");
            return double.Parse(current);
        }
    }
}