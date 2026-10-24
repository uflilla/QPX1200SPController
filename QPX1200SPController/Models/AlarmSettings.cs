using System;

namespace QPX1200SPController.Models
{
    /// <summary>Alarm thresholds and options, stored in settings.xml.</summary>
    [Serializable]
    public class AlarmSettings
    {
        public bool VoltageHighEnabled { get; set; }
        public double VoltageHigh { get; set; } = 30;
        public bool VoltageLowEnabled { get; set; }
        public double VoltageLow { get; set; } = 1;
        public bool CurrentHighEnabled { get; set; }
        public double CurrentHigh { get; set; } = 10;
        public bool CurrentLowEnabled { get; set; }
        public double CurrentLow { get; set; } = 0.01;
        public bool PowerHighEnabled { get; set; }
        public double PowerHigh { get; set; } = 500;

        /// <summary>A threshold must be exceeded this long before the alarm is raised.</summary>
        public int DelaySeconds { get; set; } = 2;

        public bool AlarmOnTrip { get; set; } = true;
        public bool AlarmOnLinkLost { get; set; } = true;
        public bool PlaySound { get; set; } = true;

        /// <summary>Switch the output off when a threshold alarm is raised.</summary>
        public bool TurnOutputOff { get; set; }
    }

    /// <summary>One sequencer step as stored in settings.xml and in sequence CSV files.</summary>
    [Serializable]
    public class SequenceStepData
    {
        public double Voltage { get; set; }
        public double Current { get; set; }
        public double DurationSeconds { get; set; }

        /// <summary>Ramp the voltage linearly from the previous value over the step instead of jumping.</summary>
        public bool Ramp { get; set; }
    }
}
