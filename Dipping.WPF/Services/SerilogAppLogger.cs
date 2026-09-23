using System.IO;
using Dipping.Core.Interfaces;
using Dipping.Core.Models;
using Serilog;
using Serilog.Events;

namespace Dipping.WPF.Services
{
    public class SerilogAppLogger : IAppLogger, IDisposable
    {
        private readonly Serilog.ILogger _file;

        public event EventHandler<string>? UiLogAppended;

        public SerilogAppLogger(LoggingConfig config)
        {
            var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, config.LogDirectory);
            Directory.CreateDirectory(logDir);
            _file = new LoggerConfiguration()
                .MinimumLevel.Is(ParseLevel(config.MinimumLevel))
                .WriteTo.File(
                    Path.Combine(logDir, "dipping-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: config.RetainedDays,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
        }

        public void Debug(string message)
        {
            _file.Debug(message);
            Append("[DBG] " + message);
        }

        public void Info(string message)
        {
            _file.Information(message);
            Append("[INF] " + message);
        }

        public void Warn(string message)
        {
            _file.Warning(message);
            Append("[WRN] " + message);
        }

        public void Error(string message, Exception? exception = null)
        {
            if (exception == null)
                _file.Error(message);
            else
                _file.Error(exception, message);
            Append("[ERR] " + message + (exception != null ? $" | {exception.Message}" : string.Empty));
        }

        private void Append(string message) =>
            UiLogAppended?.Invoke(this, $"[{DateTime.Now:HH:mm:ss}] {message}");

        private static LogEventLevel ParseLevel(string level) => level.ToUpperInvariant() switch
        {
            "DEBUG" => LogEventLevel.Debug,
            "WARNING" => LogEventLevel.Warning,
            "ERROR" => LogEventLevel.Error,
            _ => LogEventLevel.Information
        };

        public void Dispose()
        {
            (_file as IDisposable)?.Dispose();
            Log.CloseAndFlush();
        }
    }
}
