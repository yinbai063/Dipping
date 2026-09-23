namespace Dipping.Core.Interfaces
{
    /// <summary>一期：外部开始录像信号。PC 程序 / PLC 实现此接口；手动按钮不走这里。</summary>
    public interface IRecordTrigger : IDisposable
    {
        string Name { get; }
        event EventHandler<RecordTriggerEventArgs>? StartRequested;
        Task StartAsync(CancellationToken cancellationToken = default);
        Task StopAsync();
    }

    public sealed class RecordTriggerEventArgs : EventArgs
    {
        public string Source { get; init; } = string.Empty;
        public string? JobHint { get; init; }
        /// <summary>left 或 right。空则不指定相机。</summary>
        public string? CameraPosition { get; init; }
    }
}
