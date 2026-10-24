using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ScottPlot;
using ScottPlot.WPF;
using QPX1200SPController.Models;
using QPX1200SPController.Services;
using QPX1200SPController.ViewModels;

namespace QPX1200SPController.Helpers
{
    /// <summary>
    /// Updates a ScottPlot 5 WpfPlot from in-memory telemetry samples. While following live data the
    /// axes fit the newest samples; otherwise the user's zoom and pan are left alone.
    /// </summary>
    public sealed class LivePlotPresenter : IDisposable
    {
        private const float CursorSnapPixels = 28f;
        private const double DragThresholdPixels = 4;

        private readonly WpfPlot _plot;
        private readonly string _unit;
        private readonly string _valueFormat;
        private readonly Func<TelemetrySample, double> _valueSelector;
        private readonly Func<ThemePalette, System.Windows.Media.Color> _colorSelector;
        private readonly PlotYAxisSettings _yAxis;
        private readonly Popup _cursorPopup;
        private readonly TextBlock _valueText;
        private readonly TextBlock _timeText;

        private ScottPlot.Color _lineColor;
        private ScottPlot.Plottables.Scatter _scatter;
        private DateTime[] _timestamps = Array.Empty<DateTime>();
        private Point? _lastMousePos;
        private Point? _pressPos;
        private Window _hostWindow;
        private bool _disposed;

        public LivePlotPresenter(
            WpfPlot plot,
            string yLabel,
            string unit,
            string valueFormat,
            Func<TelemetrySample, double> valueSelector,
            Func<ThemePalette, System.Windows.Media.Color> colorSelector,
            PlotYAxisSettings yAxis)
        {
            _plot = plot ?? throw new ArgumentNullException(nameof(plot));
            _unit = unit;
            _valueFormat = valueFormat;
            _valueSelector = valueSelector;
            _colorSelector = colorSelector;
            _yAxis = yAxis ?? throw new ArgumentNullException(nameof(yAxis));

            _valueText = new TextBlock { FontWeight = FontWeights.Bold, FontSize = 13 };
            _timeText = new TextBlock { FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
            _timeText.SetResourceReference(TextBlock.ForegroundProperty, "MaterialDesign.Brush.Foreground");

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(_valueText, 0);
            Grid.SetRow(_timeText, 1);
            grid.Children.Add(_valueText);
            grid.Children.Add(_timeText);

            var border = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6),
                Child = grid,
                IsHitTestVisible = false,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 10,
                    ShadowDepth = 1,
                    Opacity = 0.28,
                    Color = System.Windows.Media.Colors.Black
                }
            };
            border.SetResourceReference(Border.BackgroundProperty, "MaterialDesign.Brush.Card.Background");
            border.SetResourceReference(Border.BorderBrushProperty, "MaterialDesign.Brush.Separator.Background");

            _cursorPopup = new Popup
            {
                PlacementTarget = _plot,
                Placement = PlacementMode.Relative,
                AllowsTransparency = true,
                StaysOpen = true,
                PopupAnimation = PopupAnimation.None,
                IsHitTestVisible = false,
                Child = border
            };

