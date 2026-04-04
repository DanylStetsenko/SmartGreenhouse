using System.Device.Gpio;
using System.Device.I2c;
using Iot.Device.Ads1115;
using Iot.Device.Ssd13xx;
using Iot.Device.Ssd13xx.Commands;
using Iot.Device.Ssd13xx.Commands.Ssd1306Commands;
using SkiaSharp;

namespace SmartGreenhouse.Services
{
    public class HardwareService : IDisposable
    {
        private readonly GpioController _gpio;

        // I2C устройства
        private readonly I2cDevice _i2cDeviceOled;
        private readonly Ssd1306 _display;
        private readonly I2cDevice _i2cDeviceAdc;
        private readonly Ads1115 _adc;

        // Таймер для фонового обновления
        private Timer _timer;
        private readonly object _displayLock = new object();

        // Состояния нашего интерфейса
        private enum DisplayMode { WebText, Soil, Uv }
        private DisplayMode _currentMode = DisplayMode.WebText;
        private string _webText = "Ожидание..."; // Текст с сайта по умолчанию
        private string _remoteStatus = "Ждем сигнал пульта...";

        public HardwareService()
        {
            _gpio = new GpioController();

            // 1. Инициализация OLED дисплея (Адрес 0x3C)
            _i2cDeviceOled = I2cDevice.Create(new I2cConnectionSettings(1, 0x3C));
            _display = new Ssd1306(_i2cDeviceOled, 128, 64);
            _display.SendCommand(new SetDisplayOn());
            ClearDisplay();

            // 2. Инициализация АЦП ADS1115 (Адрес 0x48)
            _i2cDeviceAdc = I2cDevice.Create(new I2cConnectionSettings(1, (int)I2cAddress.GND));
            _adc = new Ads1115(_i2cDeviceAdc);

            // Настройка кнопок D0 (Пин 5) и D1 (Пин 6)
            _gpio.OpenPin(5, PinMode.Input);
            _gpio.OpenPin(6, PinMode.Input);

            // Настройка пина VT (Пин 13) для отлова любой активности пульта
            _gpio.OpenPin(13, PinMode.Input);

            _gpio.RegisterCallbackForPinValueChangedEvent(5, PinEventTypes.Rising, (sender, args) => {
                _currentMode = DisplayMode.Soil;
                _remoteStatus = $"Кнопка D0 нажата в {DateTime.Now:HH:mm:ss}";
                UpdateSensorData();
            });

            _gpio.RegisterCallbackForPinValueChangedEvent(6, PinEventTypes.Rising, (sender, args) => {
                _currentMode = DisplayMode.Uv;
                _remoteStatus = $"Кнопка D1 нажата в {DateTime.Now:HH:mm:ss}";
                UpdateSensorData();
            });

            // VT реагирует на любую кнопку пульта
            _gpio.RegisterCallbackForPinValueChangedEvent(13, PinEventTypes.Rising, (sender, args) => {
                _remoteStatus = $"Сигнал пульта пойман (VT) в {DateTime.Now:HH:mm:ss}";
            });

            // Вешаем "прослушку" на нажатие (когда напряжение растет - Rising)
            _gpio.RegisterCallbackForPinValueChangedEvent(5, PinEventTypes.Rising, (sender, args) => {
                _currentMode = DisplayMode.Soil;
                UpdateSensorData(); // Мгновенное обновление при клике
            });

            _gpio.RegisterCallbackForPinValueChangedEvent(6, PinEventTypes.Rising, (sender, args) => {
                _currentMode = DisplayMode.Uv;
                UpdateSensorData(); // Мгновенное обновление при клике
            });

            // 4. Запуск таймера: ждать 0 секунд, повторять каждые 2000 мс
            _timer = new Timer(OnTimerTick, null, 0, 2000);
        }

        // Этот метод вызывается каждые 2 секунды
        private void OnTimerTick(object? state)
        {
            UpdateSensorData();
        }

