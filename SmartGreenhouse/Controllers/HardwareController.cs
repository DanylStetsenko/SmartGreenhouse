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

        [HttpPost("led")]
        public IActionResult ControlLed([FromQuery] bool turnOn)
        {
            _hardware.ToggleLed(turnOn);
            return Ok(new { Message = $"Светодиод {(turnOn ? "включен" : "выключен")}" });
        }
    }
}