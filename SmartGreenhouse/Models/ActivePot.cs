using System;

namespace SmartGreenhouse.Models
{
    public class ActivePot
    {
        public int Id { get; set; }

        public int PlantProfileId { get; set; }
        public PlantProfile? PlantProfile { get; set; }
        public string PlantName { get; set; }
        public int RelayPin { get; set; }
        public int SensorChannel { get; set; }

        public DateTime PlantedDate { get; set; }
    }
}
