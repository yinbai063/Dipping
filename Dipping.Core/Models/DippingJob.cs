namespace Dipping.Core.Models
{
    public class DippingJob
    {
        public string JobId { get; init; } = Guid.NewGuid().ToString("N")[..8];
        public DateTime StartedAt { get; init; } = DateTime.Now;
        public string TriggerSource { get; init; } = string.Empty;
        public int DurationSeconds { get; init; }
        public string LocalFolder { get; init; } = string.Empty;
        public List<CameraClip> Clips { get; } = new();
        public IReadOnlyList<ChannelRoi> Rois { get; init; } = Array.Empty<ChannelRoi>();
        public IReadOnlyList<ChannelVerdict> Verdicts { get; set; } = Array.Empty<ChannelVerdict>();
        public string? ShareFolder { get; set; }
        public string? PublishError { get; set; }
    }

    public class CameraClip
    {
        public string Ip { get; init; } = string.Empty;
        public string CameraName { get; init; } = string.Empty;
        public string? FilePath { get; set; }
        public int Frames { get; set; }
        public double ElapsedSeconds { get; set; }
        public string? Error { get; set; }
    }

    public class ChannelVerdict
    {
        public string CameraIp { get; init; } = string.Empty;
        public string CameraName { get; init; } = string.Empty;
        public int ChannelIndex { get; init; }
        public ChannelStatus Status { get; init; } = ChannelStatus.Unknown;
        public string Message { get; init; } = string.Empty;
    }

    public enum ChannelStatus
    {
        Unknown = 0,
        Clear = 1,
        Blocked = 2,
        NoData = 3
    }
}
