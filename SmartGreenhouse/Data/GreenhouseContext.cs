using Microsoft.EntityFrameworkCore;
using SmartGreenhouse.Models; // Подключаем наши модели

namespace SmartGreenhouse.Data
{
    public class GreenhouseContext : DbContext
    {
        // Указываем, какие таблицы нужно создать
        public DbSet<PlantProfile> PlantProfiles { get; set; }
        public DbSet<ActivePot> ActivePots { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // Указываем, что файл базы будет лежать прямо в папке с программой
            optionsBuilder.UseSqlite("Data Source=greenhouse.db");
        }
    }
}
