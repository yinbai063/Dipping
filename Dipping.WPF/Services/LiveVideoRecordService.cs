using System.IO;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Media.Imaging;
using Dipping.Core.Interfaces;
using Dipping.Core.Models;

namespace Dipping.WPF.Services
{
    public sealed class LiveVideoStopEventArgs : EventArgs
    {
        public string Ip { get; init; } = string.Empty;
        public string CameraName { get; init; } = string.Empty;
        public string? FilePath { get; init; }
        public string Folder { get; init; } = string.Empty;
        public int Frames { get; init; }
        public double ElapsedSeconds { get; init; }
        public string Reason { get; init; } = string.Empty;
        public string? Error { get; init; }
    }

    public static class LiveVideoRecordService
    {
        private const int MaxQueue = 256;
        private static readonly object Sync = new();
        private static readonly Dictionary<string, Session> Sessions = new(StringComparer.OrdinalIgnoreCase);
        private static IAppLogger? _logger;

        public static event EventHandler<LiveVideoStopEventArgs>? Stopped;

        public static bool IsRecording
        {
            get { lock (Sync) return Sessions.Count > 0; }
        }

        public static bool IsRecordingIp(string ip)
        {
            lock (Sync)
                return Sessions.ContainsKey(ip);
        }

        public static void ConfigureLogger(IAppLogger logger) => _logger = logger;

        public static string ResolveRoot(string? configured)
        {
            if (!string.IsNullOrWhiteSpace(configured))
                return configured.Trim();
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Videos");
        }

        public static string CreateSessionFolder(string? configuredRoot, string? cameraName = null)
        {
            var root = ResolveRoot(configuredRoot);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var suffix = string.IsNullOrWhiteSpace(cameraName) ? "" : "_" + Sanitize(cameraName);
            var folder = Path.Combine(root, stamp + suffix);
            Directory.CreateDirectory(folder);
            return folder;
        }

        public static bool TryStart(
            string ip,
            string cameraName,
            string sessionFolder,
            int durationSeconds,
            out string? error)
        {
            error = null;
            var seconds = Math.Clamp(durationSeconds, RecordConfig.DurationMinSeconds, RecordConfig.DurationHardMaxSeconds);
            lock (Sync)
            {
                if (Sessions.ContainsKey(ip))
                {
                    error = "该相机已在录像。";
                    return false;
                }

                Directory.CreateDirectory(sessionFolder);
                var path = Path.Combine(sessionFolder, Sanitize(cameraName) + ".avi");
                var queue = Channel.CreateBounded<RecordFrame>(new BoundedChannelOptions(MaxQueue)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.DropWrite
                });
                var session = new Session
                {
                    Ip = ip,
                    CameraName = cameraName,
                    FilePath = path,
                    Folder = sessionFolder,
                    MaxDuration = TimeSpan.FromSeconds(seconds),
                    Queue = queue,
                    Cts = new CancellationTokenSource(),
                    StartedUtc = DateTime.UtcNow
                };
                Sessions[ip] = session;
                session.Worker = Task.Run(() => RunAsync(session));
            }

            _logger?.Info($"[{cameraName}] 开始录像 {seconds}s → {sessionFolder}");
            return true;
        }

        public static void RequestStop(string ip, string reason)
        {
            lock (Sync)
            {
                if (!Sessions.ContainsKey(ip))
                    return;
            }

            _ = Task.Run(() => Stop(ip, reason));
        }

        public static void Stop(string reason)
        {
            List<Session> sessions;
            lock (Sync)
            {
                sessions = Sessions.Values.ToList();
                Sessions.Clear();
            }

            foreach (var session in sessions)
                FinishSession(session, reason);
        }

        public static void Stop(string ip, string reason)
        {
            Session? session;
            lock (Sync)
            {
                if (!Sessions.Remove(ip, out session))
                    return;
            }

            FinishSession(session, reason);
        }

        public static void TryEnqueue(string ip, VisionGrabResult grab)
        {
            Session? session;
            lock (Sync)
                Sessions.TryGetValue(ip, out session);
            if (session == null)
                return;
            if (DateTime.UtcNow - session.StartedUtc >= session.MaxDuration)
            {
                RequestStop(ip, "已到设定时长，自动停止");
                return;
            }

            RecordFrame? frame = null;
            if (grab.JpegBytes is { Length: > 0 } jpeg && grab.Width > 0 && grab.Height > 0)
            {
                frame = new RecordFrame
                {
                    Jpeg = jpeg,
                    Width = (int)grab.Width,
                    Height = (int)grab.Height
                };
            }
            else if (grab.Image is BitmapSource bitmap)
            {
                if (bitmap.CanFreeze && !bitmap.IsFrozen)
                    bitmap.Freeze();
                frame = new RecordFrame { Image = bitmap };
            }

            if (frame == null)
                return;

            if (!session.Queue.Writer.TryWrite(frame))
                Interlocked.Increment(ref session.Dropped);
        }

