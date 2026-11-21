using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Serialization;
using QPX1200SPController.Services;

namespace QPX1200SPController.Models
{
    /// <summary>Service type, subtype and SID of one CCSDS packet (docs/REMOTE_PROTOCOL_CCSDS.md).</summary>
    [Serializable]
    public class CcsdsPacketId
    {
        public CcsdsPacketId()
        {
        }

        public CcsdsPacketId(int service, int subtype, int sid)
        {
            Service = service;
            Subtype = subtype;
            Sid = sid;
        }

        [XmlAttribute] public int Service { get; set; }
        [XmlAttribute] public int Subtype { get; set; }
        [XmlAttribute] public int Sid { get; set; }

        public override string ToString() => $"{Service}/{Subtype} SID {Sid}";
    }

    /// <summary>The &lt;Ccsds&gt; section of settings.xml: APIDs, sync markers, header options and packet identification.</summary>
    [Serializable]
    public class CcsdsSettings
    {
        public const int DefaultApid = 3;
        public const string DefaultSyncHeader = "2A2A2323";
        public const string DefaultSyncFooter = "23232A2A";
        public const string DefaultTimeEpoch = "1970-01-01T00:00:00Z";
        public const int MaxSyncLength = 16;

        public int TcApid { get; set; } = DefaultApid;
        public int TmApid { get; set; } = DefaultApid;

        /// <summary>Hex bytes sent before every packet, both directions. Empty = none.</summary>
        public string SyncHeader { get; set; } = DefaultSyncHeader;

        /// <summary>Hex bytes sent after every packet, both directions. Empty = none.</summary>
        public string SyncFooter { get; set; } = DefaultSyncFooter;

        /// <summary>TC data field header has an 8-bit Source ID after the subtype.</summary>
        public bool TcHasSourceId { get; set; }

        /// <summary>Refuse TCs whose PEC (CRC-16-CCITT) is wrong.</summary>
        public bool CheckTcPec { get; set; } = true;

        /// <summary>Epoch of the CUC time in the TM header (ISO 8601, UTC).</summary>
        public string TimeEpoch { get; set; } = DefaultTimeEpoch;

        public CcsdsPacketId TcSet { get; set; } = DefaultTcSet();
        public CcsdsPacketId TcOutput { get; set; } = DefaultTcOutput();
        public CcsdsPacketId TcRead { get; set; } = DefaultTcRead();
        public CcsdsPacketId TmHousekeeping { get; set; } = DefaultTmHousekeeping();
        public CcsdsPacketId TmReadReport { get; set; } = DefaultTmReadReport();
        public CcsdsPacketId TmSetReport { get; set; } = DefaultTmSetReport();
        public CcsdsPacketId TmOutputReport { get; set; } = DefaultTmOutputReport();
        public CcsdsPacketId TmErrorReport { get; set; } = DefaultTmErrorReport();

        [XmlIgnore] public byte[] SyncHeaderBytes => ParseHex(SyncHeader) ?? new byte[0];
        [XmlIgnore] public byte[] SyncFooterBytes => ParseHex(SyncFooter) ?? new byte[0];

        [XmlIgnore]
        public DateTime EpochUtc => ParseEpoch(TimeEpoch) ?? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static CcsdsPacketId DefaultTcSet() => new CcsdsPacketId(128, 1, 1);
        private static CcsdsPacketId DefaultTcOutput() => new CcsdsPacketId(128, 1, 2);
        private static CcsdsPacketId DefaultTcRead() => new CcsdsPacketId(128, 1, 3);
        private static CcsdsPacketId DefaultTmHousekeeping() => new CcsdsPacketId(3, 25, 100);
        private static CcsdsPacketId DefaultTmReadReport() => new CcsdsPacketId(128, 2, 101);
        private static CcsdsPacketId DefaultTmSetReport() => new CcsdsPacketId(128, 2, 102);
        private static CcsdsPacketId DefaultTmOutputReport() => new CcsdsPacketId(128, 2, 103);
        private static CcsdsPacketId DefaultTmErrorReport() => new CcsdsPacketId(128, 2, 199);

