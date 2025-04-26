using System;
using System.IO;
using System.Xml.Serialization;

namespace QPX1200SPController.Models
{
    [Serializable]
    public class Settings
    {
        private static readonly string SettingsFilePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "settings.xml");

        public double MinVoltage { get; set; } = 0.0;
        public double MaxVoltage { get; set; } = 60.0;
        public double MinCurrent { get; set; } = 0.0;
        public double MaxCurrent { get; set; } = 50.0;
        public double OverVoltageProtectionLevel { get; set; } = 65.0;
        public double OverCurrentProtectionLevel { get; set; } = 55.0;
        // Removed boolean flags as OVP and OCP are now treated as numeric values (1-65)
        // Values > 0 indicate protection is active, with the value being the threshold

        public static Settings Load()
        {
            if (!File.Exists(SettingsFilePath))
            {
                return new Settings();
            }

            try
            {
                using (var stream = new FileStream(SettingsFilePath, FileMode.Open))
                {
                    var serializer = new XmlSerializer(typeof(Settings));
                    return (Settings)serializer.Deserialize(stream);
                }
            }
            catch (Exception)
            {
                return new Settings();
            }
        }

        public void Save()
        {
            try
            {
                using (var stream = new FileStream(SettingsFilePath, FileMode.Create))
                {
                    var serializer = new XmlSerializer(typeof(Settings));
                    serializer.Serialize(stream, this);
                }
            }
            catch (Exception)
            {
                // Handle exception (could log or show message to user)
            }
        }
    }
}