using System.Text.Json;
using Dipping.Core.Interfaces;
using Dipping.Core.Models;

namespace Dipping.Core.Services
{
    public static class ConfigJsonOptions
    {
        public static readonly JsonSerializerOptions Read = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public static readonly JsonSerializerOptions Write = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    public class JsonConfigService : IConfigurationService
    {
        private readonly string _configFilePath;
        private AppSettings? _settings;

        public AppSettings Current => _settings ?? new AppSettings();

        public JsonConfigService(string? configFilePath = null)
        {
            _configFilePath = configFilePath
                ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        }

        public async Task LoadAsync()
        {
            if (!File.Exists(_configFilePath))
            {
                _settings = CreateDefault();
                await SaveAsync();
                return;
            }

            var json = await File.ReadAllTextAsync(_configFilePath);
            _settings = JsonSerializer.Deserialize<AppSettings>(json, ConfigJsonOptions.Read)
                        ?? CreateDefault();
            _settings.RecordSchedule = RecordScheduleConfig.Normalize(_settings.RecordSchedule);
        }

        public async Task SaveAsync()
        {
            _settings ??= CreateDefault();
            _settings.RecordSchedule = RecordScheduleConfig.Normalize(_settings.RecordSchedule);
            var directory = Path.GetDirectoryName(_configFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(_settings, ConfigJsonOptions.Write);
            await File.WriteAllTextAsync(_configFilePath, json);
        }

        public List<CameraConfig> GetEnabledCameras() =>
            Current.Cameras.Where(c => c.Enabled && !string.IsNullOrWhiteSpace(c.Ip)).ToList();

        public static AppSettings CreateDefault() => new()
        {
            Cameras =
            {
                new CameraConfig
                {
                    Enabled = true,
                    DeviceId = "Camera_Left",
                    Name = "左相机",
                    Position = "left",
                    Ip = "192.168.2.50",
                    SdkUserName = "Admin",
                    SdkPassword = "Abc1234",
                    RememberSdkPassword = true
                },
                new CameraConfig
                {
                    Enabled = true,
                    DeviceId = "Camera_Right",
                    Name = "右相机",
                    Position = "right",
                    Ip = "192.168.2.51",
                    SdkUserName = "Admin",
                    SdkPassword = "Abc1234",
                    RememberSdkPassword = true
                }
            }
        };
    }
}
