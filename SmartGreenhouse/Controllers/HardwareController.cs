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

        [HttpPost("/display")]
        public IActionResult UpdateDisplay([FromQuery] string text)
        {
            _hardwareService.DisplayText(text);
            return Ok(new { Message = $"Текст '{text}' успешно отправлен на экран!" });
        }

        [HttpPost("/water")]
        public async Task<IActionResult> TriggerWatering()
        {
            await _hardwareService.WaterPlantsAsync();
            return Ok(new { Message = "Полив на 1 секунду успешно завершен!" });
        }

        [HttpGet("/api/sensors")]
        public IActionResult GetSensors()
        {
            _hardwareService.GetCurrentSensorValues();

            using var db = new GreenhouseContext();
            var activePots = db.ActivePots.ToList();
            var potsSensorData = new Dictionary<int, int>();

            foreach (var pot in activePots)
            {
                // Відправляємо на фронтенд не сирі дані, а одразу готові відсотки!
                potsSensorData.Add(pot.Id, _hardwareService.GetMoisturePercentForPin(pot.RelayPin));
            }

            var responseData = new
            {
                isSystemActive = _hardwareService.IsSystemActive,
                airTemp = _hardwareService.AirTemp,
                airHum = _hardwareService.AirHum,
                uvLight = _hardwareService.UvLight,
                pots = potsSensorData
            };

            return Ok(responseData);
        }

        [HttpGet("/api/pots")]
        public IActionResult GetActivePots()
        {
            using (var db = new GreenhouseContext())
            {
                var pots = db.ActivePots
                             .Include(p => p.PlantProfile)
                             .Select(p => new {
                                 id = p.Id,
                                 plantName = p.PlantProfile.Name,
                                 relayPin = p.RelayPin,
                                 wateringDoseMs = p.WateringDose // Передаем дозу на клиент
                             })
                             .ToList();

                return Ok(pots);
            }
        }

        [HttpGet("/api/plants")]
        public IActionResult GetPlants()
        {
            using var db = new GreenhouseContext();
            var plants = db.PlantProfiles.Select(p => new { p.Id, p.Name }).ToList();
            return Ok(plants);
        }

        [HttpPost("/api/debug/pump")]
        public IActionResult DebugPump([FromBody] DebugPumpRequest request)
        {
            _hardwareService.SetPumpManualStatus(request.Pin, request.IsOn);
            return Ok();
        }

        [HttpPost("/api/pots")]
        public IActionResult AddPot([FromBody] AddPotRequest request)
        {
            using var db = new GreenhouseContext();

            int[] availablePins = new[] { 17, 27, 22 };
            var usedPins = db.ActivePots.Select(p => p.RelayPin).ToList();
            var freePin = availablePins.FirstOrDefault(pin => !usedPins.Contains(pin));

            if (freePin == 0) return BadRequest("Немає вільних апаратних слотів! Максимум 3 горщики.");

            var plant = db.PlantProfiles.FirstOrDefault(p => p.Id == request.PlantProfileId);
            if (plant == null) return BadRequest("Рослина не знайдена");

            var newPot = new ActivePot
            {
                PlantProfileId = plant.Id,
                PlantProfile = plant,
                PlantName = plant.Name,
                RelayPin = freePin,
                WateringDose = 3000 // Стандартная доза при добавлении
            };

            db.ActivePots.Add(newPot);
            db.SaveChanges();

            return Ok(newPot);
        }

        [HttpDelete("/api/pots/{id}")]
        public IActionResult DeletePot(int id)
        {
            using var db = new GreenhouseContext();
            var pot = db.ActivePots.Find(id);
            if (pot == null) return NotFound("Горщик не знайдено");

            db.ActivePots.Remove(pot);
            db.SaveChanges();

            return Ok();
        }

        // --- НОВЫЙ ЭНДПОИНТ: Обновление дозы полива ---
        [HttpPut("/api/pots/{id}/dose")]
        public IActionResult UpdatePotDose(int id, [FromBody] UpdateDoseRequest request)
        {
            using var db = new GreenhouseContext();
            var pot = db.ActivePots.Find(id);
            if (pot == null) return NotFound("Горщик не знайдено");

            // Ограничиваем от случайных безумных значений
            if (request.DoseMs < 100 || request.DoseMs > 60000)
                return BadRequest("Доза повинна бути від 100 мс до 60 000 мс");

            pot.WateringDose = request.DoseMs;
            db.SaveChanges();

            return Ok();
        }
    }

    public class UpdateDoseRequest
    {
        public int DoseMs { get; set; }
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