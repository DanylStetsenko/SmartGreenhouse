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
            // Пнули сервис, чтобы он обновил цифры с железа
            _hardwareService.GetCurrentSensorValues();

            using var db = new GreenhouseContext();
            var pots = db.ActivePots.ToList();
            var sensorData = new Dictionary<int, int>();

            // Собираем показания датчиков, ориентируясь на то, какой насос (пин) привязан к горшку
            foreach (var pot in pots)
            {
                if (pot.RelayPin == 17) sensorData.Add(pot.Id, _hardwareService.RawSoil1);
                else if (pot.RelayPin == 27) sensorData.Add(pot.Id, _hardwareService.RawSoil2);
                else if (pot.RelayPin == 22) sensorData.Add(pot.Id, _hardwareService.RawSoil3);
            }

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

            // 1. Наши физические "слоты" (пины насосов: 17, 27, 22)
            int[] availablePins = new[] { 17, 27, 22 };

            // 2. Смотрим, какие пины уже заняты горшками в базе
            var usedPins = db.ActivePots.Select(p => p.RelayPin).ToList();

            // 3. Ищем первый свободный пин
            var freePin = availablePins.FirstOrDefault(pin => !usedPins.Contains(pin));

            // Если свободный пин равен 0 (ничего не нашлось), значит мест нет!
            if (freePin == 0) return BadRequest("Немає вільних апаратних слотів! Максимум 3 горщики.");

            // Ищем профиль растения
            var plant = db.PlantProfiles.FirstOrDefault(p => p.Id == request.PlantProfileId);
            if (plant == null) return BadRequest("Рослина не знайдена");

            // Создаем горшок с автоматически выбранным пином
            var newPot = new ActivePot
            {
                PlantProfileId = plant.Id,
                PlantProfile = plant,
                PlantName = plant.Name,
                RelayPin = freePin // <-- Магия здесь!
            };

            db.ActivePots.Add(newPot);
            db.SaveChanges();

            return Ok(newPot);
        }
        [HttpDelete("/api/pots/{id}")]
        public IActionResult DeletePot(int id)
        {
            using var db = new GreenhouseContext();

            // Ищем горшок в базе по его ID
            var pot = db.ActivePots.Find(id);

            // Если такого горшка нет, возвращаем ошибку
            if (pot == null) return NotFound("Горщик не знайдено");

            // Удаляем из базы и сохраняем изменения
            db.ActivePots.Remove(pot);
            db.SaveChanges();

            return Ok();
        }
    }
    public class AddPotRequest
    {
        public int PlantProfileId { get; set; }
    }
}