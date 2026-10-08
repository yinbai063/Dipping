using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Dipping.WPF.ViewModels
{
    public partial class CameraPaneViewModel : ObservableObject
    {
        public string DeviceId { get; init; } = string.Empty;
        public string Position { get; init; } = string.Empty;
        public string Ip { get; set; } = string.Empty;
        public string Name { get; init; } = string.Empty;

        [ObservableProperty]
        private ImageSource? _preview;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LoginButtonText))]
        [NotifyPropertyChangedFor(nameof(CanToggleBurst))]
        private bool _isLoggedIn;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LiveButtonText))]
        [NotifyPropertyChangedFor(nameof(ImageStatus))]
        private bool _isLive;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RecordButtonText))]
        [NotifyPropertyChangedFor(nameof(ImageStatus))]
        private bool _isRecording;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanToggleBurst))]
        private bool _isBusy;

        [ObservableProperty]
        private bool _isFound;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ImageStatus))]
        [NotifyPropertyChangedFor(nameof(StatusBrush))]
        private string _status = "未登录";

        /// <summary>相机网线/网络中断的界面报警。不自动登出、不自动重连。</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ImageStatus))]
        [NotifyPropertyChangedFor(nameof(StatusBrush))]
        private bool _isCameraAlarm;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ImageStatus))]
        private string _recordProgress = string.Empty;

        [ObservableProperty]
        private string _userName = "Admin";

        [ObservableProperty]
        private string _password = "Abc1234";

        [ObservableProperty]
        private bool _rememberPassword = true;

        [ObservableProperty]
        private bool _showPassword;

        /// <summary>本相机是否响应自动触发开录。左右各自独立。</summary>
        [ObservableProperty]
        private bool _autoRecord;

        /// <summary>二期：在画面上画 10 个通道框。一期勾了也不画。</summary>
        [ObservableProperty]
        private bool _showChannelBoxes;

        /// <summary>二期：叠加通/堵结果。一期勾了也不画。</summary>
        [ObservableProperty]
        private bool _showDetectResult;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(BurstButtonText))]
        [NotifyPropertyChangedFor(nameof(ImageStatus))]
        [NotifyPropertyChangedFor(nameof(CanToggleBurst))]
        private bool _isBursting;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasRateText))]
        private string _rateText = string.Empty;

        public bool HasRateText => !string.IsNullOrWhiteSpace(RateText);

        public Brush StatusBrush =>
            IsCameraAlarm ? Brushes.Red : new SolidColorBrush(Color.FromRgb(0x2D, 0x5F, 0x8B));

        public string LoginButtonText => IsLoggedIn ? "登出" : "登录";

        public string LiveButtonText => IsLive ? "停止预览" : "实时预览";

        public string RecordButtonText => "录像";

        public string BurstButtonText => IsBursting ? "停止连拍" : "连拍";

        /// <summary>连拍中须保持可点，以便再点停止（与 NCM 一致，避免异步命令整段执行期间按钮被禁用）。</summary>
        public bool CanToggleBurst => IsBursting || (IsLoggedIn && !IsBusy);

        public string ImageStatus
        {
            get
            {
                if (IsCameraAlarm)
                    return string.IsNullOrWhiteSpace(Status) ? "相机未更新图像，请检查相机及其接线" : Status;
                if (IsBursting)
                    return string.IsNullOrWhiteSpace(RecordProgress) ? "连拍中" : RecordProgress;
                if (IsRecording && !string.IsNullOrWhiteSpace(RecordProgress))
                    return RecordProgress;
                if (!string.IsNullOrWhiteSpace(RecordProgress))
                    return RecordProgress;
                return Status;
            }
        }
    }

    public partial class ChannelRowViewModel : ObservableObject
    {
        public int Index { get; init; }

        [ObservableProperty]
        private string _leftStatus = "—";

        [ObservableProperty]
        private string _rightStatus = "—";
    }
}
