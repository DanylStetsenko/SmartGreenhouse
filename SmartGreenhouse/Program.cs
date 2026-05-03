using SmartGreenhouse.Data;
using SmartGreenhouse.Models;
using SmartGreenhouse.Services;
Iot.Device.Graphics.SkiaSharpAdapter.SkiaSharpAdapter.Register();
var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
//builder.Services.AddSingleton<HardwareService>();
// 1. Создаем единственный и неповторимый экземпляр сервиса (Singleton)
builder.Services.AddSingleton<SmartGreenhouse.Services.HardwareService>();

// 2. Говорим системе запустить этот ЖЕ экземпляр как фоновую задачу (чтобы работал таймер)
//builder.Services.AddHostedService(provider => provider.GetRequiredService<SmartGreenhouse.Services.HardwareService>());
var app = builder.Build();
app.Services.GetRequiredService<SmartGreenhouse.Services.HardwareService>();
using (var scope = app.Services.CreateScope())
{
    using (var db = new GreenhouseContext())
    {
        db.Database.EnsureCreated(); // Создаем файл, если его нет

        // Если таблица растений пустая, заполняем ее!
        if (!db.PlantProfiles.Any())
        {
            var basil = new PlantProfile
            {
                Name = "Базилік",
                MinSoilMoisture = 40,
                MaxSoilMoisture = 70,
                WaterDoseSeconds = 1,
                SoakTimeoutMinutes = 5
            };
            var cactus = new PlantProfile
            {
                Name = "Кактус",
                MinSoilMoisture = 10,
                MaxSoilMoisture = 30,
                WaterDoseSeconds = 2,
                SoakTimeoutMinutes = 10
            };
            var tomato = new PlantProfile
            {
                Name = "Томат Черрі",
                MinSoilMoisture = 50,
                MaxSoilMoisture = 80,
                WaterDoseSeconds = 3,
                SoakTimeoutMinutes = 5
            };

            db.PlantProfiles.AddRange(basil, cactus, tomato);
            db.SaveChanges(); // Сохраняем растения, чтобы получить их ID

            // Теперь создаем физические горшки и привязываем к ним растения
            var pot1 = new ActivePot { PlantProfileId = basil.Id, RelayPin = 17, SensorChannel = 0, PlantedDate = DateTime.Now };
            var pot2 = new ActivePot { PlantProfileId = cactus.Id, RelayPin = 27, SensorChannel = 1, PlantedDate = DateTime.Now };
            var pot3 = new ActivePot { PlantProfileId = tomato.Id, RelayPin = 22, SensorChannel = 2, PlantedDate = DateTime.Now };

            db.ActivePots.AddRange(pot1, pot2, pot3);
            db.SaveChanges(); // Сохраняем горшки

            Console.WriteLine("База данных успешно наполнена тестовыми данными!");
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
