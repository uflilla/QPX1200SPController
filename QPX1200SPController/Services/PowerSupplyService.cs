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

        /// <summary>
        /// Raised for every configuration (non-query) command, every connection event and every
        /// communication error. May be raised on any thread; handlers must not block.
        /// </summary>
        public event Action<ActivityRecord> ActivityRecorded;

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
                    RecordConnection("Connect failed", $"{IpAddress}:{Port}: {LastFaultReason}");
                    SetState(ConnectionState.Disconnected);
                    return false;
                }

                ResetLatchedEventStatus();
                _consecutiveProtocolErrors = 0;
                ReconnectAttempt = 0;
                _autoReconnect = true;
                LoggingService.Instance.LogInfo($"Connected to {IpAddress}:{Port}.", nameof(PowerSupplyService), nameof(ConnectAsync));
                RecordConnection("Connected", $"{IpAddress}:{Port}");
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
            {
                LoggingService.Instance.LogInfo("Disconnected by user.", nameof(PowerSupplyService), nameof(Disconnect));
                RecordConnection("Disconnected", "by user");
            }
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
            RecordConnection("Lost", reason);

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
                        RecordConnection("Reconnected", $"{IpAddress}:{Port} after {ReconnectAttempt} attempt(s)");
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

        private void Record(ActivityRecord record)
        {
            try
            {
                ActivityRecorded?.Invoke(record);
            }
            catch (Exception ex)
            {
                LoggingService.Instance.LogException(ex, nameof(PowerSupplyService), nameof(Record));
            }
        }

        private void RecordConnection(string result, string detail)
        {
            Record(new ActivityRecord(ActivityRecord.KindConnection) { Source = "Link", Result = result, Detail = detail });
        }

        private void RecordCommError(string command, string response, string detail, double? durationMs = null)
        {
            Record(new ActivityRecord(ActivityRecord.KindCommError, command)
            {
                Source = "Link",
                Response = response,
                Result = ActivityRecord.ResultFailed,
                Detail = detail,
                DurationMs = durationMs
            });
        }

        #endregion

        #region Command transport

        /// <summary>
        /// Sends a command. Returns the response for queries, an empty string for
        /// successful non-queries, or null if the command could not be completed.
        /// Non-query commands are always recorded (see <see cref="ActivityRecorded"/>).
        /// </summary>
        public async Task<string> SendCommandAsync(string command, string source = "App")
        {
            if (string.IsNullOrWhiteSpace(command))
                return null;
            if (IsQuery(command))
                return (await ExecuteAsync(command, skipIfBusy: false)).Response;

            var record = await SendConfigCommandAsync(command, source);
            return record.Success ? string.Empty : null;
        }

        /// <summary>
        /// Sends a configuration (non-query) command checked with <c>EER?</c> and raises its log
        /// record. LOCAL is sent on its own and not checked (see <see cref="GoLocalAsync"/>).
        /// </summary>
        public Task<ActivityRecord> SendConfigCommandAsync(string command, string source)
        {
            command = command?.Trim() ?? string.Empty;
            if (IsLocalCommand(command))
                return SendUnverifiedAsync(command, source);
            return SendVerifiedAsync(command, source, null, null, null, 0);
        }

        /// <summary>
        /// Sends a command without checking it and records the result. Used for LOCAL, which must be
        /// the last thing sent: any further character puts the supply back into Remote (manual p.20).
        /// </summary>
        private async Task<ActivityRecord> SendUnverifiedAsync(string command, string source)
        {
            command = command?.Trim() ?? string.Empty;
            var record = new ActivityRecord(ActivityRecord.KindCommand, command) { Source = source };

            var outcome = await ExecuteAsync(command, skipIfBusy: false);
            record.DurationMs = outcome.DurationMs;
            record.Complete(outcome.Succeeded ? ActivityRecord.ResultOk : ActivityRecord.ResultFailed, detail: outcome.Error);
            Record(record);
            return record;
        }

        /// <summary>
        /// Sends a setting command followed by <c>EER?</c> and an optional read-back query in one
        /// exchange under the I/O lock (e.g. "V1 12.350;EER?;V1?"), and records the outcome:
        /// OK, Mismatch (read-back differs from the requested value) or Failed (link error,
        /// supply error code, or invalid reply).
        /// </summary>
        /// <param name="readbackQuery">Query (may hold several, separated by ';') or null.</param>
        /// <param name="parseReadback">Turns the read-back reply lines into a number; null if invalid.</param>
        private async Task<ActivityRecord> SendVerifiedAsync(string command, string source, double? requested,
            string readbackQuery, Func<string[], double?> parseReadback, double tolerance)
        {
            command = command?.Trim() ?? string.Empty;
            var record = new ActivityRecord(ActivityRecord.KindCommand, command)
            {
                Source = source,
                Requested = requested
            };

            // IFLOCK and IFUNLOCK answer although they are not queries: EER? comes after their reply.
            int ownReplies = ReplyCount(command);
            string line = command + ";EER?" + (readbackQuery == null ? "" : ";" + readbackQuery);
            int generation = CurrentGeneration;
            var outcome = await ExecuteAsync(line, skipIfBusy: false, recordFailure: false);
            record.DurationMs = outcome.DurationMs;

            if (!outcome.Succeeded)
            {
                record.Complete(ActivityRecord.ResultFailed, detail: outcome.Error);
                Record(record);
                return record;
            }

            string[] lines = SplitLines(outcome.Response);
            record.Response = string.Join("; ", lines);

            int errorCode = 0;
            double? readback = null;
            bool valid = lines.Length > ownReplies
                         && int.TryParse(lines[ownReplies], NumberStyles.Integer, Inv, out errorCode);
            if (valid && readbackQuery != null)
            {
                readback = parseReadback(lines.Skip(ownReplies + 1).ToArray());
                valid = readback.HasValue;
            }

            if (!valid)
            {
                record.Complete(ActivityRecord.ResultFailed, detail: "invalid reply");
                Record(record);
                await RegisterProtocolErrorAsync(line, outcome.Response,
                    $"Invalid reply to '{line}': '{Escape(outcome.Response)}'", generation, recordError: false);
                return record;
            }

            _consecutiveProtocolErrors = 0;
            if (errorCode != 0)
            {
                string detail = DescribeError(errorCode);
                LoggingService.Instance.LogWarning($"'{command}' refused by the supply: {detail}",
                    nameof(PowerSupplyService), nameof(SendVerifiedAsync));
                record.Complete(ActivityRecord.ResultFailed, readback, detail);
            }
            else if (requested.HasValue && readback.HasValue && Math.Abs(readback.Value - requested.Value) > tolerance)
            {
                string detail = $"read back {readback.Value.ToString(Inv)}, expected {requested.Value.ToString(Inv)}";
                LoggingService.Instance.LogWarning($"'{command}': {detail}", nameof(PowerSupplyService), nameof(SendVerifiedAsync));
                record.Complete(ActivityRecord.ResultMismatch, readback, detail);
            }
            else
            {
                record.Complete(ActivityRecord.ResultOk, readback);
                if (HasPart(command, "TRIPRST"))
                    ClearLatchedTrips();
            }

            Record(record);
            return record;
        }

        /// <summary>Execution Error Register codes (manual p.26).</summary>
        public static string DescribeError(int code)
        {
            switch (code)
            {
                case 0: return "no error";
                case 100: return "error 100: value out of range";
                case 101: return "error 101: store data corrupted";
                case 102: return "error 102: store is empty";
                case 103: return "error 103: second output not available (single-output supply)";
                case 200: return "error 200: no write access (another interface has the lock, or this one is read-only)";
                default:
                    return code >= 1 && code <= 9
                        ? $"error {code}: internal hardware error"
                        : $"error {code}";
            }
        }

        /// <summary>
        /// Number of reply lines a command line produces: one per query, plus IFLOCK and
        /// IFUNLOCK, which answer although they do not end in '?' (manual p.32).
        /// </summary>
        public static int ReplyCount(string command)
        {
            return Parts(command).Count(p => p.EndsWith("?") || p == "IFLOCK" || p == "IFUNLOCK");
        }

        private static bool IsLocalCommand(string command)
        {
            return HasPart(command, "LOCAL");
        }

        private static bool HasPart(string command, string part)
        {
            return Parts(command).Contains(part);
        }

        private static string[] Parts(string command)
        {
            return (command ?? string.Empty)
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim().ToUpperInvariant())
                .Where(p => p.Length > 0)
                .ToArray();
        }

        /// <summary>
        /// Sends a command typed by the operator (SCPI console). Queries are logged too, with their
        /// response; a failed query is already logged as a communication error.
        /// </summary>
        public async Task<ActivityRecord> SendConsoleCommandAsync(string command, string source)
        {
            command = command?.Trim() ?? string.Empty;
            if (!IsQuery(command))
                return await SendConfigCommandAsync(command, source);

            var record = new ActivityRecord(ActivityRecord.KindQuery, command) { Source = source };
            var outcome = await ExecuteAsync(command, skipIfBusy: false);
            record.DurationMs = outcome.DurationMs;
            record.Response = outcome.Response;
            record.Result = outcome.Succeeded ? ActivityRecord.ResultOk : ActivityRecord.ResultFailed;
            record.Detail = outcome.Error;
            if (outcome.Succeeded)
                Record(record);
            return record;
        }

        public static bool IsQuery(string command)
        {
            return command.Trim().EndsWith("?");
        }

        /// <param name="recordFailure">
        /// False when the caller records the failure itself (setting commands), so a failed query
        /// line is not logged twice.
        /// </param>
        private async Task<CommandOutcome> ExecuteAsync(string command, bool skipIfBusy, bool recordFailure = true)
        {
            command = command?.Trim();
            if (string.IsNullOrEmpty(command))
                return CommandOutcome.Failed("empty command");
            if (!IsConnected)
                return CommandOutcome.Failed("not connected");

            // Background polls are skipped when the link is busy; user commands wait their turn.
            if (!await _ioLock.WaitAsync(skipIfBusy ? 0 : CommandLockTimeoutMs))
            {
                if (skipIfBusy)
                    return CommandOutcome.Failed("skipped: link busy");

                LoggingService.Instance.LogWarning($"Command '{command}' not sent: link busy.",
                    nameof(PowerSupplyService), nameof(ExecuteAsync));
                if (recordFailure && IsQuery(command))
                    RecordCommError(command, null, "not sent: link busy");
                return CommandOutcome.Failed("not sent: link busy");
            }

            try
            {
                return await ExecuteLockedAsync(command, recordFailure);
            }
            finally
            {
                _ioLock.Release();
            }
        }

        private async Task<CommandOutcome> ExecuteLockedAsync(string command, bool recordFailure)
        {
            NetworkStream stream;
            int generation;
            lock (_stateLock)
            {
                stream = _stream;
                generation = _connectionGeneration;
            }
            if (stream == null || !IsConnected)
                return CommandOutcome.Failed("not connected");

            int replies = ReplyCount(command);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                byte[] data = Encoding.ASCII.GetBytes(command + "\n");
                TraceService.Instance.Write(">>", command);
                await WithTimeout(stream.WriteAsync(data, 0, data.Length), WriteTimeoutMs, "write");

                if (replies == 0)
                    return CommandOutcome.Ok(string.Empty, stopwatch.Elapsed.TotalMilliseconds);

                string response = await ReadLinesAsync(stream, replies, ResponseTimeoutMs);
                TraceService.Instance.Write("<<", response, stopwatch.Elapsed);
                return CommandOutcome.Ok(response, stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                // After a timeout or I/O error the stream position is unknown (an abandoned read
                // may still consume the next reply), so the only safe recovery is a fresh socket.
                double elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
                TraceService.Instance.Write("!!", $"{command}: {ex.Message}", stopwatch.Elapsed);
                LoggingService.Instance.LogWarning($"Command '{command}' failed: {ex.Message}",
                    nameof(PowerSupplyService), nameof(ExecuteLockedAsync));
                if (recordFailure && replies > 0)
                    RecordCommError(command, null, ex.Message, elapsedMs);
                HandleConnectionFault(generation, ex.Message);
                return CommandOutcome.Failed(ex.Message, elapsedMs);
            }
        }

        private struct CommandOutcome
        {
            public string Response;
            public string Error;
            public double? DurationMs;

            public bool Succeeded => Error == null;

            public static CommandOutcome Ok(string response, double durationMs)
            {
                return new CommandOutcome { Response = response, DurationMs = durationMs };
            }

            public static CommandOutcome Failed(string error, double? durationMs = null)
            {
                return new CommandOutcome { Error = error, DurationMs = durationMs };
            }
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
        private async Task RegisterProtocolErrorAsync(string command, string response, string description, int generation,
            bool recordError = true)
        {
            if (recordError)
                RecordCommError(command, response, description);
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

        public async Task<string> GetIdentityAsync()
        {
            return (await ExecuteAsync(PowerSupplyCommands.GetPsIdentityCommand, skipIfBusy: false)).Response;
        }

        /// <summary>Reads live output values and status. Returns null if the read failed or was skipped.</summary>
        public async Task<PowerSupplyStatus> GetStatusAsync(bool skipIfBusy = true)
        {
            int generation = CurrentGeneration;
            string response = (await ExecuteAsync(PowerSupplyCommands.GetPsStatusCommand, skipIfBusy)).Response;
            if (response == null)
                return null;

            var status = ParsePsStatus(response);
            if (status == null)
            {
                await RegisterProtocolErrorAsync(PowerSupplyCommands.GetPsStatusCommand, response,
                    $"Invalid status response: '{Escape(response)}'", generation);
                return null;
            }

            _consecutiveProtocolErrors = 0;
            return status;
        }

        /// <summary>Reads the programmed set-points. Returns null if the read failed or was skipped.</summary>
        public async Task<PowerSupplySettingStatus> GetPowerSupplySettingsAsync(bool skipIfBusy = false)
        {
            int generation = CurrentGeneration;
            string response = (await ExecuteAsync(PowerSupplyCommands.GetPsSettingStatusCommand, skipIfBusy)).Response;
            if (response == null)
                return null;

            var settings = ParsePsSettingStatus(response);
            if (settings == null)
            {
                await RegisterProtocolErrorAsync(PowerSupplyCommands.GetPsSettingStatusCommand, response,
                    $"Invalid settings response: '{Escape(response)}'", generation);
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

        // Setting resolutions (manual p.4): values are rounded to these before they are sent.
        public const double VoltageStep = 0.001;
        public const double CurrentStep = 0.01;
        public const double OvpStep = 0.1;
        public const double OcpStep = 0.1;

        // Each returns the command's record (check .Success / .Result). The record has already been
        // raised through ActivityRecorded with its EER? result and read-back.

        public Task<ActivityRecord> SetVoltageAsync(double voltage, string source = "App")
        {
            double value = RoundTo(voltage, VoltageStep);
            return SendVerifiedAsync("V1 " + value.ToString("F3", Inv), source, value,
                "V1?", r => ReadPrefixed(r, "V1"), VoltageStep / 2);
        }

        public Task<ActivityRecord> SetCurrentAsync(double current, string source = "App")
        {
            double value = RoundTo(current, CurrentStep);
            return SendVerifiedAsync("I1 " + value.ToString("F2", Inv), source, value,
                "I1?", r => ReadPrefixed(r, "I1"), CurrentStep / 2);
        }

        public Task<ActivityRecord> SetOutputAsync(bool enabled, string source = "App")
        {
            return SendVerifiedAsync(enabled ? "OP1 1" : "OP1 0", source, enabled ? 1 : 0,
                "OP1?", r => r.Length == 1 ? ReadBit(r[0]) : null, 0);
        }

        public Task<ActivityRecord> SetOverVoltageProtectionAsync(double voltage, string source = "App")
        {
            // The protection is always enabled on the QPX1200SP; only the level can be set.
            double value = RoundTo(voltage, OvpStep);
            return SendVerifiedAsync("OVP1 " + value.ToString("F1", Inv), source, value,
                "OVP1?", r => ReadPrefixed(r, "VP1"), OvpStep / 2);
        }

        public Task<ActivityRecord> SetOverCurrentProtectionAsync(double current, string source = "App")
        {
            // The protection is always enabled on the QPX1200SP; only the level can be set.
            double value = RoundTo(current, OcpStep);
            return SendVerifiedAsync("OCP1 " + value.ToString("F1", Inv), source, value,
                "OCP1?", r => ReadPrefixed(r, "CP1"), OcpStep / 2);
        }

        /// <summary>TRIPRST. The app's trip latches are cleared when the supply accepts it.</summary>
        public Task<ActivityRecord> TripResetAsync(string source = "App")
        {
            return SendVerifiedAsync("TRIPRST", source, null, null, null, 0);
        }

        /// <summary>
        /// LOCAL: hands the front panel back. Sent on its own and not checked, because any further
        /// character puts the supply back into Remote.
        /// </summary>
        public Task<ActivityRecord> GoLocalAsync(string source = "App")
        {
            return SendUnverifiedAsync("LOCAL", source);
        }

        private static double? ReadPrefixed(string[] lines, string prefix)
        {
            return lines.Length == 1 && TryParseWithPrefix(lines[0], prefix, out double value) ? value : (double?)null;
        }

        private static double? ReadBit(string text)
        {
            return text == "1" ? 1 : text == "0" ? 0 : (double?)null;
        }

        public static double RoundTo(double value, double step)
        {
            return Math.Round(Math.Round(value / step, MidpointRounding.AwayFromZero) * step, 6);
        }

        #endregion
    }
}
