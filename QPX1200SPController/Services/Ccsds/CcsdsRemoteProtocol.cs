using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using QPX1200SPController.Models;

namespace QPX1200SPController.Services.Ccsds
{
    /// <summary>
    /// The CCSDS remote protocol (docs/REMOTE_PROTOCOL_CCSDS.md): TC packets in, one reply TM per TC,
    /// and housekeeping TM after every poll without a request. Every packet travels between the sync
    /// header and footer from settings.xml; the receiver resyncs on the header.
    /// </summary>
    public sealed class CcsdsRemoteProtocol : IRemoteProtocol
    {
        private readonly IRemoteCommands _commands;
        private readonly CcsdsSettings _settings;
        private volatile Session _session;

        public CcsdsRemoteProtocol(IRemoteCommands commands, CcsdsSettings settings)
        {
            _commands = commands;
            _settings = settings;
        }

        public string Name => "CCSDS";

        public async Task ServeAsync(Stream stream, string address)
        {
            var session = new Session(this, stream, address);
            _session = session;
            try
            {
                await session.RunAsync().ConfigureAwait(false);
            }
            finally
            {
                if (_session == session)
                    _session = null;
            }
        }

        public Task RefuseAsync(Stream stream, string address)
        {
            return new Session(this, stream, address).SendErrorReportAsync(null, RemoteError.AnotherClient, "Another client is connected");
        }

        public void PublishHousekeeping(RemoteStatus status)
        {
            _session?.PublishHousekeeping(status);
        }

        /// <summary>One client connection: its read loop, its TM counters and its write lock.</summary>
        private sealed class Session
        {
            private const string TcSetName = "SET";
            private const string TcOutputName = "OUTPUT";
            private const string TcReadName = "READ";
            private const int SetDataLength = 20;
            private const int OutputDataLength = 1;

            private readonly CcsdsRemoteProtocol _owner;
            private readonly CcsdsSettings _settings;
            private readonly Stream _stream;
            private readonly string _address;
            private readonly byte[] _header;
            private readonly byte[] _footer;
            private readonly DateTime _epochUtc;
            private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
            private readonly Dictionary<int, int> _subcounters = new Dictionary<int, int>();
            private int _sequenceCount;
            private int _housekeepingPending;

            // Receive buffer: _count bytes from _start.
            private byte[] _buffer = new byte[4096];
            private int _start;
            private int _count;

            public Session(CcsdsRemoteProtocol owner, Stream stream, string address)
            {
                _owner = owner;
                _settings = owner._settings;
                _stream = stream;
                _address = address;
                _header = _settings.SyncHeaderBytes;
                _footer = _settings.SyncFooterBytes;
                _epochUtc = _settings.EpochUtc;
            }

            #region Receiving

            public async Task RunAsync()
            {
                int minLength = CcsdsCodec.MinTcLength(_settings);
                while (true)
                {
                    if (_header.Length > 0 && !await FindHeaderAsync().ConfigureAwait(false))
                        return;

                    if (!await FillAsync(_header.Length + CcsdsCodec.PrimaryHeaderLength).ConfigureAwait(false))
                        return;
                    int total = CcsdsCodec.TotalLength(_buffer, _start + _header.Length);
                    if (total < minLength || total > CcsdsCodec.MaxTcLength)
                    {
                        var partial = CcsdsCodec.ParseTc(Copy(_header.Length, CcsdsCodec.PrimaryHeaderLength), _settings);
                        await SendErrorReportAsync(partial, RemoteError.Malformed,
                            $"packet length {total} bytes is outside {minLength}–{CcsdsCodec.MaxTcLength}").ConfigureAwait(false);
                        if (!await ResyncAsync().ConfigureAwait(false))
                            return;
                        continue;
                    }

                    if (!await FillAsync(_header.Length + total + _footer.Length).ConfigureAwait(false))
                        return;
                    byte[] packet = Copy(_header.Length, total);
                    if (!Matches(_header.Length + total, _footer))
                    {
                        var partial = CcsdsCodec.ParseTc(packet, _settings);
                        Trace("R<", _header.Length + total + _footer.Length, "footer missing");
                        await SendErrorReportAsync(partial, RemoteError.FooterMissing,
                            $"the {_footer.Length} bytes after the packet are not the sync footer {_settings.SyncFooter}").ConfigureAwait(false);
                        if (!await ResyncAsync().ConfigureAwait(false))
                            return;
                        continue;
                    }

                    Trace("R<", _header.Length + total + _footer.Length, null);
                    Consume(_header.Length + total + _footer.Length);
                    await HandleTcAsync(packet).ConfigureAwait(false);
                }
            }

