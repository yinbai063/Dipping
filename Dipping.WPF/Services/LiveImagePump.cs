using Dipping.Core.Interfaces;
using Dipping.Core.Models;

namespace Dipping.WPF.Services
{
    /// <summary>每台已登录相机一条 Live 取流循环。</summary>
    public static class LiveImagePump
    {
        public static event EventHandler<LiveFrameEventArgs>? FrameArrived;

        private static readonly object Sync = new();
        private static readonly Dictionary<string, Worker> Workers = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, VisionGrabResult> Latest = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, RateState> Rates = new(StringComparer.OrdinalIgnoreCase);
        private static IConfigurationService? _config;
        private static IAppLogger? _logger;

        public static bool IsLive(string ip)
        {
            lock (Sync)
                return Workers.ContainsKey(ip);
        }

        public static bool TryGetLatestFrame(string ip, out VisionGrabResult? grab)
        {
            lock (Sync)
                return Latest.TryGetValue(ip, out grab);
        }

        public static bool TryGetLiveRate(string ip, out double intervalMs, out double fps)
        {
            intervalMs = 0;
            fps = 0;
            lock (Sync)
            {
                if (!Rates.TryGetValue(ip, out var rate) || rate.EmaMs <= 0.5)
                    return false;
                intervalMs = rate.EmaMs;
                fps = 1000.0 / rate.EmaMs;
                return true;
            }
        }

        public static async Task<VisionGrabResult?> WaitNextFrameAsync(
            string ip, object? previousImage, int timeoutMs, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(200, timeoutMs));
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (TryGetLatestFrame(ip, out var grab)
                    && grab != null
                    && !IsSameFrame(grab, previousImage))
                    return grab;
                await Task.Delay(1, ct).ConfigureAwait(false);
            }

            return TryGetLatestFrame(ip, out var last) ? last : null;
        }

        private static bool IsSameFrame(VisionGrabResult grab, object? previous)
        {
            if (previous == null)
                return false;
            if (grab.JpegBytes != null && ReferenceEquals(grab.JpegBytes, previous))
                return true;
            return grab.Image != null && ReferenceEquals(grab.Image, previous);
        }

        public static void Start(string ip, IConfigurationService config, IAppLogger logger)
        {
            lock (Sync)
            {
                _config = config;
                _logger = logger;
                if (Workers.ContainsKey(ip))
                    return;
                var cts = new CancellationTokenSource();
                var worker = new Worker { Cts = cts };
                worker.Loop = Task.Run(() => RunOneAsync(ip, cts.Token), cts.Token);
                Workers[ip] = worker;
                Rates[ip] = new RateState { StartedUtc = DateTime.UtcNow };
            }
        }

        /// <summary>Live 是否在 maxAge 内收到过图。只用于掉线提示，不登出、不重连。</summary>
        public static bool HasRecentFrame(string ip, TimeSpan maxAge)
        {
            lock (Sync)
            {
                if (!Rates.TryGetValue(ip, out var rate))
                    return false;
                var t = rate.LastUtc != default ? rate.LastUtc : rate.StartedUtc;
                if (t == default)
                    return false;
                return DateTime.UtcNow - t < maxAge;
            }
        }

        public static void Stop(string ip) =>
            StopAsync(ip).GetAwaiter().GetResult();

        public static async Task StopAsync(string ip)
        {
            Worker? worker;
            lock (Sync)
            {
                Latest.Remove(ip);
                Rates.Remove(ip);
                if (!Workers.Remove(ip, out worker))
                    return;
            }

            await StopWorkerAsync(worker).ConfigureAwait(false);
        }

        public static void StopAll()
        {
            List<Worker> workers;
            lock (Sync)
            {
                workers = Workers.Values.ToList();
                Workers.Clear();
                Latest.Clear();
                Rates.Clear();
            }

            foreach (var worker in workers)
                StopWorkerAsync(worker).GetAwaiter().GetResult();
        }

        private static async Task StopWorkerAsync(Worker worker)
        {
            try { worker.Cts.Cancel(); } catch { }
            if (worker.Loop != null)
            {
                var done = await Task.WhenAny(worker.Loop, Task.Delay(10000)).ConfigureAwait(false);
                if (done != worker.Loop)
                    _logger?.Warn("Live 取流停止超时，未再 Dispose 相机会话前请勿重复登录。");
                else
                    try { await worker.Loop.ConfigureAwait(false); } catch { }
            }

            try { worker.Cts.Dispose(); } catch { }
        }

        private static async Task RunOneAsync(string ip, CancellationToken ct)
        {
            const int liveGrabTimeoutMs = 200;
            await VisionSdkGate.ForCamera(ip).WaitAsync(ct).ConfigureAwait(false);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    VisionGrabResult? grab = null;
                    try
                    {
                        if (ct.IsCancellationRequested)
                            break;
                        if (VisionCameraSessionHub.TryGet(ip, out var client))
                            grab = client.TryGetLiveFrame(liveGrabTimeoutMs);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warn($"[{ip}] Live 取流：{ex.Message}");
                        await Task.Delay(20, ct).ConfigureAwait(false);
                        continue;
                    }

                    if (grab != null && (grab.JpegBytes is { Length: > 0 } || grab.Image != null))
                    {
                        var emitUi = false;
                        lock (Sync)
                        {
                            Latest[ip] = grab;
                            NoteFrame(ip);
                            emitUi = ShouldEmitPreview(ip);
                        }

                        if (LiveVideoRecordService.IsRecordingIp(ip))
                            LiveVideoRecordService.TryEnqueue(ip, grab);
                        if (emitUi)
                            FrameArrived?.Invoke(null, new LiveFrameEventArgs { Ip = ip, Grab = grab });
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                VisionSdkGate.ForCamera(ip).Release();
            }
        }

        private static void NoteFrame(string ip)
        {
            var now = DateTime.UtcNow;
            if (!Rates.TryGetValue(ip, out var rate))
            {
                rate = new RateState();
                Rates[ip] = rate;
            }

            if (rate.LastUtc != default)
            {
                var dt = (now - rate.LastUtc).TotalMilliseconds;
                if (dt > 0.5 && dt < 10_000)
                    rate.EmaMs = rate.EmaMs <= 0 ? dt : rate.EmaMs * 0.75 + dt * 0.25;
            }

            rate.LastUtc = now;
            rate.Frames++;
        }

        /// <summary>界面大约 15 帧/秒即可，避免每帧 WPF 解码拖垮取流。</summary>
        private static bool ShouldEmitPreview(string ip)
        {
            if (!Rates.TryGetValue(ip, out var rate))
                return true;
            var now = DateTime.UtcNow;
            if (rate.LastPreviewUtc != default && (now - rate.LastPreviewUtc).TotalMilliseconds < 66)
                return false;
            rate.LastPreviewUtc = now;
            return true;
        }

        private sealed class Worker
        {
            public required CancellationTokenSource Cts { get; init; }
            public Task? Loop { get; set; }
        }

        private sealed class RateState
        {
            public DateTime StartedUtc;
            public DateTime LastUtc;
            public DateTime LastPreviewUtc;
            public double EmaMs;
            public int Frames;
        }
    }

    public sealed class LiveFrameEventArgs : EventArgs
    {
        public string Ip { get; init; } = string.Empty;
        public required VisionGrabResult Grab { get; init; }
    }
}
