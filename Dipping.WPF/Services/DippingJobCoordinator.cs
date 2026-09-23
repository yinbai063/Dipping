using Dipping.Core.Interfaces;
using Dipping.Core.Models;

namespace Dipping.WPF.Services
{
    /// <summary>每台相机各自一轮录像：互不等待、互不打断。停完后该相机走二期分析并复制共享盘。</summary>
    public sealed class DippingJobCoordinator
    {
        private readonly object _sync = new();
        private readonly IConfigurationService _config;
        private readonly IAppLogger _logger;
        private readonly IChannelAnalyzer _analyzer;
        private readonly IClipPublisher _publisher;
        private readonly Dictionary<string, DippingJob> _jobs = new(StringComparer.OrdinalIgnoreCase);

        public event EventHandler<DippingJob>? JobCompleted;

        public DippingJobCoordinator(
            IConfigurationService config,
            IAppLogger logger,
            IChannelAnalyzer analyzer,
            IClipPublisher publisher)
        {
            _config = config;
            _logger = logger;
            _analyzer = analyzer;
            _publisher = publisher;
            LiveVideoRecordService.Stopped += OnStopped;
        }

        public bool TryStartOne(string triggerSource, string ip, out string? error)
        {
            error = null;
            var camera = _config.GetEnabledCameras().FirstOrDefault(c =>
                string.Equals(c.Ip, ip, StringComparison.OrdinalIgnoreCase));
            if (camera == null)
            {
                error = "该相机未启用。";
                return false;
            }

            if (!LiveImagePump.IsLive(camera.Ip))
            {
                error = $"{camera.Name} 尚未实时预览，无法录像。";
                return false;
            }

            lock (_sync)
            {
                if (_jobs.ContainsKey(camera.Ip) || LiveVideoRecordService.IsRecordingIp(camera.Ip))
                {
                    error = $"{camera.Name} 已在录像。";
                    return false;
                }

                var record = _config.Current.Record.Normalized();
                var folder = LiveVideoRecordService.CreateSessionFolder(record.LocalFolder, camera.Name);
                var job = new DippingJob
                {
                    TriggerSource = triggerSource,
                    DurationSeconds = record.DurationSeconds,
                    LocalFolder = folder,
                    Rois = _config.Current.ChannelRois.ToList()
                };
                job.Clips.Add(new CameraClip { Ip = camera.Ip, CameraName = camera.Name });

                if (!LiveVideoRecordService.TryStart(
                        camera.Ip, camera.Name, folder, record.DurationSeconds, out var startError))
                {
                    error = startError;
                    return false;
                }

                _jobs[camera.Ip] = job;
            }

            _logger.Info($"[{camera.Name}] 开始录像 {triggerSource}，{_config.Current.Record.DurationSeconds}s");
            return true;
        }

        public void RequestStop(string ip, string reason) =>
            LiveVideoRecordService.Stop(ip, reason);

        private void OnStopped(object? sender, LiveVideoStopEventArgs e)
        {
            DippingJob? finished;
            lock (_sync)
            {
                if (!_jobs.TryGetValue(e.Ip, out finished))
                    return;
                _jobs.Remove(e.Ip);
                var clip = finished.Clips.FirstOrDefault(c =>
                    string.Equals(c.Ip, e.Ip, StringComparison.OrdinalIgnoreCase));
                if (clip != null)
                {
                    clip.FilePath = e.FilePath;
                    clip.Frames = e.Frames;
                    clip.ElapsedSeconds = e.ElapsedSeconds;
                    clip.Error = e.Error;
                }
            }

            _ = FinalizeAsync(finished);
        }

        private async Task FinalizeAsync(DippingJob job)
        {
            try
            {
                job.Verdicts = await _analyzer.AnalyzeAsync(job).ConfigureAwait(false);
                var share = _config.Current.Record.Normalized().ShareFolder;
                job.ShareFolder = string.IsNullOrWhiteSpace(share) ? null : share;
                await _publisher.PublishAsync(job).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                job.PublishError = ex.Message;
                _logger.Warn($"收尾失败：{ex.Message}");
            }

            JobCompleted?.Invoke(this, job);
        }
    }
}
