using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Dipping.WPF.ViewModels;

namespace Dipping.WPF.Views
{
    public partial class CameraLoginBar : UserControl
    {
        public CameraLoginBar()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            Loaded += (_, _) => SyncPasswordBox();
        }

        private CameraPaneViewModel? Item => DataContext as CameraPaneViewModel;

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is INotifyPropertyChanged oldItem)
                oldItem.PropertyChanged -= OnItemPropertyChanged;
            if (e.NewValue is INotifyPropertyChanged newItem)
                newItem.PropertyChanged += OnItemPropertyChanged;
            SyncPasswordBox();
        }

        private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CameraPaneViewModel.ShowPassword)
                && Item is { ShowPassword: false })
                SyncPasswordBox();
            if (e.PropertyName == nameof(CameraPaneViewModel.Password) && Item is { ShowPassword: false })
                SyncPasswordBox();
        }

        private void SyncPasswordBox()
        {
            if (Item == null)
                return;
            LoginPasswordBox.Password = Item.Password ?? string.Empty;
        }

        private void LoginPasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
        {
            if (Item != null && !Item.ShowPassword && sender is PasswordBox box)
                Item.Password = box.Password;
        }
    }
}
