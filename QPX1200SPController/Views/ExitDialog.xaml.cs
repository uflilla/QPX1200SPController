using System.Windows;

namespace QPX1200SPController.Views
{
    public enum ExitChoice
    {
        Cancel,
        TurnOutputOffAndExit,
        ExitLeavingOutput
    }

    public partial class ExitDialog : Window
    {
        private ExitChoice _choice = ExitChoice.Cancel;

        private ExitDialog(bool outputOn, bool isConnected)
        {
            InitializeComponent();

            if (!isConnected)
            {
                HeaderText.Text = "Link to the supply is down";
                MessageText.Text = "The app is trying to reconnect, so the output state is unknown. " +
                                   "If you exit now, the supply keeps its current output state.";
                TurnOffButton.Visibility = Visibility.Collapsed;
                ExitButton.Content = "EXIT ANYWAY";
            }
            else if (outputOn)
            {
                HeaderText.Text = "Output is ON";
                MessageText.Text = "The power supply output is still ON. What should happen to it when the app closes?";
            }
            else
            {
                HeaderText.Text = "Exit the application?";
                MessageText.Text = "The output is OFF. The connection to the power supply will be closed.";
                HeaderIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.HelpCircleOutline;
                HeaderIcon.Foreground = (System.Windows.Media.Brush)FindResource("PrimaryHueMidBrush");
                TurnOffButton.Visibility = Visibility.Collapsed;
                ExitButton.Content = "EXIT";
            }
        }

        /// <summary>Shows the dialog modally and returns the operator's choice.</summary>
        public static ExitChoice Ask(Window owner, bool outputOn, bool isConnected)
        {
            var dialog = new ExitDialog(outputOn, isConnected);
            if (owner != null && owner.IsLoaded)
                dialog.Owner = owner;
            dialog.ShowDialog();
            return dialog._choice;
        }

        private void OnTurnOffClick(object sender, RoutedEventArgs e)
        {
            _choice = ExitChoice.TurnOutputOffAndExit;
            Close();
        }

        private void OnExitClick(object sender, RoutedEventArgs e)
        {
            _choice = ExitChoice.ExitLeavingOutput;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            _choice = ExitChoice.Cancel;
            Close();
        }
    }
}
