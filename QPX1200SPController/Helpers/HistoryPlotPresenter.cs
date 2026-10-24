using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScottPlot;
using ScottPlot.WPF;
using QPX1200SPController.Models;
using QPX1200SPController.Services;

namespace QPX1200SPController.Helpers
{
    /// <summary>
    /// Drives the three History plots (voltage, current, power): time axes linked, gaps in the data
    /// left open, command/connection/error markers as vertical lines, a shared cursor with read-out,
    /// and a debounced notification when the user zooms or pans so the window can be reloaded.
    /// </summary>
    public sealed class HistoryPlotPresenter : IDisposable
    {
        private static ScottPlot.Color CommandColor => PlotStyling.ToScottPlot(ThemeService.Instance.Palette.CommandMarker);
        private static ScottPlot.Color MismatchColor => PlotStyling.ToScottPlot(ThemeService.Instance.Palette.Warn);
        private static ScottPlot.Color ErrorColor => PlotStyling.ToScottPlot(ThemeService.Instance.Palette.Error);
        private static ScottPlot.Color ConnectionColor => PlotStyling.ToScottPlot(ThemeService.Instance.Palette.ConnectionMarker);
        private static ScottPlot.Color CursorColor => PlotStyling.ToScottPlot(ThemeService.Instance.Palette.Cursor);
        private const int MaxDrawnCommandMarkers = 150;
        private const float MarkerHitPixels = 6f;

        private readonly PlotChannel[] _channels;
        private readonly DispatcherTimer _rangeDebounce;
        private readonly Func<ActivityRecord, bool> _isMarkerVisible;
        private readonly Func<ActivityRecord, bool> _isError;

        private HistorySeries _series = HistorySeries.Empty;
        private IReadOnlyList<ActivityRecord> _markers = Array.Empty<ActivityRecord>();
        private double[] _markerXs = Array.Empty<double>();
        private ActivityRecord[] _markerRecords = Array.Empty<ActivityRecord>();
        private double _shownLeft = double.NaN;
        private double _shownRight = double.NaN;
        private Point? _mouseDown;
        private bool _disposed;

        public HistoryPlotPresenter(WpfPlot voltage, WpfPlot current, WpfPlot power,
            Func<ActivityRecord, bool> isMarkerVisible, Func<ActivityRecord, bool> isError)
        {
            _isMarkerVisible = isMarkerVisible;
            _isError = isError;
            _channels = new[]
            {
                new PlotChannel(voltage, "Voltage (V)", "V", "F3", p => p.Voltage, s => s.Voltage),
                new PlotChannel(current, "Current (A)", "A", "F3", p => p.Current, s => s.Current),
                new PlotChannel(power, "Power (W)", "W", "F2", p => p.Power, s => s.Power)
            };

            // Zooming or panning any plot moves all three.
            foreach (var a in _channels)
                foreach (var b in _channels)
                    if (a != b)
                        a.Control.Plot.Axes.Link(b.Control, x: true, y: false);

            _rangeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _rangeDebounce.Tick += OnRangeDebounceTick;

            foreach (var channel in _channels)
            {
                var control = channel.Control;
                control.Plot.RenderManager.AxisLimitsChanged += OnAxisLimitsChanged;
                control.MouseMove += OnMouseMove;
                control.MouseLeave += OnMouseLeave;
                control.PreviewMouseLeftButtonDown += OnMouseDown;
                control.PreviewMouseLeftButtonUp += OnMouseUp;
                Configure(channel);
                control.Refresh();
            }
        }

        /// <summary>The user zoomed or panned to a new time window (debounced).</summary>
        public event Action<DateTime, DateTime> VisibleRangeChanged;

        /// <summary>Text for the cursor read-out (values and nearest event under the mouse).</summary>
        public event Action<string> CursorChanged;

        /// <summary>A marker was clicked.</summary>
        public event Action<ActivityRecord> MarkerClicked;

        /// <summary>When false the Y axes keep the user's zoom instead of fitting the data after each reload.</summary>
        public bool AutoScaleY { get; set; } = true;

        public void SetData(HistorySeries series, IReadOnlyList<ActivityRecord> markers, bool resetAxes,
            DateTime visibleFrom, DateTime visibleTo)
        {
            _series = series ?? HistorySeries.Empty;
            _markers = markers ?? Array.Empty<ActivityRecord>();

            // Plot.Clear() also forgets the axis limits (the next render would auto-fit X with margins,
            // which reads as a zoom-out and triggers another reload), so keep them across the rebuild.
            var before = _channels.Select(c => c.Control.Plot.Axes.GetLimits()).ToArray();

            foreach (var channel in _channels)
            {
                var plot = channel.Control.Plot;
                plot.Clear();
                AddSeries(channel);
            }
            RebuildMarkers();

            double left = resetAxes ? visibleFrom.ToOADate() : before[0].Left;
            double right = resetAxes ? visibleTo.ToOADate() : before[0].Right;
            SetVisibleRange(left, right);
            _shownLeft = left;
            _shownRight = right;

            for (int k = 0; k < _channels.Length; k++)
            {
                var channel = _channels[k];
                if (AutoScaleY || resetAxes)
                    FitY(channel);
                else
                    channel.Control.Plot.Axes.SetLimitsY(before[k].Bottom, before[k].Top);
                channel.Control.Refresh();
            }
        }

