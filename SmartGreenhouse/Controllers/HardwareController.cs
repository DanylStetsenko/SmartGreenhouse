using Microsoft.AspNetCore.Mvc;
using SmartGreenhouse.Services;

namespace SmartGreenhouse.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class HardwareController : ControllerBase
    {
        private readonly HardwareService _hardware;
        // .NET сам передаст сюда HardwareService благодаря DI
        public HardwareController(HardwareService hardware)
        {
            _hardware = hardware;
        }

        //[HttpPost("led")]
        //public IActionResult ControlLed([FromQuery] bool turnOn)
        //{
        //    _hardware.ToggleLed(turnOn);
        //    return Ok(new { Message = $"Светодиод {(turnOn ? "включен" : "выключен")}" });
        //}
        [HttpGet("sensors")]
        public IActionResult GetSensors()
        {
            // Запрашиваем данные у нашего сервиса
            var data = _hardware.GetCurrentSensorValues();

            // Отправляем их браузеру со статусом 200 (Ok)
            return Ok(data);
        }
        [HttpPost("display")]
        public IActionResult UpdateDisplay([FromQuery] string text)
        {
            _hardware.DisplayText(text);
            return Ok(new { Message = $"Текст '{text}' успешно отправлен на экран!" });
        }
    }
}