using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dipping.Core.Interfaces;
using Dipping.Core.Models;
using Microsoft.Win32;

namespace Dipping.WPF.ViewModels
{
    public partial class ConfigViewModel : ObservableObject
    {
        private readonly IConfigurationService _config;
        private readonly IAppLogger _logger;
        private bool _suppressPairFieldClamp;

        [ObservableProperty] private string _leftIp = string.Empty;
        [ObservableProperty] private string _rightIp = string.Empty;
        [ObservableProperty] private int _durationSeconds = RecordConfig.DurationDefaultSeconds;
        [ObservableProperty] private int _durationLimitSeconds = RecordConfig.DurationLimitDefaultSeconds;
        [ObservableProperty] private string _localFolder = string.Empty;
        [ObservableProperty] private string _shareFolder = string.Empty;
        [ObservableProperty] private string _photoFolder = string.Empty;
        [ObservableProperty] private int _burstCount = CaptureConfig.BurstCountDefault;
        [ObservableProperty] private int _burstCountLimit = CaptureConfig.BurstCountLimitDefault;
        [ObservableProperty] private int _burstIntervalMs = CaptureConfig.BurstIntervalDefaultMs;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanEditBurstInterval))]
        private bool _burstFastest;

        public bool CanEditBurstInterval => !BurstFastest;
        [ObservableProperty] private string _triggerSource = TriggerConfig.SourcePcProgram;
        [ObservableProperty] private int _tcpPort = 10100;
        [ObservableProperty] private string _plcHost = string.Empty;
        [ObservableProperty] private bool _scheduleEnabled;
        [ObservableProperty] private string _statusMessage = string.Empty;

        public ObservableCollection<RecordWindowRowViewModel> ScheduleWindows { get; } = new();

        public IReadOnlyList<string> TriggerSources { get; } =
            new[] { TriggerConfig.SourcePcProgram, TriggerConfig.SourcePlc };

        /// <summary>保存前由配置窗注入：提交文本框（各组先上限、后时长/张数）。</summary>
        public Action? CommitPendingConfigEdits { get; set; }

        public ConfigViewModel(IConfigurationService config, IAppLogger logger)
        {
            _config = config;
            _logger = logger;
            LoadFromSettings();
        }

        /// <summary>从内存中的 <see cref="IConfigurationService.Current"/> 重新加载（再次打开配置窗时避免仍显示旧快照）。</summary>
        public void ReloadFromSettings() => LoadFromSettings();

        private void LoadFromSettings()
        {
            var s = _config.Current;
            LeftIp = s.Cameras.FirstOrDefault(c => c.Position == "left")?.Ip ?? "192.168.2.50";
            RightIp = s.Cameras.FirstOrDefault(c => c.Position == "right")?.Ip ?? "192.168.2.51";
            var r = s.Record.Normalized();
            LocalFolder = r.LocalFolder;
            ShareFolder = r.ShareFolder;
            var cap = (s.Capture ?? new CaptureConfig()).Normalized();
            PhotoFolder = cap.Folder;
            _suppressPairFieldClamp = true;
            try
            {
                DurationLimitSeconds = r.DurationLimitSeconds;
                DurationSeconds = r.DurationSeconds;
                BurstCountLimit = cap.BurstCountLimit;
                BurstCount = cap.BurstCount;
            }
            finally
            {
                _suppressPairFieldClamp = false;
            }

            BurstIntervalMs = cap.BurstIntervalMs;
            BurstFastest = cap.BurstFastest;
            TriggerSource = string.IsNullOrWhiteSpace(s.Trigger.Source)
                ? TriggerConfig.SourcePcProgram
                : s.Trigger.Source;
            TcpPort = s.Trigger.TcpPort;
            PlcHost = s.Trigger.PlcHost;
            var schedule = RecordScheduleConfig.Normalize(s.RecordSchedule);
            ScheduleEnabled = schedule.Enabled;
            ScheduleWindows.Clear();
            foreach (var window in schedule.Windows)
            {
                ScheduleWindows.Add(new RecordWindowRowViewModel
                {
                    Index = window.Index,
                    Enabled = window.Enabled,
                    TimeRange = window.ToRangeText(),
                    MaxCount = window.MaxCount
                });
            }
        }

        [RelayCommand]
        private async Task SaveAsync()
        {
            CommitPendingConfigEdits?.Invoke();

            if (DurationLimitSeconds < RecordConfig.DurationMinSeconds
                || DurationLimitSeconds > RecordConfig.DurationHardMaxSeconds)
            {
                StatusMessage = $"录像时长上限须在 {RecordConfig.DurationMinSeconds}～{RecordConfig.DurationHardMaxSeconds} 秒。";
                return;
            }

            if (DurationSeconds < RecordConfig.DurationMinSeconds || DurationSeconds > DurationLimitSeconds)
            {
                StatusMessage = $"录像时长须在 {RecordConfig.DurationMinSeconds}～{DurationLimitSeconds} 秒。";
                return;
            }

            if (BurstCountLimit < CaptureConfig.BurstCountMin || BurstCountLimit > CaptureConfig.BurstCountHardMax)
            {
                StatusMessage = $"连拍张数上限须在 {CaptureConfig.BurstCountMin}～{CaptureConfig.BurstCountHardMax}。";
                return;
            }

            if (BurstCount < CaptureConfig.BurstCountMin || BurstCount > BurstCountLimit)
            {
                StatusMessage = $"连拍张数须在 {CaptureConfig.BurstCountMin}～{BurstCountLimit}。";
                return;
            }

            if (!BurstFastest
                && (BurstIntervalMs < CaptureConfig.BurstIntervalMinMs || BurstIntervalMs > CaptureConfig.BurstIntervalMaxMs))
            {
                StatusMessage = $"连拍间隔须在 {CaptureConfig.BurstIntervalMinMs}～{CaptureConfig.BurstIntervalMaxMs} 毫秒。";
                return;
            }

            if (!TryReadScheduleWindows(out var windows, out var scheduleError))
            {
                StatusMessage = scheduleError;
                return;
            }

            var s = _config.Current;
            EnsureCamera(s, "left", "Camera_Left", "左相机", LeftIp.Trim());
            EnsureCamera(s, "right", "Camera_Right", "右相机", RightIp.Trim());
            s.Record.DurationLimitSeconds = DurationLimitSeconds;
            s.Record.DurationSeconds = DurationSeconds;
            s.Record.LocalFolder = LocalFolder.Trim();
            s.Record.ShareFolder = ShareFolder.Trim();
            s.Capture ??= new CaptureConfig();
            s.Capture.Folder = PhotoFolder.Trim();
            s.Capture.BurstCountLimit = BurstCountLimit;
            s.Capture.BurstCount = BurstCount;
            s.Capture.BurstIntervalMs = BurstIntervalMs;
            s.Capture.BurstFastest = BurstFastest;
            s.Trigger.Source = TriggerSource;
            s.Trigger.TcpPort = TcpPort;
            s.Trigger.PlcHost = PlcHost.Trim();
            s.RecordSchedule = new RecordScheduleConfig
            {
                Enabled = ScheduleEnabled,
                Windows = windows
            };
            await _config.SaveAsync();
            ReloadFromSettings();
            StatusMessage = "已保存。";
            _logger.Info("配置已保存");
        }

        partial void OnDurationLimitSecondsChanged(int value)
        {
            if (_suppressPairFieldClamp)
                return;

            var clamped = value < RecordConfig.DurationMinSeconds
                ? RecordConfig.DurationMinSeconds
                : value > RecordConfig.DurationHardMaxSeconds
                    ? RecordConfig.DurationHardMaxSeconds
                    : value;
            if (clamped != value)
            {
                DurationLimitSeconds = clamped;
                return;
            }

            if (DurationSeconds > clamped)
                DurationSeconds = clamped;
        }

        partial void OnDurationSecondsChanged(int value)
        {
            if (_suppressPairFieldClamp)
                return;

            var max = DurationLimitSeconds >= RecordConfig.DurationMinSeconds
                ? DurationLimitSeconds
                : RecordConfig.DurationHardMaxSeconds;
            var clamped = value < RecordConfig.DurationMinSeconds
                ? RecordConfig.DurationMinSeconds
                : value > max ? max : value;
            if (clamped != value)
                DurationSeconds = clamped;
        }

        partial void OnBurstCountLimitChanged(int value)
        {
            if (_suppressPairFieldClamp)
                return;

            var clamped = value < CaptureConfig.BurstCountMin
                ? CaptureConfig.BurstCountMin
                : value > CaptureConfig.BurstCountHardMax
                    ? CaptureConfig.BurstCountHardMax
                    : value;
            if (clamped != value)
            {
                BurstCountLimit = clamped;
                return;
            }

            if (BurstCount > clamped)
                BurstCount = clamped;
        }

        partial void OnBurstCountChanged(int value)
        {
            if (_suppressPairFieldClamp)
                return;

            var max = BurstCountLimit >= CaptureConfig.BurstCountMin
                ? BurstCountLimit
                : CaptureConfig.BurstCountHardMax;
            var clamped = value < CaptureConfig.BurstCountMin
                ? CaptureConfig.BurstCountMin
                : value > max ? max : value;
            if (clamped != value)
                BurstCount = clamped;
        }

        private static void EnsureCamera(AppSettings s, string position, string id, string name, string ip)
        {
            var camera = s.Cameras.FirstOrDefault(c => c.Position == position);
            if (camera == null)
            {
                camera = new CameraConfig { Position = position, DeviceId = id, Name = name, Enabled = true };
                s.Cameras.Add(camera);
            }

            camera.Ip = ip;
            camera.Enabled = !string.IsNullOrWhiteSpace(ip);
            camera.Name = name;
            camera.DeviceId = id;
        }

        [RelayCommand]
        private void BrowseLocalFolder()
        {
            var folder = PickFolder("选择本地保存目录", LocalFolder);
            if (folder != null)
                LocalFolder = folder;
        }

        [RelayCommand]
        private void BrowsePhotoFolder()
        {
            var folder = PickFolder("选择存图目录", PhotoFolder);
            if (folder != null)
                PhotoFolder = folder;
        }

        [RelayCommand]
        private void BrowseShareFolder()
        {
            var folder = PickFolder("选择网络共享目录", ShareFolder);
            if (folder != null)
                ShareFolder = folder;
        }

        private static string? PickFolder(string title, string? current)
        {
            var dialog = new OpenFolderDialog
            {
                Title = title,
                Multiselect = false
            };
            var path = (current ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    if (Directory.Exists(path))
                        dialog.InitialDirectory = path;
                }
                catch
                {
                    // UNC 不可达时仍弹出对话框，让用户自己选或新建。
                }
            }

            return dialog.ShowDialog() == true ? dialog.FolderName : null;
        }

        private bool TryReadScheduleWindows(out List<RecordWindowConfig> windows, out string error)
        {
            windows = new List<RecordWindowConfig>();
            error = string.Empty;
            if (ScheduleWindows.Count != RecordScheduleConfig.WindowCount)
            {
                error = $"时段表须为 {RecordScheduleConfig.WindowCount} 行。";
                return false;
            }

            for (var i = 0; i < ScheduleWindows.Count; i++)
            {
                var row = ScheduleWindows[i];
                if (!RecordScheduleConfig.TryParseRange(row.TimeRange, out var start, out var end))
                {
                    error = $"第 {row.Index} 行时间段格式应为 07:00-09:00。";
                    return false;
                }

                if (start == end)
                {
                    error = $"第 {row.Index} 行开始与结束不能相同。";
                    return false;
                }

                if (row.MaxCount < 0 || row.MaxCount > 9999)
                {
                    error = $"第 {row.Index} 行次数须在 0～9999。";
                    return false;
                }

                windows.Add(new RecordWindowConfig
                {
                    Index = i + 1,
                    Enabled = row.Enabled,
                    Start = RecordScheduleConfig.FormatTime(start),
                    End = RecordScheduleConfig.FormatTime(end),
                    MaxCount = row.MaxCount
                });
            }

            return true;
        }
    }

    public partial class RecordWindowRowViewModel : ObservableObject
    {
        [ObservableProperty] private int _index;
        [ObservableProperty] private bool _enabled;
        [ObservableProperty] private string _timeRange = "07:00-09:00";
        [ObservableProperty] private int _maxCount = 1;
    }
}
