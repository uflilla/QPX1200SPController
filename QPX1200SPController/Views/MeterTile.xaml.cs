using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;

namespace QPX1200SPController.Views
{
    /// <summary>
    /// A large read-out for one measurement: label and icon in the accent colour, the value, the unit
    /// and two footer lines (set-point, limit). The value is greyed out while the output is off.
    /// </summary>
    public partial class MeterTile : UserControl
    {
        public static readonly DependencyProperty LabelProperty = Register(nameof(Label), typeof(string), null);
        public static readonly DependencyProperty IconProperty = Register(nameof(Icon), typeof(PackIconKind), PackIconKind.Gauge);
        public static readonly DependencyProperty ValueProperty = Register(nameof(Value), typeof(string), null);
        public static readonly DependencyProperty UnitProperty = Register(nameof(Unit), typeof(string), null);
        public static readonly DependencyProperty AccentProperty = Register(nameof(Accent), typeof(SolidColorBrush), Brushes.Gray);
        public static readonly DependencyProperty FooterLeftProperty = Register(nameof(FooterLeft), typeof(string), null);
        public static readonly DependencyProperty FooterRightProperty = Register(nameof(FooterRight), typeof(string), null);
        public static readonly DependencyProperty IsLiveProperty = Register(nameof(IsLive), typeof(bool), false);

        public MeterTile()
        {
            InitializeComponent();
            UpdateValueBrush();
        }

        public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
        public PackIconKind Icon { get => (PackIconKind)GetValue(IconProperty); set => SetValue(IconProperty, value); }
        public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
        public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
        public SolidColorBrush Accent { get => (SolidColorBrush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
        public string FooterLeft { get => (string)GetValue(FooterLeftProperty); set => SetValue(FooterLeftProperty, value); }
        public string FooterRight { get => (string)GetValue(FooterRightProperty); set => SetValue(FooterRightProperty, value); }

        /// <summary>True while the output is on: the value is drawn in the accent colour, otherwise muted.</summary>
        public bool IsLive { get => (bool)GetValue(IsLiveProperty); set => SetValue(IsLiveProperty, value); }

        private static DependencyProperty Register(string name, System.Type type, object defaultValue)
        {
            return DependencyProperty.Register(name, type, typeof(MeterTile), new PropertyMetadata(defaultValue, OnChanged));
        }

        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (e.Property == IsLiveProperty || e.Property == AccentProperty)
                ((MeterTile)d).UpdateValueBrush();
        }

        private void UpdateValueBrush()
        {
            if (ValueText == null)
                return;
            if (IsLive)
                ValueText.SetValue(TextBlock.ForegroundProperty, Accent);
            else
                ValueText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        }
    }
}