            _plot.MouseMove += OnPlotMouseMove;
            _plot.MouseLeave += OnPlotMouseLeave;
            _plot.PreviewMouseDown += OnPlotMouseDown;
            _plot.PreviewMouseUp += OnPlotMouseUp;
            _plot.PreviewMouseWheel += OnPlotMouseWheel;
            _hostWindow = Window.GetWindow(_plot);
            if (_hostWindow != null)
                _hostWindow.Deactivated += OnHostDeactivated;

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
            ApplyTheme();
        }

        /// <summary>The user zoomed or panned this plot (mouse wheel or drag).</summary>
        public event Action UserNavigated;

        /// <summary>
        /// Shows <paramref name="samples"/>. With <paramref name="follow"/> the axes fit the data
        /// (or <paramref name="window"/> when there is none); otherwise the current view is kept.
        /// </summary>
        public void Update(IReadOnlyList<TelemetrySample> samples, bool follow, TimeSpan? window)
        {
            if (_scatter != null)
            {
                _plot.Plot.Remove(_scatter);
                _scatter = null;
            }
            _timestamps = Array.Empty<DateTime>();

            if (samples != null && samples.Count > 0)
            {
                double[] xs = samples.Select(s => s.Timestamp.ToOADate()).ToArray();
                double[] ys = samples.Select(_valueSelector).ToArray();
                _timestamps = samples.Select(s => s.Timestamp).ToArray();

                _scatter = _plot.Plot.Add.Scatter(xs, ys);
                _scatter.Color = _lineColor;
                _scatter.LineWidth = 1.5f;
                _scatter.MarkerSize = 0;
            }

            if (follow)
            {
                if (_scatter != null)
                {
                    _plot.Plot.Axes.AutoScale();
                }
                else
                {
                    var now = DateTime.Now;
                    _plot.Plot.Axes.SetLimitsX((now - (window ?? TimeSpan.FromMinutes(15))).ToOADate(), now.ToOADate());
                    _plot.Plot.Axes.SetLimitsY(0, 1);
                }
                ApplyManualY();
            }

            _plot.Refresh();
            UpdateCursorPopup();
        }

        /// <summary>Applies the Y-axis mode after the user changed it (fits the data in Auto mode).</summary>
        public void ApplyYAxis()
        {
            if (!ApplyManualY() && _scatter != null)
                _plot.Plot.Axes.AutoScaleY();
            _plot.Refresh();
        }

        public void ApplyTheme()
        {
            var palette = ThemeService.Instance.Palette;
            var accent = _colorSelector(palette);
            _lineColor = PlotStyling.ToScottPlot(accent);

            PlotStyling.ApplyTheme(_plot.Plot);
            _plot.Plot.Axes.Left.Label.ForeColor = _lineColor;
            if (_scatter != null)
                _scatter.Color = _lineColor;
            _valueText.Foreground = new SolidColorBrush(accent);
            _plot.Refresh();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            _plot.MouseMove -= OnPlotMouseMove;
            _plot.MouseLeave -= OnPlotMouseLeave;
            _plot.PreviewMouseDown -= OnPlotMouseDown;
            _plot.PreviewMouseUp -= OnPlotMouseUp;
            _plot.PreviewMouseWheel -= OnPlotMouseWheel;
            if (_hostWindow != null)
                _hostWindow.Deactivated -= OnHostDeactivated;
            HideCursorPopup();
        }

        /// <summary>Sets the manual Y range if that mode is selected. Returns false in Auto mode.</summary>
        private bool ApplyManualY()
        {
            if (!_yAxis.IsManual)
                return false;

            double lo = Math.Min(_yAxis.YMin, _yAxis.YMax);
            double hi = Math.Max(_yAxis.YMin, _yAxis.YMax);
            if (hi - lo < 1e-12)
                hi = lo + 1;
            _plot.Plot.Axes.SetLimitsY(lo, hi);
            return true;
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
            _lastMousePos = e.GetPosition(_plot);

            // A drag with any button pans (left) or zooms (right) the plot.
            bool pressed = e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed
                           || e.MiddleButton == MouseButtonState.Pressed;
            if (pressed && _pressPos.HasValue && (_lastMousePos.Value - _pressPos.Value).Length > DragThresholdPixels)
            {
                _pressPos = null;
                UserNavigated?.Invoke();
            }

            UpdateCursorPopup();
        }

        private void OnPlotMouseLeave(object sender, MouseEventArgs e)
        {
            _lastMousePos = null;
            HideCursorPopup();
        }

        private void OnHostDeactivated(object sender, EventArgs e)
        {
            _lastMousePos = null;
            HideCursorPopup();
        }

        private void UpdateCursorPopup()
        {
            if (_disposed || !_lastMousePos.HasValue || _scatter == null || _timestamps.Length == 0)
            {
                HideCursorPopup();
                return;
            }

            Point p = _lastMousePos.Value;
            float scale = _plot.DisplayScale;
            var mousePixel = new Pixel((float)(p.X * scale), (float)(p.Y * scale));

            var lastRender = _plot.Plot.LastRender;
            if (!lastRender.DataRect.Contains(mousePixel))
            {
                HideCursorPopup();
                return;
            }

            Coordinates mouseLocation = _plot.Plot.GetCoordinates(mousePixel);
            DataPoint nearest = _scatter.GetNearest(mouseLocation, lastRender, CursorSnapPixels);
            if (!nearest.IsReal || nearest.Index < 0 || nearest.Index >= _timestamps.Length)
            {
                HideCursorPopup();
                return;
            }

            _valueText.Text = $"{nearest.Y.ToString(_valueFormat, CultureInfo.InvariantCulture)} {_unit}";
            _timeText.Text = _timestamps[nearest.Index].ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

            const double popupWidth = 210;
            const double popupHeight = 56;
            double offsetX = 14;
            double offsetY = 14;
            if (p.X + offsetX + popupWidth > _plot.ActualWidth)
                offsetX = -popupWidth - 8;
            if (p.Y + offsetY + popupHeight > _plot.ActualHeight)
                offsetY = -popupHeight - 8;

            _cursorPopup.HorizontalOffset = p.X + offsetX;
            _cursorPopup.VerticalOffset = p.Y + offsetY;
            _cursorPopup.IsOpen = true;
        }

        private void HideCursorPopup()
        {
            if (_cursorPopup.IsOpen)
                _cursorPopup.IsOpen = false;
        }
    }
}
