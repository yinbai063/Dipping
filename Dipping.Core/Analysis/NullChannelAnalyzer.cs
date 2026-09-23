using Dipping.Core.Interfaces;
using Dipping.Core.Models;

namespace Dipping.Core.Analysis
{
    /// <summary>一期占位：每个通道返回 Unknown，不读视频。二期换成真实分析即可。</summary>
    public sealed class NullChannelAnalyzer : IChannelAnalyzer
    {
        public Task<IReadOnlyList<ChannelVerdict>> AnalyzeAsync(
            DippingJob job,
            CancellationToken cancellationToken = default)
        {
            var list = new List<ChannelVerdict>();
            foreach (var clip in job.Clips)
            {
                for (var i = 1; i <= 10; i++)
                {
                    list.Add(new ChannelVerdict
                    {
                        CameraIp = clip.Ip,
                        CameraName = clip.CameraName,
                        ChannelIndex = i,
                        Status = ChannelStatus.Unknown,
                        Message = "二期分析未启用"
                    });
                }
            }

            return Task.FromResult<IReadOnlyList<ChannelVerdict>>(list);
        }
    }
}
