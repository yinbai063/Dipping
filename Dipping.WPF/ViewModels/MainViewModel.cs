using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dipping.Core.Interfaces;
using Dipping.Core.Models;
using Dipping.Core.Services;
using Dipping.Core.Triggers;
using Dipping.WPF.Services;
using Dipping.WPF.Views;

namespace Dipping.WPF.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly IConfigurationService _config;
        private readonly IAppLogger _logger;
        private readonly DippingJobCoordinator _jobs;
        private readonly RecordScheduleGate _scheduleGate = new();
        private readonly DispatcherTimer _progressTimer;
        private readonly Dictionary<string, CancellationTokenSource> _burstCts = new(StringComparer.OrdinalIgnoreCase);
        private IRecordTrigger? _autoTrigger;
        private IReadOnlyList<DiscoveredCameraItem> _discovered = Array.Empty<DiscoveredCameraItem>();
        private CameraMonitorWindow? _monitorWindow;
        private ConfigWindow? _configWindow;

        public ObservableCollection<CameraPaneViewModel> Cameras { get; } = new();
        public ObservableCollection<ChannelRowViewModel> Channels { get; } = new();
        public IReadOnlyList<string> UserNameOptions { get; } = new[] { "Admin", "管理员" };

        public CameraPaneViewModel? LeftCamera =>
            Cameras.FirstOrDefault(c => c.Position == "left") ?? Cameras.ElementAtOrDefault(0);

        public CameraPaneViewModel? RightCamera =>
            Cameras.FirstOrDefault(c => c.Position == "right") ?? Cameras.ElementAtOrDefault(1);

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ToggleRecordForCommand))]
        [NotifyCanExecuteChangedFor(nameof(StopRecordForCommand))]
        private bool _isRecording;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ToggleLoginCommand))]
        [NotifyCanExecuteChangedFor(nameof(FindCamerasCommand))]
        private bool _isEnumerating;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusMessageBrush))]
        private string _statusMessage = "正在查找相机…";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusMessageBrush))]
        private bool _tcpLinkLost;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SdkVersionText))]
        private string _sdkVersion = string.Empty;

        [ObservableProperty]
        private string _logText = string.Empty;

        [ObservableProperty]
        private string _lastJobSummary = "尚未录像。一期只保存视频；二期分析完成后在此显示通道通/堵。";

        public string SdkVersionText =>
            string.IsNullOrWhiteSpace(SdkVersion) ? "SDK 版本：未读取" : $"SDK 版本：{SdkVersion}";

        public Brush StatusMessageBrush =>
            TcpLinkLost || Cameras.Any(c => c.IsCameraAlarm)
                ? Brushes.Red
                : new SolidColorBrush(Color.FromRgb(0x2D, 0x5F, 0x8B));

        public MainViewModel(
            IConfigurationService config,
            IAppLogger logger,
            IChannelAnalyzer analyzer,
            IClipPublisher publisher)
        {
            _config = config;
            _logger = logger;
            _jobs = new DippingJobCoordinator(config, logger, analyzer, publisher);
            _jobs.JobCompleted += OnJobCompleted;
            LiveImagePump.FrameArrived += OnFrame;
            logger.UiLogAppended += (_, line) =>
                Application.Current?.Dispatcher.BeginInvoke(() => AppendLog(line));

            ReloadPanes();
            for (var i = 1; i <= 10; i++)
                Channels.Add(new ChannelRowViewModel { Index = i });

            _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _progressTimer.Tick += (_, _) => RefreshProgress();
            _progressTimer.Start();
            _ = ApplyAutoTriggerAsync();
            _ = FindCamerasAsync();
        }

        private readonly List<string> _logBuffer = new();

        private void AppendLog(string line)
        {
            _logBuffer.Add(line);
            while (_logBuffer.Count > 400)
                _logBuffer.RemoveAt(0);
            LogText = string.Join(Environment.NewLine, _logBuffer);
        }

        public void ReloadPanes()
        {
            foreach (var oldPane in Cameras)
                oldPane.PropertyChanged -= OnPanePropertyChanged;

            var previous = Cameras.ToList();
            Cameras.Clear();
            foreach (var camera in _config.Current.Cameras.OrderBy(c => c.Position == "left" ? 0 : 1))
            {
                var login = camera.ResolveSdkLogin(_config.Current.VisionSdk);
                var pane = new CameraPaneViewModel
                {
                    DeviceId = camera.DeviceId,
                    Position = camera.Position,
                    Ip = camera.Ip,
                    Name = camera.Name,
                    UserName = login.UserName,
                    Password = login.Password,
                    RememberPassword = camera.RememberSdkPassword,
                    AutoRecord = camera.AutoRecord
                };
                var old = previous.FirstOrDefault(p =>
                    string.Equals(p.Ip, camera.Ip, StringComparison.OrdinalIgnoreCase));
                if (old != null)
                {
                    pane.IsLoggedIn = old.IsLoggedIn;
                    pane.IsLive = old.IsLive;
                    pane.IsRecording = old.IsRecording;
                    pane.Status = old.Status;
                    pane.Preview = old.Preview;
                    pane.IsFound = old.IsFound;
                    pane.UserName = old.UserName;
                    pane.Password = old.Password;
                    pane.RememberPassword = old.RememberPassword;
                    pane.ShowChannelBoxes = old.ShowChannelBoxes;
                    pane.ShowDetectResult = old.ShowDetectResult;
                    pane.AutoRecord = old.AutoRecord;
                    pane.IsBursting = old.IsBursting;
                    pane.IsCameraAlarm = old.IsCameraAlarm;
                }

                pane.PropertyChanged += OnPanePropertyChanged;
                Cameras.Add(pane);
            }

            ApplyDiscoveryToCameras();
            OnPropertyChanged(nameof(LeftCamera));
            OnPropertyChanged(nameof(RightCamera));
            NotifyLiveAndRecordCommands();
        }

        private void OnPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(CameraPaneViewModel.AutoRecord))
                return;
            if (sender is not CameraPaneViewModel pane)
                return;
            var cfg = _config.Current.Cameras.FirstOrDefault(c =>
                string.Equals(c.Ip, pane.Ip, StringComparison.OrdinalIgnoreCase));
            if (cfg != null)
                cfg.AutoRecord = pane.AutoRecord;
            _config.Current.Trigger.AutoRecord = Cameras.Any(c => c.AutoRecord);
            _logger.Info(pane.AutoRecord
                ? $"[{pane.Name}] 已勾选自动录像"
                : $"[{pane.Name}] 已取消勾选自动录像，上位机指令将不执行该相机开录");
            _ = _config.SaveAsync();
            NotifyLiveAndRecordCommands();
        }

        private bool CanFindCameras() => !IsEnumerating;

        [RelayCommand(CanExecute = nameof(CanFindCameras))]
        public async Task FindCamerasAsync()
        {
            IsEnumerating = true;
            StatusMessage = "正在查找相机…";
            VisionCameraEnumResult result;
            try
            {
                try
                {
                    result = await Task.Run(VisionCameraEnumService.Enumerate).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    result = new VisionCameraEnumResult { Success = false, Message = "查找失败：" + ex.Message };
                }

                SdkVersion = result.SdkVersion;
                _discovered = result.Devices;
                ApplyDiscoveryToCameras();
                ToggleLoginCommand.NotifyCanExecuteChanged();

                if (!result.Success)
                {
                    StatusMessage = result.Message;
                    _logger.Warn(result.Message);
                    return;
                }

                foreach (var d in result.Devices)
                    _logger.Info(d.DisplayName);

                StatusMessage = result.Devices.Count == 0
                    ? result.Message
                    : $"{result.Message}。请分别点左/右相机的「登录」。";
                _logger.Info(StatusMessage + (string.IsNullOrEmpty(result.SdkVersion) ? "" : $" SDK {result.SdkVersion}"));
            }
            finally
            {
                IsEnumerating = false;
            }
        }

        private void ApplyDiscoveryToCameras()
        {
            foreach (var pane in Cameras)
            {
                var found = _discovered.Any(d =>
                    string.Equals(d.Ip, pane.Ip, StringComparison.OrdinalIgnoreCase));
                pane.IsFound = found;
                if (!pane.IsLoggedIn)
                    pane.Status = found ? "已找到，可登录" : "网上未找到该 IP";
            }
        }

        private bool CanToggleLogin(CameraPaneViewModel? camera) =>
            camera != null
            && !camera.IsBusy
            && !IsEnumerating
            && (camera.IsLoggedIn || !string.IsNullOrWhiteSpace(camera.UserName));

        [RelayCommand(CanExecute = nameof(CanToggleLogin))]
        private async Task ToggleLoginAsync(CameraPaneViewModel? camera)
        {
            if (camera == null)
                return;
            if (camera.IsLoggedIn)
            {
                await LogoutAsync(camera).ConfigureAwait(true);
                return;
            }

            camera.IsBusy = true;
            StatusMessage = $"正在登录 {camera.Name}（{camera.Ip}）…";
            ToggleLoginCommand.NotifyCanExecuteChanged();
            var startLive = false;
            try
            {
                await VisionSdkGate.ForCamera(camera.Ip).WaitAsync().ConfigureAwait(true);
                try
                {
                    if (VisionCameraSessionHub.TryGet(camera.Ip, out _))
                    {
                        camera.IsLoggedIn = true;
                        camera.Status = "已登录";
                        startLive = true;
                    }
                    else
                    {
                        var user = VisionCameraClient.NormalizeUserName(camera.UserName);
                        var device = _discovered.FirstOrDefault(d =>
                            string.Equals(d.Ip, camera.Ip, StringComparison.OrdinalIgnoreCase));

                        var login = await Task.Run(() =>
                        {
                            if (device != null)
                            {
                                var ok = VisionCameraClient.TryConnect(
                                    camera.Ip, user, camera.Password ?? string.Empty, device.DeviceInfo,
                                    out var connected, out var error);
                                return (ok, connected, error);
                            }

                            var found = VisionCameraClient.TryFindAndConnect(
                                camera.Ip, user, camera.Password ?? string.Empty,
                                out var connectedFind, out var errorFind);
                            return (ok: found, connected: connectedFind, error: errorFind);
                        }).ConfigureAwait(true);

                        if (!login.ok || login.connected == null)
                        {
                            ShowLoginFailure(camera, login.error);
                            return;
                        }

                        VisionCameraSessionHub.Set(camera.Ip, login.connected);
                        camera.IsLoggedIn = true;
                        camera.IsFound = true;
                        camera.IsCameraAlarm = false;
                        camera.Status = "已登录";
                        await SaveCredentialsAsync(camera).ConfigureAwait(true);
                        StatusMessage = $"{camera.Name} 登录成功";
                        _logger.Info($"[{camera.Name}] 登录成功，用户={user}");
                        startLive = true;
                    }
                }
                finally
                {
                    VisionSdkGate.ForCamera(camera.Ip).Release();
                }

                if (startLive)
                    await StartLiveForAsync(camera).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                ShowLoginFailure(camera, ex.Message);
            }
            finally
            {
                camera.IsBusy = false;
                NotifyLiveAndRecordCommands();
            }
        }

        private void ShowLoginFailure(CameraPaneViewModel camera, string? error)
        {
            var occupied = VisionCameraClient.IsOccupiedLogin(error);
            var prompt = occupied || string.IsNullOrWhiteSpace(error)
                ? VisionCameraClient.OccupiedLoginMessage
                : error!;
            camera.IsLoggedIn = false;
            camera.Status = occupied ? "相机正在被登录使用中" : "登录失败";
            StatusMessage = $"{camera.Name}：{prompt}";
            _logger.Warn($"[{camera.Name}] 登录失败：{prompt}");
            MessageBox.Show(prompt, "无法登录", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async Task LogoutAsync(CameraPaneViewModel camera)
        {
            camera.IsBusy = true;
            StatusMessage = $"正在登出 {camera.Name}…";
            try
            {
                CancelBurst(camera.Ip);
                camera.IsBursting = false;

                if (LiveVideoRecordService.IsRecordingIp(camera.Ip))
                    LiveVideoRecordService.Stop(camera.Ip, "登出");

                await LiveImagePump.StopAsync(camera.Ip).ConfigureAwait(true);
                var gate = VisionSdkGate.ForCamera(camera.Ip);
                var gotGate = false;
                try
                {
                    using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    await gate.WaitAsync(waitCts.Token).ConfigureAwait(true);
                    gotGate = true;
                    if (VisionCameraSessionHub.TryGet(camera.Ip, out var client))
                    {
                        try
                        {
                            var restoreError = client.StopLivePreviewAndRestore();
                            if (!string.IsNullOrWhiteSpace(restoreError))
                                _logger.Warn($"[{camera.Name}] 登出前恢复失败：{restoreError}");
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn($"[{camera.Name}] 登出时恢复触发失败：{ex.Message}");
                        }
                    }

                    VisionCameraSessionHub.Remove(camera.Ip);
                }
                catch (Exception ex)
                {
                    _logger.Warn($"[{camera.Name}] 登出相机会话失败（仍清除本机登录状态）：{ex.Message}");
                    try { VisionCameraSessionHub.Remove(camera.Ip); } catch { }
                }
                finally
                {
                    if (gotGate)
                        gate.Release();
                }

                camera.IsLoggedIn = false;
                camera.IsLive = false;
                camera.IsCameraAlarm = false;
                camera.RateText = string.Empty;
                camera.Preview = null;
                camera.Status = camera.IsFound ? "已登出" : "网上未找到该 IP";
                StatusMessage = $"{camera.Name} 已登出";
                _logger.Info($"[{camera.Name}] 已登出");
            }
            finally
            {
                camera.IsBusy = false;
                NotifyLiveAndRecordCommands();
            }
        }

        private async Task StartLiveForAsync(CameraPaneViewModel pane)
        {
            try
            {
                if (!VisionCameraSessionHub.TryGet(pane.Ip, out var client))
                    return;

                await VisionSdkGate.ForCamera(pane.Ip).WaitAsync().ConfigureAwait(true);
                try
                {
                    client.StartLivePreview();
                }
                finally
                {
                    VisionSdkGate.ForCamera(pane.Ip).Release();
                }

                LiveImagePump.Start(pane.Ip, _config, _logger);
                pane.IsLive = true;
                pane.Status = "实时预览";
                StatusMessage = $"{pane.Name} 实时预览已开（临时内部触发，停预览后写回外部触发）。未勾选自动录像时可点「录像」。";
                NotifyLiveAndRecordCommands();
            }
            catch (Exception ex)
            {
                pane.Status = "Live 失败：" + ex.Message;
                _logger.Warn($"[{pane.Name}] 开 Live 失败：{ex.Message}");
            }
        }

        private bool CanToggleLiveFor(CameraPaneViewModel? camera) =>
            camera != null && camera.IsLoggedIn && !camera.IsBusy && !camera.IsBursting;

        [RelayCommand(CanExecute = nameof(CanToggleLiveFor))]
        private async Task ToggleLiveForAsync(CameraPaneViewModel? camera)
        {
            if (camera == null)
                return;
            if (camera.IsLive || LiveImagePump.IsLive(camera.Ip))
                await StopLiveForAsync(camera).ConfigureAwait(true);
            else
                await StartLiveForAsync(camera).ConfigureAwait(true);
        }

        private async Task StopLiveForAsync(CameraPaneViewModel pane)
        {
            if (pane.IsRecording || LiveVideoRecordService.IsRecordingIp(pane.Ip))
                _jobs.RequestStop(pane.Ip, "停止预览");

            await LiveImagePump.StopAsync(pane.Ip).ConfigureAwait(true);
            if (VisionCameraSessionHub.TryGet(pane.Ip, out var client))
            {
                await VisionSdkGate.ForCamera(pane.Ip).WaitAsync().ConfigureAwait(true);
                try { client.StopLivePreviewAndRestore(); }
                finally { VisionSdkGate.ForCamera(pane.Ip).Release(); }
            }

            pane.IsLive = false;
            pane.IsCameraAlarm = false;
            pane.RateText = string.Empty;
            pane.Status = "已登录（已恢复外部触发）";
            StatusMessage = $"{pane.Name} 预览已停，已写回外部触发。再点「实时预览」才会临时改成内部触发。";
            NotifyLiveAndRecordCommands();
        }

        private bool CanToggleRecordFor(CameraPaneViewModel? camera) =>
            camera != null
            && camera.IsLoggedIn
            && !camera.IsBusy
            && !camera.IsRecording
            && !camera.AutoRecord
            && !camera.IsBursting;

        [RelayCommand(CanExecute = nameof(CanToggleRecordFor))]
        private async Task ToggleRecordForAsync(CameraPaneViewModel? camera)
        {
            if (camera == null)
                return;
            if (camera.IsRecording || LiveVideoRecordService.IsRecordingIp(camera.Ip))
                return;

            await RecordOneAsync("手动", camera).ConfigureAwait(true);
        }

        private bool CanStopRecordFor(CameraPaneViewModel? camera) =>
            camera != null
            && (camera.IsRecording || LiveVideoRecordService.IsRecordingIp(camera.Ip));

        [RelayCommand(CanExecute = nameof(CanStopRecordFor))]
        private void StopRecordFor(CameraPaneViewModel? camera)
        {
            if (camera == null)
                return;
            if (!camera.IsRecording && !LiveVideoRecordService.IsRecordingIp(camera.Ip))
                return;
            _jobs.RequestStop(camera.Ip, "手动停止");
            StatusMessage = $"{camera.Name} 正在停止录像…";
        }

        private bool CanGrabPhoto(CameraPaneViewModel? camera) =>
            camera != null && camera.IsLoggedIn && !camera.IsBusy && !camera.IsBursting;

        [RelayCommand(CanExecute = nameof(CanGrabPhoto))]
        private async Task GrabPhotoAsync(CameraPaneViewModel? camera)
        {
            if (camera == null)
                return;

            camera.IsBusy = true;
            NotifyLiveAndRecordCommands();
            try
            {
                var grab = await CaptureOneAsync(camera).ConfigureAwait(true);
                if (grab == null)
                {
                    StatusMessage = $"{camera.Name} 拍照失败：未收到图像。";
                    return;
                }

                var path = ImageFileSaver.SaveGrab(grab, PhotoFolder(), camera.Name);
                var preview = grab.EnsureBitmap();
                if (preview != null)
                    camera.Preview = preview;
                StatusMessage = $"{camera.Name} 已拍照：{path}";
                _logger.Info($"[{camera.Name}] 拍照 {path}");
            }
            catch (Exception ex)
            {
                StatusMessage = $"{camera.Name} 拍照失败：{ex.Message}";
                _logger.Warn(StatusMessage);
            }
            finally
            {
                camera.IsBusy = false;
                NotifyLiveAndRecordCommands();
            }
        }

        private bool CanToggleBurst(CameraPaneViewModel? camera) =>
            camera != null && camera.IsLoggedIn && !camera.IsBusy;

        [RelayCommand(CanExecute = nameof(CanToggleBurst))]
        private async Task ToggleBurstAsync(CameraPaneViewModel? camera)
        {
            if (camera == null)
                return;
            if (camera.IsBursting)
            {
                CancelBurst(camera.Ip);
                return;
            }

            await RunBurstAsync(camera).ConfigureAwait(true);
        }

        private async Task<bool> RecordOneAsync(string source, CameraPaneViewModel pane)
        {
            if (!pane.IsLoggedIn)
            {
                StatusMessage = $"{pane.Name} 未登录，无法录像。";
                return false;
            }

            if (LiveVideoRecordService.IsRecordingIp(pane.Ip))
                return false;

            if (!LiveImagePump.IsLive(pane.Ip))
                await StartLiveForAsync(pane).ConfigureAwait(true);

            if (!LiveImagePump.IsLive(pane.Ip))
            {
                StatusMessage = $"{pane.Name} 尚未实时预览，无法录像。";
                return false;
            }

            if (!_jobs.TryStartOne(source, pane.Ip, out var error))
            {
                StatusMessage = error ?? $"{pane.Name} 无法开始录像";
                _logger.Warn(StatusMessage);
                return false;
            }

            pane.IsRecording = true;
            StatusMessage = $"{pane.Name} 录像中（{source}）{_config.Current.Record.DurationSeconds}s，到时自动停并回到 Live。";
            NotifyLiveAndRecordCommands();
            return true;
        }

        [RelayCommand]
        private void CopyLog()
        {
            if (string.IsNullOrEmpty(LogText))
                return;
            Clipboard.SetText(LogText);
            StatusMessage = "日志已复制。";
        }

        [RelayCommand]
        private void OpenCameraMonitor()
        {
            if (ActivateIfOpen(_monitorWindow))
                return;

            var window = new CameraMonitorWindow(this) { Owner = Application.Current?.MainWindow };
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_monitorWindow, window))
                    _monitorWindow = null;
            };
            _monitorWindow = window;
            window.Show();
        }

        [RelayCommand]
        private void OpenConfig()
        {
            if (ActivateIfOpen(_configWindow))
                return;

            var vm = new ConfigViewModel(_config, _logger);
            var window = new ConfigWindow(vm) { Owner = Application.Current?.MainWindow };
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_configWindow, window))
                    _configWindow = null;
                ReloadPanes();
                _ = ApplyAutoTriggerAsync();
            };
            _configWindow = window;
            window.Show();
        }

        private static bool ActivateIfOpen(Window? window)
        {
            if (window == null)
                return false;
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Show();
            window.Activate();
            return true;
        }

        private async Task SaveCredentialsAsync(CameraPaneViewModel camera)
        {
            if (!camera.RememberPassword)
                return;
            var cfg = _config.Current.Cameras.FirstOrDefault(c =>
                string.Equals(c.Ip, camera.Ip, StringComparison.OrdinalIgnoreCase));
            if (cfg == null)
                return;
            cfg.SdkUserName = VisionCameraClient.NormalizeUserName(camera.UserName);
            cfg.SdkPassword = camera.Password ?? string.Empty;
            cfg.RememberSdkPassword = true;
            await _config.SaveAsync().ConfigureAwait(true);
        }

        private async Task ApplyAutoTriggerAsync()
        {
            if (_autoTrigger != null)
            {
                _autoTrigger.StartRequested -= OnAutoStart;
                if (_autoTrigger is TcpRecordTrigger oldTcp)
                    oldTcp.ClientConnectedChanged -= OnTcpClientChanged;
                await _autoTrigger.StopAsync();
                _autoTrigger.Dispose();
                _autoTrigger = null;
                TcpLinkLost = false;
            }

            _autoTrigger = RecordTriggerFactory.Create(_config.Current.Trigger, _logger);
            _autoTrigger.StartRequested += OnAutoStart;
            if (_autoTrigger is TcpRecordTrigger tcp)
                tcp.ClientConnectedChanged += OnTcpClientChanged;
            await _autoTrigger.StartAsync();
            StatusMessage = $"{_autoTrigger.Name} 已监听。未勾选自动录像的相机只收指令不开录，可用手动录像。关闭本程序后上位机须断开再连。";
        }

        private void OnTcpClientChanged(object? sender, bool connected)
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                TcpLinkLost = !connected;
                StatusMessage = connected
                    ? "上位PC已连接"
                    : "上位PC连接已断开，等待重连";
                OnPropertyChanged(nameof(StatusMessageBrush));
            });
        }

        private void OnAutoStart(object? sender, RecordTriggerEventArgs e)
        {
            Application.Current?.Dispatcher.BeginInvoke(async () =>
            {
                if (string.IsNullOrWhiteSpace(e.CameraPosition))
                {
                    StatusMessage = "开录指令未指定左/右相机，已忽略。";
                    _logger.Warn(StatusMessage);
                    return;
                }

                var pane = Cameras.FirstOrDefault(c =>
                    string.Equals(c.Position, e.CameraPosition, StringComparison.OrdinalIgnoreCase));
                if (pane == null)
                {
                    StatusMessage = $"收到 {(e.CameraPosition == "right" ? "右" : "左")}相机开录指令，但配置里没有这台相机。";
                    _logger.Warn(StatusMessage);
                    return;
                }

                if (!pane.AutoRecord)
                {
                    StatusMessage = $"{pane.Name} 收到开录指令，未勾选自动录像，已忽略。可用手动「录像」。";
                    _logger.Info(StatusMessage);
                    return;
                }

                if (!pane.IsLoggedIn)
                {
                    StatusMessage = $"{pane.Name} 收到开录指令，但未登录，已忽略。";
                    return;
                }

                if (LiveVideoRecordService.IsRecordingIp(pane.Ip))
                    return;

                if (!_scheduleGate.TryAllowAutoRecord(
                        _config.Current.RecordSchedule,
                        DateTime.Now,
                        pane.Position,
                        pane.Name,
                        out var windowIndex,
                        out var denyReason))
                {
                    StatusMessage = denyReason;
                    _logger.Info(denyReason);
                    return;
                }

                if (await RecordOneAsync(e.Source, pane).ConfigureAwait(true))
                    _scheduleGate.CommitAutoRecord(_config.Current.RecordSchedule, DateTime.Now, pane.Position, windowIndex);
            });
        }

        private void OnFrame(object? sender, LiveFrameEventArgs e)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;
            dispatcher.BeginInvoke(() =>
            {
                var pane = Cameras.FirstOrDefault(c =>
                    string.Equals(c.Ip, e.Ip, StringComparison.OrdinalIgnoreCase));
                if (pane == null)
                    return;
                pane.Preview = e.Grab.EnsureBitmap() ?? e.Grab.Image;
                if (pane.IsCameraAlarm)
                {
                    pane.IsCameraAlarm = false;
                    pane.Status = "实时预览";
                    StatusMessage = $"{pane.Name} 相机画面已恢复";
                    _logger.Info($"[{pane.Name}] 相机画面已恢复");
                    OnPropertyChanged(nameof(StatusMessageBrush));
                }
                if (pane.IsLoggedIn && !pane.IsLive)
                {
                    pane.IsLive = true;
                    if (!pane.IsCameraAlarm)
                        pane.Status = "实时预览";
                    NotifyLiveAndRecordCommands();
                }
            }, DispatcherPriority.Background);
        }

        private void RefreshProgress()
        {
            var any = false;
            var liveChanged = false;
            foreach (var pane in Cameras)
            {
                var pumpLive = pane.IsLoggedIn && LiveImagePump.IsLive(pane.Ip);
                if (pumpLive != pane.IsLive)
                {
                    pane.IsLive = pumpLive;
                    if (!pane.IsCameraAlarm)
                    {
                        if (pumpLive)
                            pane.Status = "实时预览";
                        else if (pane.IsLoggedIn)
                            pane.Status = "已登录";
                    }
                    liveChanged = true;
                }

                UpdateCameraAlarm(pane);

                var recording = LiveVideoRecordService.IsRecordingIp(pane.Ip);
                if (pane.IsRecording != recording)
                {
                    pane.IsRecording = recording;
                    liveChanged = true;
                }

                if (pane.IsBursting)
                    continue;

                if (pane.IsLive && LiveImagePump.TryGetLiveRate(pane.Ip, out var liveMs, out var liveFps))
                    pane.RateText = $"Live 实测：本帧 {liveMs:0} ms，{liveFps:0.0} 帧/秒";
                else if (!pane.IsLive)
                    pane.RateText = string.Empty;

                if (!recording)
                {
                    pane.RecordProgress = pane.IsLive ? "预览中" : pane.Status;
                    continue;
                }

                any = true;
                var (elapsed, max, frames, recFps) = LiveVideoRecordService.GetProgress(pane.Ip);
                pane.RecordProgress = recFps > 0.05
                    ? $"录像 {elapsed}/{max}s  {frames}帧  {recFps:0.0} 帧/秒"
                    : $"录像 {elapsed}/{max}s  {frames}帧";
            }

            if (IsRecording != any)
            {
                IsRecording = any;
                liveChanged = true;
            }

            if (liveChanged)
                NotifyLiveAndRecordCommands();
            OnPropertyChanged(nameof(StatusMessageBrush));
        }

        private void UpdateCameraAlarm(CameraPaneViewModel pane)
        {
            if (!pane.IsLive || !LiveImagePump.IsLive(pane.Ip))
                return;

            var recent = LiveImagePump.HasRecentFrame(pane.Ip, TimeSpan.FromSeconds(5));
            if (!recent && !pane.IsCameraAlarm)
            {
                pane.IsCameraAlarm = true;
                pane.Status = "相机未更新图像，请检查相机及其接线";
                StatusMessage = $"{pane.Name} 相机未更新图像，请检查相机及其接线";
                _logger.Warn($"[{pane.Name}] 相机未更新图像（Live 超过 5 秒无新图）");
            }
            else if (recent && pane.IsCameraAlarm)
            {
                pane.IsCameraAlarm = false;
                pane.Status = "实时预览";
                StatusMessage = $"{pane.Name} 相机画面已恢复";
                _logger.Info($"[{pane.Name}] 相机画面已恢复");
            }
        }

        private void NotifyLiveAndRecordCommands()
        {
            ToggleLoginCommand.NotifyCanExecuteChanged();
            ToggleLiveForCommand.NotifyCanExecuteChanged();
            ToggleRecordForCommand.NotifyCanExecuteChanged();
            StopRecordForCommand.NotifyCanExecuteChanged();
            GrabPhotoCommand.NotifyCanExecuteChanged();
            ToggleBurstCommand.NotifyCanExecuteChanged();
        }

        private void OnJobCompleted(object? sender, DippingJob job)
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                foreach (var clip in job.Clips)
                {
                    var pane = Cameras.FirstOrDefault(c =>
                        string.Equals(c.Ip, clip.Ip, StringComparison.OrdinalIgnoreCase));
                    if (pane != null)
                        pane.IsRecording = false;
                }

                NotifyLiveAndRecordCommands();
                ApplyVerdicts(job);
                var name = job.Clips.FirstOrDefault()?.CameraName ?? "相机";
                var share = string.IsNullOrWhiteSpace(job.ShareFolder)
                    ? "未配置共享，仅本地"
                    : (job.PublishError == null ? "已复制共享" : "共享复制失败：" + job.PublishError);
                StatusMessage = $"{name} 本轮完成，仍在实时预览（内部触发）。点「停止预览」才写回外部触发。{job.LocalFolder}；{share}";
                LastJobSummary =
                    $"{job.StartedAt:yyyy-MM-dd HH:mm:ss}  {name}  来源 {job.TriggerSource}  {job.DurationSeconds}s\n" +
                    $"本地：{job.LocalFolder}\n{share}\n" +
                    "一期：只保存视频。二期将在此列出各通道通/堵。";
                _logger.Info(StatusMessage);
            });
        }

        private void ApplyVerdicts(DippingJob job)
        {
            foreach (var clip in job.Clips)
            {
                var position = _config.Current.Cameras.FirstOrDefault(c =>
                    string.Equals(c.Ip, clip.Ip, StringComparison.OrdinalIgnoreCase))?.Position;
                foreach (var row in Channels)
                {
                    var text = StatusText(job, clip.Ip, row.Index);
                    if (position == "left")
                        row.LeftStatus = text;
                    else if (position == "right")
                        row.RightStatus = text;
                }
            }
        }

        private static string StatusText(DippingJob job, string ip, int channel)
        {
            var v = job.Verdicts.FirstOrDefault(x =>
                string.Equals(x.CameraIp, ip, StringComparison.OrdinalIgnoreCase)
                && x.ChannelIndex == channel);
            if (v == null)
                return "—";
            return v.Status switch
            {
                ChannelStatus.Clear => "通",
                ChannelStatus.Blocked => "堵",
                ChannelStatus.NoData => "无图",
                _ => "待分析"
            };
        }

        private async Task<VisionGrabResult?> CaptureOneAsync(CameraPaneViewModel camera)
        {
            if (camera.IsLive || LiveImagePump.IsLive(camera.Ip))
                return CaptureLiveSnapshot(camera);

            var timeout = _config.Current.Capture.Normalized().GrabTimeoutMs;
            await VisionSdkGate.ForCamera(camera.Ip).WaitAsync().ConfigureAwait(true);
            try
            {
                if (!VisionCameraSessionHub.TryGet(camera.Ip, out var client))
                    throw new InvalidOperationException("未登录。");
                return await Task.Run(() =>
                {
                    var grab = client.GrabSoftwareTrigger(timeout, out var error);
                    if (grab == null && !string.IsNullOrWhiteSpace(error))
                        throw new InvalidOperationException(error);
                    return grab;
                }).ConfigureAwait(true);
            }
            finally
            {
                VisionSdkGate.ForCamera(camera.Ip).Release();
            }
        }

        private static VisionGrabResult? CaptureLiveSnapshot(CameraPaneViewModel camera)
        {
            if (LiveImagePump.TryGetLatestFrame(camera.Ip, out var grab) && grab != null
                && (grab.JpegBytes is { Length: > 0 } || grab.Image != null))
                return grab;

            if (AsBitmap(camera.Preview) is BitmapSource preview)
                return new VisionGrabResult { Image = preview };
            return null;
        }

        private async Task RunBurstAsync(CameraPaneViewModel camera)
        {
            var cap = _config.Current.Capture.Normalized();
            var folder = cap.ResolveFolder();
            var cts = new CancellationTokenSource();
            lock (_burstCts)
            {
                if (_burstCts.TryGetValue(camera.Ip, out var old))
                {
                    try { old.Cancel(); } catch { }
                    old.Dispose();
                }
                _burstCts[camera.Ip] = cts;
            }

            camera.IsBursting = true;
            camera.RecordProgress = $"连拍 0/{cap.BurstCount}";
            camera.RateText = cap.BurstFastest ? "连拍实测：最快，测量中…" : "连拍实测：按间隔，测量中…";
            NotifyLiveAndRecordCommands();
            var saved = 0;
            var live = camera.IsLive || LiveImagePump.IsLive(camera.Ip);
            try
            {
                if (live)
                {
                    saved = await BurstFromLiveAsync(camera, folder, cap, cts.Token).ConfigureAwait(true);
                }
                else
                {
                    saved = await BurstSoftwareAsync(camera, folder, cap, cts.Token).ConfigureAwait(true);
                }

                StatusMessage = cts.IsCancellationRequested
                    ? $"{camera.Name} 连拍已停止，已存 {saved} 张。"
                    : $"{camera.Name} 连拍完成，已存 {saved}/{cap.BurstCount} 张：{folder}";
                _logger.Info($"[{camera.Name}] {StatusMessage}");
            }
            catch (OperationCanceledException)
            {
                StatusMessage = $"{camera.Name} 连拍已停止，已存 {saved} 张。";
            }
            catch (Exception ex)
            {
                StatusMessage = $"{camera.Name} 连拍失败：{ex.Message}";
                _logger.Warn(StatusMessage);
            }
            finally
            {
                lock (_burstCts)
                {
                    if (_burstCts.TryGetValue(camera.Ip, out var current) && ReferenceEquals(current, cts))
                        _burstCts.Remove(camera.Ip);
                }

                cts.Dispose();
                camera.IsBursting = false;
                camera.RecordProgress = camera.IsLive ? "预览中" : camera.Status;
                NotifyLiveAndRecordCommands();
            }
        }

        private async Task<int> BurstFromLiveAsync(
            CameraPaneViewModel camera, string folder, CaptureConfig cap, CancellationToken ct)
        {
            var saved = 0;
            var cycleSum = 0;
            var cycleCount = 0;
            object? previousImage = null;
            for (var i = 1; i <= cap.BurstCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var sw = Stopwatch.StartNew();
                var grab = cap.BurstFastest && previousImage != null
                    ? await LiveImagePump.WaitNextFrameAsync(camera.Ip, previousImage, cap.GrabTimeoutMs, ct)
                        .ConfigureAwait(true)
                    : CaptureLiveSnapshot(camera);

                if (cap.BurstFastest && previousImage != null && grab != null
                    && (ReferenceEquals(grab.JpegBytes, previousImage) || ReferenceEquals(grab.Image, previousImage)))
                {
                    camera.RecordProgress = $"连拍 {i}/{cap.BurstCount} 等待新图超时";
                }
                else if (grab != null && (grab.JpegBytes is { Length: > 0 } || grab.Image != null))
                {
                    var path = ImageFileSaver.SaveGrab(grab, folder, camera.Name, i);
                    saved++;
                    previousImage = grab.JpegBytes ?? (object?)grab.Image;
                    if (grab.Image != null)
                        camera.Preview = grab.Image;
                    camera.RecordProgress = $"连拍 {i}/{cap.BurstCount}";
                    StatusMessage = $"{camera.Name} 连拍 {i}/{cap.BurstCount}：{path}";
                }
                else
                {
                    camera.RecordProgress = $"连拍 {i}/{cap.BurstCount} 无图";
                }

                await WaitBurstPaceAsync(cap, sw, i, ct).ConfigureAwait(true);
                ApplyBurstCycle(camera, i, cap, (int)sw.ElapsedMilliseconds, cap.BurstFastest, ref cycleSum, ref cycleCount);
            }

            return saved;
        }

        private async Task<int> BurstSoftwareAsync(
            CameraPaneViewModel camera, string folder, CaptureConfig cap, CancellationToken ct)
        {
            await VisionSdkGate.ForCamera(camera.Ip).WaitAsync(ct).ConfigureAwait(true);
            try
            {
                if (!VisionCameraSessionHub.TryGet(camera.Ip, out var client))
                    throw new InvalidOperationException("未登录。");

                await Task.Run(() => client.BeginBurstCapture(), ct).ConfigureAwait(true);
                var saved = 0;
                var cycleSum = 0;
                var cycleCount = 0;
                try
                {
                    for (var i = 1; i <= cap.BurstCount; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var sw = Stopwatch.StartNew();
                        var grab = await Task.Run(() =>
                        {
                            var frame = client.GrabBurstFrame(cap.GrabTimeoutMs, out var error);
                            if (frame == null && !string.IsNullOrWhiteSpace(error))
                                throw new InvalidOperationException(error);
                            return frame;
                        }, ct).ConfigureAwait(true);

                        if (grab != null && (grab.JpegBytes is { Length: > 0 } || grab.Image != null))
                        {
                            var path = ImageFileSaver.SaveGrab(grab, folder, camera.Name, i);
                            saved++;
                            var preview = grab.EnsureBitmap();
                            if (preview != null)
                                camera.Preview = preview;
                            camera.RecordProgress = $"连拍 {i}/{cap.BurstCount}";
                            StatusMessage = $"{camera.Name} 连拍 {i}/{cap.BurstCount}：{path}";
                        }
                        else
                        {
                            camera.RecordProgress = $"连拍 {i}/{cap.BurstCount} 无图";
                        }

                        await WaitBurstPaceAsync(cap, sw, i, ct).ConfigureAwait(true);
                        ApplyBurstCycle(camera, i, cap, (int)sw.ElapsedMilliseconds, cap.BurstFastest, ref cycleSum, ref cycleCount);
                    }
                }
                finally
                {
                    try { client.EndBurstCapture(); } catch { }
                }

                return saved;
            }
            finally
            {
                VisionSdkGate.ForCamera(camera.Ip).Release();
            }
        }

        private static async Task WaitBurstPaceAsync(
            CaptureConfig cap, Stopwatch sw, int index, CancellationToken ct)
        {
            if (cap.BurstFastest || index >= cap.BurstCount)
                return;
            var remain = cap.BurstIntervalMs - (int)sw.ElapsedMilliseconds;
            if (remain > 0)
                await Task.Delay(remain, ct).ConfigureAwait(true);
        }

        private void ApplyBurstCycle(
            CameraPaneViewModel camera, int index, CaptureConfig cap, int cycleMs, bool fastest,
            ref int cycleSum, ref int cycleCount)
        {
            if (cycleMs <= 0)
                return;
            cycleCount++;
            cycleSum += cycleMs;
            var avg = cycleSum / cycleCount;
            var fps = 1000.0 / cycleMs;
            var avgFps = 1000.0 / avg;
            var mode = fastest ? "最快" : "按间隔";
            camera.RateText =
                $"连拍实测：本张 {cycleMs} ms（{fps:0.0} 帧/秒），平均 {avg} ms（{avgFps:0.0} 帧/秒），已拍 {cycleCount} 张，{mode}";
            camera.RecordProgress = $"连拍 {index}/{cap.BurstCount}";
            StatusMessage = $"{camera.Name} 连拍 {index}/{cap.BurstCount}，本张 {cycleMs} ms，平均 {avg} ms";
        }

        private void CancelBurst(string ip)
        {
            lock (_burstCts)
            {
                if (!_burstCts.TryGetValue(ip, out var cts))
                    return;
                try { cts.Cancel(); } catch { }
            }
        }

        private string PhotoFolder() => _config.Current.Capture.Normalized().ResolveFolder();

        private static BitmapSource? AsBitmap(ImageSource? image) => image as BitmapSource;

        public void Shutdown()
        {
            foreach (var pane in Cameras)
            {
                pane.PropertyChanged -= OnPanePropertyChanged;
                CancelBurst(pane.Ip);
                pane.IsBursting = false;
            }

            lock (_burstCts)
            {
                foreach (var cts in _burstCts.Values)
                {
                    try { cts.Cancel(); } catch { }
                    cts.Dispose();
                }
                _burstCts.Clear();
            }

            if (_autoTrigger is TcpRecordTrigger tcp)
                tcp.ClientConnectedChanged -= OnTcpClientChanged;
            _progressTimer.Stop();
            _autoTrigger?.Dispose();
            LiveVideoRecordService.Stop("退出程序");

            foreach (var pane in Cameras)
            {
                try { LiveImagePump.StopAsync(pane.Ip).GetAwaiter().GetResult(); } catch { }
                var gate = VisionSdkGate.ForCamera(pane.Ip);
                var gotGate = false;
                try
                {
                    gotGate = gate.Wait(TimeSpan.FromSeconds(5));
                    if (VisionCameraSessionHub.TryGet(pane.Ip, out var client))
                        client.StopLivePreviewAndRestore();
                }
                catch (Exception ex)
                {
                    _logger.Warn($"[{pane.Name}] 退出时恢复相机失败：{ex.Message}");
                }
                finally
                {
                    if (gotGate)
                        gate.Release();
                }
            }

            VisionCameraSessionHub.DisposeAll();
        }
    }
}
