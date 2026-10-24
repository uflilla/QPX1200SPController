using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QPX1200SPSimulator
{
    /// <summary>
    /// SCPI-over-TCP front end for <see cref="SimulatedSupply"/> with switches for
    /// injecting communication faults (silence, delays, garbage, dropped links, outages).
    /// </summary>
    public sealed class SimulatorServer
    {
        private readonly SimulatedSupply _supply;
        private readonly IPAddress _bindAddress;
        private readonly int _port;
        private readonly object _clientsLock = new object();
        private readonly List<TcpClient> _clients = new List<TcpClient>();
        private TcpListener _listener;
        private int _garbleCount;

        public SimulatorServer(SimulatedSupply supply, IPAddress bindAddress, int port)
        {
            _supply = supply;
            _bindAddress = bindAddress;
            _port = port;
        }

        /// <summary>Receive commands but never answer (simulates a hung instrument).</summary>
        public volatile bool Mute;

        /// <summary>Extra delay before every response, in milliseconds.</summary>
        public volatile int ResponseDelayMs;

        /// <summary>Send each response in two TCP segments to exercise partial reads.</summary>
        public volatile bool Fragment = true;

        /// <summary>Print every command and response.</summary>
        public volatile bool Verbose = true;

        public event Action<string> Log;

        public bool IsListening => _listener != null;

        public int ClientCount
        {
            get { lock (_clientsLock) return _clients.Count; }
        }

        /// <summary>Garble the next <paramref name="count"/> query responses.</summary>
        public void GarbleNext(int count)
        {
            Interlocked.Exchange(ref _garbleCount, count);
        }

        public void Start()
        {
            if (_listener != null)
                return;
            _listener = new TcpListener(_bindAddress, _port);
            _listener.Start();
            Log?.Invoke($"Listening on {_bindAddress}:{_port}");
            var listener = _listener;
            Task.Run(() => AcceptLoop(listener));
        }

        public void Stop()
        {
            var listener = _listener;
            _listener = null;
            try { listener?.Stop(); } catch { }
            DropClients();
        }

        public int DropClients()
        {
            List<TcpClient> clients;
            lock (_clientsLock)
            {
                clients = new List<TcpClient>(_clients);
                _clients.Clear();
            }
            foreach (var c in clients)
            {
                try { c.Close(); } catch { }
            }
            return clients.Count;
        }

        /// <summary>Simulates the instrument dropping off the network for a while.</summary>
        public async Task OutageAsync(TimeSpan duration)
        {
            Stop();
            Log?.Invoke($"Network outage for {duration.TotalSeconds:F0} s...");
            await Task.Delay(duration);
            Start();
        }

        private async Task AcceptLoop(TcpListener listener)
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync();
                }
                catch
                {
                    return; // listener stopped
                }

                client.NoDelay = true;
                lock (_clientsLock)
                    _clients.Add(client);
                Log?.Invoke($"Client connected from {client.Client.RemoteEndPoint}");
                var _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            string remote = SafeEndpoint(client);
            try
            {
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                string line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (line.Trim().Length == 0)
                        continue;
                    if (Verbose)
                        Log?.Invoke(">> " + line.Trim());

                    var response = new StringBuilder();
                    foreach (string part in line.Split(';'))
                    {
                        if (part.Trim().Length == 0)
                            continue;
                        bool recognised;
                        string reply = _supply.Handle(part, out recognised);
                        if (!recognised)
                            Log?.Invoke($"Unknown command '{part.Trim()}' (no response sent)");
                        if (reply != null)
                            response.Append(reply).Append("\r\n");
                    }

                    if (response.Length == 0)
                        continue;

                    if (Mute)
                    {
                        if (Verbose)
                            Log?.Invoke("   (muted - no response)");
                        continue;
                    }

                    string text = response.ToString();
                    if (Interlocked.Decrement(ref _garbleCount) >= 0)
                    {
                        text = "#GARBAGE\r\n" + text.Substring(text.IndexOf('\n') + 1);
                        Log?.Invoke("   (garbled response)");
                    }
                    else
                    {
                        Interlocked.Exchange(ref _garbleCount, 0);
                    }

                    int delay = ResponseDelayMs;
                    if (delay > 0)
                        await Task.Delay(delay);

                    if (Verbose)
                        Log?.Invoke("<< " + text.TrimEnd().Replace("\r\n", " | "));

                    byte[] bytes = Encoding.ASCII.GetBytes(text);
                    if (Fragment && bytes.Length > 1)
                    {
                        int half = bytes.Length / 2;
                        await stream.WriteAsync(bytes, 0, half);
                        await stream.FlushAsync();
                        await Task.Delay(5);
                        await stream.WriteAsync(bytes, half, bytes.Length - half);
                    }
                    else
                    {
                        await stream.WriteAsync(bytes, 0, bytes.Length);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is SocketException)
            {
                // client went away
            }
            finally
            {
                lock (_clientsLock)
                    _clients.Remove(client);
                try { client.Close(); } catch { }
                Log?.Invoke($"Client {remote} disconnected");
            }
        }

        private static string SafeEndpoint(TcpClient client)
        {
            try { return client.Client.RemoteEndPoint.ToString(); }
            catch { return "?"; }
        }
    }
}
