using Dipping.Core.Models;

namespace Dipping.Core.Interfaces
{
    /// <summary>
    /// 二期：根据一次 dipping 的左右录像 + 每相机 10 个通道 ROI 判断堵塞。
    /// 一期用 <see cref="Analysis.NullChannelAnalyzer"/>，不改录像流程。
    /// </summary>
    public interface IChannelAnalyzer
    {
        Task<IReadOnlyList<ChannelVerdict>> AnalyzeAsync(
            DippingJob job,
            CancellationToken cancellationToken = default);
    }
}
