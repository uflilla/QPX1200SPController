using System;
using System.Globalization;

namespace QPX1200SPSimulator
{
    /// <summary>
    /// Behavioural model of a QPX1200SP output driving a resistive load:
    /// CV/CC cross-over, the 1200 W power envelope, OVP/OCP trips and the LSR1 register.
    /// All members are thread-safe.
    /// </summary>
    public sealed class SimulatedSupply
    {
        public const double MaxVoltage = 60.0;
        public const double MaxCurrent = 50.0;
        public const double MaxPower = 1200.0;

        // LSR1 bits
        public const int CvBit = 1 << 0;
        public const int CcBit = 1 << 1;
        public const int UnregBit = 1 << 2;
        public const int OvpBit = 1 << 3;
        public const int OcpBit = 1 << 4;
        public const int SenseBit = 1 << 5;
        public const int FaultBit = 1 << 6;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private readonly object _sync = new object();
        private readonly Random _random = new Random();

        private double _setVoltage = 12.0;
        private double _setCurrent = 1.0;
        private double _ovp = 61.0;
        private double _ocp = 51.0;
        private bool _outputOn;
        private double _loadOhms = 20.0;
        private int _tripBits;          // latched until TRIPRST
        private int _pendingTripBits;   // reported once by LSR1? (register clears on read)
        private bool _noise = true;
        private int _executionError;    // EER?: last error, cleared on read (manual: Error Reporting)
        private bool _readOnly;         // set commands refused with error 200 (another interface has the lock)
        private double? _voltageCap;    // V1 silently capped: the read-back differs (app shows Mismatch)

        public string Identity { get; set; } = "THURLBY THANDAR, QPX1200SP, SIM0001, 1.00-SIM";

        /// <summary>Raised with a human-readable message when the model changes state on its own (e.g. a trip).</summary>
        public event Action<string> Notice;

        public double LoadOhms
        {
            get { lock (_sync) return _loadOhms; }
            set { lock (_sync) { _loadOhms = value; EvaluateProtectionUnlocked(); } }
        }

        public bool Noise
        {
            get { lock (_sync) return _noise; }
            set { lock (_sync) _noise = value; }
        }

        /// <summary>Set commands are refused with error 200, as when another interface holds the lock.</summary>
        public bool ReadOnly
        {
            get { lock (_sync) return _readOnly; }
            set { lock (_sync) _readOnly = value; }
        }

        /// <summary>V1 values above this are silently capped (null = off), so the read-back differs.</summary>
        public double? VoltageCap
        {
            get { lock (_sync) return _voltageCap; }
            set { lock (_sync) _voltageCap = value; }
        }

        /// <summary>Forces a trip as if the instrument had detected it.</summary>
        public void ForceTrip(int bit, string name)
        {
            lock (_sync)
                TripUnlocked(bit, name + " (forced)");
        }

        /// <summary>
        /// Handles one SCPI command (no ';'). Returns the response line for queries,
        /// or null when the instrument sends nothing back.
        /// </summary>
        public string Handle(string command, out bool recognised)
        {
            recognised = true;
            string c = command.Trim();
            string upper = c.ToUpperInvariant();

            lock (_sync)
            {
                switch (upper)
                {
                    case "*IDN?": return Identity;
                    case "V1?": return "V1 " + F3(_setVoltage);
                    case "I1?": return "I1 " + F3(_setCurrent);
                    case "OVP1?": return "VP1 " + F3(_ovp);
                    case "OCP1?": return "CP1 " + F3(_ocp);
                    case "OP1?": return _outputOn ? "1" : "0";
                    case "V1O?": return F3(MeasureUnlocked().Voltage) + "V";
                    case "I1O?": return F3(MeasureUnlocked().Current) + "A";
                    case "LSR1?": return ReadLsrUnlocked().ToString(Inv);
                    case "EER?":
                        int error = _executionError;
                        _executionError = 0;
                        return error.ToString(Inv);
                    // Other status registers (always clear in the simulator).
                    case "QER?":
                    case "*ESR?":
                    case "*STB?":
                    case "LSE1?": return "0";
                    case "*OPC?": return "1";
                    case "*CLS":
                        return null;
                    case "TRIPRST":
                        if (RefuseIfReadOnly(c))
                            return null;
                        _tripBits = 0;
                        RaiseNotice("Trips cleared (TRIPRST).");
                        return null;
                    case "LOCAL":
                        RaiseNotice("Returned to local control (LOCAL).");
                        return null;
                    case "*RST":
                        _setVoltage = 1; _setCurrent = 0.1; _outputOn = false; _tripBits = 0;
                        return null;
                }

                string[] parts = upper.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && double.TryParse(parts[1].Trim(), NumberStyles.Float, Inv, out double value))
                {
                    // Ranges and setting resolutions from the manual's specification; a value outside
                    // the range is refused with execution error 100 and the setting is left as it was.
                    switch (parts[0])
                    {
                        case "V1":
                            if (RefuseIfReadOnly(c) || RefuseIfOutOfRange(c, value, 0, MaxVoltage))
                                return null;
                            value = Round(value, 0.001);
                            if (_voltageCap.HasValue && value > _voltageCap.Value)
                            {
                                RaiseNotice($"'{c}' capped to {F3(_voltageCap.Value)} V (vcap).");
                                value = _voltageCap.Value;
                            }
                            _setVoltage = value;
                            EvaluateProtectionUnlocked();
                            return null;
                        case "I1":
                            if (RefuseIfReadOnly(c) || RefuseIfOutOfRange(c, value, 0, MaxCurrent))
                                return null;
                            _setCurrent = Round(value, 0.01);
                            EvaluateProtectionUnlocked();
                            return null;
                        case "OVP1":
                            if (RefuseIfReadOnly(c) || RefuseIfOutOfRange(c, value, 2, 65))
                                return null;
                            _ovp = Round(value, 0.1);
                            EvaluateProtectionUnlocked();
                            return null;
                        case "OCP1":
                            if (RefuseIfReadOnly(c) || RefuseIfOutOfRange(c, value, 2, 55))
                                return null;
                            _ocp = Round(value, 0.1);
                            EvaluateProtectionUnlocked();
                            return null;
                        case "OP1":
                            if (RefuseIfReadOnly(c))
                                return null;
                            if (value != 0 && value != 1)
                            {
                                _executionError = 100;
                                RaiseNotice($"'{c}' refused: error 100 (only 0 and 1 allowed).");
                                return null;
                            }
                            SetOutputUnlocked(value == 1);
                            return null;
                    }
                }
                else if (parts.Length == 2 && parts[1].Contains(","))
                {
                    // Typical mistake on comma-decimal PCs, e.g. "V1 12,000": the real supply rejects it.
                    RaiseNotice($"Rejected '{c}': comma used as decimal separator.");
                    return null;
                }
            }

