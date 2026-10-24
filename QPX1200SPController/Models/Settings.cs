using System;
using System.IO;
using System.Xml.Serialization;
using QPX1200SPController.Services;

namespace QPX1200SPController.Models
{
    /// <summary>
    /// User preferences persisted to settings.xml next to the executable.
    /// A single shared instance (<see cref="Current"/>) is used by every view model so
    /// saving from one place never overwrites values changed in another.
    /// </summary>
    [Serializable]
    public class Settings
    {
        // QPX1200SP instrument ratings. Soft limits are clamped to these.
        public const double InstrumentMaxVoltage = 60.0;
        public const double InstrumentMaxCurrent = 50.0;
        public const string DefaultIpAddress = "170.170.170.220";
        public static readonly double[] SampleRateOptionsHz = { 0.5, 1.0, 2.0, 5.0 };

        private static readonly string SettingsFilePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "settings.xml");

        private static readonly Lazy<Settings> _current = new Lazy<Settings>(Load);
        public static Settings Current => _current.Value;

        // Soft limits (operator-defined window inside the instrument range).
        public double MinVoltage { get; set; } = 0;
        public double MaxVoltage { get; set; } = InstrumentMaxVoltage;
        public double MinCurrent { get; set; } = 0;
        public double MaxCurrent { get; set; } = InstrumentMaxCurrent;

        public double SampleRateHz { get; set; } = 1.0;
        public string LastIpAddress { get; set; } = DefaultIpAddress;

        /// <summary>Database rows older than this are deleted. 0 keeps everything.</summary>
        public int RetentionDays { get; set; } = 0;

        [XmlIgnore] public double MinOvpLevel => 0;
        [XmlIgnore] public double MaxOvpLevel => 61;
        [XmlIgnore] public double MinOcpLevel => 0;
        [XmlIgnore] public double MaxOcpLevel => 50;

        private static Settings Load()
        {
            Settings settings;
            if (!File.Exists(SettingsFilePath))
            {
                settings = new Settings();
            }
            else
            {
                try
                {
                    using (var stream = new FileStream(SettingsFilePath, FileMode.Open, FileAccess.Read))
                    {
                        var serializer = new XmlSerializer(typeof(Settings));
                        settings = (Settings)serializer.Deserialize(stream);
                    }
                }
                catch (Exception ex)
                {
                    LoggingService.Instance.LogException(ex, nameof(Settings), nameof(Load));
                    settings = new Settings();
                }
            }

            settings.Sanitize();
            return settings;
        }

        /// <summary>
        /// Repairs missing or invalid values (e.g. an old settings.xml with Max = 0,
        /// which used to disable every SET button).
        /// </summary>
        private void Sanitize()
        {
            MinVoltage = Clamp(MinVoltage, 0, InstrumentMaxVoltage);
            MaxVoltage = MaxVoltage <= 0 ? InstrumentMaxVoltage : Clamp(MaxVoltage, 0, InstrumentMaxVoltage);
            if (MinVoltage > MaxVoltage)
                MinVoltage = 0;

            MinCurrent = Clamp(MinCurrent, 0, InstrumentMaxCurrent);
            MaxCurrent = MaxCurrent <= 0 ? InstrumentMaxCurrent : Clamp(MaxCurrent, 0, InstrumentMaxCurrent);
            if (MinCurrent > MaxCurrent)
                MinCurrent = 0;

            if (Array.IndexOf(SampleRateOptionsHz, SampleRateHz) < 0)
                SampleRateHz = 1.0;

            if (RetentionDays < 0)
                RetentionDays = 0;

            if (string.IsNullOrWhiteSpace(LastIpAddress))
                LastIpAddress = DefaultIpAddress;
        }

        private static double Clamp(double value, double min, double max)
        {
            if (double.IsNaN(value))
                return min;
            return Math.Max(min, Math.Min(max, value));
        }

        public void Save()
        {
            try
            {
                Sanitize();
                using (var stream = new FileStream(SettingsFilePath, FileMode.Create))
                {
                    var serializer = new XmlSerializer(typeof(Settings));
                    serializer.Serialize(stream, this);
                }
            }
            catch (Exception ex)
            {
                LoggingService.Instance.LogException(ex, nameof(Settings), nameof(Save));
            }
        }
    }
}
