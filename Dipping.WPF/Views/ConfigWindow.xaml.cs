using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Dipping.WPF.ViewModels;

namespace Dipping.WPF.Views
{
    public partial class ConfigWindow : Window
    {
        public ConfigWindow(ConfigViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            viewModel.CommitPendingConfigEdits = CommitPendingNumericBindings;
        }

        private void Save_OnPreviewMouseDown(object sender, MouseButtonEventArgs e) =>
            CommitPendingNumericBindings();

        private void CommitPendingNumericBindings()
        {
            CommitBinding(DurationLimitSecondsBox);
            CommitBinding(DurationSecondsBox);
            CommitBinding(BurstCountLimitBox);
            CommitBinding(BurstCountBox);
        }

        private static void CommitBinding(TextBox? box)
        {
            if (box == null)
                return;
            if (BindingOperations.GetBindingExpression(box, TextBox.TextProperty) is { } expr)
                expr.UpdateSource();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