        /// <summary>Raised after markers are drawn: a note about markers hidden because they were too dense (or null).</summary>
        public event Action<string> MarkerNoteChanged;

        /// <summary>Redraws the markers (after loading or when the marker filters changed).</summary>
        public void RebuildMarkers()
        {
            var visible = _markers.Where(_isMarkerVisible).ToList();

            // Hundreds of command lines would hide the data: above the limit only errors and
            // connection events are drawn, and the rest appear again when zoomed in.
            int hiddenCommands = 0;
            int commands = visible.Count(m => !_isError(m) && m.Kind == ActivityRecord.KindCommand);
            if (commands > MaxDrawnCommandMarkers)
            {
                hiddenCommands = commands;
                visible.RemoveAll(m => !_isError(m) && m.Kind == ActivityRecord.KindCommand);
            }

            _markerRecords = visible.ToArray();
            _markerXs = visible.Select(m => m.Timestamp.ToOADate()).ToArray();

            foreach (var channel in _channels)
            {
                var plot = channel.Control.Plot;
                foreach (var line in channel.MarkerLines)
                    plot.Remove(line);
                channel.MarkerLines.Clear();

                foreach (var record in visible)
                {
                    var line = plot.Add.VerticalLine(record.Timestamp.ToOADate(), 1.5f, MarkerColorFor(record).WithAlpha(0.75),
                        record.Kind == ActivityRecord.KindConnection ? LinePattern.Dashed : LinePattern.Solid);
                    line.EnableAutoscale = false;
                    channel.MarkerLines.Add(line);
                }

                // Data above the markers, the cursor above everything.
                foreach (var data in channel.DataPlottables)
                    plot.MoveToTop(data);
                if (channel.Cursor != null)
                    plot.MoveToTop(channel.Cursor);
                channel.Control.Refresh();
            }

            MarkerNoteChanged?.Invoke(hiddenCommands > 0
                ? $"{hiddenCommands:N0} command markers are not drawn at this zoom level (too dense) - zoom in to see them. They are all in the list below."
                : null);
        }

        /// <summary>Moves the visible window (same width) so <paramref name="time"/> is in the middle.</summary>
        public void CenterOn(DateTime time)
        {
            var limits = _channels[0].Control.Plot.Axes.GetLimits();
            double half = (limits.Right - limits.Left) / 2;
            double x = time.ToOADate();
            SetVisibleRange(x - half, x + half);
            foreach (var channel in _channels)
                channel.Control.Refresh();
        }

