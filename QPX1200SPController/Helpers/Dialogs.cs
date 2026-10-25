using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;
using QPX1200SPController.Services;

namespace QPX1200SPController.Helpers
{
    /// <summary>Colour of a dialog's confirm button.</summary>
    public enum DialogTone
    {
        Primary,
        /// <summary>Green: switches something on or starts something.</summary>
        Go,
        /// <summary>Red: stops, deletes or leaves something in an unknown state.</summary>
        Danger
    }

    /// <summary>Answer of <see cref="Dialogs.ConfirmAsync"/>.</summary>
    public sealed class ConfirmResult
    {
        public bool Confirmed { get; set; }

        /// <summary>The operator ticked "Don't ask again" (only when it was offered).</summary>
        public bool DontAskAgain { get; set; }
    }

    /// <summary>
    /// Themed confirmation and information dialogs shown inside the main window (DialogHost
    /// "RootDialog"), so every question has the same look. Copied from CPX400DP and extended with
    /// an icon, a details box and "Don't ask again".
    /// </summary>
    public static class Dialogs
    {
        public const string HostId = "RootDialog";

        /// <summary>
        /// Asks the operator to confirm an action. Cancel is the default button, so Enter never
        /// confirms by accident.
        /// </summary>
        public static async Task<ConfirmResult> ConfirmAsync(string title, string message, string confirmText,
            PackIconKind icon = PackIconKind.HelpCircleOutline, string details = null, DialogTone tone = DialogTone.Primary,
            bool offerDontAsk = false)
        {
            var result = new ConfirmResult();
            if (!CanShow(title))
                return result;

            var confirm = CreateButton(confirmText, true, "MaterialDesignRaisedButton");
            System.Windows.Automation.AutomationProperties.SetName(confirm, confirmText);
            if (tone == DialogTone.Go)
            {
                confirm.Background = confirm.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
                confirm.Foreground = Brushes.White;
            }
            else if (tone == DialogTone.Danger)
            {
                confirm.SetResourceReference(Control.BackgroundProperty, "ErrorBrush");
                confirm.SetResourceReference(Control.BorderBrushProperty, "ErrorBrush");
                confirm.Foreground = Brushes.White;
            }

            var cancel = CreateButton("CANCEL", false, "MaterialDesignFlatButton");
            cancel.Margin = new Thickness(0, 0, 8, 0);
            cancel.IsCancel = true;
            cancel.IsDefault = true;

            CheckBox dontAsk = null;
            if (offerDontAsk)
            {
                dontAsk = new CheckBox
                {
                    Content = "Don't ask again (can be changed under Soft limits & storage)",
                    Margin = new Thickness(0, 0, 0, 16)
                };
            }

            var iconBrushKey = tone == DialogTone.Danger ? "ErrorBrush" : "WarnBrush";
            object answer = await DialogHost.Show(CreateContent(title, message, details, icon, iconBrushKey, dontAsk, cancel, confirm), HostId);
            result.Confirmed = answer is bool accepted && accepted;
            result.DontAskAgain = result.Confirmed && dontAsk?.IsChecked == true;
            return result;
        }

        /// <summary>Shows a message with an OK button. <paramref name="isError"/> gives it a red icon.</summary>
        public static async Task InfoAsync(string title, string message, bool isError = false, string details = null)
        {
            if (!CanShow(title))
                return;
            var ok = CreateButton("OK", true, "MaterialDesignRaisedButton");
            ok.IsDefault = true;
            ok.IsCancel = true;
            var icon = isError ? PackIconKind.AlertCircleOutline : PackIconKind.InformationOutline;
            await DialogHost.Show(CreateContent(title, message, details, icon,
                isError ? "ErrorBrush" : "MaterialDesign.Brush.Primary", null, ok), HostId);
        }

        /// <summary>Only one dialog at a time can be open in the host; a second one is logged instead.</summary>
        private static bool CanShow(string title)
        {
            if (Application.Current == null || !DialogHost.IsDialogOpen(HostId))
                return Application.Current != null;
            LoggingService.Instance.LogWarning($"Dialog '{title}' not shown: another dialog is open.",
                nameof(Dialogs), nameof(CanShow));
            return false;
        }

        private static FrameworkElement CreateContent(string title, string message, string details, PackIconKind icon,
            string iconBrushKey, CheckBox dontAsk, params Button[] buttons)
        {
            var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 16), Width = 440 };

            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            var headerIcon = new PackIcon { Kind = icon, Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center };
            headerIcon.SetResourceReference(Control.ForegroundProperty, iconBrushKey);
            header.Children.Add(headerIcon);
            header.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 400
            });
            panel.Children.Add(header);

            panel.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14),
                Opacity = 0.85,
                LineHeight = 20
            });

            if (!string.IsNullOrEmpty(details))
            {
                var box = new Border
                {
                    Margin = new Thickness(0, 0, 0, 16),
                    Padding = new Thickness(12, 8, 12, 8),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Child = new TextBlock { Text = details, FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap }
                };
                box.SetResourceReference(Border.BorderBrushProperty, "MaterialDesign.Brush.Separator.Background");
                panel.Children.Add(box);
            }

            if (dontAsk != null)
                panel.Children.Add(dontAsk);

            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var button in buttons)
                row.Children.Add(button);
            panel.Children.Add(row);
            return panel;
        }

        private static Button CreateButton(string text, bool result, string styleKey)
        {
            return new Button
            {
                Content = text,
                Style = (Style)Application.Current.FindResource(styleKey),
                Command = DialogHost.CloseDialogCommand,
                CommandParameter = result,
                MinWidth = 90
            };
        }
    }
}
