using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using QPX1200SPController.Models;

namespace QPX1200SPController.Services
{
    /// <summary>Wire format of the remote control server (settings.xml RemoteProtocol).</summary>
    public enum RemoteProtocolKind
    {
        /// <summary>One JSON object per line (docs/REMOTE_PROTOCOL.md).</summary>
        Json,

        /// <summary>CCSDS space packets with a PUS-A data field header (docs/REMOTE_PROTOCOL_CCSDS.md).</summary>
        Ccsds
    }

    /// <summary>
    /// Why a remote request was refused or failed. The numbers are the CCSDS error codes
    /// (docs/REMOTE_PROTOCOL_CCSDS.md); JSON sends the message text instead.
    /// </summary>
    public enum RemoteError : byte
    {
        None = 0,
        Malformed = 1,
        FooterMissing = 2,
        PecWrong = 3,
        WrongApid = 4,
        UnknownService = 5,
        UnknownSid = 6,
        InvalidParameter = 7,
        NoValue = 8,
        VoltageRange = 10,
        CurrentRange = 11,
        OvpRange = 12,
        OcpRange = 13,
        NotConnected = 20,
        NoReply = 21,
        SequenceRunning = 22,
        Tripped = 23,
        Failed = 24,
        Mismatch = 25,
        OutputKeptOff = 26,
        AnotherClient = 30,
        AppClosing = 31,
        Internal = 32
    }

    /// <summary>A request the app refuses; the code and message go back to the client.</summary>
    public sealed class RemoteException : Exception
    {
        public RemoteException(RemoteError code, string message) : base(message)
        {
            Code = code;
        }

        public RemoteError Code { get; }
    }

    public enum RemoteMode : byte
    {
        Off = 0,
        CV = 1,
        CC = 2,
        Unreg = 3
    }

    public enum RemoteTrip : byte
    {
        None = 0,
        Ovp = 1,
        Ocp = 2,
        Sense = 3,
        Fault = 4
    }

    /// <summary>Result of one command sent to the supply.</summary>
    public enum RemoteResult : byte
    {
        NotSent = 0,
        Ok = 1,
        Mismatch = 2,
        Failed = 3
    }

    public enum RemoteSetting
    {
        Voltage,
        Current,
        Ovp,
        Ocp
    }

    /// <summary>Measurements and settings, as sent by the remote "read" and the CCSDS housekeeping.</summary>
    public sealed class RemoteStatus
    {
        public DateTime Timestamp { get; set; }
        public bool Connected { get; set; }
        public bool OutputOn { get; set; }
        public RemoteMode Mode { get; set; }
        public RemoteTrip Trip { get; set; }
        public bool SequenceRunning { get; set; }
        public double Voltage { get; set; }
        public double Current { get; set; }
        public double Power { get; set; }
        public double SetVoltage { get; set; }
        public double SetCurrent { get; set; }
        public double Ovp { get; set; }
        public double Ocp { get; set; }

        /// <summary>Not connected to the supply: everything else zero.</summary>
        public static RemoteStatus Disconnected(DateTime timestamp)
        {
            return new RemoteStatus { Timestamp = timestamp };
        }

        public static RemoteStatus From(PowerSupplyStatus status, PowerSupplySettingStatus settings, bool sequenceRunning)
        {
            return new RemoteStatus
            {
                Timestamp = status.Timestamp,
                Connected = true,
                OutputOn = status.OutputEnabled,
                Mode = ModeOf(status),
                Trip = TripOf(status.EventStatus),
                SequenceRunning = sequenceRunning,
                Voltage = status.Voltage,
                Current = status.Current,
                Power = status.Power,
                SetVoltage = settings?.SetVoltage ?? 0,
                SetCurrent = settings?.SetCurrent ?? 0,
                Ovp = settings?.SetOvpLevel ?? 0,
                Ocp = settings?.SetOcpLevel ?? 0
            };
        }