        /// <summary>Saves the three plots, stacked as on screen, to a PNG at twice the screen resolution.</summary>
        public void SavePng(FrameworkElement container, string path)
        {
            const double scale = 2.0;
            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(container.ActualWidth * scale), (int)Math.Ceiling(container.ActualHeight * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);

            // Render onto the plot background colour (the plot borders are partly transparent).
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(ThemeService.Instance.PlotBackground), null, new Rect(0, 0, container.ActualWidth, container.ActualHeight));
                dc.DrawRectangle(new VisualBrush(container), null, new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            }
            bitmap.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = System.IO.File.Create(path))
                encoder.Save(stream);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _rangeDebounce.Stop();
            foreach (var channel in _channels)
            {
                channel.Control.Plot.RenderManager.AxisLimitsChanged -= OnAxisLimitsChanged;
                channel.Control.MouseMove -= OnMouseMove;
                channel.Control.MouseLeave -= OnMouseLeave;
                channel.Control.PreviewMouseLeftButtonDown -= OnMouseDown;
                channel.Control.PreviewMouseLeftButtonUp -= OnMouseUp;
            }
        }

        #region Drawing

        /// <summary>Redraws everything in the current theme's colours, keeping the zoom.</summary>
        public void ApplyTheme()
        {
            var before = _channels.Select(c => c.Control.Plot.Axes.GetLimits()).ToArray();
            foreach (var channel in _channels)
            {
                ApplyChannelTheme(channel);
                channel.Control.Plot.Clear();
                AddSeries(channel);
            }
            RebuildMarkers();

            for (int k = 0; k < _channels.Length; k++)
            {
                _channels[k].Control.Plot.Axes.SetLimits(before[k]);
                _channels[k].Control.Refresh();
            }
        }

        private static void ApplyChannelTheme(PlotChannel channel)
        {
            PlotStyling.ApplyTheme(channel.Control.Plot);
            channel.Control.Plot.Axes.Left.Label.ForeColor = channel.Color;
        }

        private static void Configure(PlotChannel channel)
        {
            var plot = channel.Control.Plot;
            PlotStyling.UseTimeAxis(plot);
            plot.Axes.Left.Label.Text = channel.Title;
            ApplyChannelTheme(channel);
            plot.Axes.Left.Label.FontSize = 14;
            plot.Axes.Left.TickLabelStyle.FontSize = 12;
            // Same Y-axis width on all three plots so their time axes (and markers) line up.
            plot.Axes.Left.MinimumSize = 80;
            plot.Axes.Left.MaximumSize = 80;
            plot.Axes.Bottom.TickLabelStyle.FontSize = 12;
            plot.Axes.Bottom.Label.Text = string.Empty;
        }

        private void AddSeries(PlotChannel channel)
        {
            var plot = channel.Control.Plot;
            var xs = _series.Xs;
            var ys = channel.Selector(_series);
            double gapDays = _series.GapThresholdSeconds / 86400.0;

            // Split into runs so the line is not drawn across periods without data.
            channel.DataPlottables.Clear();
            int start = 0;
            for (int k = 1; k <= xs.Length; k++)
            {
                if (k < xs.Length && xs[k] - xs[k - 1] <= gapDays)
                    continue;

                int count = k - start;
                if (count == 1)
                {
                    channel.DataPlottables.Add(plot.Add.Marker(xs[start], ys[start], MarkerShape.FilledCircle, 4, channel.Color));
                }
                else if (count > 1)
                {
                    var segX = new double[count];
                    var segY = new double[count];
                    Array.Copy(xs, start, segX, 0, count);
                    Array.Copy(ys, start, segY, 0, count);
                    var signal = plot.Add.SignalXY(segX, segY, channel.Color);
                    signal.LineWidth = 1.5f;
                    signal.MarkerSize = 0;
                    channel.DataPlottables.Add(signal);
                }
                start = k;
            }

            channel.Cursor = plot.Add.VerticalLine(0, 1, CursorColor, LinePattern.Dotted);
            channel.Cursor.EnableAutoscale = false;
            channel.Cursor.IsVisible = false;
            channel.MarkerLines.Clear();
        }

        private void FitY(PlotChannel channel)
        {
            var plot = channel.Control.Plot;
            if (_series.Xs.Length == 0)
            {
                plot.Axes.SetLimitsY(0, 1);
                return;
            }
            plot.Axes.AutoScaleY();
        }

        private void SetVisibleRange(double left, double right)
        {
            foreach (var channel in _channels)
                channel.Control.Plot.Axes.SetLimitsX(left, right);
        }

        private ScottPlot.Color MarkerColorFor(ActivityRecord record)
        {
            if (record.Result == ActivityRecord.ResultMismatch)
                return MismatchColor;
            if (_isError(record))
                return ErrorColor;
            return record.Kind == ActivityRecord.KindConnection ? ConnectionColor : CommandColor;
        }

        #endregion

        #region Zoom / pan notification

        private void OnAxisLimitsChanged(object sender, RenderDetails e)
        {
            if (_disposed)
                return;
            _rangeDebounce.Stop();
            _rangeDebounce.Start();
        }

        private void OnRangeDebounceTick(object sender, EventArgs e)
        {
            _rangeDebounce.Stop();
            var limits = _channels[0].Control.Plot.Axes.GetLimits();
            double left = limits.Left, right = limits.Right;
            if (double.IsNaN(left) || double.IsNaN(right) || right <= left)
                return;

            // Ignore Y-only changes and tiny X jitter.
            double tolerance = (right - left) * 0.005;
            if (Math.Abs(left - _shownLeft) <= tolerance && Math.Abs(right - _shownRight) <= tolerance)
                return;

            _shownLeft = left;
            _shownRight = right;
            try
            {
                VisibleRangeChanged?.Invoke(DateTime.FromOADate(left), DateTime.FromOADate(right));
            }
            catch (ArgumentException)
            {
                // panned outside the representable date range
            }
        }

        #endregion

        #region Cursor and marker hit-testing

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            var control = (WpfPlot)sender;
            var position = e.GetPosition(control);
            var pixel = new Pixel((float)(position.X * control.DisplayScale), (float)(position.Y * control.DisplayScale));
            if (!control.Plot.LastRender.DataRect.Contains(pixel))
            {
                HideCursor();
                return;
            }

            double x = control.Plot.GetCoordinates(pixel).X;
            foreach (var channel in _channels)
            {
                if (channel.Cursor == null)
                    continue;
                channel.Cursor.X = x;
                channel.Cursor.IsVisible = true;
                channel.Control.Refresh();
            }

            CursorChanged?.Invoke(DescribeCursor(x, FindMarkerNear(control, pixel)));
        }

        private void OnMouseLeave(object sender, MouseEventArgs e)
        {
            HideCursor();
        }

        private void HideCursor()
        {
            foreach (var channel in _channels)
            {
                if (channel.Cursor != null && channel.Cursor.IsVisible)
                {
                    channel.Cursor.IsVisible = false;
                    channel.Control.Refresh();
                }
            }
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            _mouseDown = e.GetPosition((IInputElement)sender);
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            var control = (WpfPlot)sender;
            var up = e.GetPosition(control);
            if (_mouseDown == null || (up - _mouseDown.Value).Length > 3)
                return; // that was a pan, not a click
            _mouseDown = null;

            var pixel = new Pixel((float)(up.X * control.DisplayScale), (float)(up.Y * control.DisplayScale));
            var marker = FindMarkerNear(control, pixel);
            if (marker != null)
                MarkerClicked?.Invoke(marker);
        }

        private ActivityRecord FindMarkerNear(WpfPlot control, Pixel pixel)
        {
            if (_markerXs.Length == 0)
                return null;

            double x = control.Plot.GetCoordinates(pixel).X;
            int index = Array.BinarySearch(_markerXs, x);
            if (index < 0)
                index = ~index;

            ActivityRecord best = null;
            float bestDistance = MarkerHitPixels * control.DisplayScale;
            for (int k = Math.Max(0, index - 2); k <= Math.Min(_markerXs.Length - 1, index + 2); k++)
            {
                float markerPixel = control.Plot.GetPixel(new Coordinates(_markerXs[k], 0)).X;
                float distance = Math.Abs(markerPixel - pixel.X);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = _markerRecords[k];
                }
            }
            return best;
        }

        private string DescribeCursor(double x, ActivityRecord marker)
        {
            var inv = CultureInfo.InvariantCulture;
            string text = DateTime.FromOADate(x).ToString("yyyy-MM-dd HH:mm:ss.fff", inv);

            var xs = _series.Xs;
            if (xs.Length > 0)
            {
                int i = Array.BinarySearch(xs, x);
                if (i < 0)
                {
                    i = ~i;
                    if (i >= xs.Length || (i > 0 && x - xs[i - 1] < xs[i] - x))
                        i--;
                }

                bool inGap = Math.Abs(xs[i] - x) * 86400.0 > _series.GapThresholdSeconds;
                text += inGap
                    ? "\nNo data at this time"
                    : string.Format(inv, "\nV {0:F3} V    I {1:F3} A    P {2:F2} W{3}",
                        _series.Voltage[i], _series.Current[i], _series.Power[i],
                        _series.IsDownsampled ? "\n(min/max envelope point - zoom in for exact samples)" : "");
            }

            if (marker != null)
            {
                text += $"\n\n{marker.Timestamp.ToString("HH:mm:ss.fff", inv)}  {marker.Kind}: {marker.Summary}";
                if (!string.IsNullOrEmpty(marker.Source))
                    text += $"\nSource: {marker.Source}";
                if (marker.Requested.HasValue || marker.Readback.HasValue)
                    text += string.Format(inv, "\nRequested {0}  Read-back {1}",
                        marker.Requested?.ToString("F3", inv) ?? "-", marker.Readback?.ToString("F3", inv) ?? "-");
                if (!string.IsNullOrEmpty(marker.Detail) && marker.Kind != ActivityRecord.KindConnection)
                    text += "\n" + marker.Detail;
            }
            return text;
        }

        #endregion

        private sealed class PlotChannel
        {
            private readonly Func<ThemePalette, System.Windows.Media.Color> _colorSelector;

            public PlotChannel(WpfPlot control, string title, string unit, string format,
                Func<ThemePalette, System.Windows.Media.Color> colorSelector, Func<HistorySeries, double[]> selector)
            {
                Control = control;
                Title = title;
                Unit = unit;
                Format = format;
                _colorSelector = colorSelector;
                Selector = selector;
            }

            public WpfPlot Control { get; }
            public string Title { get; }
            public string Unit { get; }
            public string Format { get; }
            public ScottPlot.Color Color => PlotStyling.ToScottPlot(_colorSelector(ThemeService.Instance.Palette));
            public Func<HistorySeries, double[]> Selector { get; }
            public ScottPlot.Plottables.VerticalLine Cursor { get; set; }
            public List<ScottPlot.Plottables.VerticalLine> MarkerLines { get; } = new List<ScottPlot.Plottables.VerticalLine>();
            public List<IPlottable> DataPlottables { get; } = new List<IPlottable>();
        }
    }
}