        public static (int ElapsedSeconds, int MaxSeconds, int Frames, double Fps) GetProgress(string ip)
        {
            lock (Sync)
            {
                if (!Sessions.TryGetValue(ip, out var session))
                    return (0, 0, 0, 0);
                var elapsed = Math.Max(0, (DateTime.UtcNow - session.StartedUtc).TotalSeconds);
                var fps = elapsed > 0.2 ? session.Frames / elapsed : 0;
                return ((int)elapsed, (int)session.MaxDuration.TotalSeconds, session.Frames, fps);
            }
        }

        private static void FinishSession(Session session, string reason)
        {
            session.Queue.Writer.TryComplete();
            session.Cts.Cancel();
            try { session.Worker?.Wait(TimeSpan.FromSeconds(8)); }
            catch { }

            var elapsed = Math.Max(0, (DateTime.UtcNow - session.StartedUtc).TotalSeconds);
            RaiseStopped(new LiveVideoStopEventArgs
            {
                Ip = session.Ip,
                CameraName = session.CameraName,
                FilePath = session.FilePath,
                Folder = session.Folder,
                Frames = session.Frames,
                ElapsedSeconds = elapsed,
                Reason = reason,
                Error = session.Error
            });
        }

        private static async Task RunAsync(Session session)
        {
            MjpegAviWriter? writer = null;
            try
            {
                await foreach (var frame in session.Queue.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    byte[] jpeg;
                    int width, height;
                    if (frame.Jpeg is { Length: > 0 } raw && frame.Width > 0 && frame.Height > 0)
                    {
                        jpeg = raw;
                        width = frame.Width;
                        height = frame.Height;
                    }
                    else if (frame.Image != null)
                    {
                        jpeg = EncodeJpeg(frame.Image);
                        width = frame.Image.PixelWidth;
                        height = frame.Image.PixelHeight;
                    }
                    else
                    {
                        continue;
                    }

                    if (jpeg.Length == 0)
                        continue;
                    writer ??= CreateWriter(session, width, height);
                    if (width != writer.Width || height != writer.Height)
                        continue;
                    writer.AddJpeg(jpeg);
                    session.Frames = writer.FrameCount;
                }
            }
            catch (Exception ex)
            {
                session.Error = ex.Message;
                _logger?.Warn($"[{session.CameraName}] 录像写入失败：{ex.Message}");
            }
            finally
            {
                var elapsed = Math.Max(0.05, (DateTime.UtcNow - session.StartedUtc).TotalSeconds);
                try { writer?.Finish(elapsed); }
                catch (Exception ex)
                {
                    session.Error = string.IsNullOrWhiteSpace(session.Error) ? ex.Message : session.Error;
                    _logger?.Warn($"[{session.CameraName}] 关闭录像文件失败：{ex.Message}");
                }

                session.Cts.Dispose();
            }
        }

        private static MjpegAviWriter CreateWriter(Session session, int width, int height)
        {
            var writer = new MjpegAviWriter();
            writer.Start(session.FilePath, width, height);
            return writer;
        }

        private static byte[] EncodeJpeg(BitmapSource bitmap)
        {
            if (bitmap.CanFreeze && !bitmap.IsFrozen)
                bitmap.Freeze();
            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        private static void RaiseStopped(LiveVideoStopEventArgs args)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
                Stopped?.Invoke(null, args);
            else
                dispatcher.BeginInvoke(() => Stopped?.Invoke(null, args));
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "camera";
            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.Trim().ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0)
                    chars[i] = '_';
            }

            var cleaned = new string(chars).Trim();
            return string.IsNullOrWhiteSpace(cleaned) ? "camera" : cleaned;
        }

        private sealed class Session
        {
            public required string Ip { get; init; }
            public required string CameraName { get; init; }
            public required string FilePath { get; set; }
            public required string Folder { get; init; }
            public required TimeSpan MaxDuration { get; init; }
            public required Channel<RecordFrame> Queue { get; init; }
            public required CancellationTokenSource Cts { get; init; }
            public required DateTime StartedUtc { get; init; }
            public Task? Worker { get; set; }
            public int Frames;
            public int Dropped;
            public string? Error;
        }

        private sealed class RecordFrame
        {
            public BitmapSource? Image { get; init; }
            public byte[]? Jpeg { get; init; }
            public int Width { get; init; }
            public int Height { get; init; }
        }
    }
}
