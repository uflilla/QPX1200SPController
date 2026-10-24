using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;

namespace QPX1200SPController.Services
{
    /// <summary>
    /// Switches between the dark (default) and light theme: the MaterialDesign base theme, the app's own
    /// brushes (meter accents, status colours, event markers) and the colours the plots are drawn with.
    /// XAML uses the brushes as DynamicResource so everything follows a switch immediately.
    /// </summary>
    public sealed class ThemeService
    {
        private static readonly Lazy<ThemeService> _instance = new Lazy<ThemeService>(() => new ThemeService());
        public static ThemeService Instance => _instance.Value;

        private ThemeService() { }

        public bool IsDark { get; private set; } = true;

        public ThemePalette Palette { get; private set; } = ThemePalette.Dark;

        /// <summary>Raised on the UI thread after the theme was switched (plots redraw themselves).</summary>
        public event Action ThemeChanged;

        public void Apply(bool dark)
        {
            var helper = new PaletteHelper();
            var theme = helper.GetTheme();
            theme.SetBaseTheme(dark ? BaseTheme.Dark : BaseTheme.Light);
            // Teal 500 is too dim on dark surfaces; use a lighter teal there.
            theme.SetPrimaryColor(dark ? Color.FromRgb(0x4D, 0xB6, 0xAC) : Color.FromRgb(0x00, 0x96, 0x88));
            helper.SetTheme(theme);

            IsDark = dark;
            Palette = dark ? ThemePalette.Dark : ThemePalette.Light;

            var resources = Application.Current.Resources;
            foreach (var entry in Palette.CreateBrushes())
                resources[entry.Key] = entry.Value;

            ThemeChanged?.Invoke();
        }

        /// <summary>The colour of a solid theme brush from the application resources.</summary>
        public static Color ResourceColor(string key, Color fallback)
        {
            return Application.Current?.TryFindResource(key) is SolidColorBrush brush ? brush.Color : fallback;
        }

        /// <summary>Card background: the plots use it so they blend into their cards.</summary>
        public Color PlotBackground => ResourceColor("MaterialDesign.Brush.Card.Background", IsDark ? Color.FromRgb(0x2A, 0x2A, 0x2A) : Colors.White);
    }

    public sealed class ThemePalette
    {
        public static readonly ThemePalette Dark = new ThemePalette
        {
            Voltage = Rgb(0xFF, 0xA5, 0x4F),
            Current = Rgb(0x4F, 0xC3, 0xF7),
            Power = Rgb(0xCE, 0x93, 0xD8),
            Ok = Rgb(0x66, 0xBB, 0x6A),
            Warn = Rgb(0xFF, 0xCA, 0x28),
            Error = Rgb(0xEF, 0x53, 0x50),
            Muted = Rgb(0x8D, 0x93, 0x9C),
            CommandMarker = Rgb(0x4D, 0xB6, 0xAC),
            ConnectionMarker = Rgb(0x9E, 0x9E, 0x9E),
            Cursor = Rgb(0xB0, 0xB0, 0xB0),
            PlotForeground = Rgb(0xC8, 0xCC, 0xD2),
            PlotGrid = Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF),
            Overlay = Color.FromArgb(0xE6, 0x26, 0x29, 0x30),
            OverlayBorder = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF),
            HeaderStart = Rgb(0x28, 0x35, 0x93),
            HeaderEnd = Rgb(0x00, 0x69, 0x5C)
        };

        public static readonly ThemePalette Light = new ThemePalette
        {
            Voltage = Rgb(0xE6, 0x51, 0x00),
            Current = Rgb(0x02, 0x77, 0xBD),
            Power = Rgb(0x8E, 0x24, 0xAA),
            Ok = Rgb(0x2E, 0x7D, 0x32),
            Warn = Rgb(0xEF, 0x6C, 0x00),
            Error = Rgb(0xC6, 0x28, 0x28),
            Muted = Rgb(0x9E, 0x9E, 0x9E),
            CommandMarker = Rgb(0x00, 0x89, 0x7B),
            ConnectionMarker = Rgb(0x75, 0x75, 0x75),
            Cursor = Rgb(0x42, 0x42, 0x42),
            PlotForeground = Rgb(0x42, 0x42, 0x42),
            PlotGrid = Color.FromArgb(0x1F, 0x00, 0x00, 0x00),
            Overlay = Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF),
            OverlayBorder = Color.FromArgb(0x33, 0x00, 0x00, 0x00),
            HeaderStart = Rgb(0x39, 0x49, 0xAB),
            HeaderEnd = Rgb(0x00, 0x89, 0x7B)
        };

        public Color Voltage { get; private set; }
        public Color Current { get; private set; }
        public Color Power { get; private set; }
        public Color Ok { get; private set; }
        public Color Warn { get; private set; }
        public Color Error { get; private set; }
        public Color Muted { get; private set; }
        public Color CommandMarker { get; private set; }
        public Color ConnectionMarker { get; private set; }
        public Color Cursor { get; private set; }
        public Color PlotForeground { get; private set; }
        public Color PlotGrid { get; private set; }
        public Color Overlay { get; private set; }
        public Color OverlayBorder { get; private set; }
        public Color HeaderStart { get; private set; }
        public Color HeaderEnd { get; private set; }

        /// <summary>The brush resources the XAML refers to (by key).</summary>
        public Dictionary<string, Brush> CreateBrushes()
        {
            var header = new LinearGradientBrush(HeaderStart, HeaderEnd, 0);
            header.Freeze();
            return new Dictionary<string, Brush>
            {
                ["VoltageBrush"] = Solid(Voltage),
                ["CurrentBrush"] = Solid(Current),
                ["PowerBrush"] = Solid(Power),
                ["OkBrush"] = Solid(Ok),
                ["WarnBrush"] = Solid(Warn),
                ["ErrorBrush"] = Solid(Error),
                ["MutedBrush"] = Solid(Muted),
                ["CommandMarkerBrush"] = Solid(CommandMarker),
                ["ConnectionMarkerBrush"] = Solid(ConnectionMarker),
                ["OverlayBrush"] = Solid(Overlay),
                ["OverlayBorderBrush"] = Solid(OverlayBorder),
                ["HeaderBrush"] = header
            };
        }

        private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

        private static SolidColorBrush Solid(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
