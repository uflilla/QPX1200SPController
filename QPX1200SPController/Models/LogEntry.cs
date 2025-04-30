using System;

namespace QPX1200SPController.Models
{
    public class LogEntry
    {
        //public int Id { get; set; }
        public DateTime Timestamp { get; set; }
        public double Voltage { get; set; }
        public double Current { get; set; }
        public bool OutputEnabled { get; set; }
        public bool CvMode { get; set; }
        public bool CcMode { get; set; }
        public bool UnregulatedMode { get; set; }
        public bool OvpTrip { get; set; }
        public bool OcpTrip { get; set; }
        
    }
}