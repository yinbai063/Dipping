using System.Windows;
using Dipping.Core.Analysis;
using Dipping.Core.Interfaces;
using Dipping.Core.Services;
using Dipping.WPF.Services;
using Dipping.WPF.ViewModels;
using Dipping.WPF.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Dipping.WPF
{
    public partial class App : Application
    {
        public static IServiceProvider ServiceProvider { get; private set; } = null!;

        public App()
        {
            try
            {
                var services = new ServiceCollection();
                services.AddSingleton<IConfigurationService, JsonConfigService>();
                services.AddSingleton<IAppLogger>(sp =>
                {
                    var config = sp.GetRequiredService<IConfigurationService>();
                    return new SerilogAppLogger(config.Current.Logging);
                });
                services.AddSingleton<IChannelAnalyzer, NullChannelAnalyzer>();
                services.AddSingleton<IClipPublisher, LocalThenSharePublisher>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
                ServiceProvider = services.BuildServiceProvider();

                var config = ServiceProvider.GetRequiredService<IConfigurationService>();
                config.LoadAsync().Wait();
                LiveVideoRecordService.ConfigureLogger(ServiceProvider.GetRequiredService<IAppLogger>());

                ShutdownMode = ShutdownMode.OnMainWindowClose;
                var main = ServiceProvider.GetRequiredService<MainWindow>();
                MainWindow = main;
                main.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"启动错误：{ex.Message}\n\n{ex.StackTrace}", "Dipping",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (ServiceProvider.GetService(typeof(MainViewModel)) is MainViewModel vm)
                vm.Shutdown();
            if (ServiceProvider is IDisposable d)
                d.Dispose();
            base.OnExit(e);
        }
    }
}
