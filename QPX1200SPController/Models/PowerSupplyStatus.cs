using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QPX1200SPController.Models
{
    public class PowerSupplyStatus
    {
        public double Voltage { get; set; }
        public double Current { get; set; }
        public double Power { get; set; }
        public bool OutputEnabled { get; set; }
        public string ConnectionStatus { get; set; }
        public EventStatus EventStatus { get; set; }
        public DateTime Timestamp { get; set; }
        public string Time { get; set; }
    }

    public class PowerSupplySettingStatus
    {
        public double SetVoltage { get; set; }
        public double SetCurrent { get; set; }
        public double SetOvpLevel { get; set; }
        public double SetOcpLevel { get; set; }
    }

    public static class PowerSupplyCommands
    {
        public static string GetPsIdentityCommand => "*IDN?";
        public static string GetPsSettingStatusCommand => "V1?;I1?;OVP1?;OCP1?";
        public static string GetPsStatusCommand => "V1O?;I1O?;OP1?;LSR1?";
    }


}
