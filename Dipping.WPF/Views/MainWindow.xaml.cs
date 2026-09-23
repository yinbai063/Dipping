using System.Windows;
using Dipping.WPF.ViewModels;

namespace Dipping.WPF.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            Closed += (_, _) => viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(MainViewModel.LogText))
                return;
            DebugLogBox.CaretIndex = DebugLogBox.Text?.Length ?? 0;
            DebugLogBox.ScrollToEnd();
        }
    }
}
