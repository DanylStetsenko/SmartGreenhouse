using Iot.Device.Ads1115;
using Iot.Device.Bmxx80;
using Iot.Device.Bmxx80.PowerMode;
using Iot.Device.Ssd13xx;
using Iot.Device.Ssd13xx.Commands;
using Iot.Device.Ssd13xx.Commands.Ssd1306Commands;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;
using SmartGreenhouse.Data;
using SmartGreenhouse.Models;
using System.Device.Gpio;
using System.Device.I2c;

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
        // Главный рубильник системы
        public bool IsSystemActive { get; private set; } = true;

        // I2C для BME280
        private readonly I2cDevice _i2cDeviceBme;
        private readonly Bme280 _bme280;
        private readonly I2cDevice _i2cDeviceOled;
        private readonly Ssd1306 _display;
        private readonly I2cDevice _i2cDeviceAdc;
        private readonly Ads1115 _adc;
        private Timer _timer;
        private readonly object _displayLock = new object();
        // --- НАСТРОЙКИ АВТОМАТИКИ ---
        private const int WATERING_COOLDOWN_MINUTES = 5;
        private const int PUMP_BURST_MS = 3000;
        private async Task AutomationLoopAsync()
        {
            while (true)
            {
                await Task.Delay(5000); // Опитуємо систему кожні 5 секунд

                if (!IsSystemActive) continue; // Якщо рубильник вимкнений - нічого не робимо

                try
                {
                    using var db = new GreenhouseContext();
                    var pots = db.ActivePots.Include(p => p.PlantProfile).ToList();
                    var currentTime = DateTime.Now.TimeOfDay;

                    foreach (var pot in pots)
                    {
                        // Перевіряємо, чи є вже пам'ять для цього горщика. Якщо ні - створюємо.
                        if (!_wateringStates.ContainsKey(pot.Id))
                        {
                            _wateringStates[pot.Id] = new PotWateringState();
                        }
                        var state = _wateringStates[pot.Id];

                        // 1. БІОЛОГІЧНИЙ ГОДИННИК (Сон)
                        bool isNight = false;
                        var sleep = pot.PlantProfile.SleepTime;
                        var wake = pot.PlantProfile.WakeUpTime;

                        if (sleep > wake) // Наприклад: спить з 22:00 до 08:00
                            isNight = currentTime >= sleep || currentTime < wake;
                        else
                            isNight = currentTime >= sleep && currentTime < wake;

                        if (isNight)
                        {
                            // Якщо настала ніч, жорстко скидаємо цикл поливу
                            state.IsWateringCycle = false;
                            continue;
                        }

                        // 2. ЧИТАЄМО ДАТЧИК З УРАХУВАННЯМ КАЛІБРОВКИ
                        short rawVal = 0;
                        int dry = 15000; // значення за замовчуванням
                        int wet = 0;

                        if (pot.RelayPin == 17)
                        {
                            rawVal = RawSoil1;
                            dry = _calibrations[17].Dry;
                            wet = _calibrations[17].Wet;
                        }
                        else if (pot.RelayPin == 27)
                        {
                            rawVal = RawSoil2;
                            dry = _calibrations[27].Dry;
                            wet = _calibrations[27].Wet;
                        }
                        else if (pot.RelayPin == 22)
                        {
                            rawVal = RawSoil3;
                            dry = _calibrations[22].Dry;
                            wet = _calibrations[22].Wet;
                        }

                        // Перетворюємо у відсотки за індивідуальною формулою датчика
                        int moisturePercent = MapToPercent(rawVal, dry, wet);

                        // 3. ЛОГІКА ГІСТЕРЕЗИСУ (Автомат станів)
                        if (!state.IsWateringCycle)
                        {
                            // СТАН 1: Звичайний моніторинг
                            if (moisturePercent < pot.PlantProfile.MinSoilMoisture)
                            {
                                Console.WriteLine($"[АВТОМАТИКА] Горщик #{pot.Id} (Слот {pot.RelayPin}) висох ({moisturePercent}%). Починаю цикл поливу!");
                                state.IsWateringCycle = true;

                                await PulsePump(pot.RelayPin);
                                state.LastPulseTime = DateTime.Now;
                            }
                        }
                        else
                        {
                            // СТАН 2: Режим насичення вологою
                            if ((DateTime.Now - state.LastPulseTime).TotalMinutes >= WATERING_COOLDOWN_MINUTES)
                            {
                                if (moisturePercent >= pot.PlantProfile.MaxSoilMoisture)
                                {
                                    Console.WriteLine($"[АВТОМАТИКА] Горщик #{pot.Id} напився ({moisturePercent}%). Завершую цикл.");
                                    state.IsWateringCycle = false;
                                }
                                else
                                {
                                    Console.WriteLine($"[АВТОМАТИКА] Горщик #{pot.Id} ще хоче пити ({moisturePercent}%). Даю ще дозу.");
                                    await PulsePump(pot.RelayPin);
                                    state.LastPulseTime = DateTime.Now;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ПОМИЛКА АВТОМАТИКИ] {ex.Message}");
                }
            }
        }
        // Структура для калибровки одного датчика
        private class SensorCalibration
        {
            public int Dry { get; set; }
            public int Wet { get; set; }
        }

        // Словник калібровок з прив'язкою до пінів реле (апаратних слотів)
        // Поки ти калібруєш, я залишив тут твій еталон для першого порту і заглушки для інших.
        // Як тільки отримаєш цифри — просто впиши їх сюди:
        private readonly Dictionary<int, SensorCalibration> _calibrations = new()
{
    { 17, new SensorCalibration { Dry = 21225, Wet = 7648 } }, // Слот 1 (Насос 17, АЦП AIN0)
    { 27, new SensorCalibration { Dry = 21551, Wet = 7528 } }, // Слот 2 (Насос 27, АЦП AIN2) - ЗАГЛУШКА
    { 22, new SensorCalibration { Dry = 21429, Wet = 7542 } }  // Слот 3 (Насос 22, АЦП AIN3) - ЗАГЛУШКА
};

        // Тепер функція приймає індивідуальні значення сухості/вологості
        private int MapToPercent(short rawValue, int dry, int wet)
        {
            if (dry == wet) return 0; // Захист від ділення на нуль (на випадок помилки в конфігу)

            int percent = 100 - (int)Math.Round((double)(rawValue - wet) / (dry - wet) * 100.0);

            if (percent < 0) return 0;
            if (percent > 100) return 100;

            return percent;
        }
        // Публічний метод для контролера, щоб він міг забрати готові відсотки
        public int GetMoisturePercentForPin(int pin)
        {
            short rawVal = 0;
            int dry = 15000;
            int wet = 0;

            if (pin == 17)
            {
                rawVal = RawSoil1;
                dry = _calibrations[17].Dry;
                wet = _calibrations[17].Wet;
            }
            else if (pin == 27)
            {
                rawVal = RawSoil2;
                dry = _calibrations[27].Dry;
                wet = _calibrations[27].Wet;
            }
            else if (pin == 22)
            {
                rawVal = RawSoil3;
                dry = _calibrations[22].Dry;
                wet = _calibrations[22].Wet;
            }
            else
            {
                return 0;
            }

            return MapToPercent(rawVal, dry, wet);
        }

        private async Task PulsePump(int pin)
        {
            try
            {
                _gpio.Write(pin, PinValue.High);
                await Task.Delay(PUMP_BURST_MS);
            }
            finally
            {
                _gpio.Write(pin, PinValue.Low);
            }
        }

        // Состояния нашего интерфейса
        private enum DisplayMode { WebText, Soil, Uv }
        private DisplayMode _currentMode = DisplayMode.WebText;
        private string _webText = "Ожидание..."; // Текст с сайта по умолчанию
        private string _remoteStatus = "Ждем сигнал пульта...";
        private class PotWateringState
        {
            public bool IsWateringCycle { get; set; } = false;
            public DateTime LastPulseTime { get; set; } = DateTime.MinValue;
        }
        private readonly Dictionary<int, PotWateringState> _wateringStates = new();

        // Метод для прямого керування пінами з дебаг-панелі
        public void SetPumpManualStatus(int pin, bool turnOn)
        {
            // Список дозволених пінів для безпеки (наші насоси)
            int[] allowedPins = { 17, 27, 22 };
            if (!allowedPins.Contains(pin)) return;

            // Просто міняємо стан: High - включено, Low - виключено
            _gpio.Write(pin, turnOn ? PinValue.High : PinValue.Low);

            Console.WriteLine($"[DEBUG] Порт GPIO {pin} переведено в стан: {(turnOn ? "HIGH" : "LOW")}");
        }
        public void SetSystemState(bool isActive)
        {
            IsSystemActive = isActive;

            using (var db = new GreenhouseContext())
            {
                var setting = db.SystemSettings.FirstOrDefault(s => s.Key == "MainSwitch");
                if (setting == null)
                {
                    db.SystemSettings.Add(new SystemSetting { Key = "MainSwitch", Value = isActive });
                }
                else
                {
                    setting.Value = isActive;
                }
                db.SaveChanges();
            }

            // Если систему поставили на паузу — ЖЕЛЕЗОБЕТОННО ВЫКЛЮЧАЕМ ВСЕ РЕЛЕ
            if (!IsSystemActive)
            {
                _gpio.Write(17, PinValue.Low);
                _gpio.Write(27, PinValue.Low);
                _gpio.Write(22, PinValue.Low);
                Console.WriteLine("🛑 Система поставлена на паузу. Все насосы принудительно отключены.");
            }
            else
            {
                Console.WriteLine("✅ Система автоматики снова активна.");
            }
        }
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

            using (var db = new GreenhouseContext())
            {
                var setting = db.SystemSettings.FirstOrDefault(s => s.Key == "MainSwitch");
                // Если в базе еще нет такой записи, считаем, что по умолчанию включено
                IsSystemActive = setting?.Value ?? true;
            }
            Task.Run(AutomationLoopAsync);

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
                await Task.Delay(4000);
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