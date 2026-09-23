using Dipping.Core.Interfaces;
using Dipping.Core.Models;

namespace Dipping.Core.Services
{
    public sealed class LocalThenSharePublisher : IClipPublisher
    {
        private readonly IAppLogger _logger;

        public LocalThenSharePublisher(IAppLogger logger)
        {
            _logger = logger;
        }

        public async Task PublishAsync(DippingJob job, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(job.ShareFolder))
            {
                _logger.Info($"[{job.JobId}] 未配置共享目录，片子只留在 {job.LocalFolder}");
                return;
            }

            if (!Directory.Exists(job.LocalFolder))
            {
                job.PublishError = "本地目录不存在，无法复制到共享盘";
                _logger.Warn($"[{job.JobId}] {job.PublishError}");
                return;
            }

            var dest = Path.Combine(job.ShareFolder, Path.GetFileName(job.LocalFolder.TrimEnd('\\', '/')));
            try
            {
                await Task.Run(() => CopyDirectory(job.LocalFolder, dest), cancellationToken).ConfigureAwait(false);
                _logger.Info($"[{job.JobId}] 已复制到共享目录 {dest}");
            }
            catch (Exception ex)
            {
                job.PublishError = ex.Message;
                _logger.Warn($"[{job.JobId}] 复制到共享目录失败：{ex.Message}");
            }
        }

        private static void CopyDirectory(string source, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (var file in Directory.GetFiles(source))
            {
                var name = Path.GetFileName(file);
                File.Copy(file, Path.Combine(dest, name), overwrite: true);
            }
        }
    }
}
