using Dipping.Core.Models;

namespace Dipping.Core.Interfaces
{
    /// <summary>本地片子写完后复制到共享盘。失败只记日志，不挡下一轮录像。</summary>
    public interface IClipPublisher
    {
        Task PublishAsync(DippingJob job, CancellationToken cancellationToken = default);
    }
}
