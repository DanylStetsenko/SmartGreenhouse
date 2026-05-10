namespace SmartGreenhouse.Models
{
    public class SystemSetting
    {
        public int Id { get; set; }
        public string Key { get; set; } = string.Empty; 
        public bool Value { get; set; }
    }
}