using System;
using System.Globalization;
using System.Net;

namespace QPX1200SPSimulator
{
    /// <summary>
    /// QPX1200SP simulator: a fake power supply that speaks the same SCPI-over-TCP protocol
    /// as the real instrument, for testing QPX1200SPController without hardware.
    /// </summary>
    internal static class Program
    {
        private static readonly object ConsoleLock = new object();
        private static SimulatedSupply _supply;
        private static SimulatorServer _server;

        private static int Main(string[] args)
        {
            int port = 9221;
            IPAddress bind = IPAddress.Loopback;
            double load = 20.0;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if ((a == "--port" || a == "-p") && i + 1 < args.Length && int.TryParse(args[i + 1], out int p))
                {
                    port = p;
                    i++;
                }
                else if (a == "--any")
                {
                    bind = IPAddress.Any;
                }
                else if (a == "--load" && i + 1 < args.Length && TryParseLoad(args[i + 1], out double l))
                {
                    load = l;
                    i++;
                }
                else if (a == "--help" || a == "-h" || a == "/?")
                {
                    PrintUsage();
                    return 0;
                }
                else
                {
                    Console.Error.WriteLine($"Unknown argument '{args[i]}'.");
                    PrintUsage();
                    return 1;
                }
            }

            Console.Title = "QPX1200SP Simulator";
            _supply = new SimulatedSupply { LoadOhms = load };
            _server = new SimulatorServer(_supply, bind, port);
            _supply.Notice += msg => Write(msg, ConsoleColor.Yellow);
            _server.Log += msg => Write(msg, msg.StartsWith(">>") ? ConsoleColor.Cyan
                                                : msg.StartsWith("<<") ? ConsoleColor.Green
                                                : ConsoleColor.Gray);

            try
            {
                _server.Start();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not listen on {bind}:{port}: {ex.Message}");
                Console.Error.WriteLine("Is another simulator (or the real supply software) already using that port?");
                return 2;
            }

            Write("QPX1200SP simulator ready. In the app, connect to " +
                  (bind.Equals(IPAddress.Any) ? "this PC's IP address" : "127.0.0.1") +
                  ". Type 'help' for commands.", ConsoleColor.White);

            string line;
            while ((line = Console.ReadLine()) != null)
            {
                try
                {
                    if (!Execute(line.Trim()))
                        break;
                }
                catch (Exception ex)
                {
                    Write("Error: " + ex.Message, ConsoleColor.Red);
                }
            }

            _server.Stop();
            return 0;
        }

        /// <summary>Runs one console command. Returns false to quit.</summary>
        private static bool Execute(string line)
        {
            if (line.Length == 0)
                return true;

            string[] words = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string cmd = words[0].ToLowerInvariant();
            string arg = words.Length > 1 ? words[1].ToLowerInvariant() : null;

            switch (cmd)
            {
                case "help":
                case "?":
                    PrintHelp();
                    break;

                case "status":
                case "s":
                    Write(_supply.Describe(), ConsoleColor.White);
                    Write($"Clients: {_server.ClientCount}  listening: {_server.IsListening}  mute: {_server.Mute}  " +
                          $"delay: {_server.ResponseDelayMs} ms  fragment: {_server.Fragment}  verbose: {_server.Verbose}",
                          ConsoleColor.White);
                    break;

                case "load":
                    if (arg != null && TryParseLoad(arg, out double ohms))
                    {
                        _supply.LoadOhms = ohms;
                        Write("Load set to " + (double.IsPositiveInfinity(ohms) ? "open circuit" : ohms.ToString("F3", CultureInfo.InvariantCulture) + " ohm") + ".", ConsoleColor.White);
                    }
                    else
                    {
                        Write("Usage: load <ohms> | load open", ConsoleColor.Red);
                    }
                    break;

                case "trip":
                    switch (arg)
                    {
                        case "ovp": _supply.ForceTrip(SimulatedSupply.OvpBit, "OVP trip"); break;
                        case "ocp": _supply.ForceTrip(SimulatedSupply.OcpBit, "OCP trip"); break;
                        case "sense": _supply.ForceTrip(SimulatedSupply.SenseBit, "Sense trip"); break;
                        case "fault": _supply.ForceTrip(SimulatedSupply.FaultBit, "Fault trip"); break;
                        default: Write("Usage: trip ovp|ocp|sense|fault", ConsoleColor.Red); break;
                    }
                    break;

                case "readonly":
                    _supply.ReadOnly = arg != "off";
                    Write(_supply.ReadOnly
                        ? "Read only: set commands are refused with error 200 (EER?), as when another interface has the lock."
                        : "Read only off.", ConsoleColor.White);
                    break;

                case "vcap":
                    if (arg == "off")
                    {
                        _supply.VoltageCap = null;
                        Write("Voltage cap off.", ConsoleColor.White);
                    }
                    else if (arg != null && double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out double cap) && cap >= 0)
                    {
                        _supply.VoltageCap = cap;
                        Write($"V1 above {cap.ToString("F3", CultureInfo.InvariantCulture)} V is silently capped (the app should show Mismatch).", ConsoleColor.White);
                    }
                    else
                    {
                        Write("Usage: vcap <volts> | vcap off", ConsoleColor.Red);
                    }
                    break;

                case "mute":
                    _server.Mute = arg != "off";
                    Write(_server.Mute
                        ? "Muted: commands are received but never answered (the app should time out and reconnect)."
                        : "Unmuted.", ConsoleColor.White);
                    break;

                case "delay":
                    if (arg != null && int.TryParse(arg, out int ms) && ms >= 0)
                    {
                        _server.ResponseDelayMs = ms;
                        Write($"Response delay {ms} ms (the app times out after 3000 ms).", ConsoleColor.White);
                    }
                    else
                    {
                        Write("Usage: delay <ms>", ConsoleColor.Red);
                    }
                    break;

                case "garble":
                    int count = 1;
                    if (arg != null && (!int.TryParse(arg, out count) || count < 1))
                    {
                        Write("Usage: garble [count]", ConsoleColor.Red);
                        break;
                    }
                    _server.GarbleNext(count);
                    Write($"Next {count} response(s) will be garbled (3 in a row make the app reconnect).", ConsoleColor.White);
                    break;

                case "drop":
                    Write($"Dropped {_server.DropClients()} client connection(s).", ConsoleColor.White);
                    break;

                case "outage":
                    int seconds = 15;
                    if (arg != null && (!int.TryParse(arg, out seconds) || seconds < 1))
                    {
                        Write("Usage: outage [seconds]", ConsoleColor.Red);
                        break;
                    }
                    var _ = _server.OutageAsync(TimeSpan.FromSeconds(seconds));
                    break;

                case "fragment":
                    _server.Fragment = arg != "off";
                    Write("Fragmented responses " + (_server.Fragment ? "on" : "off") + ".", ConsoleColor.White);
                    break;

                case "noise":
                    _supply.Noise = arg != "off";
                    Write("Measurement noise " + (_supply.Noise ? "on" : "off") + ".", ConsoleColor.White);
                    break;

                case "verbose":
                case "v":
                    _server.Verbose = arg == null ? !_server.Verbose : arg != "off";
                    Write("Verbose " + (_server.Verbose ? "on" : "off") + ".", ConsoleColor.White);
                    break;

                case "quit":
                case "exit":
                case "q":
                    return false;

                default:
                    Write($"Unknown command '{words[0]}'. Type 'help'.", ConsoleColor.Red);
                    break;
            }