            recognised = false;
            return null;
        }

        public string Describe()
        {
            lock (_sync)
            {
                var m = MeasureUnlocked();
                string mode = !_outputOn ? "OFF" : m.Mode;
                return $"Output {(_outputOn ? "ON" : "OFF")} ({mode})  set {F3(_setVoltage)} V / {F3(_setCurrent)} A  " +
                       $"OVP {F3(_ovp)} V  OCP {F3(_ocp)} A  load {(double.IsPositiveInfinity(_loadOhms) ? "open" : F3(_loadOhms) + " ohm")}\n" +
                       $"Measured {F3(m.Voltage)} V  {F3(m.Current)} A  {m.Voltage * m.Current:F2} W  trips: {DescribeTrips(_tripBits)}";
            }
        }

        private bool RefuseIfReadOnly(string command)
        {
            if (!_readOnly)
                return false;
            _executionError = 200;
            RaiseNotice($"'{command}' refused: error 200 (read only).");
            return true;
        }

        private bool RefuseIfOutOfRange(string command, double value, double min, double max)
        {
            if (value >= min && value <= max)
                return false;
            _executionError = 100;
            RaiseNotice($"'{command}' refused: error 100 (range {min.ToString(Inv)} to {max.ToString(Inv)}).");
            return true;
        }

        private static double Round(double value, double step) => Math.Round(Math.Round(value / step) * step, 6);

        private void SetOutputUnlocked(bool on)
        {
            if (on && _tripBits != 0)
            {
                RaiseNotice($"OP1 1 ignored: trip active ({DescribeTrips(_tripBits)}). Send TRIPRST first.");
                return;
            }
            _outputOn = on;
            RaiseNotice("Output " + (on ? "ON" : "OFF") + ".");
            EvaluateProtectionUnlocked();
        }

        private void EvaluateProtectionUnlocked()
        {
            if (!_outputOn)
                return;
            var m = MeasureUnlocked();
            if (m.Voltage > _ovp)
                TripUnlocked(OvpBit, $"OVP trip: {F3(m.Voltage)} V > {F3(_ovp)} V");
            else if (m.Current > _ocp)
                TripUnlocked(OcpBit, $"OCP trip: {F3(m.Current)} A > {F3(_ocp)} A");
        }

        private void TripUnlocked(int bit, string reason)
        {
            _tripBits |= bit;
            _pendingTripBits |= bit;
            _outputOn = false;
            RaiseNotice(reason + " - output switched OFF.");
        }

        private int ReadLsrUnlocked()
        {
            int value = _pendingTripBits;
            _pendingTripBits = 0;
            if (_outputOn)
            {
                switch (MeasureUnlocked().Mode)
                {
                    case "CV": value |= CvBit; break;
                    case "CC": value |= CcBit; break;
                    default: value |= UnregBit; break;
                }
            }
            return value;
        }

        private Measurement MeasureUnlocked()
        {
            if (!_outputOn)
                return new Measurement(0, 0, "OFF");

            double r = _loadOhms;
            double v, i;
            string mode;
            if (double.IsPositiveInfinity(r) || _setVoltage / r <= _setCurrent)
            {
                v = _setVoltage;
                i = double.IsPositiveInfinity(r) ? 0 : v / r;
                mode = "CV";
            }
            else
            {
                i = _setCurrent;
                v = i * r;
                mode = "CC";
            }

            if (v * i > MaxPower)
            {
                // Outside the PowerFlex envelope: output folds back and is unregulated.
                double scale = Math.Sqrt(MaxPower / (v * i));
                v *= scale;
                i *= scale;
                mode = "UNREG";
            }

            if (_noise)
            {
                v += (_random.NextDouble() - 0.5) * 0.002;
                i += (_random.NextDouble() - 0.5) * 0.002;
            }

            return new Measurement(Math.Max(0, v), Math.Max(0, i), mode);
        }

        private void RaiseNotice(string message)
        {
            Notice?.Invoke(message);
        }

        private static string DescribeTrips(int bits)
        {
            if (bits == 0)
                return "none";
            string s = "";
            if ((bits & OvpBit) != 0) s += "OVP ";
            if ((bits & OcpBit) != 0) s += "OCP ";
            if ((bits & SenseBit) != 0) s += "SENSE ";
            if ((bits & FaultBit) != 0) s += "FAULT ";
            return s.Trim();
        }

        private static string F3(double value) => value.ToString("F3", Inv);

        private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));

        private struct Measurement
        {
            public Measurement(double voltage, double current, string mode)
            {
                Voltage = voltage;
                Current = current;
                Mode = mode;
            }

            public double Voltage { get; }
            public double Current { get; }
            public string Mode { get; }
        }
    }
}
