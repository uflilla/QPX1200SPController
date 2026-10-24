using System;

namespace QPX1200SPController.Models
{
    /// <summary>
    /// Measurements for a time range, ready for plotting. X values are OLE automation dates.
    /// For long ranges each time bucket contributes its minimum and maximum, so spikes stay visible.
    /// </summary>
    public sealed class HistorySeries
    {
        public static readonly HistorySeries Empty = new HistorySeries(
            Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(), false, 0, 10);

        public HistorySeries(double[] xs, double[] voltage, double[] current, double[] power,
            bool isDownsampled, long sourceRows, double gapThresholdSeconds)
        {
            Xs = xs;
            Voltage = voltage;
            Current = current;
            Power = power;
            IsDownsampled = isDownsampled;
            SourceRows = sourceRows;
            GapThresholdSeconds = gapThresholdSeconds;
        }

        public double[] Xs { get; }
        public double[] Voltage { get; }
        public double[] Current { get; }
        public double[] Power { get; }

        /// <summary>True when the points are a min/max envelope rather than every sample.</summary>
        public bool IsDownsampled { get; }

        /// <summary>Number of database rows the points represent.</summary>
        public long SourceRows { get; }

        /// <summary>Consecutive points further apart than this are not joined (link was down / app closed).</summary>
        public double GapThresholdSeconds { get; }
    }

    /// <summary>Exact statistics over every sample in a time range.</summary>
    public sealed class HistoryStats
    {
        public long Samples { get; set; }
        public DateTime? First { get; set; }
        public DateTime? Last { get; set; }
        public double VoltageMin { get; set; }
        public double VoltageMax { get; set; }
        public double VoltageAvg { get; set; }
        public double CurrentMin { get; set; }
        public double CurrentMax { get; set; }
        public double CurrentAvg { get; set; }
        public double PowerMin { get; set; }
        public double PowerMax { get; set; }
        public double PowerAvg { get; set; }

        /// <summary>Integral of power over time (gaps longer than the sample timeout are not integrated).</summary>
        public double EnergyWh { get; set; }

        /// <summary>Integral of current over time.</summary>
        public double ChargeAh { get; set; }

        public double OutputOnSeconds { get; set; }
        public int CommandCount { get; set; }
        public int ErrorCount { get; set; }
    }
}