            /// <summary>
            /// After a bad packet: with a sync header, search again from the byte after it; without one
            /// the packet boundaries are lost, so the connection is closed (false).
            /// </summary>
            private Task<bool> ResyncAsync()
            {
                if (_header.Length == 0)
                {
                    LoggingService.Instance.LogWarning($"Remote control (CCSDS): no sync header to resync on, closing {_address}.",
                        nameof(CcsdsRemoteProtocol), nameof(ResyncAsync));
                    return Task.FromResult(false);
                }
                Consume(1);
                return Task.FromResult(true);
            }

            /// <summary>Skips bytes until the buffer starts with the sync header. False at the end of the stream.</summary>
            private async Task<bool> FindHeaderAsync()
            {
                int skipped = 0;
                while (true)
                {
                    int index = IndexOfHeader();
                    if (index >= 0)
                    {
                        skipped += index;
                        Consume(index);
                        break;
                    }
                    // Keep a possible partial header at the end.
                    int keep = Math.Min(_count, _header.Length - 1);
                    skipped += _count - keep;
                    Consume(_count - keep);
                    if (!await FillAsync(_count + 1).ConfigureAwait(false))
                        return false;
                }
                if (skipped > 0)
                {
                    TraceService.Instance.Write("R<", $"{_address} skipped {skipped} bytes before the sync header");
                    LoggingService.Instance.LogWarning($"Remote control (CCSDS): skipped {skipped} bytes before the sync header.",
                        nameof(CcsdsRemoteProtocol), nameof(FindHeaderAsync));
                }
                return true;
            }

            private int IndexOfHeader()
            {
                for (int k = 0; k + _header.Length <= _count; k++)
                {
                    if (Matches(k, _header))
                        return k;
                }
                return -1;
            }

            /// <summary>True if the buffer has <paramref name="marker"/> at <paramref name="offset"/> (from the start of the unread bytes).</summary>
            private bool Matches(int offset, byte[] marker)
            {
                for (int k = 0; k < marker.Length; k++)
                {
                    if (_buffer[_start + offset + k] != marker[k])
                        return false;
                }
                return true;
            }

            /// <summary>Reads until at least <paramref name="needed"/> unread bytes are buffered. False at the end of the stream.</summary>
            private async Task<bool> FillAsync(int needed)
            {
                if (_count >= needed)
                    return true;
                if (_start + needed > _buffer.Length)
                {
                    var target = needed > _buffer.Length ? new byte[Math.Max(needed, _buffer.Length * 2)] : _buffer;
                    Array.Copy(_buffer, _start, target, 0, _count);
                    _buffer = target;
                    _start = 0;
                }
                while (_count < needed)
                {
                    int read = await _stream.ReadAsync(_buffer, _start + _count, _buffer.Length - _start - _count).ConfigureAwait(false);
                    if (read <= 0)
                        return false;
                    _count += read;
                }
                return true;
            }

            private void Consume(int count)
            {
                _start += count;
                _count -= count;
                if (_count == 0)
                    _start = 0;
            }

            private byte[] Copy(int offset, int count)
            {
                var bytes = new byte[count];
                Array.Copy(_buffer, _start + offset, bytes, 0, count);
                return bytes;
            }

