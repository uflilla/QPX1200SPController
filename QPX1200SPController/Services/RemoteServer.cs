using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace QPX1200SPController.Services
{
    /// <summary>
    /// TCP server for remote control: one client at a time, on all network interfaces. Knows nothing
    /// about the wire format or the supply; the <see cref="IRemoteProtocol"/> given to
    /// <see cref="Start"/> (JSON lines or CCSDS packets) serves the client.
    /// </summary>
    public sealed class RemoteServer
    {
        private readonly object _sync = new object();
        private TcpListener _listener;
        private TcpClient _client;
        private IRemoteProtocol _protocol;

        /// <summary>Raised (on a background thread) when a client connects or leaves; the address, or null.</summary>
        public event Action<string> ClientChanged;

        public bool IsListening { get { lock (_sync) return _listener != null; } }
        public int Port { get; private set; }

        /// <summary>The protocol given to the last <see cref="Start"/>.</summary>
        public IRemoteProtocol Protocol { get { lock (_sync) return _protocol; } }

        /// <summary>Address of the connected client ("192.168.1.20"), or null.</summary>
        public string ClientAddress { get; private set; }

        /// <summary>Opens the port on all network interfaces. Throws if the port is in use.</summary>
        public void Start(int port, IRemoteProtocol protocol)
        {
            TcpListener listener;
            lock (_sync)
            {
                if (_listener != null)
                    return;
                listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                _listener = listener;
                _protocol = protocol;
                Port = port;
            }
            LoggingService.Instance.LogInfo($"Remote control: listening on port {port} ({protocol.Name}).", nameof(RemoteServer), nameof(Start));
            _ = AcceptLoopAsync(listener, protocol);
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

        /// <summary>Passes housekeeping to the connected client's protocol (CCSDS sends it; JSON ignores it).</summary>
        public void PublishHousekeeping(RemoteStatus status)
        {
            IRemoteProtocol protocol;
            lock (_sync)
            {
                if (_listener == null || _client == null)
                    return;
                protocol = _protocol;
            }
            protocol?.PublishHousekeeping(status);
        }

        private async Task AcceptLoopAsync(TcpListener listener, IRemoteProtocol protocol)
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
                    _ = RefuseAsync(client, protocol);
                    continue;
                }
                _ = ServeAsync(client, protocol);
            }
        }

        private static async Task RefuseAsync(TcpClient client, IRemoteProtocol protocol)
        {
            string address = AddressOf(client);
            LoggingService.Instance.LogWarning($"Remote control: refused {address}, another client is connected.", nameof(RemoteServer), nameof(RefuseAsync));
            try
            {
                using (client)
                {
                    await protocol.RefuseAsync(client.GetStream(), address).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // The client went away already.
            }
        }

        private async Task ServeAsync(TcpClient client, IRemoteProtocol protocol)
        {
            string address = AddressOf(client);
            ClientAddress = address;
            LoggingService.Instance.LogInfo($"Remote control: client {address} connected.", nameof(RemoteServer), nameof(ServeAsync));
            ClientChanged?.Invoke(address);
            try
            {
                client.NoDelay = true;
                await protocol.ServeAsync(client.GetStream(), address).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is SocketException || ex is InvalidOperationException)
            {
                // Connection closed by the client or by Stop().
            }
            catch (Exception ex)
            {
                LoggingService.Instance.LogException(ex, nameof(RemoteServer), nameof(ServeAsync));
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
