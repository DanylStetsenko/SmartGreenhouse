using Microsoft.AspNetCore.Mvc;
using SmartGreenhouse.Data;
using SmartGreenhouse.Services;
using Microsoft.EntityFrameworkCore;

namespace SmartGreenhouse.Controllers
{
    [ApiController]
    [Route("/api/[controller]")]
    public class HardwareController : ControllerBase
    {
        // 1. Создаем приватную переменную, в которой будет храниться ссылка на сервис
        private readonly HardwareService _hardwareService;

        // 2. Создаем КОНСТРУКТОР. 
        // Когда ASP.NET создает контроллер, он сам найдет HardwareService и подставит его сюда.
        public HardwareController(HardwareService hardwareService)
        {
            _hardwareService = hardwareService;
        }

        // ... а дальше уже идут твои методы [HttpGet("/api/pots")] и остальные ...

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

        // ...
        private readonly HardwareService _hardware;
        // .NET сам передаст сюда HardwareService благодаря DI
        //public HardwareController(HardwareService hardware)
        //{
        //    _hardware = hardware;
        //}

        //[HttpPost("led")]
        //public IActionResult ControlLed([FromQuery] bool turnOn)
        //{
        //    _hardware.ToggleLed(turnOn);
        //    return Ok(new { Message = $"Светодиод {(turnOn ? "включен" : "выключен")}" });
        //}
        //[HttpGet("sensors")]
        //public IActionResult GetSensors()
        //{
        //    // Запрашиваем данные у нашего сервиса
        //    var data = _hardware.GetCurrentSensorValues();

        //    // Отправляем их браузеру со статусом 200 (Ok)
        //    return Ok(data);
        //}
        [HttpPost("/display")]
        public IActionResult UpdateDisplay([FromQuery] string text)
        {
            _hardware.DisplayText(text);
            return Ok(new { Message = $"Текст '{text}' успешно отправлен на экран!" });
        }
        [HttpPost("/water")]
        public async Task<IActionResult> TriggerWatering()
        {
            // Запускаем наш метод полива
            await _hardware.WaterPlantsAsync();

            // Возвращаем ответ в браузер
            return Ok(new { Message = "Полив на 1 секунду успешно завершен!" });
        }

        // 2. Эндпоинт для датчиков
        [HttpGet("/api/sensors")]
        public IActionResult GetSensors()
        {
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
    }
}