            return true;
        }

        private static bool TryParseLoad(string text, out double ohms)
        {
            if (text.Equals("open", StringComparison.OrdinalIgnoreCase))
            {
                ohms = double.PositiveInfinity;
                return true;
            }
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out ohms) && ohms > 0;
        }

        private static void Write(string message, ConsoleColor color)
        {
            lock (ConsoleLock)
            {
                var previous = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write(DateTime.Now.ToString("HH:mm:ss.fff ", CultureInfo.InvariantCulture));
                Console.ForegroundColor = color;
                Console.WriteLine(message);
                Console.ForegroundColor = previous;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage: QPX1200SPSimulator [--port 9221] [--any] [--load <ohms>|open]");
            Console.WriteLine("  --port   TCP port to listen on (default 9221, same as the real supply)");
            Console.WriteLine("  --any    listen on all network interfaces instead of 127.0.0.1 only");
            Console.WriteLine("  --load   initial resistive load in ohms, or 'open' (default 20)");
        }

        private static void PrintHelp()
        {
            lock (ConsoleLock)
            {
                Console.WriteLine();
                Console.WriteLine("  status | s             show set-points, measurements, trips and fault switches");
                Console.WriteLine("  load <ohms> | open     change the resistive load (e.g. load 0.5 forces CC mode)");
                Console.WriteLine("  trip ovp|ocp|sense|fault   force a trip (output goes OFF until TRIPRST)");
                Console.WriteLine("  readonly [off]         refuse set commands with error 200 (EER?), like another interface holding the lock");
                Console.WriteLine("  vcap <volts>|off       silently cap V1 so the read-back differs (app shows Mismatch)");
                Console.WriteLine("  mute [off]             stop answering queries (app should time out and reconnect)");
                Console.WriteLine("  delay <ms>             delay every response (> 3000 ms makes the app time out)");
                Console.WriteLine("  garble [n]             corrupt the next n responses (3 in a row => app reconnects)");
                Console.WriteLine("  drop                   close the current connection(s)");
                Console.WriteLine("  outage [seconds]       go off the network for a while (default 15 s)");
                Console.WriteLine("  fragment on|off        split responses into two TCP segments (default on)");
                Console.WriteLine("  noise on|off           +/-1 mV / mA measurement noise (default on)");
                Console.WriteLine("  verbose | v [on|off]   print every command/response (default on)");
                Console.WriteLine("  quit | q               exit");
                Console.WriteLine();
                Console.WriteLine("  Natural behaviour: CV/CC cross-over into the load, 1200 W power envelope,");
                Console.WriteLine("  OVP/OCP trips against the programmed levels, and rejection of comma decimals.");
                Console.WriteLine();
            }
        }
    }
}
