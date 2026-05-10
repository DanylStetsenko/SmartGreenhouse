using Iot.Device.Ads1115;
using Iot.Device.Ssd13xx;
using Iot.Device.Ssd13xx.Commands;
using Iot.Device.Ssd13xx.Commands.Ssd1306Commands;
using SkiaSharp;
using SmartGreenhouse.Data;
using System.Device.Gpio;
using System.Device.I2c;
using Microsoft.EntityFrameworkCore;
using Iot.Device.Bmxx80;
using Iot.Device.Bmxx80.PowerMode;

namespace SmartGreenhouse.Services
{
    public class HardwareService : IDisposable
    {
        private readonly GpioController _gpio;

        public short RawSoil1 { get; private set; }
        public short RawSoil2 { get; private set; }
        public short RawSoil3 { get; private set; }
        public short UvLight { get; private set; }
        // Публичные свойства для контроллера
        public double AirTemp { get; private set; }
        public double AirHum { get; private set; }

        // I2C для BME280
        private readonly I2cDevice _i2cDeviceBme;
        private readonly Bme280 _bme280;
        private readonly I2cDevice _i2cDeviceOled;
        private readonly Ssd1306 _display;
        private readonly I2cDevice _i2cDeviceAdc;
        private readonly Ads1115 _adc;
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

            //// 1. Инициализация OLED дисплея (Адрес 0x3C)
            _i2cDeviceOled = I2cDevice.Create(new I2cConnectionSettings(1, 0x3C));
            _display = new Ssd1306(_i2cDeviceOled, 128, 64);
            _display.SendCommand(new SetDisplayOn());
            ClearDisplay();

            //// 2. Инициализация АЦП ADS1115 (Адрес 0x48)
            _i2cDeviceAdc = I2cDevice.Create(new I2cConnectionSettings(1, (int)I2cAddress.GND));
            _adc = new Ads1115(_i2cDeviceAdc);

            //// 3. Открываем пины в самом простом, безопасном режиме (без подтяжек)
            _gpio.OpenPin(5, PinMode.Input);
            _gpio.OpenPin(6, PinMode.Input);
            _gpio.OpenPin(13, PinMode.Input);

            _i2cDeviceBme = I2cDevice.Create(new I2cConnectionSettings(1, 0x76));
            _bme280 = new Bme280(_i2cDeviceBme);

            // НОВОЕ: Настраиваем порты для реле насосов (GPIO 23, 24, 25)
            _gpio.OpenPin(17, PinMode.Output);
            _gpio.OpenPin(27, PinMode.Output);
            _gpio.OpenPin(22, PinMode.Output);

            // КРИТИЧЕСКИ ВАЖНО: Сразу подаем LOW (0 Вольт), чтобы насосы были ВЫКЛЮЧЕНЫ
            _gpio.Write(17, PinValue.Low);
            _gpio.Write(27, PinValue.Low);
            _gpio.Write(22, PinValue.Low);

