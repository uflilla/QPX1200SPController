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
        public double OverVoltageProtection { get; set; }
        public double OverCurrentProtection { get; set; }
        public string ConnectionStatus { get; set; }
    }
}
