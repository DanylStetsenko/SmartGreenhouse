using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartGreenhouse.Data;
using SmartGreenhouse.Models;
using SmartGreenhouse.Services;

namespace SmartGreenhouse.Controllers
{
    [ApiController]
    [Route("/api/[controller]")]
    public class HardwareController : ControllerBase
    {
        private readonly HardwareService _hardwareService;
        public HardwareController(HardwareService hardwareService)
        {
            _hardwareService = hardwareService;
        }
        [HttpPost("/api/water/test")]
        public async Task<IActionResult> TestWater()
        {
            await _hardwareService.TestWaterPumpAsync();
            return Ok();
        }
        [HttpPost("/api/water/{id}")]
        public async Task<IActionResult> WaterPot(int id)
        {
            await _hardwareService.WaterPotByIdAsync(id);
            return Ok();
        }
        private readonly HardwareService _hardware;
        [HttpPost("/display")]
        public IActionResult UpdateDisplay([FromQuery] string text)
        {
            _hardware.DisplayText(text);
            return Ok(new { Message = $"Текст '{text}' успешно отправлен на экран!" });
        }
        [HttpPost("/water")]
        public async Task<IActionResult> TriggerWatering()
        {
            await _hardware.WaterPlantsAsync();

            return Ok(new { Message = "Полив на 1 секунду успешно завершен!" });
        }
        [HttpGet("/api/sensors")]
        public IActionResult GetSensors()
        {
            _hardwareService.GetCurrentSensorValues();
            // Здесь мы создаем словарь: Ключ = ID горшка в базе, Значение = текущая влажность.
            // ВНИМАНИЕ: Замени MoistureA0 и т.д. на те реальные переменные/свойства из твоего 
            // HardwareService, в которых сейчас хранятся цифры с АЦП!
            var sensorData = new Dictionary<int, int>
            {
                { 1, _hardwareService.RawSoil1 }, // Данные для горшка с ID 1
                { 2, _hardwareService.RawSoil2 }, // Данные для горшка с ID 2
                { 3, _hardwareService.RawSoil3 }  // Данные для горшка с ID 3
            };

            return Ok(sensorData);
        }
        [HttpGet("/api/pots")]
        public IActionResult GetActivePots()
        {
            using (var db = new GreenhouseContext())
            {
                // Достаем все горшки и заодно "приклеиваем" к ним данные о растении (Include)
                var pots = db.ActivePots
                             .Include(p => p.PlantProfile)
                             .Select(p => new {
                                 id = p.Id,
                                 plantName = p.PlantProfile.Name,
                                 relayPin = p.RelayPin,
                                 // Сюда же можно вытягивать текущую влажность, если она сохранена, 
                                 // или пока отдавать 0
                             })
                             .ToList();

                return Ok(pots); // Возвращаем в виде красивого JSON
            }
        }
        // 1. Отдаем список доступных растений для менюшки
        [HttpGet("/api/plants")]
        public IActionResult GetPlants()
        {
            using var db = new GreenhouseContext();
            // Берем из базы только ID и Имя, нам больше для выпадающего списка не нужно
            var plants = db.PlantProfiles.Select(p => new { p.Id, p.Name }).ToList();
            return Ok(plants);
        }

        // 2. Принимаем запрос на создание нового горшка
        [HttpPost("/api/pots")]
        public IActionResult AddPot([FromBody] AddPotRequest request)
        {
            using var db = new GreenhouseContext();

            // Ищем профиль растения, который выбрал пользователь
            var plant = db.PlantProfiles.FirstOrDefault(p => p.Id == request.PlantProfileId);
            if (plant == null) return BadRequest("Рослина не знайдена");

            // Создаем новую запись для горшка
            var newPot = new ActivePot
            {
                PlantProfileId = plant.Id,
                PlantProfile = plant,
                PlantName = plant.Name,
                RelayPin = request.RelayPin
            };

            db.ActivePots.Add(newPot);
            db.SaveChanges(); // Сохраняем в SQLite

            return Ok(newPot);
        }
    }
    public class AddPotRequest
    {
        public int PlantProfileId { get; set; }
        public int RelayPin { get; set; }
    }
}