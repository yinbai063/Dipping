using System.Windows;
using Dipping.WPF.ViewModels;

namespace Dipping.WPF.Views
{
    public partial class CameraMonitorWindow : Window
    {
        public CameraMonitorWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            if (DataContext is MainViewModel vm)
                await vm.FindCamerasCommand.ExecuteAsync(null);
        }

        private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(MainViewModel.LogText))
                return;
            DebugLogBox.CaretIndex = DebugLogBox.Text?.Length ?? 0;
            DebugLogBox.ScrollToEnd();
        }

        private void OnClosed(object? sender, System.EventArgs e)
        {
            if (DataContext is MainViewModel vm)
                vm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