            Task.Run(ButtonListenerLoop);
            _timer = new Timer(OnTimerTick, null, 0, 2000);

        }
        public async Task TestWaterPumpAsync()
        {
            int testPin = 17; // Хардкодим пин для теста
            try
            {
                _gpio.Write(testPin, PinValue.High);
                await Task.Delay(1000); // Крутим 1 секунду
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TEST ПОЛИВ ОШИБКА] {ex.Message}");
            }
            finally
            {
                _gpio.Write(testPin, PinValue.Low);
            }
        }
        public async Task WaterPlantsAsync()
        {
            try
            {
                // Включаем насосы (подаем 3.3V, реле в режиме HIGH замыкаются)
                _gpio.Write(17, PinValue.High);
                _gpio.Write(27, PinValue.High);
                _gpio.Write(22, PinValue.High);

                // Ждем ровно 1000 миллисекунд (1 секунду)
                await Task.Delay(1000);
            }
            catch (Exception ex)
            {
                // Если ошибка произойдет прямо во время ожидания, мы ее увидим в консоли
                Console.WriteLine($"[КРИТИЧЕСКАЯ ОШИБКА] Сбой во время полива: {ex.Message}");
            }
            finally
            {
                // Блок finally выполнится АБСОЛЮТНО ВСЕГДА. 
                // Даже если программа крашится, таймер сходит с ума или пропадает сеть.
                // Выключаем насосы, спасаем квартиру от потопа:
                _gpio.Write(17, PinValue.Low);
                _gpio.Write(27, PinValue.Low);
                _gpio.Write(22, PinValue.Low);
            }
        }
        // Новая функция в HardwareService для надежного чтения АЦП
        private short ReadFilteredAdc(InputMultiplexer channel)
        {
            // 1. Переключаем "рубильник" на нужный датчик
            _adc.InputMultiplexer = channel;

            // Даем АЦП пару миллисекунд, чтобы напряжение на входе стабилизировалось
            Thread.Sleep(5);

            const int SAMPLES_COUNT = 5;
            short[] samples = new short[SAMPLES_COUNT]; // У ReadRaw тип short, поэтому массив тоже short

            // 2. Делаем 5 быстрых замеров
            for (int i = 0; i < SAMPLES_COUNT; i++)
            {
                samples[i] = _adc.ReadRaw(); // Читаем БЕЗ аргументов
                Thread.Sleep(10);
            }

            // 3. Сортируем и берем медиану
            Array.Sort(samples);
            return samples[SAMPLES_COUNT / 2];
        }
        public async Task WaterPotByIdAsync(int potId)
        {
            using var db = new GreenhouseContext();

            // Ищем горшок в базе и подтягиваем профиль растения
            var pot = db.ActivePots.Include(p => p.PlantProfile).FirstOrDefault(p => p.Id == potId);

            // Если горшок не найден в базе — ничего не делаем
            if (pot == null) return;

            try
            {
                // Включаем насос для конкретного горшка (используем пин из базы!)
                _gpio.Write(pot.RelayPin, PinValue.High);

                // Ждем столько секунд, сколько прописано в дозе полива для этого растения
                await Task.Delay(1000);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ОШИБКА ПОЛИВА] Горщик {potId}: {ex.Message}");
            }
            finally
            {
                // Железобетонно выключаем именно этот пин
                _gpio.Write(pot.RelayPin, PinValue.Low);
            }
        }
        // БЕЗОПАСНЫЙ ОПРОС КНОПОК
        private async Task ButtonListenerLoop()
        {
            // Переменные для защиты от "залипания" кнопки
            bool lastD0 = false;
            bool lastD1 = false;
            bool lastVt = false;

            while (true)
            {
                // Читаем текущее состояние пинов (High означает, что есть напряжение 3.3V)
                bool currentD0 = _gpio.Read(5) == PinValue.High;
                bool currentD1 = _gpio.Read(6) == PinValue.High;
                bool currentVt = _gpio.Read(13) == PinValue.High;

                // Если D0 только что нажали
                if (currentD0 && !lastD0)
                {
                    _currentMode = DisplayMode.Soil;
                    _remoteStatus = $"Кнопка D0 нажата в {DateTime.Now:HH:mm:ss}";
                    UpdateSensorData();
                }

                // Если D1 только что нажали
                if (currentD1 && !lastD1)
                {
                    _currentMode = DisplayMode.Uv;
                    _remoteStatus = $"Кнопка D1 нажата в {DateTime.Now:HH:mm:ss}";
                    UpdateSensorData();
                }

                // Если поймали сигнал от пульта (VT)
                if (currentVt && !lastVt)
                {
                    _remoteStatus = $"Сигнал пульта пойман (VT) в {DateTime.Now:HH:mm:ss}";
                }

                // Запоминаем состояния для следующего цикла
                lastD0 = currentD0;
                lastD1 = currentD1;
                lastVt = currentVt;

                // Ждем 100 миллисекунд (10 проверок в секунду)
                await Task.Delay(100);
            }
        }

        // Этот метод вызывается каждые 2 секунды
        private void OnTimerTick(object? state)
        {
            try
            {
                // Пытаемся обновить экран и прочитать датчики
                UpdateSensorData();
            }
            catch (System.IO.IOException ex)
            {
                // Если I2C шину "пробило" помехой от реле, мы ловим ошибку здесь!
                // Программа НЕ крашится, мы просто пишем в консоль и ждем следующего тика.
                Console.WriteLine($"[I2C EMI ПОМЕХА] Экран завис, но мы продолжаем работу. Ошибка: {ex.Message}");
            }
            catch (Exception ex)
            {
                // Ловим любые другие ошибки таймера
                Console.WriteLine($"[ОШИБКА ТАЙМЕРА] {ex.Message}");
            }
        }

        // Логика опроса датчиков
        private void UpdateSensorData()
        {
            if (_currentMode == DisplayMode.Soil)
            {
                _adc.InputMultiplexer = InputMultiplexer.AIN0;
                short rawSoil = _adc.ReadRaw();
                DrawTextOnOled($"Почва (A0):\n\n {rawSoil}");
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
            // Скармливаем нашей умной функции нужные каналы
            RawSoil1 = ReadFilteredAdc(InputMultiplexer.AIN0);
            RawSoil2 = ReadFilteredAdc(InputMultiplexer.AIN2);
            RawSoil3 = ReadFilteredAdc(InputMultiplexer.AIN3);

            // УФ-датчик тоже прогоним через фильтр, ему стабилизация не повредит
            UvLight = ReadFilteredAdc(InputMultiplexer.AIN1);
            try
            {
                _bme280.SetPowerMode(Bmx280PowerMode.Forced); // Будим датчик для замера
                Thread.Sleep(50); // Даем ему 50мс на подумать

                _bme280.TryReadTemperature(out var tempValue);
                AirTemp = Math.Round(tempValue.DegreesCelsius, 1); // Округляем до 1 знака (напр. 24.5)

                _bme280.TryReadHumidity(out var humValue);
                AirHum = Math.Round(humValue.Percent, 1);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка BME280: {ex.Message}");
            }
            return new
            {
                Soil1 = RawSoil1,
                Soil2 = RawSoil2,
                Soil3 = RawSoil3,
                Uv = UvLight,
                remote = _remoteStatus
            };
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
            if (_gpio.IsPinOpen(17)) { _gpio.Write(17, PinValue.Low); _gpio.ClosePin(17); }
            if (_gpio.IsPinOpen(27)) { _gpio.Write(27, PinValue.Low); _gpio.ClosePin(27); }
            if (_gpio.IsPinOpen(22)) { _gpio.Write(22, PinValue.Low); _gpio.ClosePin(22); }
            _gpio.Dispose();



            ClearDisplay();
            _display.SendCommand(new SetDisplayOff());
            _display.Dispose();
            _i2cDeviceOled.Dispose();
            _i2cDeviceAdc.Dispose();

        }
    }
}