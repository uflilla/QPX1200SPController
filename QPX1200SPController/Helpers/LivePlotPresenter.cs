using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using ScottPlot;
using ScottPlot.DataSources;
using ScottPlot.WPF;
using QPX1200SPController.Models;
using QPX1200SPController.Services;
using QPX1200SPController.ViewModels;

namespace QPX1200SPController.Helpers
{
    /// <summary>
    /// Draws one live plot (voltage or current) from a <see cref="LivePlotBuffer"/>. Each
    /// segment of the buffer is one line that reads the buffer's lists directly, so a new sample is
    /// drawn with a refresh only and gaps in the data stay empty; lines and markers are rebuilt only
    /// when a segment or event is added or removed. While following live data the newest sample
    /// stays at the right edge and the Y axis follows its mode; otherwise the user's zoom and pan
    /// are left alone. Adapted from CPX400DP LivePlotPresenter.
    /// </summary>
    public sealed class LivePlotPresenter : IDisposable
    {
        private const double DragThresholdPixels = 4;

        private readonly WpfPlot _plot;
        private readonly string _seriesName;
        private readonly string _unit;
        private readonly string _format;
        private readonly double _minSignalSpan;
        private readonly Func<LiveSegment, List<double>> _values;
        private readonly Func<ThemePalette, System.Windows.Media.Color> _colorSelector;
        private readonly PlotYAxisSettings _yAxis;
        private readonly PlotHover _hover;
        private readonly List<ScottPlot.Plottables.Scatter> _lines = new List<ScottPlot.Plottables.Scatter>();
        private readonly List<ScottPlot.Plottables.VerticalLine> _markers = new List<ScottPlot.Plottables.VerticalLine>();

        private LivePlotBuffer _buffer;
        private int _drawnVersion = -1;
        private bool _drawnMarkers;
        private Point? _pressPos;
        private bool _disposed;

        /// <param name="minSignalSpan">Smallest Y span in Signal mode (about ten steps of the supply's resolution).</param>
        public LivePlotPresenter(WpfPlot plot, string yLabel, string seriesName, string unit, string format, double minSignalSpan,
            Func<LiveSegment, List<double>> values, Func<ThemePalette, System.Windows.Media.Color> colorSelector,
            PlotYAxisSettings yAxis)
        {
            _plot = plot ?? throw new ArgumentNullException(nameof(plot));
            _seriesName = seriesName;
            _unit = unit;
            _format = format;
            _minSignalSpan = minSignalSpan;
            _values = values;
            _colorSelector = colorSelector;
            _yAxis = yAxis ?? throw new ArgumentNullException(nameof(yAxis));

            var axes = _plot.Plot.Axes;
            PlotStyling.UseTimeAxis(_plot.Plot);
            axes.Left.Label.Text = yLabel;
            axes.Left.Label.FontSize = 15;
            axes.Left.Label.Bold = true;
            axes.Left.TickLabelStyle.FontSize = 13;
            axes.Bottom.TickLabelStyle.FontSize = 13;
            axes.Bottom.Label.Text = string.Empty;
            // Same Y-axis width on the three plots so their time axes line up.
            axes.Left.MinimumSize = 76;
            axes.Left.MaximumSize = 76;

            _hover = new PlotHover(_plot);
            _plot.PreviewMouseDown += OnPlotMouseDown;
            _plot.PreviewMouseUp += OnPlotMouseUp;
            _plot.PreviewMouseMove += OnPlotMouseMove;
            _plot.PreviewMouseWheel += OnPlotMouseWheel;
            ApplyTheme();
        }

        /// <summary>The user zoomed or panned this plot (mouse wheel or drag).</summary>
        public event Action UserNavigated;

        /// <summary>
        /// Shows <paramref name="buffer"/>. With <paramref name="follow"/> the time axis shows the last
        /// <paramref name="window"/> up to the newest sample and the Y axis follows its mode.
        /// </summary>
        public void Update(LivePlotBuffer buffer, bool follow, TimeSpan window, bool showMarkers)
        {
            if (_disposed || buffer == null)
                return;
            if (buffer != _buffer || buffer.Version != _drawnVersion || showMarkers != _drawnMarkers)
                Rebuild(buffer, showMarkers);

            if (follow)
            {
                DateTime end = buffer.Newest ?? DateTime.Now;
                double right = end.ToOADate() + window.TotalDays * 0.01;
                double left = (end - window).ToOADate();
                _plot.Plot.Axes.SetLimitsX(left, right);
                // Skip the points left of the view (up to a day of samples per line).
                foreach (var line in _lines)
                {
                    var times = ((LineSource)line.Data).Times;
                    line.Data.MinRenderIndex = Math.Max(0, LivePlotBuffer.LowerBound(times, left) - 1);
                }
                FitY(left, right);
            }
            else
            {
                foreach (var line in _lines)
                    line.Data.MinRenderIndex = 0;
            }

            _plot.Refresh();
        }

        /// <summary>Applies the Y-axis mode after the user changed it, over the time range now shown.</summary>
        public void ApplyYAxis()
        {
            var limits = _plot.Plot.Axes.GetLimits();
            FitY(limits.Left, limits.Right);
            _plot.Refresh();
        }

        public void ApplyTheme()
        {
            var color = PlotStyling.ToScottPlot(_colorSelector(ThemeService.Instance.Palette));
            PlotStyling.ApplyTheme(_plot.Plot);
            _plot.Plot.Axes.Left.Label.ForeColor = color;
            // Lines and markers take the new colours when they are rebuilt.
            _drawnVersion = -1;
            if (_buffer != null)
                Rebuild(_buffer, _drawnMarkers);
            _plot.Refresh();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _plot.PreviewMouseDown -= OnPlotMouseDown;
            _plot.PreviewMouseUp -= OnPlotMouseUp;
            _plot.PreviewMouseMove -= OnPlotMouseMove;
            _plot.PreviewMouseWheel -= OnPlotMouseWheel;
            _hover.Dispose();
        }

