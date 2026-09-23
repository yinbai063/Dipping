namespace Dipping.Core.Interfaces
{
    public interface IAppLogger
    {
        void Debug(string message);
        void Info(string message);
        void Warn(string message);
        void Error(string message, Exception? exception = null);
        event EventHandler<string>? UiLogAppended;
    }
}