            private void Trace(string direction, int frameLength, string note)
            {
                if (!TraceService.Instance.IsEnabled)
                    return;
                string text = $"{_address} {BigEndian.Hex(_buffer, _start, frameLength)}";
                TraceService.Instance.Write(direction, note == null ? text : text + "  (" + note + ")");
            }

            #endregion

            #region Telecommands

            private async Task HandleTcAsync(byte[] packet)
            {
                var tc = CcsdsCodec.ParseTc(packet, _settings);
                if (tc.Error != RemoteError.None)
                {
                    await SendErrorReportAsync(tc, tc.Error, tc.Message).ConfigureAwait(false);
                    return;
                }

                string name = Identify(tc, out RemoteError error, out string message);
                if (name == null)
                {
                    await SendErrorReportAsync(tc, error, message).ConfigureAwait(false);
                    return;
                }

                int expected = name == TcSetName ? SetDataLength : name == TcOutputName ? OutputDataLength : 0;
                if (tc.Data.Length != expected)
                {
                    await SendErrorReportAsync(tc, RemoteError.Malformed,
                        $"{name} needs {expected} bytes of application data, got {tc.Data.Length}").ConfigureAwait(false);
                    return;
                }

                try
                {
                    switch (name)
                    {
                        case TcSetName: await SetAsync(tc).ConfigureAwait(false); break;
                        case TcOutputName: await OutputAsync(tc).ConfigureAwait(false); break;
                        default: await ReadAsync(tc).ConfigureAwait(false); break;
                    }
                }
                catch (Exception ex) when (!(ex is IOException || ex is ObjectDisposedException))
                {
                    LoggingService.Instance.LogException(ex, nameof(CcsdsRemoteProtocol), nameof(HandleTcAsync));
                    await SendErrorReportAsync(tc, RemoteError.Internal, "Internal error: " + ex.Message).ConfigureAwait(false);
                }
            }

            /// <summary>"SET", "OUTPUT" or "READ"; null with the error if no TC has this service, subtype and SID.</summary>
            private string Identify(TcPacket tc, out RemoteError error, out string message)
            {
                var tcs = new[]
                {
                    Tuple.Create(TcSetName, _settings.TcSet),
                    Tuple.Create(TcOutputName, _settings.TcOutput),
                    Tuple.Create(TcReadName, _settings.TcRead)
                };
                bool serviceKnown = false;
                foreach (var candidate in tcs)
                {
                    var id = candidate.Item2;
                    if (id.Service != tc.Service || id.Subtype != tc.Subtype)
                        continue;
                    serviceKnown = true;
                    if (id.Sid == tc.Sid)
                    {
                        error = RemoteError.None;
                        message = null;
                        return candidate.Item1;
                    }
                }
                error = serviceKnown ? RemoteError.UnknownSid : RemoteError.UnknownService;
                message = serviceKnown
                    ? $"unknown SID {tc.Sid} for service {tc.Service}/{tc.Subtype}"
                    : $"unknown service {tc.Service}/{tc.Subtype}";
                return null;
            }

