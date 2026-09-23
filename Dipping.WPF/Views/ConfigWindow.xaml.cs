using System.Windows;
using Dipping.WPF.ViewModels;

namespace Dipping.WPF.Views
{
    public partial class ConfigWindow : Window
    {
        public ConfigWindow(ConfigViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
