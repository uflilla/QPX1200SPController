// Example CCSDS client for the QPX1200SP Controller's remote control (docs/REMOTE_PROTOCOL_CCSDS.md).
// Sets the output, turns it on, reads it, turns it off, and prints the housekeeping (HK) packets
// that arrive in between. In the app, pick Protocol "CCSDS" and switch "Remote control" on first.
// Uses the default settings: APIDs 3, sync 2A2A2323 / 23232A2A, no TC Source ID, the default SIDs.
//
// Build (.NET Framework, no packages needed):
//   "C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\Roslyn\csc.exe" CcsdsClientExample.cs
// Run: CcsdsClientExample.exe [host] [port]      (default 127.0.0.1 9501)

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;

public sealed class CcsdsSupplyClient : IDisposable
{
    // settings.xml defaults
    private static readonly byte[] SyncHeader = { 0x2A, 0x2A, 0x23, 0x23 };
    private static readonly byte[] SyncFooter = { 0x23, 0x23, 0x2A, 0x2A };
    private const int TcApid = 3;
    private const int TcService = 128, TcSubtype = 1;
    public const int SidSet = 1, SidOutput = 2, SidRead = 3;
    public const int SidHk = 100, SidReadReport = 101, SidSetReport = 102, SidOutputReport = 103, SidErrorReport = 199;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private int _sequenceCount;

    public CcsdsSupplyClient(string host, int port)
    {
        _tcp = new TcpClient(host, port) { ReceiveTimeout = 15000 };
        _stream = _tcp.GetStream();
    }

    /// <summary>Called for every HK packet that arrives while waiting for a reply.</summary>
    public Action<Status> Housekeeping { get; set; }

    public sealed class Status
    {
        public bool Connected, On, SequenceRunning;
        public int Mode, Trip;          // mode 0 OFF, 1 CV, 2 CC, 3 UNREG; trip 0 none, 1 OVP, 2 OCP, 3 SENSE, 4 FAULT
        public float Voltage, Current, Power, SetVoltage, SetCurrent, Ovp, Ocp;

        public override string ToString() =>
            $"connected {Connected}, on {On}, mode {new[] { "OFF", "CV", "CC", "UNREG" }[Mode]}, trip {Trip}, " +
            $"{Voltage:F3} V {Current:F3} A {Power:F2} W, set {SetVoltage:F3} V {SetCurrent:F2} A, OVP {Ovp:F1} V, OCP {Ocp:F1} A";
    }

    /// <summary>A decoded TM packet.</summary>
    public sealed class Tm
    {
        public int Sid, Service, Subtype, SequenceCount;
        public DateTime TimeUtc;
        public byte[] Data;             // application data after the SID

        public uint TcSequenceCount => U32(Data, 0);
        public int Error => Data[4];
    }

    // ---- Commands --------------------------------------------------------------------------

    /// <summary>Values that are null are left as they are. Returns the SET report.</summary>
    public Tm Set(double? voltage = null, double? current = null, double? ovp = null, double? ocp = null)
    {
        var data = new List<byte>();
        foreach (var value in new[] { voltage, current, ovp, ocp })
        {
            data.Add((byte)(value.HasValue ? 1 : 0));
            data.AddRange(F32((float)(value ?? 0)));
        }
        return Request(SidSet, data.ToArray(), SidSetReport);
    }

    public Tm Output(bool on) => Request(SidOutput, new[] { (byte)(on ? 1 : 0) }, SidOutputReport);

    public Status Read()
    {
        var tm = Request(SidRead, new byte[0], SidReadReport);
        return ParseStatus(tm.Data, 5);
    }

    /// <summary>Sends a TC and returns its reply. Throws if the app refused it.</summary>
    private Tm Request(int sid, byte[] data, int replySid)
    {
        int seq = _sequenceCount = (_sequenceCount + 1) & 0x3FFF;
        byte[] frame = Frame(BuildTc(sid, seq, data));
        _stream.Write(frame, 0, frame.Length);

        while (true)
        {
            var tm = ReadTm();
            if (tm.Sid == SidHk)
            {
                Housekeeping?.Invoke(ParseStatus(tm.Data, 0));
                continue;
            }
            if (tm.TcSequenceCount != seq)
                continue;   // a reply to an earlier TC
            if (tm.Error != 0)
                throw new InvalidOperationException($"TC seq {seq} refused: error {tm.Error}" + (tm.Sid == SidErrorReport ? " (ERROR report)" : ""));
            if (tm.Sid != replySid)
                throw new InvalidOperationException($"Expected SID {replySid}, got {tm.Sid}");
            return tm;
        }
    }

    // ---- Packets ---------------------------------------------------------------------------

    private static byte[] BuildTc(int sid, int seq, byte[] data)
    {
        int total = 6 + 3 + 2 + data.Length + 2;
        var p = new byte[total];
        p[0] = (byte)(0x18 | (TcApid >> 8));        // version 0, type 1 (TC), data field header 1, APID
        p[1] = (byte)TcApid;
        p[2] = (byte)(0xC0 | (seq >> 8));           // sequence flags 11, sequence count
        p[3] = (byte)seq;
        p[4] = (byte)((total - 7) >> 8);            // packet length
        p[5] = (byte)(total - 7);
        p[6] = 0x10;                                // PUS version 1, no acks
        p[7] = TcService;
        p[8] = TcSubtype;
        p[9] = (byte)(sid >> 8);
        p[10] = (byte)sid;
        data.CopyTo(p, 11);
        ushort pec = Crc16(p, total - 2);
        p[total - 2] = (byte)(pec >> 8);
        p[total - 1] = (byte)pec;
        return p;
    }