            private async Task SetAsync(TcPacket tc)
            {
                // Packet order V, I, OVP, OCP; each apply (u8) + value (f32).
                var settings = new[] { RemoteSetting.Voltage, RemoteSetting.Current, RemoteSetting.Ovp, RemoteSetting.Ocp };
                var apply = new int[4];
                var values = new double[4];
                for (int k = 0; k < 4; k++)
                {
                    apply[k] = tc.Data[5 * k];
                    values[k] = BigEndian.F32(tc.Data, 5 * k + 1);
                }

                SetResult result;
                try
                {
                    for (int k = 0; k < 4; k++)
                    {
                        if (apply[k] > 1)
                            throw new RemoteException(RemoteError.InvalidParameter, $"{SettingName(settings[k])} apply is {apply[k]}, must be 0 or 1");
                    }
                    var request = new SetRequest
                    {
                        Voltage = apply[0] == 1 ? values[0] : (double?)null,
                        Current = apply[1] == 1 ? values[1] : (double?)null,
                        Ovp = apply[2] == 1 ? values[2] : (double?)null,
                        Ocp = apply[3] == 1 ? values[3] : (double?)null
                    };
                    result = await _owner._commands.SetAsync(request).ConfigureAwait(false);
                }
                catch (RemoteException ex)
                {
                    result = new SetResult { Error = ex.Code, Message = ex.Message };
                }

                var w = ReplyBlock(tc, result.Error);
                for (int k = 0; k < 4; k++)
                {
                    var sent = result.Results.Find(r => r.Setting == settings[k]);
                    if (sent == null || sent.Result == RemoteResult.NotSent)
                        w.U8((int)RemoteResult.NotSent).F32(values[k]).F32(0);
                    else
                        w.U8((int)sent.Result).F32(sent.Requested ?? values[k]).F32(sent.Readback ?? 0);
                }
                await SendReplyAsync(tc, TcSetName, _settings.TmSetReport, DateTime.UtcNow, w.ToArray(), result.Error, result.Message).ConfigureAwait(false);
            }

            private async Task OutputAsync(TcPacket tc)
            {
                OutputResult result;
                int value = tc.Data[0];
                try
                {
                    if (value > 1)
                        throw new RemoteException(RemoteError.InvalidParameter, $"on is {value}, must be 0 or 1");
                    result = await _owner._commands.OutputAsync(value == 1).ConfigureAwait(false);
                }
                catch (RemoteException ex)
                {
                    result = new OutputResult { Error = ex.Code, Message = ex.Message, Result = RemoteResult.NotSent };
                }

                var w = ReplyBlock(tc, result.Error)
                    .U8(result.On.HasValue ? (result.On.Value ? 1 : 0) : 255)
                    .U8((int)result.Result);
                await SendReplyAsync(tc, TcOutputName, _settings.TmOutputReport, DateTime.UtcNow, w.ToArray(), result.Error, result.Message).ConfigureAwait(false);
            }

            private async Task ReadAsync(TcPacket tc)
            {
                RemoteStatus status;
                RemoteError error = RemoteError.None;
                string message = null;
                try
                {
                    status = await _owner._commands.ReadAsync().ConfigureAwait(false);
                }
                catch (RemoteException ex)
                {
                    error = ex.Code;
                    message = ex.Message;
                    status = RemoteStatus.Disconnected(DateTime.Now);
                }

                var w = ReplyBlock(tc, error);
                WriteStatusBlock(w, error == RemoteError.None ? status : RemoteStatus.Disconnected(status.Timestamp));
                await SendReplyAsync(tc, TcReadName, _settings.TmReadReport, status.Timestamp.ToUniversalTime(), w.ToArray(), error, message).ConfigureAwait(false);
            }

            private static string SettingName(RemoteSetting setting)
            {
                switch (setting)
                {
                    case RemoteSetting.Voltage: return "voltage";
                    case RemoteSetting.Current: return "current";
                    case RemoteSetting.Ovp: return "ovp";
                    default: return "ocp";
                }
            }

            #endregion

            #region Telemetry

            /// <summary>TC sequence count (u32, all ones if unknown) and error code (u8).</summary>
            private static BigEndianWriter ReplyBlock(TcPacket tc, RemoteError error)
            {
                return new BigEndianWriter()
                    .U32(tc?.SequenceCount.HasValue == true ? (uint)tc.SequenceCount.Value : uint.MaxValue)
                    .U8((int)error);
            }

            /// <summary>The 33-byte status block of HK and the READ report.</summary>
            private static void WriteStatusBlock(BigEndianWriter w, RemoteStatus s)
            {
                w.Flag(s.Connected)
                    .Flag(s.OutputOn)
                    .U8((int)s.Mode)
                    .U8((int)s.Trip)
                    .Flag(s.SequenceRunning)
                    .F32(s.Voltage)
                    .F32(s.Current)
                    .F32(s.Power)
                    .F32(s.SetVoltage)
                    .F32(s.SetCurrent)
                    .F32(s.Ovp)
                    .F32(s.Ocp);
            }

