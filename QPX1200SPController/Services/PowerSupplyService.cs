using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QPX1200SPController.Models;

namespace QPX1200SPController.Services
{
    public enum ConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Reconnecting
    }

    /// <summary>
    /// SCPI-over-TCP transport for the QPX1200SP.
    /// All traffic is serialised through one lock, every network operation has a timeout,
    /// numbers are always formatted/parsed with the invariant culture, and a lost link is
    /// re-established automatically until the user disconnects.
    /// </summary>
    public class PowerSupplyService
    {
        public const int DefaultPort = 9221;

        private const int ConnectTimeoutMs = 5000;
        private const int WriteTimeoutMs = 2000;
        private const int ResponseTimeoutMs = 3000;
        private const int CommandLockTimeoutMs = ConnectTimeoutMs + 1000;
        private const int MaxConsecutiveProtocolErrors = 3;
        private static readonly int[] ReconnectDelaysMs = { 1000, 2000, 5000, 10000 };
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly SemaphoreSlim _ioLock = new SemaphoreSlim(1, 1);
        private readonly object _stateLock = new object();
        private readonly EventStatus _latchedEventStatus = new EventStatus();

        private TcpClient _client;
        private NetworkStream _stream;
        private string _rxRemainder = string.Empty;
        private int _connectionGeneration;
        private int _consecutiveProtocolErrors;
        private volatile bool _autoReconnect;
        private CancellationTokenSource _reconnectCts;
        private volatile ConnectionState _state = ConnectionState.Disconnected;

        /// <summary>
        /// Raised whenever the connection state changes (and on every reconnect attempt).
        /// May be raised on any thread; handlers must not block.
        /// </summary>
        public event Action<ConnectionState> ConnectionStateChanged;

        public ConnectionState State => _state;
        public bool IsConnected => _state == ConnectionState.Connected;
        public string IpAddress { get; private set; }
        public int Port { get; private set; } = DefaultPort;
        public int ReconnectAttempt { get; private set; }
        public string LastFaultReason { get; private set; }

        #region Connection management

        public async Task<bool> ConnectAsync(string ipAddress, int port = DefaultPort)
        {
            _autoReconnect = false;
            StopReconnect();

            await _ioLock.WaitAsync();
            try
            {
                CloseSocket();
                IpAddress = ipAddress?.Trim();
                Port = port;
                SetState(ConnectionState.Connecting);

                if (!await OpenSocketAsync(logFailureAsError: true))
                {
                    SetState(ConnectionState.Disconnected);
                    return false;
                }

                ResetLatchedEventStatus();
                _consecutiveProtocolErrors = 0;
                ReconnectAttempt = 0;
                _autoReconnect = true;
                LoggingService.Instance.LogInfo($"Connected to {IpAddress}:{Port}.", nameof(PowerSupplyService), nameof(ConnectAsync));
                SetState(ConnectionState.Connected);
                return true;
            }
            finally
            {
                _ioLock.Release();
            }
        }

        /// <summary>User-initiated disconnect: closes the link and stops any reconnect attempts.</summary>
        public void Disconnect()
        {
            _autoReconnect = false;
            StopReconnect();
            CloseSocket();
            ResetLatchedEventStatus();
            if (_state != ConnectionState.Disconnected)
                LoggingService.Instance.LogInfo("Disconnected by user.", nameof(PowerSupplyService), nameof(Disconnect));
            SetState(ConnectionState.Disconnected);
        }

        /// <summary>Opens a new socket. Caller must hold <see cref="_ioLock"/>.</summary>
        private async Task<bool> OpenSocketAsync(bool logFailureAsError)
        {
            var client = new TcpClient { NoDelay = true };
            try
            {
                var connectTask = client.ConnectAsync(IpAddress, Port);
                await WithTimeout(connectTask, ConnectTimeoutMs, $"connection to {IpAddress}:{Port}");
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                lock (_stateLock)
                {
                    _client = client;
                    _stream = client.GetStream();
                    _rxRemainder = string.Empty;
                    _connectionGeneration++;
                }

                TraceService.Instance.Write("--", $"connected to {IpAddress}:{Port}");
                return true;
            }
            catch (Exception ex)
            {
                client.Close();
                LastFaultReason = ex.Message;
                TraceService.Instance.Write("!!", $"connect to {IpAddress}:{Port} failed: {ex.Message}");
                string message = $"Could not connect to {IpAddress}:{Port}: {ex.Message}";
                if (logFailureAsError)
                    LoggingService.Instance.LogError(message, nameof(PowerSupplyService), nameof(OpenSocketAsync));
                else
                    LoggingService.Instance.LogWarning(message, nameof(PowerSupplyService), nameof(OpenSocketAsync));
                return false;
            }
        }

        private void CloseSocket()
        {
            TcpClient client;
            NetworkStream stream;
            lock (_stateLock)
            {
                client = _client;
                stream = _stream;
                _client = null;
                _stream = null;
                _rxRemainder = string.Empty;
                // Any operation still running on the old socket is now stale and must not
                // trigger fault handling for a newer connection.
                _connectionGeneration++;
            }

            if (client == null)
                return;

            try
            {
                stream?.Close();
                client.Close();
            }
            catch (Exception ex)
            {
                LoggingService.Instance.LogException(ex, nameof(PowerSupplyService), nameof(CloseSocket));
            }
            TraceService.Instance.Write("--", "socket closed");
        }

        private void HandleConnectionFault(int generation, string reason)
        {
            lock (_stateLock)
            {
                if (generation != _connectionGeneration)
                    return;
            }

            CloseSocket();
            LastFaultReason = reason;

            if (_autoReconnect)
            {
                LoggingService.Instance.LogWarning($"Connection to power supply lost ({reason}). Reconnecting...",
                    nameof(PowerSupplyService), nameof(HandleConnectionFault));
                StartReconnect();
            }
            else
            {
                SetState(ConnectionState.Disconnected);
            }
        }

        private void StartReconnect()
        {
            StopReconnect();
            var cts = new CancellationTokenSource();
            _reconnectCts = cts;
            ReconnectAttempt = 0;
            SetState(ConnectionState.Reconnecting);
            _ = ReconnectLoopAsync(cts.Token);
        }

        private void StopReconnect()
        {
            var cts = _reconnectCts;
            _reconnectCts = null;
            if (cts == null)
                return;
            cts.Cancel();
            cts.Dispose();
        }

        private async Task ReconnectLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    int delay = ReconnectDelaysMs[Math.Min(ReconnectAttempt, ReconnectDelaysMs.Length - 1)];
                    await Task.Delay(delay, token);
                    ReconnectAttempt++;
                    SetState(ConnectionState.Reconnecting);

                    bool ok;
                    await _ioLock.WaitAsync(token);
                    try
                    {
                        if (token.IsCancellationRequested)
                            return;
                        ok = await OpenSocketAsync(logFailureAsError: false);
                        if (ok && token.IsCancellationRequested)
                        {
                            CloseSocket();
                            return;
                        }
                    }
                    finally
                    {
                        _ioLock.Release();
                    }

                    if (ok)
                    {
                        _consecutiveProtocolErrors = 0;
                        LoggingService.Instance.LogInfo($"Reconnected to {IpAddress}:{Port} after {ReconnectAttempt} attempt(s).",
                            nameof(PowerSupplyService), nameof(ReconnectLoopAsync));
                        SetState(ConnectionState.Connected);
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // user disconnected or connected manually
            }
            catch (ObjectDisposedException)
            {
                // cancellation source disposed while waiting
            }
            catch (Exception ex)
            {
                LoggingService.Instance.LogException(ex, nameof(PowerSupplyService), nameof(ReconnectLoopAsync));
            }
        }

        private void SetState(ConnectionState state)
        {
            _state = state;
            ConnectionStateChanged?.Invoke(state);
        }

        #endregion

        #region Command transport

        /// <summary>
        /// Sends a command. Returns the response for queries, an empty string for
        /// successful non-queries, or null if the command could not be completed.
        /// </summary>
        public Task<string> SendCommandAsync(string command)
        {
            return ExecuteAsync(command, skipIfBusy: false);
        }

        private async Task<string> ExecuteAsync(string command, bool skipIfBusy)
        {
            if (string.IsNullOrWhiteSpace(command) || !IsConnected)
                return null;

            // Background polls are skipped when the link is busy; user commands wait their turn.
            if (!await _ioLock.WaitAsync(skipIfBusy ? 0 : CommandLockTimeoutMs))
            {
                if (!skipIfBusy)
                    LoggingService.Instance.LogWarning($"Command '{command.Trim()}' not sent: link busy.",
                        nameof(PowerSupplyService), nameof(ExecuteAsync));
                return null;
            }

            try
            {
                return await ExecuteLockedAsync(command.Trim());
            }
            finally
            {
                _ioLock.Release();
            }
        }

        private async Task<string> ExecuteLockedAsync(string command)
        {
            NetworkStream stream;
            int generation;
            lock (_stateLock)
            {
                stream = _stream;
                generation = _connectionGeneration;
            }
            if (stream == null || !IsConnected)
                return null;

            bool isQuery = command.EndsWith("?");
            var stopwatch = Stopwatch.StartNew();
            try
            {
                byte[] data = Encoding.ASCII.GetBytes(command + "\n");
                TraceService.Instance.Write(">>", command);
                await WithTimeout(stream.WriteAsync(data, 0, data.Length), WriteTimeoutMs, "write");

                if (command.Equals("TRIPRST", StringComparison.OrdinalIgnoreCase))
                    ClearLatchedTrips();

                if (!isQuery)
                    return string.Empty;

                string response = await ReadLinesAsync(stream, GetExpectedQueryLineCount(command), ResponseTimeoutMs);
                TraceService.Instance.Write("<<", response, stopwatch.Elapsed);
                return response;
            }
            catch (Exception ex)
            {
                // After a timeout or I/O error the stream position is unknown (an abandoned read
                // may still consume the next reply), so the only safe recovery is a fresh socket.
                TraceService.Instance.Write("!!", $"{command}: {ex.Message}", stopwatch.Elapsed);
                LoggingService.Instance.LogWarning($"Command '{command}' failed: {ex.Message}",
                    nameof(PowerSupplyService), nameof(ExecuteLockedAsync));
                HandleConnectionFault(generation, ex.Message);
                return null;
            }
        }

        private static int GetExpectedQueryLineCount(string command)
        {
            var parts = command.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            int queryCount = parts.Count(p => p.TrimEnd().EndsWith("?"));
            return Math.Max(queryCount, 1);
        }

        /// <summary>
        /// Reads until <paramref name="expectedLines"/> complete lines are available (handles
        /// partial TCP reads and CRLF). Leftover bytes are kept for the next read.
        /// </summary>
        private async Task<string> ReadLinesAsync(NetworkStream stream, int expectedLines, int timeoutMs)
        {
            var lines = new string[expectedLines];
            int lineCount = 0;
            var fragment = _rxRemainder ?? string.Empty;
            _rxRemainder = string.Empty;
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            byte[] buffer = new byte[1024];

            while (true)
            {
                int newlineIndex;
                while (lineCount < expectedLines && (newlineIndex = fragment.IndexOf('\n')) >= 0)
                {
                    lines[lineCount++] = fragment.Substring(0, newlineIndex).TrimEnd('\r');
                    fragment = fragment.Substring(newlineIndex + 1);
                }

                if (lineCount >= expectedLines)
                    break;

                int remainingMs = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remainingMs <= 0)
                    throw new TimeoutException($"No complete response within {timeoutMs} ms (got {lineCount}/{expectedLines} line(s)).");

                int bytesRead = await WithTimeout(stream.ReadAsync(buffer, 0, buffer.Length), remainingMs, "response");
                if (bytesRead == 0)
                    throw new IOException("Connection closed by the power supply.");

                fragment += Encoding.ASCII.GetString(buffer, 0, bytesRead);
            }

            _rxRemainder = fragment;
            return string.Join("\n", lines);
        }

        /// <summary>
        /// Called after an unparseable reply. Discards anything left in the receive path so the
        /// next exchange starts clean; repeated errors force a reconnect.
        /// </summary>
        private async Task RegisterProtocolErrorAsync(string description, int generation)
        {
            _consecutiveProtocolErrors++;
            LoggingService.Instance.LogWarning(
                $"{description} (consecutive invalid responses: {_consecutiveProtocolErrors})",
                nameof(PowerSupplyService), nameof(RegisterProtocolErrorAsync));
            TraceService.Instance.Write("!!", description);

            if (_consecutiveProtocolErrors >= MaxConsecutiveProtocolErrors)
            {
                _consecutiveProtocolErrors = 0;
                HandleConnectionFault(generation, "repeated invalid responses");
                return;
            }

            await _ioLock.WaitAsync();
            try
            {
                NetworkStream stream;
                lock (_stateLock)
                {
                    if (generation != _connectionGeneration)
                        return;
                    stream = _stream;
                }

                _rxRemainder = string.Empty;
                await Task.Delay(50); // let any straggling bytes arrive
                byte[] buffer = new byte[1024];
                while (stream != null && stream.DataAvailable)
                    stream.Read(buffer, 0, buffer.Length);
            }
            catch (Exception ex)
            {
                LoggingService.Instance.LogException(ex, nameof(PowerSupplyService), nameof(RegisterProtocolErrorAsync));
            }
            finally
            {
                _ioLock.Release();
            }
        }

        private int CurrentGeneration
        {
            get
            {
                lock (_stateLock)
                    return _connectionGeneration;
            }
        }

        private static async Task WithTimeout(Task task, int timeoutMs, string operation)
        {
            if (await Task.WhenAny(task, Task.Delay(timeoutMs)) != task)
            {
                Observe(task);
                throw new TimeoutException($"Timed out after {timeoutMs} ms waiting for {operation}.");
            }
            await task;
        }

        private static async Task<T> WithTimeout<T>(Task<T> task, int timeoutMs, string operation)
        {
            if (await Task.WhenAny(task, Task.Delay(timeoutMs)) != task)
            {
                Observe(task);
                throw new TimeoutException($"Timed out after {timeoutMs} ms waiting for {operation}.");
            }
            return await task;
        }

        /// <summary>Prevents an abandoned task's exception from surfacing as unobserved.</summary>
        private static void Observe(Task task)
        {
            task.ContinueWith(t => { var _ = t.Exception; },
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }

        #endregion

        #region Queries

        public Task<string> GetIdentityAsync()
        {
            return ExecuteAsync(PowerSupplyCommands.GetPsIdentityCommand, skipIfBusy: false);
        }

        /// <summary>Reads live output values and status. Returns null if the read failed or was skipped.</summary>
        public async Task<PowerSupplyStatus> GetStatusAsync(bool skipIfBusy = true)
        {
            int generation = CurrentGeneration;
            string response = await ExecuteAsync(PowerSupplyCommands.GetPsStatusCommand, skipIfBusy);
            if (response == null)
                return null;

            var status = ParsePsStatus(response);
            if (status == null)
            {
                await RegisterProtocolErrorAsync($"Invalid status response: '{Escape(response)}'", generation);
                return null;
            }

            _consecutiveProtocolErrors = 0;
            return status;
        }

        /// <summary>Reads the programmed set-points. Returns null if the read failed or was skipped.</summary>
        public async Task<PowerSupplySettingStatus> GetPowerSupplySettingsAsync(bool skipIfBusy = false)
        {
            int generation = CurrentGeneration;
            string response = await ExecuteAsync(PowerSupplyCommands.GetPsSettingStatusCommand, skipIfBusy);
            if (response == null)
                return null;

            var settings = ParsePsSettingStatus(response);
            if (settings == null)
            {
                await RegisterProtocolErrorAsync($"Invalid settings response: '{Escape(response)}'", generation);
                return null;
            }

            _consecutiveProtocolErrors = 0;
            return settings;
        }

        private PowerSupplyStatus ParsePsStatus(string response)
        {
            // V1O? -> "<NR2>V", I1O? -> "<NR2>A", OP1? -> "0|1", LSR1? -> "<NR1>"
            var parts = SplitLines(response);
            if (parts.Length != 4)
                return null;

            if (!TryParseWithSuffix(parts[0], "V", out double voltage)
                || !TryParseWithSuffix(parts[1], "A", out double current)
                || (parts[2] != "0" && parts[2] != "1"))
                return null;

            var freshEvents = ParseEventStatus(parts[3]);
            if (freshEvents == null)
                return null;

            var now = DateTime.Now;
            return new PowerSupplyStatus
            {
                Voltage = voltage,
                Current = current,
                Power = voltage * current,
                OutputEnabled = parts[2] == "1",
                EventStatus = MergeLatchedEventStatus(freshEvents),
                ConnectionStatus = "Connected",
                Timestamp = now,
                Time = now.ToString("yyyy-MM-dd HH:mm:ss", Inv)
            };
        }

        private static PowerSupplySettingStatus ParsePsSettingStatus(string response)
        {
            // V1? -> "V1 <NR2>", I1? -> "I1 <NR2>", OVP1? -> "VP1 <NR2>", OCP1? -> "CP1 <NR2>"
            var parts = SplitLines(response);
            if (parts.Length != 4)
                return null;

            if (!TryParseWithPrefix(parts[0], "V1", out double setVoltage)
                || !TryParseWithPrefix(parts[1], "I1", out double setCurrent)
                || !TryParseWithPrefix(parts[2], "VP1", out double ovp)
                || !TryParseWithPrefix(parts[3], "CP1", out double ocp))
                return null;

            return new PowerSupplySettingStatus
            {
                SetVoltage = setVoltage,
                SetCurrent = setCurrent,
                SetOvpLevel = ovp,
                SetOcpLevel = ocp
            };
        }

        private static EventStatus ParseEventStatus(string value)
        {
            if (!byte.TryParse(value?.Trim(), NumberStyles.Integer, Inv, out byte register))
                return null;

            return new EventStatus
            {
                ConstantVoltageMode = (register >> 0) & 1,
                ConstantCurrentMode = (register >> 1) & 1,
                UnregulatedMode = (register >> 2) & 1,
                OverVoltageTrip = (register >> 3) & 1,
                OverCurrentTrip = (register >> 4) & 1,
                OutputSenseTrip = (register >> 5) & 1,
                FaultTrip = (register >> 6) & 1
            };
        }

        private static string[] SplitLines(string response)
        {
            return response.Split('\n').Select(p => p.Trim()).ToArray();
        }

        private static bool TryParseWithPrefix(string text, string prefix, out double value)
        {
            value = 0;
            text = text?.Trim();
            if (string.IsNullOrEmpty(text) || !text.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase))
                return false;
            return TryParseNumber(text.Substring(prefix.Length), out value);
        }

        private static bool TryParseWithSuffix(string text, string suffix, out double value)
        {
            value = 0;
            text = text?.Trim();
            if (string.IsNullOrEmpty(text) || !text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return false;
            return TryParseNumber(text.Substring(0, text.Length - suffix.Length), out value);
        }

        private static bool TryParseNumber(string text, out double value)
        {
            return double.TryParse(text.Trim(), NumberStyles.Float, Inv, out value)
                   && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static string Escape(string text)
        {
            return text?.Replace("\r", "\\r").Replace("\n", "\\n");
        }

        #endregion

        #region Event status latching

        /// <summary>
        /// Clears latched protection trip indicators (OVP/OCP/Sense/Fault).
        /// Called after TRIPRST is sent.
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

        /// <summary>
        /// LSR1? clears the instrument register on every read.
        /// Mode bits (CV/CC/Unreg) are passed through as reported this poll.
        /// Trip bits are latched in software until TRIPRST, so brief trips remain visible
        /// (latches survive an automatic reconnect).
        /// </summary>
        private EventStatus MergeLatchedEventStatus(EventStatus fresh)
        {
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
                ConstantVoltageMode = fresh.ConstantVoltageMode,
                ConstantCurrentMode = fresh.ConstantCurrentMode,
                UnregulatedMode = fresh.UnregulatedMode,
                OverVoltageTrip = _latchedEventStatus.OverVoltageTrip,
                OverCurrentTrip = _latchedEventStatus.OverCurrentTrip,
                OutputSenseTrip = _latchedEventStatus.OutputSenseTrip,
                FaultTrip = _latchedEventStatus.FaultTrip
            };
        }

        #endregion

        #region Set commands

        public async Task<bool> SetVoltageAsync(double voltage)
        {
            return await SendCommandAsync("V1 " + voltage.ToString("F3", Inv)) != null;
        }

        public async Task<bool> SetCurrentAsync(double current)
        {
            return await SendCommandAsync("I1 " + current.ToString("F3", Inv)) != null;
        }

        public async Task<bool> SetOutputAsync(bool enabled)
        {
            return await SendCommandAsync(enabled ? "OP1 1" : "OP1 0") != null;
        }

        public async Task<bool> SetOverVoltageProtectionAsync(double voltage)
        {
            // The protection is always enabled on the QPX1200SP; only the level can be set.
            return await SendCommandAsync("OVP1 " + voltage.ToString("F3", Inv)) != null;
        }

        public async Task<bool> SetOverCurrentProtectionAsync(double current)
        {
            // The protection is always enabled on the QPX1200SP; only the level can be set.
            return await SendCommandAsync("OCP1 " + current.ToString("F3", Inv)) != null;
        }

        #endregion
    }
}
