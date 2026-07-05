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
        public double AirTemp { get; private set; }
        public double AirHum { get; private set; }
        public bool IsSystemActive { get; private set; } = true;

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

        private async Task AutomationLoopAsync()
        {
            while (true)
            {
                await Task.Delay(5000);

                if (!IsSystemActive) continue;

                try
                {
                    using var db = new GreenhouseContext();
                    var pots = db.ActivePots.Include(p => p.PlantProfile).ToList();
                    var currentTime = DateTime.Now.TimeOfDay;

                    foreach (var pot in pots)
                    {
                        if (!_wateringStates.ContainsKey(pot.Id))
                        {
                            _wateringStates[pot.Id] = new PotWateringState();
                        }
                        var state = _wateringStates[pot.Id];

                        bool isNight = false;
                        var sleep = pot.PlantProfile.SleepTime;
                        var wake = pot.PlantProfile.WakeUpTime;

                        if (sleep > wake)
                            isNight = currentTime >= sleep || currentTime < wake;
                        else
                            isNight = currentTime >= sleep && currentTime < wake;

                        if (isNight)
                        {
                            state.IsWateringCycle = false;
                            continue;
                        }

                        short rawVal = 0;
                        int dry = 15000;
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

                        int moisturePercent = MapToPercent(rawVal, dry, wet);

                        if (!state.IsWateringCycle)
                        {
                            if (moisturePercent < pot.PlantProfile.MinSoilMoisture)
                            {
                                Console.WriteLine($"[АВТОМАТИКА] Горщик #{pot.Id} (Слот {pot.RelayPin}) висох ({moisturePercent}%). Починаю цикл поливу!");
                                state.IsWateringCycle = true;

                                // Передаем индивидуальную дозу полива из базы
                                await PulsePump(pot.RelayPin, pot.WateringDose > 0 ? pot.WateringDose : 3000);
                                state.LastPulseTime = DateTime.Now;
                            }
                        }
                        else
                        {
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
                                    await PulsePump(pot.RelayPin, pot.WateringDose > 0 ? pot.WateringDose : 3000);
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

        private class SensorCalibration
        {
            public int Dry { get; set; }
            public int Wet { get; set; }
        }

        private readonly Dictionary<int, SensorCalibration> _calibrations = new()
        {
            { 17, new SensorCalibration { Dry = 21225, Wet = 7648 } },
            { 27, new SensorCalibration { Dry = 21551, Wet = 7528 } },
            { 22, new SensorCalibration { Dry = 21429, Wet = 7542 } }
        };

        private int MapToPercent(short rawValue, int dry, int wet)
        {
            if (dry == wet) return 0;
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

        // Обновленная функция: теперь принимает время работы в мс
        private async Task PulsePump(int pin, int durationMs)
        {
            try
            {
                _gpio.Write(pin, PinValue.High);
                await Task.Delay(durationMs);
            }
            finally
            {
                _gpio.Write(pin, PinValue.Low);
            }
        }

        private enum DisplayMode { WebText, Soil, Uv }
        private DisplayMode _currentMode = DisplayMode.WebText;
        private string _webText = "Ожидание...";
        private string _remoteStatus = "Ждем сигнал пульта...";

        private class PotWateringState
        {
            public bool IsWateringCycle { get; set; } = false;
            public DateTime LastPulseTime { get; set; } = DateTime.MinValue;
        }
        private readonly Dictionary<int, PotWateringState> _wateringStates = new();

        public void SetPumpManualStatus(int pin, bool turnOn)
        {
            int[] allowedPins = { 17, 27, 22 };
            if (!allowedPins.Contains(pin)) return;
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

            _i2cDeviceOled = I2cDevice.Create(new I2cConnectionSettings(1, 0x3C));
            _display = new Ssd1306(_i2cDeviceOled, 128, 64);
            _display.SendCommand(new SetDisplayOn());
            ClearDisplay();

            _i2cDeviceAdc = I2cDevice.Create(new I2cConnectionSettings(1, (int)I2cAddress.GND));
            _adc = new Ads1115(_i2cDeviceAdc);

            _gpio.OpenPin(5, PinMode.Input);
            _gpio.OpenPin(6, PinMode.Input);
            _gpio.OpenPin(13, PinMode.Input);

            _i2cDeviceBme = I2cDevice.Create(new I2cConnectionSettings(1, 0x76));
            _bme280 = new Bme280(_i2cDeviceBme);

            _gpio.OpenPin(17, PinMode.Output);
            _gpio.OpenPin(27, PinMode.Output);
            _gpio.OpenPin(22, PinMode.Output);

            _gpio.Write(17, PinValue.Low);
            _gpio.Write(27, PinValue.Low);
            _gpio.Write(22, PinValue.Low);

            Task.Run(ButtonListenerLoop);
            _timer = new Timer(OnTimerTick, null, 0, 2000);

            using (var db = new GreenhouseContext())
            {
                var setting = db.SystemSettings.FirstOrDefault(s => s.Key == "MainSwitch");
                IsSystemActive = setting?.Value ?? true;
            }
            Task.Run(AutomationLoopAsync);
        }

        public async Task TestWaterPumpAsync()
        {
            int testPin = 17;
            try
            {
                _gpio.Write(testPin, PinValue.High);
                await Task.Delay(1000);
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
                _gpio.Write(17, PinValue.High);
                _gpio.Write(27, PinValue.High);
                _gpio.Write(22, PinValue.High);
                await Task.Delay(1000);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[КРИТИЧЕСКАЯ ОШИБКА] Сбой во время полива: {ex.Message}");
            }
            finally
            {
                _gpio.Write(17, PinValue.Low);
                _gpio.Write(27, PinValue.Low);
                _gpio.Write(22, PinValue.Low);
            }
        }

        private short ReadFilteredAdc(InputMultiplexer channel)
        {
            _adc.InputMultiplexer = channel;
            Thread.Sleep(5);
            const int SAMPLES_COUNT = 5;
            short[] samples = new short[SAMPLES_COUNT];
            for (int i = 0; i < SAMPLES_COUNT; i++)
            {
                samples[i] = _adc.ReadRaw();
                Thread.Sleep(10);
            }
            Array.Sort(samples);
            return samples[SAMPLES_COUNT / 2];
        }

        public async Task WaterPotByIdAsync(int potId)
        {
            using var db = new GreenhouseContext();
            var pot = db.ActivePots.Include(p => p.PlantProfile).FirstOrDefault(p => p.Id == potId);
            if (pot == null) return;

            try
            {
                _gpio.Write(pot.RelayPin, PinValue.High);
                // Используем индивидуальную дозу полива
                await Task.Delay(pot.WateringDose > 0 ? pot.WateringDose : 3000);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ОШИБКА ПОЛИВА] Горщик {potId}: {ex.Message}");
            }
            finally
            {
                _gpio.Write(pot.RelayPin, PinValue.Low);
            }
        }

        private async Task ButtonListenerLoop()
        {
            bool lastD0 = false;
            bool lastD1 = false;
            bool lastVt = false;

            while (true)
            {
                bool currentD0 = _gpio.Read(5) == PinValue.High;
                bool currentD1 = _gpio.Read(6) == PinValue.High;
                bool currentVt = _gpio.Read(13) == PinValue.High;

                if (currentD0 && !lastD0)
                {
                    _currentMode = DisplayMode.Soil;
                    _remoteStatus = $"Кнопка D0 нажата в {DateTime.Now:HH:mm:ss}";
                    UpdateSensorData();
                }

                if (currentD1 && !lastD1)
                {
                    _currentMode = DisplayMode.Uv;
                    _remoteStatus = $"Кнопка D1 нажата в {DateTime.Now:HH:mm:ss}";
                    UpdateSensorData();
                }

                if (currentVt && !lastVt)
                {
                    _remoteStatus = $"Сигнал пульта пойман (VT) в {DateTime.Now:HH:mm:ss}";
                }

                lastD0 = currentD0;
                lastD1 = currentD1;
                lastVt = currentVt;

                await Task.Delay(100);
            }
        }

        private void OnTimerTick(object? state)
        {
            try
            {
                UpdateSensorData();
            }
            catch (System.IO.IOException ex)
            {
                Console.WriteLine($"[I2C EMI ПОМЕХА] Экран завис, но мы продолжаем работу. Ошибка: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ОШИБКА ТАЙМЕРА] {ex.Message}");
            }
        }

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

        public void DisplayText(string text)
        {
            _currentMode = DisplayMode.WebText;
            _webText = text;
            UpdateSensorData();
        }

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

        public object GetCurrentSensorValues()
        {
            RawSoil1 = ReadFilteredAdc(InputMultiplexer.AIN0);
            RawSoil2 = ReadFilteredAdc(InputMultiplexer.AIN2);
            RawSoil3 = ReadFilteredAdc(InputMultiplexer.AIN3);
            UvLight = ReadFilteredAdc(InputMultiplexer.AIN1);
            try
            {
                _bme280.SetPowerMode(Bmx280PowerMode.Forced);
                Thread.Sleep(50);

                _bme280.TryReadTemperature(out var tempValue);
                AirTemp = Math.Round(tempValue.DegreesCelsius, 1);

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