using Dipping.Core.Interfaces;

namespace Dipping.Core.Triggers
{
    /// <summary>PLC 开录占位。接线方式未定时只打日志，不产生开始信号。</summary>
    public sealed class PlcRecordTrigger : IRecordTrigger
    {
        private readonly IAppLogger _logger;

        public string Name => "PLC";
#pragma warning disable CS0067
        public event EventHandler<RecordTriggerEventArgs>? StartRequested;
#pragma warning restore CS0067

        public PlcRecordTrigger(IAppLogger logger)
        {
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            _logger.Warn("自动录像源选了 PLC，但 PLC 协议尚未确定，当前不会自动开录。可改用 PC 程序 TCP，或保留手动录像。");
            return Task.CompletedTask;
        }

        public Task StopAsync() => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
