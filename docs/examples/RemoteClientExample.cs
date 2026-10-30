// Example test software client for the QPX1200SP Controller's remote control (docs/REMOTE_PROTOCOL.md).
// Sets the output, turns it on, reads it, turns it off. Switch "Remote control" on in the app first.
// The same client drives the CPX400DP Controller (port 9500, output 1 or 2).
//
// Build (.NET Framework, no packages needed):
//   "C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\Roslyn\csc.exe" -r:System.Web.Extensions.dll RemoteClientExample.cs
// Run: RemoteClientExample.exe [host] [port]      (default 127.0.0.1 9501)

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Web.Script.Serialization;

public sealed class SupplyClient : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly JavaScriptSerializer _json = new JavaScriptSerializer();

    public SupplyClient(string host, int port)
    {
        _tcp = new TcpClient(host, port) { ReceiveTimeout = 15000 };
        var utf8 = new UTF8Encoding(false);
        _reader = new StreamReader(_tcp.GetStream(), utf8);
        _writer = new StreamWriter(_tcp.GetStream(), utf8) { NewLine = "\n", AutoFlush = true };
    }

    /// <summary>Sends one request and returns the reply. Throws if the app refused it or the supply did not confirm it.</summary>
    public Dictionary<string, object> Send(object request)
    {
        string line = _json.Serialize(request);
        Console.WriteLine("-> " + line);
        _writer.WriteLine(line);
        string replyLine = _reader.ReadLine();
        if (replyLine == null)
            throw new IOException("The app closed the connection (Remote control switched off?)");
        Console.WriteLine("<- " + replyLine);

        var reply = (Dictionary<string, object>)_json.DeserializeObject(replyLine);
        if (!(bool)reply["ok"])
            throw new InvalidOperationException((string)reply["error"]);
        return reply;
    }

    public void Dispose()
    {
        _tcp.Close();
    }
}

public static class Program
{
    public static int Main(string[] args)
    {
        string host = args.Length > 0 ? args[0] : "127.0.0.1";
        int port = args.Length > 1 ? int.Parse(args[1]) : 9501;

        try
        {
            using (var supply = new SupplyClient(host, port))
            {
                // Step 1: settings for the unit under test (the app sends the protection levels first).
                supply.Send(new { cmd = "set", output = 1, voltage = 12.0, current = 1.0, ovp = 14.0, ocp = 2.0 });

                // Step 2: power the unit.
                supply.Send(new { cmd = "output", output = 1, on = true });

                // Step 3: what the supply delivers now. The test software would check the unit over serial here.
                var reading = supply.Send(new { cmd = "read", output = 1 });
                Console.WriteLine($"   {reading["voltage"]} V, {reading["current"]} A, {reading["mode"]}, trip: {reading["trip"] ?? "none"}");

                // Step 4: power off.
                supply.Send(new { cmd = "output", output = 1, on = false });
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Test step failed: " + ex.Message);
            return 1;
        }
    }
}