        private void Rebuild(LivePlotBuffer buffer, bool showMarkers)
        {
            _buffer = buffer;
            _drawnVersion = buffer.Version;
            _drawnMarkers = showMarkers;

            var plot = _plot.Plot;
            var palette = ThemeService.Instance.Palette;
            var accent = _colorSelector(palette);
            var color = PlotStyling.ToScottPlot(accent);

            foreach (var marker in _markers)
                plot.Remove(marker);
            _markers.Clear();
            foreach (var line in _lines)
                plot.Remove(line);
            _lines.Clear();

            var events = new List<HoverEvent>();
            if (showMarkers)
            {
                foreach (var ev in buffer.Events)
                {
                    System.Windows.Media.Color markerColor;
                    LinePattern pattern;
                    float width = 1f;
                    switch (ev.Kind)
                    {
                        case LiveEvent.KindTrip:
                            markerColor = palette.Error;
                            pattern = LinePattern.Solid;
                            width = 1.6f;
                            break;
                        case LiveEvent.KindAlarm:
                            markerColor = ev.Title == "Alarm" ? palette.Error : palette.Warn;
                            pattern = LinePattern.Dashed;
                            width = 1.3f;
                            break;
                        case LiveEvent.KindConnection:
                            markerColor = palette.ConnectionMarker;
                            pattern = LinePattern.Dotted;
                            break;
                        default:
                            markerColor = palette.CommandMarker;
                            pattern = LinePattern.Dashed;
                            break;
                    }

                    var marker = plot.Add.VerticalLine(ev.X, width, PlotStyling.ToScottPlot(markerColor).WithAlpha(0.75), pattern);
                    marker.EnableAutoscale = false;
                    _markers.Add(marker);
                    events.Add(new HoverEvent(ev.X, ev.Title, ev.Text, markerColor));
                }
            }

            var series = new List<HoverSeries>();
            foreach (var segment in buffer.Segments)
            {
                var line = plot.Add.Scatter(new LineSource(segment.Times, _values(segment)));
                line.Color = color;
                line.LineWidth = 1.5f;
                line.MarkerSize = 0;
                _lines.Add(line);
                series.Add(new HoverSeries(line, _seriesName, _unit, _format, accent));
            }

            _hover.SetSeries(series);
            _hover.SetEvents(events);
        }

        /// <summary>Sets the Y range for the time range left..right (OLE dates) according to the axis mode.</summary>
        private void FitY(double left, double right)
        {
            if (_yAxis.IsManual)
            {
                double lo = Math.Min(_yAxis.YMin, _yAxis.YMax);
                double hi = Math.Max(_yAxis.YMin, _yAxis.YMax);
                if (hi - lo < 1e-9)
                    hi = lo + 1;
                _plot.Plot.Axes.SetLimitsY(lo, hi);
                return;
            }

            double min = double.MaxValue;
            double max = double.MinValue;
            if (_buffer != null)
            {
                foreach (var segment in _buffer.Segments)
                {
                    var times = segment.Times;
                    var values = _values(segment);
                    for (int i = LivePlotBuffer.LowerBound(times, left); i < times.Count && times[i] <= right; i++)
                    {
                        min = Math.Min(min, values[i]);
                        max = Math.Max(max, values[i]);
                    }
                }
            }
            bool hasData = min <= max;

            if (_yAxis.IsSignal && hasData)
            {
                // Fit the measured values (ripple, drift), with a floor so noise does not fill the plot.
                double span = Math.Max(max - min, _minSignalSpan);
                double middle = (max + min) / 2;
                double low = middle - span * 0.6;
                double high = middle + span * 0.6;
                // Readings are never negative: do not show negative values for a flat 0 V / 0 A trace.
                if (min >= 0 && low < 0)
                {
                    high -= low;
                    low = 0;
                }
                _plot.Plot.Axes.SetLimitsY(low, high);
                return;
            }

            // Auto: from 0 up to the highest value shown.
            double top = hasData ? max : 0;
            double bottom = hasData ? Math.Min(0, min) : 0;
            _plot.Plot.Axes.SetLimitsY(bottom, Math.Max(_minSignalSpan, top * 1.08));
        }

        private void OnPlotMouseWheel(object sender, MouseWheelEventArgs e)
        {
            UserNavigated?.Invoke();
        }

        private void OnPlotMouseDown(object sender, MouseButtonEventArgs e)
        {
            _pressPos = e.GetPosition(_plot);
        }

        private void OnPlotMouseUp(object sender, MouseButtonEventArgs e)
        {
            _pressPos = null;
        }

        private void OnPlotMouseMove(object sender, MouseEventArgs e)
        {
            // A drag with any button pans (left) or zooms (right) the plot.
            bool pressed = e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed
                           || e.MiddleButton == MouseButtonState.Pressed;
            if (!pressed || !_pressPos.HasValue)
                return;
            if ((e.GetPosition(_plot) - _pressPos.Value).Length > DragThresholdPixels)
            {
                _pressPos = null;
                UserNavigated?.Invoke();
            }
        }

        /// <summary>One line drawn straight from the buffer's lists; keeps the time list at hand for skipping points left of the view.</summary>
        private sealed class LineSource : ScatterSourceGenericList<double, double>
        {
            public LineSource(List<double> times, List<double> values) : base(times, values)
            {
                Times = times;
            }

            public List<double> Times { get; }
        }
    }
}