        public static RemoteMode ModeOf(PowerSupplyStatus status)
        {
            var e = status.EventStatus;
            if (!status.OutputEnabled)
                return RemoteMode.Off;
            if (e != null && e.UnregulatedMode == 1)
                return RemoteMode.Unreg;
            return e != null && e.ConstantCurrentMode == 1 ? RemoteMode.CC : RemoteMode.CV;
        }

        /// <summary>The first latched trip (the app keeps them until TRIP RESET).</summary>
        public static RemoteTrip TripOf(EventStatus e)
        {
            if (e == null)
                return RemoteTrip.None;
            if (e.OverVoltageTrip == 1)
                return RemoteTrip.Ovp;
            if (e.OverCurrentTrip == 1)
                return RemoteTrip.Ocp;
            if (e.OutputSenseTrip == 1)
                return RemoteTrip.Sense;
            return e.FaultTrip == 1 ? RemoteTrip.Fault : RemoteTrip.None;
        }

        /// <summary>"CV", "CC", "UNREG" or "OFF" (JSON).</summary>
        public static string ModeName(RemoteMode mode)
        {
            switch (mode)
            {
                case RemoteMode.CV: return "CV";
                case RemoteMode.CC: return "CC";
                case RemoteMode.Unreg: return "UNREG";
                default: return "OFF";
            }
        }

        /// <summary>"OVP", "OCP", "SENSE", "FAULT", or null for none (JSON).</summary>
        public static string TripName(RemoteTrip trip)
        {
            switch (trip)
            {
                case RemoteTrip.Ovp: return "OVP";
                case RemoteTrip.Ocp: return "OCP";
                case RemoteTrip.Sense: return "SENSE";
                case RemoteTrip.Fault: return "FAULT";
                default: return null;
            }
        }
    }

    /// <summary>Values to set; null = leave as it is.</summary>
    public sealed class SetRequest
    {
        public double? Voltage { get; set; }
        public double? Current { get; set; }
        public double? Ovp { get; set; }
        public double? Ocp { get; set; }
    }

    public sealed class SettingResult
    {
        public RemoteSetting Setting { get; set; }

        /// <summary>"voltage", "current", "ovp" or "ocp".</summary>
        public string Name { get; set; }

        /// <summary>The value sent after rounding; for one not sent, the value requested.</summary>
        public double? Requested { get; set; }

        public double? Readback { get; set; }
        public RemoteResult Result { get; set; }
    }

    public sealed class SetResult
    {
        /// <summary>Every value given, in the order they are sent (OVP, OCP, current, voltage).</summary>
        public List<SettingResult> Results { get; } = new List<SettingResult>();

        public RemoteError Error { get; set; }
        public string Message { get; set; }
    }

    public sealed class OutputResult
    {
        /// <summary>What the supply reports afterwards; null if unknown.</summary>
        public bool? On { get; set; }

        public RemoteResult Result { get; set; }
        public RemoteError Error { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// The three remote commands, independent of the wire format. Each throws
    /// <see cref="RemoteException"/> when the request is refused before anything is sent.
    /// </summary>
    public interface IRemoteCommands
    {
        Task<SetResult> SetAsync(SetRequest request);
        Task<OutputResult> OutputAsync(bool on);
        Task<RemoteStatus> ReadAsync();
    }

    /// <summary>A wire format served by <see cref="RemoteServer"/>.</summary>
    public interface IRemoteProtocol
    {
        /// <summary>"JSON" or "CCSDS".</summary>
        string Name { get; }

        /// <summary>Serves one client until it leaves or the stream is closed.</summary>
        Task ServeAsync(Stream stream, string address);

        /// <summary>Tells a second client that another one is connected; the server then closes it.</summary>
        Task RefuseAsync(Stream stream, string address);

        /// <summary>Sends unrequested housekeeping to the connected client, if the protocol has it.</summary>
        void PublishHousekeeping(RemoteStatus status);
    }
}
