namespace QPX1200SPController.Models
{
    public class EventStatus
    {
        public int ConstantVoltageMode { get; set; }
        public int ConstantCurrentMode { get; set; }
        public int UnregulatedMode { get; set; }
        public int OverVoltageTrip { get; set; }
        public int OverCurrentTrip { get; set; }
        public int OutputSenseTrip { get; set; }
        public int FaultTrip { get; set; }

    }
}