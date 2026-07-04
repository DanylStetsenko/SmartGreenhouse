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
        [HttpPost("/api/system/toggle")]
        public IActionResult ToggleSystem([FromBody] ToggleRequest request)
        {
            _hardwareService.SetSystemState(request.IsActive);
            return Ok();
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
            // 1. Опрашиваем железо
            _hardwareService.GetCurrentSensorValues();

            // 2. Достаем актуальные горшки из базы, чтобы знать их настоящие ID (например, 1, 4, 5)
            using var db = new GreenhouseContext();
            var activePots = db.ActivePots.ToList();
            var potsSensorData = new Dictionary<int, int>();

            // 3. Умно сопоставляем показания АЦП с ID горшков, ориентируясь на их пин насоса
            // 3. Умно сопоставляем показания АЦП с ID горшков
            foreach (var pot in activePots)
            {
                // Тепер ми відправляємо на фронтенд не сирі дані, а одразу готові відсотки!
                potsSensorData.Add(pot.Id, _hardwareService.GetMoisturePercentForPin(pot.RelayPin));
            }

            // 4. Упаковываем всё в красивый JSON для сайта
            var responseData = new
            {
                isSystemActive = _hardwareService.IsSystemActive,
                airTemp = _hardwareService.AirTemp,
                airHum = _hardwareService.AirHum,
                uvLight = _hardwareService.UvLight,
                pots = potsSensorData // <-- Теперь тут динамический словарь с правильными ID!
            };

            return Ok(responseData);
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
        [HttpPost("/api/debug/pump")]
        public IActionResult DebugPump([FromBody] DebugPumpRequest request)
        {
            _hardwareService.SetPumpManualStatus(request.Pin, request.IsOn);
            return Ok();
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
    public class DebugPumpRequest
    {
        public int Pin { get; set; }
        public bool IsOn { get; set; }
    }
    public class AddPotRequest
    {
        public int PlantProfileId { get; set; }
    }
    public class ToggleRequest
    {
        public bool IsActive { get; set; }
    }
}