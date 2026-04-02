using System.Device.Gpio;

namespace SmartGreenhouse.Services
{
    public class HardwareService : IDisposable
    {
        private readonly GpioController _gpio;
        private const int LedPin = 17; // Мы договорились использовать GPIO 17

        public HardwareService()
        {
            _gpio = new GpioController();
            // Инициализируем пин на выход
            _gpio.OpenPin(LedPin, PinMode.Output);
        }

        public void ToggleLed(bool state)
        {
            // Записываем состояние: High (3.3V) или Low (0V)
            _gpio.Write(LedPin, state ? PinValue.High : PinValue.Low);
        }

        // Правило хорошего тона: освобождаем ресурсы при закрытии программы
        public void Dispose()
        {
            if (_gpio.IsPinOpen(LedPin))
                _gpio.ClosePin(LedPin);
            _gpio.Dispose();
        }
    }
}