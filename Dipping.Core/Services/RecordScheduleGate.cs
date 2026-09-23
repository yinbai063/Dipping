using System.Text.Json;
using Dipping.Core.Models;

namespace Dipping.Core.Services
{
    /// <summary>
    /// 自动录像时段/次数闸门。到点不开录；只决定是否响应上位机指令。
    /// 次数按「当天 07:00 起的 24 小时」累计，写到 Data/record-schedule-usage.json。
    /// </summary>
    public sealed class RecordScheduleGate
    {
        private readonly string _usagePath;
        private readonly object _sync = new();

        public RecordScheduleGate(string? usagePath = null)
        {
            _usagePath = usagePath
                ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "record-schedule-usage.json");
        }

        public bool TryAllowAutoRecord(
            RecordScheduleConfig? raw,
            DateTime now,
            string cameraKey,
            string cameraName,
            out int windowIndex,
            out string denyReason)
        {
            windowIndex = 0;
            denyReason = string.Empty;
            var schedule = RecordScheduleConfig.Normalize(raw);
            if (!schedule.Enabled)
                return true;

            var nowTime = now.TimeOfDay;
            RecordWindowConfig? hit = null;
            TimeSpan hitStart = default, hitEnd = default;
            foreach (var window in schedule.Windows)
            {
                if (!window.Enabled)
                    continue;
                if (!RecordScheduleConfig.TryParseTime(window.Start, out var start)
                    || !RecordScheduleConfig.TryParseTime(window.End, out var end))
                    continue;
                if (!RecordScheduleConfig.Contains(nowTime, start, end))
                    continue;
                hit = window;
                hitStart = start;
                hitEnd = end;
                break;
            }

            if (hit == null)
            {
                denyReason = $"{cameraName}：当前 {now:HH:mm} 不在已启用的录像时段内，忽略开录指令。";
                return false;
            }

            var used = ReadUsed(RecordScheduleConfig.ScheduleDate(now), cameraKey, hit.Index);
            if (used >= hit.MaxCount)
            {
                denyReason =
                    $"{cameraName}：时段 {RecordScheduleConfig.FormatTime(hitStart)}-{RecordScheduleConfig.FormatTime(hitEnd)} 已录像 {used}/{hit.MaxCount} 次，忽略开录指令。";
                return false;
            }

            windowIndex = hit.Index;
            return true;
        }

        public void CommitAutoRecord(RecordScheduleConfig? raw, DateTime now, string cameraKey, int windowIndex)
        {
            if (windowIndex <= 0 || string.IsNullOrWhiteSpace(cameraKey))
                return;
            var schedule = RecordScheduleConfig.Normalize(raw);
            if (!schedule.Enabled)
                return;

            lock (_sync)
            {
                var date = RecordScheduleConfig.ScheduleDate(now);
                var usage = LoadUnlocked();
                if (!string.Equals(usage.ScheduleDate, FormatDate(date), StringComparison.Ordinal))
                {
                    usage.ScheduleDate = FormatDate(date);
                    usage.Used = new Dictionary<string, int>(StringComparer.Ordinal);
                }

                var key = UsageKey(cameraKey, windowIndex);
                usage.Used.TryGetValue(key, out var used);
                usage.Used[key] = used + 1;
                SaveUnlocked(usage);
            }
        }

        private int ReadUsed(DateTime scheduleDate, string cameraKey, int windowIndex)
        {
            lock (_sync)
            {
                var usage = LoadUnlocked();
                if (!string.Equals(usage.ScheduleDate, FormatDate(scheduleDate), StringComparison.Ordinal))
                    return 0;
                return usage.Used.TryGetValue(UsageKey(cameraKey, windowIndex), out var used) ? used : 0;
            }
        }

        private static string UsageKey(string cameraKey, int windowIndex) =>
            $"{cameraKey.Trim().ToLowerInvariant()}:{windowIndex}";

        private UsageFile LoadUnlocked()
        {
            try
            {
                if (!File.Exists(_usagePath))
                    return new UsageFile();
                var json = File.ReadAllText(_usagePath);
                return JsonSerializer.Deserialize<UsageFile>(json, ConfigJsonOptions.Read) ?? new UsageFile();
            }
            catch
            {
                return new UsageFile();
            }
        }

        private void SaveUnlocked(UsageFile usage)
        {
            var directory = Path.GetDirectoryName(_usagePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(_usagePath, JsonSerializer.Serialize(usage, ConfigJsonOptions.Write));
        }

        private static string FormatDate(DateTime date) => date.ToString("yyyy-MM-dd");

        private sealed class UsageFile
        {
            public string ScheduleDate { get; set; } = string.Empty;
            public Dictionary<string, int> Used { get; set; } = new(StringComparer.Ordinal);
        }
    }
}
