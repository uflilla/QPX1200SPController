using System.Windows;
using MaterialDesignThemes.Wpf;

namespace QPX1200SPController.Views
{
    /// <summary>A themed yes/no question with an optional details box (Cancel is the default button).</summary>
    public partial class ConfirmDialog : Window
    {
        private bool _confirmed;

        private ConfirmDialog()
        {
            InitializeComponent();
        }

        /// <summary>Shows the dialog modally. Returns true if the operator confirmed.</summary>
        public static bool Ask(string header, string message, string details, string confirmText,
            PackIconKind icon = PackIconKind.Power)
        {
            return Ask(header, message, details, confirmText, icon, offerDontAsk: false, out _);
        }

        /// <summary>As <see cref="Ask(string,string,string,string,PackIconKind)"/>, with a "Don't ask again" box.</summary>
        public static bool Ask(string header, string message, string details, string confirmText,
            PackIconKind icon, bool offerDontAsk, out bool dontAskAgain)
        {
            var dialog = new ConfirmDialog();
            dialog.Title = header;
            dialog.HeaderText.Text = header;
            dialog.HeaderIcon.Kind = icon;
            dialog.MessageText.Text = message;
            dialog.DetailText.Text = details;
            dialog.DetailBorder.Visibility = string.IsNullOrEmpty(details) ? Visibility.Collapsed : Visibility.Visible;
            dialog.ConfirmButton.Content = confirmText;
            dialog.DontAskCheckBox.Visibility = offerDontAsk ? Visibility.Visible : Visibility.Collapsed;

            var owner = Application.Current?.MainWindow;
            if (owner != null && owner.IsLoaded && owner != dialog)
                dialog.Owner = owner;
            dialog.ShowDialog();

            dontAskAgain = dialog._confirmed && dialog.DontAskCheckBox.IsChecked == true;
            return dialog._confirmed;
        }

        private void OnConfirmClick(object sender, RoutedEventArgs e)
        {
            _confirmed = true;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