            private Task SendReplyAsync(TcPacket tc, string name, CcsdsPacketId id, DateTime timeUtc, byte[] data, RemoteError error, string message)
            {
                if (error != RemoteError.None)
                    LoggingService.Instance.LogWarning($"Remote control (CCSDS): TC seq {tc.SequenceCount} {name} refused, error {(int)error}: {message}",
                        nameof(CcsdsRemoteProtocol), nameof(SendReplyAsync));
                string summary = $"{name} report to TC seq {tc.SequenceCount}, error {(int)error}" + (message == null ? "" : ": " + message);
                return SendTmAsync(id, timeUtc, data, "R>", summary);
            }

            /// <summary>ERROR report: reply block, then the TC's service, subtype and SID (all ones if unknown).</summary>
            public Task SendErrorReportAsync(TcPacket tc, RemoteError error, string message)
            {
                LoggingService.Instance.LogWarning($"Remote control (CCSDS): {_address} TC seq {tc?.SequenceCount?.ToString() ?? "?"} refused, error {(int)error}: {message}",
                    nameof(CcsdsRemoteProtocol), nameof(SendErrorReportAsync));
                var w = ReplyBlock(tc, error)
                    .U8(tc?.Service ?? 0xFF)
                    .U8(tc?.Subtype ?? 0xFF)
                    .U32(tc?.Sid.HasValue == true ? (uint)tc.Sid.Value : uint.MaxValue);
                return SendTmAsync(_settings.TmErrorReport, DateTime.UtcNow, w.ToArray(), "R>", $"ERROR report, error {(int)error}: {message}");
            }

            /// <summary>Sends HK on a background thread; skipped if the previous HK is still being sent.</summary>
            public void PublishHousekeeping(RemoteStatus status)
            {
                if (Interlocked.CompareExchange(ref _housekeepingPending, 1, 0) != 0)
                    return;
                Task.Run(async () =>
                {
                    try
                    {
                        var w = new BigEndianWriter();
                        WriteStatusBlock(w, status);
                        await SendTmAsync(_settings.TmHousekeeping, status.Timestamp.ToUniversalTime(), w.ToArray(), "RH", null).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // The connection is closing; the read loop ends it.
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _housekeepingPending, 0);
                    }
                });
            }

            /// <summary>Numbers, frames and writes one TM. Packets go out whole, in the order of their sequence counts.</summary>
            private async Task SendTmAsync(CcsdsPacketId id, DateTime timeUtc, byte[] data, string traceDirection, string summary)
            {
                await _writeLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    int key = (id.Service << 8) | id.Subtype;
                    _subcounters.TryGetValue(key, out int subcounter);
                    _subcounters[key] = (subcounter + 1) & 0xFF;
                    int sequenceCount = _sequenceCount;
                    _sequenceCount = (_sequenceCount + 1) & 0x3FFF;

                    byte[] packet = CcsdsCodec.BuildTm(_settings.TmApid, sequenceCount, id, subcounter, timeUtc, _epochUtc, data);
                    var frame = new byte[_header.Length + packet.Length + _footer.Length];
                    _header.CopyTo(frame, 0);
                    packet.CopyTo(frame, _header.Length);
                    _footer.CopyTo(frame, _header.Length + packet.Length);

                    await _stream.WriteAsync(frame, 0, frame.Length).ConfigureAwait(false);
                    await _stream.FlushAsync().ConfigureAwait(false);

                    if (TraceService.Instance.IsEnabled)
                        TraceService.Instance.Write(traceDirection, $"{_address} {BigEndian.Hex(frame, 0, frame.Length)}" + (summary == null ? "" : "  (" + summary + ")"));
                }
                finally
                {
                    _writeLock.Release();
                }
            }

            #endregion
        }
    }
}