        // Логика опроса датчиков
        private void UpdateSensorData()
        {
            if (_currentMode == DisplayMode.Soil)
            {
                _adc.InputMultiplexer = InputMultiplexer.AIN0;
                short rawSoil = _adc.ReadRaw();
                DrawTextOnOled($"Почва (A0):\n\n {rawSoil}"); // Выдаем "сырые" цифры для калибровки
            }
            else if (_currentMode == DisplayMode.Uv)
            {
                _adc.InputMultiplexer = InputMultiplexer.AIN1;
                short rawUv = _adc.ReadRaw();
                DrawTextOnOled($"УФ (A1):\n\n {rawUv}");
            }
            else if (_currentMode == DisplayMode.WebText)
            {
                DrawTextOnOled($"Сообщение:\n\n {_webText}");
            }
        }

        // Метод, который дергает твой HardwareController из браузера
        public void DisplayText(string text)
        {
            _currentMode = DisplayMode.WebText;
            _webText = text;
            UpdateSensorData();
        }

        // Наша бронебойная отрисовка текста с защитой потоков
        private void DrawTextOnOled(string text)
        {
            lock (_displayLock)
            {
                using var bitmap = new SKBitmap(128, 64);
                using var canvas = new SKCanvas(bitmap);
                canvas.Clear(SKColors.Black);

                using var paint = new SKPaint
                {
                    Color = SKColors.White,
                    TextSize = 18,
                    IsAntialias = false
                };

                // Простенький перенос строк для символа \n
                var lines = text.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    canvas.DrawText(lines[i], 0, 20 + (i * 20), paint);
                }

                for (int page = 0; page < 8; page++)
                {
                    _i2cDeviceOled.Write(new byte[] { 0x00, (byte)(0xB0 + page) });
                    _i2cDeviceOled.Write(new byte[] { 0x00, 0x00 });
                    _i2cDeviceOled.Write(new byte[] { 0x00, 0x10 });

                    byte[] pageData = new byte[128];
                    for (int x = 0; x < 128; x++)
                    {
                        for (int y = 0; y < 8; y++)
                        {
                            if (bitmap.GetPixel(x, page * 8 + y).Red > 127)
                            {
                                pageData[x] |= (byte)(1 << y);
                            }
                        }
                    }
                    _display.SendData(pageData);
                }
            }
        }
        // Метод для получения свежих данных для веб-сайта
        public object GetCurrentSensorValues()
        {
            // Читаем почву (канал A0)
            _adc.InputMultiplexer = InputMultiplexer.AIN0;
            short rawSoil = _adc.ReadRaw();

            // Читаем УФ (канал A1)
            _adc.InputMultiplexer = InputMultiplexer.AIN1;
            short rawUv = _adc.ReadRaw();

            // Возвращаем анонимный объект, который ASP.NET сам превратит в JSON
            return new { Soil = rawSoil, Uv = rawUv, remote = _remoteStatus };
        }
        public void ClearDisplay()
        {
            lock (_displayLock)
            {
                for (int page = 0; page < 8; page++)
                {
                    _i2cDeviceOled.Write(new byte[] { 0x00, (byte)(0xB0 + page) });
                    _i2cDeviceOled.Write(new byte[] { 0x00, 0x00 });
                    _i2cDeviceOled.Write(new byte[] { 0x00, 0x10 });
                    _display.SendData(new byte[128]);
                }
            }
        }

        public void Dispose()
        {
            _timer?.Dispose();

            if (_gpio.IsPinOpen(5)) _gpio.ClosePin(5);
            if (_gpio.IsPinOpen(6)) _gpio.ClosePin(6);
            if (_gpio.IsPinOpen(13)) _gpio.ClosePin(13);
            _gpio.Dispose();
            ClearDisplay();
            _display.SendCommand(new SetDisplayOff());
            _display.Dispose();
            _i2cDeviceOled.Dispose();
            _i2cDeviceAdc.Dispose();
        }
    }
}