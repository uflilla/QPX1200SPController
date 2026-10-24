using System;
using System.Globalization;
using ScottPlot;
using ScottPlot.TickGenerators;
using QPX1200SPController.Services;

namespace QPX1200SPController.Helpers
{
    /// <summary>Theme colours and time-axis labels shared by the live and History plots.</summary>
    public static class PlotStyling
    {
        public static ScottPlot.Color ToScottPlot(System.Windows.Media.Color c)
        {
            return new ScottPlot.Color(c.R, c.G, c.B, c.A);
        }

        /// <summary>Background, axes and grid in the current theme's colours.</summary>
        public static void ApplyTheme(Plot plot)
        {
            var theme = ThemeService.Instance;
            var background = ToScottPlot(theme.PlotBackground);
            plot.FigureBackground.Color = background;
            plot.DataBackground.Color = background;
            plot.Axes.Color(ToScottPlot(theme.Palette.PlotForeground));
            plot.Grid.MajorLineColor = ToScottPlot(theme.Palette.PlotGrid);
        }

        /// <summary>
        /// Uses a date/time bottom axis whose labels only show what the zoom level needs
        /// ("14:05:30", "14:05", "10-24 14:00", "2026-10-24") instead of the culture's long format.
        /// </summary>
        public static void UseTimeAxis(Plot plot)
        {
            var axis = plot.Axes.DateTimeTicksBottom();
            if (axis.TickGenerator is DateTimeAutomatic ticks)
                ticks.LabelFormatter = t => FormatTimeTick(t, plot.Axes.Bottom.Range.Span);
        }

        public static string FormatTimeTick(DateTime time, double spanDays)
        {
            var inv = CultureInfo.InvariantCulture;
            bool midnight = time.TimeOfDay == TimeSpan.Zero;
            if (spanDays <= 15.0 / 1440)
                return time.ToString(time.Millisecond == 0 ? "HH:mm:ss" : "HH:mm:ss.f", inv);
            if (spanDays <= 1.5)
                return time.ToString(midnight ? "MM-dd" : "HH:mm", inv);
            if (spanDays <= 10)
                return time.ToString(midnight ? "MM-dd" : "MM-dd HH:mm", inv);
            return time.ToString("yyyy-MM-dd", inv);
        }
    }
}
