namespace SmartGreenhouse.Models
{
    public class PlantProfile
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;

        public int MinSoilMoisture { get; set; }
        public int MaxSoilMoisture { get; set; }

        public int MinAirHum { get; set; }
        public int MaxAirHum { get; set; }

        public int MinLightLevel { get; set; }

        public TimeSpan WakeUpTime { get; set; }
        public TimeSpan SleepTime { get; set; }
   
    }
}
