using SmartGreenhouse.Data;
using SmartGreenhouse.Models;
using SmartGreenhouse.Services;
Iot.Device.Graphics.SkiaSharpAdapter.SkiaSharpAdapter.Register();
var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddSingleton<HardwareService>();
var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    using (var db = new GreenhouseContext())
    {
        db.Database.EnsureCreated(); // Создает базу, если ее нет

        // Если таблица растений пустая, заполняем ее!
        // Если таблица растений пустая, заполняем ее энциклопедией!
        if (!db.PlantProfiles.Any())
        {
            var encyclopedia = new List<PlantProfile>
        {
        // Твои личные заказы
            new PlantProfile { Name = "Томат балконний", MinSoilMoisture = 50, MaxSoilMoisture = 80, MinAirHum = 50, MaxAirHum = 70 },
            new PlantProfile { Name = "Грошове дерево (Крассула)", MinSoilMoisture = 10, MaxSoilMoisture = 30, MinAirHum = 30, MaxAirHum = 50 },
            new PlantProfile { Name = "Амариліс", MinSoilMoisture = 30, MaxSoilMoisture = 60, MinAirHum = 40, MaxAirHum = 60 },

        // Классика наших подоконников (Суккуленты и засухоустойчивые)
            new PlantProfile { Name = "Кактус", MinSoilMoisture = 5, MaxSoilMoisture = 20, MinAirHum = 20, MaxAirHum = 40 },
            new PlantProfile { Name = "Заміокулькас (Доларове дерево)", MinSoilMoisture = 15, MaxSoilMoisture = 35, MinAirHum = 30, MaxAirHum = 50 },
            new PlantProfile { Name = "Сансев'єрія (Тещин язик)", MinSoilMoisture = 10, MaxSoilMoisture = 30, MinAirHum = 30, MaxAirHum = 50 },
            new PlantProfile { Name = "Алое Віра", MinSoilMoisture = 10, MaxSoilMoisture = 30, MinAirHum = 30, MaxAirHum = 50 },

        // Тропики и любители влаги (Тут параметры воздуха важны!)
            new PlantProfile { Name = "Папороть", MinSoilMoisture = 60, MaxSoilMoisture = 85, MinAirHum = 65, MaxAirHum = 85 },
            new PlantProfile { Name = "Монстера", MinSoilMoisture = 40, MaxSoilMoisture = 70, MinAirHum = 60, MaxAirHum = 80 },
            new PlantProfile { Name = "Спатифілум (Жіноче щастя)", MinSoilMoisture = 50, MaxSoilMoisture = 80, MinAirHum = 60, MaxAirHum = 80 },
            new PlantProfile { Name = "Антуріум (Чоловіче щастя)", MinSoilMoisture = 40, MaxSoilMoisture = 70, MinAirHum = 60, MaxAirHum = 80 },
            new PlantProfile { Name = "Орхідея Фаленопсис", MinSoilMoisture = 20, MaxSoilMoisture = 50, MinAirHum = 60, MaxAirHum = 80 }, // Почва суховата, но воздух влажный

        // Декоративно-лиственные и универсальные
            new PlantProfile { Name = "Фікус Бенджаміна", MinSoilMoisture = 30, MaxSoilMoisture = 60, MinAirHum = 50, MaxAirHum = 70 },
            new PlantProfile { Name = "Драцена", MinSoilMoisture = 25, MaxSoilMoisture = 55, MinAirHum = 40, MaxAirHum = 60 },
            new PlantProfile { Name = "Хлорофітум", MinSoilMoisture = 30, MaxSoilMoisture = 60, MinAirHum = 40, MaxAirHum = 60 },
            new PlantProfile { Name = "Епіпремнум (Сциндапсус)", MinSoilMoisture = 35, MaxSoilMoisture = 65, MinAirHum = 45, MaxAirHum = 65 },

        // Цветущие
            new PlantProfile { Name = "Пеларгонія (Герань)", MinSoilMoisture = 25, MaxSoilMoisture = 55, MinAirHum = 30, MaxAirHum = 50 },
            new PlantProfile { Name = "Фіалка (Сенполія)", MinSoilMoisture = 40, MaxSoilMoisture = 65, MinAirHum = 50, MaxAirHum = 70 },
            new PlantProfile { Name = "Бегонія", MinSoilMoisture = 40, MaxSoilMoisture = 70, MinAirHum = 50, MaxAirHum = 70 },
        
        // Пряные травы на балкон
            new PlantProfile { Name = "Базилік", MinSoilMoisture = 40, MaxSoilMoisture = 75, MinAirHum = 40, MaxAirHum = 60 }
        };

            db.PlantProfiles.AddRange(encyclopedia);
            db.SaveChanges(); // Сохраняем всю пачку в базу одним махом

            Console.WriteLine("База данных успешно наполнена энциклопедией из 20 растений!");
        }
    }
}
app.UseDefaultFiles(); // Позволяет серверу искать index.html по умолчанию
app.UseStaticFiles();  // Разрешает раздачу файлов из папки wwwroot
// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthorization();

app.MapControllers();

app.Run();
