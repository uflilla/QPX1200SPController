using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace QPX1200SPController.Services
{
    /// <summary>
    /// TCP server for the remote protocol (docs/REMOTE_PROTOCOL.md): one JSON object per line, one
    /// reply per request, one client at a time. Knows nothing about the supply; each request is
    /// passed to the handler given to the constructor, one at a time, in the order received.
    /// </summary>
    public sealed class RemoteServer
    {
        private const int MaxLineLength = 64 * 1024;
        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        private readonly Func<Dictionary<string, object>, Task<Dictionary<string, object>>> _handler;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly object _sync = new object();
        private TcpListener _listener;
        private TcpClient _client;

        public RemoteServer(Func<Dictionary<string, object>, Task<Dictionary<string, object>>> handler)
        {
            _handler = handler;
        }

        /// <summary>Raised (on a background thread) when a client connects or leaves; the address, or null.</summary>
        public event Action<string> ClientChanged;

        public bool IsListening { get { lock (_sync) return _listener != null; } }
        public int Port { get; private set; }

        /// <summary>Address of the connected client ("192.168.1.20"), or null.</summary>
        public string ClientAddress { get; private set; }

        /// <summary>Opens the port on all network interfaces. Throws if the port is in use.</summary>
        public void Start(int port)
        {
            lock (_sync)
            {
                if (_listener != null)
                    return;
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                _listener = listener;
                Port = port;
            }
            LoggingService.Instance.LogInfo($"Remote control: listening on port {port}.", nameof(RemoteServer), nameof(Start));
            _ = AcceptLoopAsync(_listener);
        }

        /// <summary>Closes the port and the client connection, if any.</summary>
        public void Stop()
        {
            TcpListener listener;
            TcpClient client;
            lock (_sync)
            {
                listener = _listener;
                client = _client;
                _listener = null;
            }
            if (listener == null)
                return;
            listener.Stop();
            client?.Close();
            LoggingService.Instance.LogInfo("Remote control: stopped listening.", nameof(RemoteServer), nameof(Stop));
        }

        private async Task AcceptLoopAsync(TcpListener listener)
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return; // Stop() closed the listener
                }

                bool busy;
                lock (_sync)
                {
                    busy = _client != null || _listener != listener;
                    if (!busy)
                        _client = client;
                }

                if (busy)
                {
                    _ = RefuseAsync(client);
                    continue;
                }
                _ = ServeAsync(client);
            }
        }

        private async Task RefuseAsync(TcpClient client)
        {
            string address = AddressOf(client);
            LoggingService.Instance.LogWarning($"Remote control: refused {address}, another client is connected.", nameof(RemoteServer), nameof(RefuseAsync));
            try
            {
                using (client)
                using (var writer = new StreamWriter(client.GetStream(), Utf8) { NewLine = "\n" })
                {
                    await writer.WriteLineAsync(Serialize(Error("Another client is connected"))).ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // The client went away already.
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            string address = AddressOf(client);
            ClientAddress = address;
            LoggingService.Instance.LogInfo($"Remote control: client {address} connected.", nameof(RemoteServer), nameof(ServeAsync));
            ClientChanged?.Invoke(address);
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                using (var reader = new StreamReader(stream, Utf8))
                using (var writer = new StreamWriter(stream, Utf8) { NewLine = "\n", AutoFlush = true })
                {
                    while (true)
                    {
                        string line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line == null)
                            break;
                        if (line.Trim().Length == 0)
                            continue;

                        TraceService.Instance.Write("R<", $"{address} {line}");
                        string reply = Serialize(await HandleLineAsync(line).ConfigureAwait(false));
                        TraceService.Instance.Write("R>", $"{address} {reply}");
                        await writer.WriteLineAsync(reply).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is SocketException || ex is InvalidOperationException)
            {
                // Connection closed by the client or by Stop().
            }
            finally
            {
                client.Close();
                lock (_sync)
                {
                    if (_client == client)
                        _client = null;
                }
                ClientAddress = null;
                LoggingService.Instance.LogInfo($"Remote control: client {address} left.", nameof(RemoteServer), nameof(ServeAsync));
                ClientChanged?.Invoke(null);
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
                return await _handler(request).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LoggingService.Instance.LogException(ex, nameof(RemoteServer), nameof(HandleLineAsync));
                return Error("Internal error: " + ex.Message);
            }
        }

        private string Serialize(Dictionary<string, object> reply)
        {
            return _json.Serialize(reply);
        }

        public static Dictionary<string, object> Error(string message)
        {
            return new Dictionary<string, object> { ["ok"] = false, ["error"] = message };
        }

        private static string AddressOf(TcpClient client)
        {
            try
            {
                return ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
            }
            catch (Exception)
            {
                return "unknown";
            }
        }
    }
}
