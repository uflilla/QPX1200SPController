using System;
using System.Collections.Generic;
using System.Text;
using QPX1200SPController.Models;

namespace QPX1200SPController.Services.Ccsds
{
    /// <summary>CRC-16-CCITT as used for the PUS packet error control: poly 0x1021, init 0xFFFF, no reflection, no final XOR.</summary>
    public static class Crc16Ccitt
    {
        private static readonly ushort[] Table = CreateTable();

        public static ushort Compute(byte[] data, int offset, int count)
        {
            ushort crc = 0xFFFF;
            for (int k = offset; k < offset + count; k++)
                crc = (ushort)((crc << 8) ^ Table[((crc >> 8) ^ data[k]) & 0xFF]);
            return crc;
        }

        private static ushort[] CreateTable()
        {
            var table = new ushort[256];
            for (int n = 0; n < 256; n++)
            {
                ushort value = (ushort)(n << 8);
                for (int bit = 0; bit < 8; bit++)
                    value = (value & 0x8000) != 0 ? (ushort)((value << 1) ^ 0x1021) : (ushort)(value << 1);
                table[n] = value;
            }
            return table;
        }
    }

    /// <summary>Builds big-endian application data.</summary>
    public sealed class BigEndianWriter
    {
        private readonly List<byte> _bytes = new List<byte>(64);

        public int Length => _bytes.Count;

        public BigEndianWriter U8(int value)
        {
            _bytes.Add((byte)value);
            return this;
        }

        public BigEndianWriter Flag(bool value) => U8(value ? 1 : 0);

        public BigEndianWriter U16(int value)
        {
            _bytes.Add((byte)(value >> 8));
            _bytes.Add((byte)value);
            return this;
        }

        public BigEndianWriter U32(uint value)
        {
            _bytes.Add((byte)(value >> 24));
            _bytes.Add((byte)(value >> 16));
            _bytes.Add((byte)(value >> 8));
            _bytes.Add((byte)value);
            return this;
        }

        public BigEndianWriter F32(double value)
        {
            var bytes = BitConverter.GetBytes((float)value);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            _bytes.AddRange(bytes);
            return this;
        }

        public BigEndianWriter Bytes(byte[] bytes)
        {
            _bytes.AddRange(bytes);
            return this;
        }

        public byte[] ToArray() => _bytes.ToArray();
    }

    public static class BigEndian
    {
        public static int U16(byte[] b, int offset) => (b[offset] << 8) | b[offset + 1];

        public static uint U32(byte[] b, int offset) =>
            ((uint)b[offset] << 24) | ((uint)b[offset + 1] << 16) | ((uint)b[offset + 2] << 8) | b[offset + 3];

        public static float F32(byte[] b, int offset)
        {
            var bytes = new[] { b[offset], b[offset + 1], b[offset + 2], b[offset + 3] };
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return BitConverter.ToSingle(bytes, 0);
        }

        public static string Hex(byte[] b, int offset, int count)
        {
            var text = new StringBuilder(count * 2);
            for (int k = offset; k < offset + count; k++)
                text.Append(b[k].ToString("X2"));
            return text.ToString();
        }
    }

    /// <summary>
    /// A received TC, or as much of it as could be read. When <see cref="Error"/> is not None,
    /// the fields that could not be read are null.
    /// </summary>
    public sealed class TcPacket
    {
        public int? SequenceCount { get; set; }
        public int? Service { get; set; }
        public int? Subtype { get; set; }
        public int? Sid { get; set; }

        /// <summary>Application data after the SID (without the PEC).</summary>
        public byte[] Data { get; set; }

