using System.Collections.Concurrent;

namespace Dipping.WPF.Services
{
    public static class VisionSdkGate
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> CameraLocks =
            new(StringComparer.OrdinalIgnoreCase);

        public static readonly SemaphoreSlim Process = new(1, 1);

        public static SemaphoreSlim ForCamera(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip))
                throw new ArgumentException("相机 IP 不能为空。", nameof(ip));
            return CameraLocks.GetOrAdd(ip.Trim(), static _ => new SemaphoreSlim(1, 1));
        }
    }
}
