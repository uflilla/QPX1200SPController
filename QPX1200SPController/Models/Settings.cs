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

        public double MinVoltage { get; set; } 
        public double MaxVoltage { get; set; } 
        public double MinCurrent { get; set; }
        public double MaxCurrent { get; set; } 
        [XmlIgnore] public double MinOvpLevel => 0;
        [XmlIgnore] public double MaxOvpLevel => 61;
        [XmlIgnore] public double MinOcpLevel => 0;
        [XmlIgnore] public double MaxOcpLevel => 50;
        

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