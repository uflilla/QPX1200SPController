using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QPX1200SPController.ViewModels;

namespace QPX1200SPController.Views
{
    public partial class ScpiConsoleView : UserControl
    {
        public ScpiConsoleView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private ScpiConsoleViewModel ViewModel => DataContext as ScpiConsoleViewModel;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            if (ViewModel != null)
                ViewModel.EntryAdded += entry => EntryList.ScrollIntoView(entry);
        }

        private void OnCommandKeyDown(object sender, KeyEventArgs e)
        {
            var vm = ViewModel;
            if (vm == null)
                return;

            switch (e.Key)
            {
                case Key.Enter:
                    if (vm.SendCommand.CanExecute(null))
                        vm.SendCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Up:
                    vm.BrowseHistory(-1);
                    CommandBox.CaretIndex = CommandBox.Text.Length;
                    e.Handled = true;
                    break;
                case Key.Down:
                    vm.BrowseHistory(+1);
                    CommandBox.CaretIndex = CommandBox.Text.Length;
                    e.Handled = true;
                    break;
            }
        }
    }
}