    private static byte[] Frame(byte[] packet)
    {
        var frame = new byte[SyncHeader.Length + packet.Length + SyncFooter.Length];
        SyncHeader.CopyTo(frame, 0);
        packet.CopyTo(frame, SyncHeader.Length);
        SyncFooter.CopyTo(frame, SyncHeader.Length + packet.Length);
        return frame;
    }

    /// <summary>Reads the next TM frame: finds the sync header, reads the packet by its length, checks footer and PEC.</summary>
    public Tm ReadTm()
    {
        // Slide over the stream until the last bytes read are the sync header.
        var window = new byte[SyncHeader.Length];
        for (int read = 0; ; read++)
        {
            int b = _stream.ReadByte();
            if (b < 0)
                throw new EndOfStreamException("Connection closed");
            Array.Copy(window, 1, window, 0, window.Length - 1);
            window[window.Length - 1] = (byte)b;
            if (read + 1 >= window.Length && ((IStructuralEquatable)window).Equals(SyncHeader, StructuralComparisons.StructuralEqualityComparer))
                break;
        }

        byte[] primary = ReadExactly(6);
        int total = ((primary[4] << 8) | primary[5]) + 7;
        var p = new byte[total];
        primary.CopyTo(p, 0);
        ReadExactly(total - 6).CopyTo(p, 6);
        byte[] footer = ReadExactly(SyncFooter.Length);
        for (int k = 0; k < footer.Length; k++)
            if (footer[k] != SyncFooter[k])
                throw new InvalidDataException("Sync footer missing");
        if (Crc16(p, total - 2) != ((p[total - 2] << 8) | p[total - 1]))
            throw new InvalidDataException("PEC wrong");

        uint coarse = U32(p, 10), fine = U32(p, 14);
        var tm = new Tm
        {
            SequenceCount = ((p[2] & 0x3F) << 8) | p[3],
            Service = p[7],
            Subtype = p[8],
            TimeUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(coarse + fine / 4294967296.0),
            Sid = (p[18] << 8) | p[19],
            Data = new byte[total - 22]
        };
        Array.Copy(p, 20, tm.Data, 0, tm.Data.Length);
        return tm;
    }

    private byte[] ReadExactly(int count)
    {
        var bytes = new byte[count];
        for (int read = 0; read < count;)
        {
            int n = _stream.Read(bytes, read, count - read);
            if (n <= 0)
                throw new EndOfStreamException("Connection closed");
            read += n;
        }
        return bytes;
    }

    public static Status ParseStatus(byte[] d, int o) => new Status
    {
        Connected = d[o] == 1,
        On = d[o + 1] == 1,
        Mode = d[o + 2],
        Trip = d[o + 3],
        SequenceRunning = d[o + 4] == 1,
        Voltage = ReadF32(d, o + 5),
        Current = ReadF32(d, o + 9),
        Power = ReadF32(d, o + 13),
        SetVoltage = ReadF32(d, o + 17),
        SetCurrent = ReadF32(d, o + 21),
        Ovp = ReadF32(d, o + 25),
        Ocp = ReadF32(d, o + 29)
    };

    /// <summary>CRC-16-CCITT: poly 0x1021, init 0xFFFF.</summary>
    private static ushort Crc16(byte[] data, int count)
    {
        ushort crc = 0xFFFF;
        for (int i = 0; i < count; i++)
        {
            crc ^= (ushort)(data[i] << 8);
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    private static byte[] F32(float value)
    {
        var b = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(b);
        return b;
    }

    public static float ReadF32(byte[] d, int o)
    {
        var b = new[] { d[o], d[o + 1], d[o + 2], d[o + 3] };
        if (BitConverter.IsLittleEndian)
            Array.Reverse(b);
        return BitConverter.ToSingle(b, 0);
    }

    private static uint U32(byte[] d, int o) => ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];

    public void Dispose() => _tcp.Close();
}

public static class Program
{
    public static int Main(string[] args)
    {
        string host = args.Length > 0 ? args[0] : "127.0.0.1";
        int port = args.Length > 1 ? int.Parse(args[1]) : 9501;
        try
        {
            using (var supply = new CcsdsSupplyClient(host, port))
            {
                supply.Housekeeping = hk => Console.WriteLine("  HK   " + hk);

                Console.WriteLine("First HK packet:");
                var first = supply.ReadTm();
                Console.WriteLine($"  SID {first.Sid}, {first.TimeUtc:HH:mm:ss.fff} UTC");

                var set = supply.Set(voltage: 12.0, current: 1.0, ovp: 14.0, ocp: 2.0);
                Console.WriteLine($"SET    error {set.Error}, voltage read back {CcsdsSupplyClient.ReadF32(set.Data, 5 + 5):F3} V");

                supply.Output(true);
                Console.WriteLine("OUTPUT on");
                System.Threading.Thread.Sleep(1500);
                Console.WriteLine("READ   " + supply.Read());

                // ... the test software checks the unit here ...

                supply.Output(false);
                Console.WriteLine("OUTPUT off");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }
}
