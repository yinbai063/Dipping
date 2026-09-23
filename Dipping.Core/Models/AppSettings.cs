namespace Dipping.Core.Models
{
    public class AppSettings
    {
        public List<CameraConfig> Cameras { get; set; } = new();
        public VisionSdkConfig VisionSdk { get; set; } = new();
        public RecordConfig Record { get; set; } = new();
        /// <summary>按时段/次数限制自动录像。关总开关则与以前一样不限制。</summary>
        public RecordScheduleConfig RecordSchedule { get; set; } = RecordScheduleConfig.CreateDefault();
        public CaptureConfig Capture { get; set; } = new();
        public TriggerConfig Trigger { get; set; } = new();
        public LoggingConfig Logging { get; set; } = new();
        /// <summary>二期通道框。每台相机 10 个。一期只保存，不画、不分析。</summary>
        public List<ChannelRoi> ChannelRois { get; set; } = ChannelRoi.CreateDefaultSet();
    }

    public class CameraConfig
    {
        public bool Enabled { get; set; } = true;
        public string DeviceId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Position { get; set; } = string.Empty;
        public string Ip { get; set; } = string.Empty;

        /// <summary>SCMVS 登录用户名。左右各自独立，默认 Admin。</summary>
        public string SdkUserName { get; set; } = "Admin";

        /// <summary>SCMVS 登录密码。左右各自独立，默认 Abc1234。</summary>
        public string SdkPassword { get; set; } = "Abc1234";

        /// <summary>登录成功后是否把本相机账号密码写回 config.json。</summary>
        public bool RememberSdkPassword { get; set; } = true;

        /// <summary>本相机是否响应 TCP/PLC 自动开录。左右各自独立。</summary>
        public bool AutoRecord { get; set; }

        public (string UserName, string Password) ResolveSdkLogin(VisionSdkConfig? sdk)
        {
            var user = string.IsNullOrWhiteSpace(SdkUserName)
                ? (string.IsNullOrWhiteSpace(sdk?.UserName) ? "Admin" : sdk!.UserName)
                : SdkUserName.Trim();
            var password = string.IsNullOrEmpty(SdkPassword) ? (sdk?.Password ?? string.Empty) : SdkPassword;
            return (user, password ?? string.Empty);
        }
    }

    public class VisionSdkConfig
    {
        public string UserName { get; set; } = "Admin";
        public string Password { get; set; } = "Abc1234";
        public int GrabTimeoutMs { get; set; } = 200;
    }

    public class RecordConfig
    {
        public const int DurationDefaultSeconds = 3;
        public const int DurationMinSeconds = 1;
        public const int DurationLimitDefaultSeconds = 60;
        public const int DurationHardMaxSeconds = 3600;

        /// <summary>本地保存根目录。空则程序目录 Data/Videos。</summary>
        public string LocalFolder { get; set; } = string.Empty;

        /// <summary>网络共享目录（UNC，如 \\pc-name\DippingClips）。空则不复制。</summary>
        public string ShareFolder { get; set; } = string.Empty;

        /// <summary>一次 dipping 录像秒数，到点自动停。</summary>
        public int DurationSeconds { get; set; } = DurationDefaultSeconds;

        /// <summary>录像时长上限，可在配置里改。默认 60 秒。</summary>
        public int DurationLimitSeconds { get; set; } = DurationLimitDefaultSeconds;

        public int ResolveDurationLimit()
        {
            if (DurationLimitSeconds < DurationMinSeconds)
                return DurationLimitDefaultSeconds;
            return DurationLimitSeconds > DurationHardMaxSeconds
                ? DurationHardMaxSeconds
                : DurationLimitSeconds;
        }

        public RecordConfig Normalized()
        {
            var limit = ResolveDurationLimit();
            var seconds = DurationSeconds < DurationMinSeconds
                ? DurationMinSeconds
                : DurationSeconds > limit ? limit : DurationSeconds;
            return new RecordConfig
            {
                LocalFolder = (LocalFolder ?? string.Empty).Trim(),
                ShareFolder = (ShareFolder ?? string.Empty).Trim(),
                DurationSeconds = seconds,
                DurationLimitSeconds = limit
            };
        }
    }

    public class CaptureConfig
    {
        /// <summary>拍照/连拍保存。空则程序目录 Data/Images。</summary>
        public string Folder { get; set; } = string.Empty;

        public const int BurstCountDefault = 5;
        public const int BurstCountMin = 1;
        public const int BurstCountLimitDefault = 200;
        public const int BurstCountHardMax = 100_000;
        public const int BurstIntervalDefaultMs = 500;
        public const int BurstIntervalMinMs = 50;
        public const int BurstIntervalMaxMs = 10000;
        public const int GrabTimeoutDefaultMs = 3000;

        public int BurstCount { get; set; } = BurstCountDefault;
        /// <summary>连拍张数上限，可在配置里改。默认 200。</summary>
        public int BurstCountLimit { get; set; } = BurstCountLimitDefault;
        public int BurstIntervalMs { get; set; } = BurstIntervalDefaultMs;
        public bool BurstFastest { get; set; }
        public int GrabTimeoutMs { get; set; } = GrabTimeoutDefaultMs;

        public int ResolveBurstCountLimit()
        {
            if (BurstCountLimit < BurstCountMin)
                return BurstCountLimitDefault;
            return BurstCountLimit > BurstCountHardMax ? BurstCountHardMax : BurstCountLimit;
        }

        public CaptureConfig Normalized()
        {
            var limit = ResolveBurstCountLimit();
            var count = BurstCount < BurstCountMin ? BurstCountMin : BurstCount > limit ? limit : BurstCount;
            var interval = BurstIntervalMs < BurstIntervalMinMs
                ? BurstIntervalMinMs
                : BurstIntervalMs > BurstIntervalMaxMs ? BurstIntervalMaxMs : BurstIntervalMs;
            var timeout = GrabTimeoutMs < 200 ? 200 : GrabTimeoutMs > 15000 ? 15000 : GrabTimeoutMs;
            return new CaptureConfig
            {
                Folder = (Folder ?? string.Empty).Trim(),
                BurstCount = count,
                BurstCountLimit = limit,
                BurstIntervalMs = interval,
                BurstFastest = BurstFastest,
                GrabTimeoutMs = timeout
            };
        }

        public string ResolveFolder()
        {
            var folder = (Folder ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(folder))
                return folder;
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Images");
        }
    }

    public class TriggerConfig
    {
        public const string SourcePcProgram = "PcProgram";
        public const string SourcePlc = "Plc";

        /// <summary>勾选后由 PC 程序或 PLC 自动开录，手动录像按钮失能。</summary>
        public bool AutoRecord { get; set; }

        /// <summary><see cref="SourcePcProgram"/> 或 <see cref="SourcePlc"/>。</summary>
        public string Source { get; set; } = SourcePcProgram;

        public int TcpPort { get; set; } = 10100;

        /// <summary>二期/PLC：Modbus 或其它地址，一期只占位。</summary>
        public string PlcHost { get; set; } = string.Empty;
        public int PlcPort { get; set; } = 502;
        public ushort PlcAddress { get; set; }
    }

    /// <summary>每天 12 个两小时时段。到点不会自动开录，只拦截上位机/PLC 开录指令。</summary>
    public class RecordScheduleConfig
    {
        public const int WindowCount = 12;
        public static readonly TimeSpan DayStart = new(7, 0, 0);

        /// <summary>总开关。默认关；关掉则自动录像不受时段和次数限制。</summary>
        public bool Enabled { get; set; }

        public List<RecordWindowConfig> Windows { get; set; } = new();

        public static RecordScheduleConfig CreateDefault() =>
            new() { Enabled = false, Windows = CreateDefaultWindows() };

        public static List<RecordWindowConfig> CreateDefaultWindows()
        {
            var list = new List<RecordWindowConfig>(WindowCount);
            var start = DayStart;
            for (var i = 0; i < WindowCount; i++)
            {
                var from = start + TimeSpan.FromHours(i * 2);
                var to = from + TimeSpan.FromHours(2);
                list.Add(new RecordWindowConfig
                {
                    Index = i + 1,
                    Enabled = i == 0,
                    Start = FormatTime(from),
                    End = FormatTime(to),
                    MaxCount = 1
                });
            }

            return list;
        }

        public static RecordScheduleConfig Normalize(RecordScheduleConfig? raw)
        {
            var result = new RecordScheduleConfig { Enabled = raw?.Enabled ?? false };
            var defaults = CreateDefaultWindows();
            var existing = raw?.Windows ?? new List<RecordWindowConfig>();
            for (var i = 0; i < WindowCount; i++)
            {
                var fallback = defaults[i];
                var row = existing.FirstOrDefault(w => w.Index == i + 1)
                          ?? (i < existing.Count ? existing[i] : null);
                if (row == null)
                {
                    result.Windows.Add(fallback);
                    continue;
                }

                result.Windows.Add(new RecordWindowConfig
                {
                    Index = i + 1,
                    Enabled = row.Enabled,
                    Start = TryParseTime(row.Start, out _) ? FormatTime(ParseTime(row.Start, fallback.Start)) : fallback.Start,
                    End = TryParseTime(row.End, out _) ? FormatTime(ParseTime(row.End, fallback.End)) : fallback.End,
                    MaxCount = row.MaxCount < 0 ? 0 : row.MaxCount > 9999 ? 9999 : row.MaxCount
                });
            }

            return result;
        }

        public static DateTime ScheduleDate(DateTime now)
        {
            var date = now.Date;
            return now.TimeOfDay < DayStart ? date.AddDays(-1) : date;
        }

        public static bool TryParseTime(string? text, out TimeSpan value)
        {
            value = default;
            var t = (text ?? string.Empty).Trim();
            if (TimeSpan.TryParse(t, out value))
            {
                value = new TimeSpan(value.Hours, value.Minutes, 0);
                return true;
            }

            if (DateTime.TryParse(t, out var dt))
            {
                value = new TimeSpan(dt.Hour, dt.Minute, 0);
                return true;
            }

            return false;
        }

        public static TimeSpan ParseTime(string? text, string fallback)
        {
            if (TryParseTime(text, out var value))
                return value;
            TryParseTime(fallback, out value);
            return value;
        }

        public static string FormatTime(TimeSpan value)
        {
            var wrapped = TimeSpan.FromMinutes(((int)value.TotalMinutes % (24 * 60) + 24 * 60) % (24 * 60));
            return $"{wrapped.Hours:D2}:{wrapped.Minutes:D2}";
        }

        public static bool Contains(TimeSpan now, TimeSpan start, TimeSpan end)
        {
            now = new TimeSpan(now.Hours, now.Minutes, now.Seconds);
            start = new TimeSpan(start.Hours, start.Minutes, 0);
            end = new TimeSpan(end.Hours, end.Minutes, 0);
            if (start == end)
                return false;
            if (start < end)
                return now >= start && now < end;
            return now >= start || now < end;
        }

        public static bool TryParseRange(string? text, out TimeSpan start, out TimeSpan end)
        {
            start = default;
            end = default;
            var raw = (text ?? string.Empty).Trim().Replace('～', '-').Replace('—', '-');
            var parts = raw.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 2 && TryParseTime(parts[0], out start) && TryParseTime(parts[1], out end);
        }
    }

    public class RecordWindowConfig
    {
        public int Index { get; set; }
        public bool Enabled { get; set; }
        public string Start { get; set; } = "07:00";
        public string End { get; set; } = "09:00";
        public int MaxCount { get; set; } = 1;

        public string ToRangeText() => $"{Start}-{End}";
    }

    public class LoggingConfig
    {
        public string LogDirectory { get; set; } = "Logs";
        public string MinimumLevel { get; set; } = "Information";
        public int RetainedDays { get; set; } = 30;
    }

    /// <summary>一台相机画面上的一个通道框。坐标相对结果图像素。</summary>
    public class ChannelRoi
    {
        public string CameraIp { get; set; } = string.Empty;
        public int ChannelIndex { get; set; }
        public bool Enabled { get; set; } = true;
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }

        public static List<ChannelRoi> CreateDefaultSet()
        {
            var list = new List<ChannelRoi>();
            foreach (var ip in new[] { "192.168.2.50", "192.168.2.51" })
            {
                for (var i = 1; i <= 10; i++)
                    list.Add(new ChannelRoi { CameraIp = ip, ChannelIndex = i, Enabled = true });
            }

            return list;
        }
    }
}