        /// <summary>Replaces each invalid value by its default, with a warning in the App Logs.</summary>
        public void Sanitize()
        {
            TcApid = CheckApid(TcApid, nameof(TcApid));
            TmApid = CheckApid(TmApid, nameof(TmApid));

            if (SyncHeader == null || ParseHex(SyncHeader) == null)
            {
                Warn($"SyncHeader '{SyncHeader}' is not hex of up to {MaxSyncLength} bytes; using {DefaultSyncHeader}.");
                SyncHeader = DefaultSyncHeader;
            }
            if (SyncFooter == null || ParseHex(SyncFooter) == null)
            {
                Warn($"SyncFooter '{SyncFooter}' is not hex of up to {MaxSyncLength} bytes; using {DefaultSyncFooter}.");
                SyncFooter = DefaultSyncFooter;
            }
            SyncHeader = SyncHeader.Replace(" ", "").ToUpperInvariant();
            SyncFooter = SyncFooter.Replace(" ", "").ToUpperInvariant();

            if (ParseEpoch(TimeEpoch) == null)
            {
                Warn($"TimeEpoch '{TimeEpoch}' is not a date; using {DefaultTimeEpoch}.");
                TimeEpoch = DefaultTimeEpoch;
            }

            TcSet = CheckId(TcSet, nameof(TcSet), DefaultTcSet);
            TcOutput = CheckId(TcOutput, nameof(TcOutput), DefaultTcOutput);
            TcRead = CheckId(TcRead, nameof(TcRead), DefaultTcRead);
            TmHousekeeping = CheckId(TmHousekeeping, nameof(TmHousekeeping), DefaultTmHousekeeping);
            TmReadReport = CheckId(TmReadReport, nameof(TmReadReport), DefaultTmReadReport);
            TmSetReport = CheckId(TmSetReport, nameof(TmSetReport), DefaultTmSetReport);
            TmOutputReport = CheckId(TmOutputReport, nameof(TmOutputReport), DefaultTmOutputReport);
            TmErrorReport = CheckId(TmErrorReport, nameof(TmErrorReport), DefaultTmErrorReport);
        }

        /// <summary>Null if remote control can start with these settings, otherwise why not.</summary>
        public string Validate()
        {
            var tcs = new Dictionary<string, CcsdsPacketId>
            {
                [nameof(TcSet)] = TcSet,
                [nameof(TcOutput)] = TcOutput,
                [nameof(TcRead)] = TcRead
            };
            var duplicate = tcs.GroupBy(t => $"{t.Value.Service}/{t.Value.Subtype}/{t.Value.Sid}").FirstOrDefault(g => g.Count() > 1);
            if (duplicate != null)
                return $"{string.Join(" and ", duplicate.Select(t => t.Key))} have the same service, subtype and SID ({duplicate.First().Value}) in settings.xml.";
            return null;
        }

        /// <summary>The bytes of a hex string ("2A2A2323", spaces allowed), or null if it is not valid.</summary>
        public static byte[] ParseHex(string hex)
        {
            if (hex == null)
                return null;
            hex = hex.Replace(" ", "");
            if (hex.Length % 2 != 0 || hex.Length / 2 > MaxSyncLength)
                return null;
            var bytes = new byte[hex.Length / 2];
            for (int k = 0; k < bytes.Length; k++)
            {
                if (!byte.TryParse(hex.Substring(2 * k, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[k]))
                    return null;
            }
            return bytes;
        }

        private static DateTime? ParseEpoch(string text)
        {
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var epoch))
                return DateTime.SpecifyKind(epoch, DateTimeKind.Utc);
            return null;
        }

        private static int CheckApid(int apid, string name)
        {
            if (apid >= 0 && apid <= 2046)
                return apid;
            Warn($"{name} {apid} is outside 0–2046; using {DefaultApid}.");
            return DefaultApid;
        }

        private static CcsdsPacketId CheckId(CcsdsPacketId id, string name, Func<CcsdsPacketId> fallback)
        {
            if (id != null && id.Service >= 0 && id.Service <= 255 && id.Subtype >= 0 && id.Subtype <= 255 && id.Sid >= 0 && id.Sid <= 65535)
                return id;
            var replacement = fallback();
            if (id != null)
                Warn($"{name} ({id}) is outside service/subtype 0–255, SID 0–65535; using {replacement}.");
            return replacement;
        }

        private static void Warn(string message)
        {
            LoggingService.Instance.LogWarning("settings.xml Ccsds: " + message, nameof(CcsdsSettings), nameof(Sanitize));
        }
    }
}