        public RemoteError Error { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// CCSDS space packets with the PUS-A (ECSS-E-70-41A) data field header, laid out as in
    /// docs/REMOTE_PROTOCOL_CCSDS.md (TM as in "TM Packet Format.xlsx").
    /// </summary>
    public static class CcsdsCodec
    {
        public const int PrimaryHeaderLength = 6;

        /// <summary>TM data field header: flags, service, subtype, subcounter, 8-byte CUC time.</summary>
        public const int TmDataFieldHeaderLength = 12;

        public const int SidLength = 2;
        public const int PecLength = 2;

        /// <summary>Largest TC accepted, header and PEC included.</summary>
        public const int MaxTcLength = 1024;

        /// <summary>TC data field header: flags, service, subtype, and the optional Source ID.</summary>
        public static int TcDataFieldHeaderLength(CcsdsSettings settings) => settings.TcHasSourceId ? 4 : 3;

        /// <summary>Shortest valid TC: headers, SID and PEC, no application data.</summary>
        public static int MinTcLength(CcsdsSettings settings) =>
            PrimaryHeaderLength + TcDataFieldHeaderLength(settings) + SidLength + PecLength;

        /// <summary>Total packet length from the Packet Length field of a primary header at <paramref name="offset"/>.</summary>
        public static int TotalLength(byte[] b, int offset) => BigEndian.U16(b, offset + 4) + 7;

        /// <summary>Reads what it can of a TC (the whole packet, PEC included) and checks it.</summary>
        public static TcPacket ParseTc(byte[] p, CcsdsSettings settings)
        {
            var tc = new TcPacket();
            int sidOffset = PrimaryHeaderLength + TcDataFieldHeaderLength(settings);

            if (p.Length >= 4)
                tc.SequenceCount = BigEndian.U16(p, 2) & 0x3FFF;
            if (p.Length >= 8)
                tc.Service = p[7];
            if (p.Length >= 9)
                tc.Subtype = p[8];
            if (p.Length >= sidOffset + SidLength)
                tc.Sid = BigEndian.U16(p, sidOffset);

            if (p.Length < MinTcLength(settings))
                return Fail(tc, RemoteError.Malformed, $"packet of {p.Length} bytes is shorter than {MinTcLength(settings)}");
            if (p.Length > MaxTcLength)
                return Fail(tc, RemoteError.Malformed, $"packet of {p.Length} bytes is longer than {MaxTcLength}");

            if (settings.CheckTcPec)
            {
                int expected = Crc16Ccitt.Compute(p, 0, p.Length - PecLength);
                int received = BigEndian.U16(p, p.Length - PecLength);
                if (expected != received)
                    return Fail(tc, RemoteError.PecWrong, $"PEC is 0x{received:X4}, expected 0x{expected:X4}");
            }

            int version = p[0] >> 5;
            int type = (p[0] >> 4) & 1;
            int dataFieldHeader = (p[0] >> 3) & 1;
            int sequenceFlags = p[2] >> 6;
            if (version != 0)
                return Fail(tc, RemoteError.Malformed, $"version number is {version}, must be 0");
            if (type != 1)
                return Fail(tc, RemoteError.Malformed, "packet type is TM, must be TC");
            if (dataFieldHeader != 1)
                return Fail(tc, RemoteError.Malformed, "data field header flag is 0, must be 1");
            if (sequenceFlags != 3)
                return Fail(tc, RemoteError.Malformed, $"sequence flags are {Convert.ToString(sequenceFlags, 2).PadLeft(2, '0')}, must be 11 (stand-alone packet)");

            int apid = ((p[0] & 0x07) << 8) | p[1];
            if (apid != settings.TcApid)
                return Fail(tc, RemoteError.WrongApid, $"APID is {apid}, the app accepts {settings.TcApid}");

            int dataOffset = sidOffset + SidLength;
            tc.Data = new byte[p.Length - dataOffset - PecLength];
            Array.Copy(p, dataOffset, tc.Data, 0, tc.Data.Length);
            return tc;
        }

        private static TcPacket Fail(TcPacket tc, RemoteError error, string message)
        {
            tc.Error = error;
            tc.Message = message;
            return tc;
        }

        /// <summary>A complete TM packet, PEC included (sync markers not included).</summary>
        public static byte[] BuildTm(int apid, int sequenceCount, CcsdsPacketId id, int subcounter, DateTime timeUtc, DateTime epochUtc, byte[] data)
        {
            int total = PrimaryHeaderLength + TmDataFieldHeaderLength + SidLength + data.Length + PecLength;
            ToCuc(timeUtc, epochUtc, out uint coarse, out uint fine);
            var w = new BigEndianWriter()
                .U16(0x0800 | (apid & 0x7FF))              // version 0, type 0 (TM), data field header 1, APID
                .U16(0xC000 | (sequenceCount & 0x3FFF))    // grouping flags 11, source sequence count
                .U16(total - 7)                            // packet length
                .U8(0x10)                                  // spare 0, PUS version 1, spare 0000
                .U8(id.Service)
                .U8(id.Subtype)
                .U8(subcounter)
                .U32(coarse)
                .U32(fine)
                .U16(id.Sid)
                .Bytes(data);
            var packet = new byte[total];
            w.ToArray().CopyTo(packet, 0);
            ushort pec = Crc16Ccitt.Compute(packet, 0, total - PecLength);
            packet[total - 2] = (byte)(pec >> 8);
            packet[total - 1] = (byte)pec;
            return packet;
        }

        /// <summary>CUC 4+4: whole seconds since the epoch, and the fraction of a second × 2^32.</summary>
        public static void ToCuc(DateTime timeUtc, DateTime epochUtc, out uint coarse, out uint fine)
        {
            long ticks = Math.Max(0, (timeUtc - epochUtc).Ticks);
            coarse = (uint)(ticks / TimeSpan.TicksPerSecond);
            long remainder = ticks % TimeSpan.TicksPerSecond;
            fine = (uint)((remainder << 32) / TimeSpan.TicksPerSecond);
        }
    }
}
