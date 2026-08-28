using System;
using System.Collections.Generic;
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
using QPX1200SPController.ViewModels;

namespace QPX1200SPController.Helpers
{
    /// <summary>
    /// Updates a ScottPlot 5 WpfPlot from in-memory telemetry samples.
    /// </summary>
    public sealed class LivePlotPresenter : IDisposable
    {
        private const float CursorSnapPixels = 28f;

        private readonly WpfPlot _plot;
        private readonly string _title;
        private readonly string _yLabel;
        private readonly string _unit;
        private readonly string _valueFormat;
        private readonly Func<TelemetrySample, double> _valueSelector;
        private readonly ScottPlot.Color _lineColor;
        private readonly PlotYAxisSettings _yAxis;
        private readonly Popup _cursorPopup;
        private readonly TextBlock _valueText;
        private readonly TextBlock _timeText;

        private ScottPlot.Plottables.Scatter _scatter;
        private DateTime[] _timestamps = Array.Empty<DateTime>();
        private Point? _lastMousePos;
        private Window _hostWindow;
        private bool _disposed;

        public LivePlotPresenter(
            WpfPlot plot,
            string title,
            string yLabel,
            string unit,
            string valueFormat,
            Func<TelemetrySample, double> valueSelector,
            ScottPlot.Color lineColor,
            PlotYAxisSettings yAxis)
        {
            _plot = plot ?? throw new ArgumentNullException(nameof(plot));
            _title = title;
            _yLabel = yLabel;
            _unit = unit;
            _valueFormat = valueFormat;
            _valueSelector = valueSelector;
            _lineColor = lineColor;
            _yAxis = yAxis ?? throw new ArgumentNullException(nameof(yAxis));

            _valueText = new TextBlock
            {
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Foreground = new SolidColorBrush(ToWpfColor(_lineColor))
            };
            _timeText = new TextBlock
            {
                FontSize = 11,
                Foreground = Brushes.Black,
                Margin = new Thickness(0, 2, 0, 0)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(_valueText, 0);
            Grid.SetRow(_timeText, 1);
            grid.Children.Add(_valueText);
            grid.Children.Add(_timeText);

            var border = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDD, 0xDD, 0xDD)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
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
            _hostWindow = Window.GetWindow(_plot);
            if (_hostWindow != null)
                _hostWindow.Deactivated += OnHostDeactivated;

            ConfigureAxes();
        }

        public void Update(IReadOnlyList<TelemetrySample> samples)
        {
            _plot.Plot.Clear();
            _scatter = null;
            _timestamps = Array.Empty<DateTime>();
            ConfigureAxes();

            if (samples == null || samples.Count == 0)
            {
                ApplyYAxisScale();
                _plot.Refresh();
                UpdateCursorPopup();
                return;
            }

            double[] xs = samples.Select(s => s.Timestamp.ToOADate()).ToArray();
            double[] ys = samples.Select(_valueSelector).ToArray();
            _timestamps = samples.Select(s => s.Timestamp).ToArray();

            _scatter = _plot.Plot.Add.Scatter(xs, ys);
            _scatter.Color = _lineColor;
            _scatter.LineWidth = 1.5f;
            _scatter.MarkerSize = 0;

            ApplyYAxisScale();
            _plot.Refresh();
            UpdateCursorPopup();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            _plot.MouseMove -= OnPlotMouseMove;
            _plot.MouseLeave -= OnPlotMouseLeave;
            if (_hostWindow != null)
                _hostWindow.Deactivated -= OnHostDeactivated;
            HideCursorPopup();
        }

        private void ApplyYAxisScale()
        {
            _plot.Plot.Axes.AutoScale();
            if (!_yAxis.IsManual)
                return;

            double lo = Math.Min(_yAxis.YMin, _yAxis.YMax);
            double hi = Math.Max(_yAxis.YMin, _yAxis.YMax);
            if (hi - lo < 1e-12)
                hi = lo + 1;
            _plot.Plot.Axes.SetLimitsY(lo, hi);
        }

        private void ConfigureAxes()
        {
            const float axisFontSize = 24; // 2x ScottPlot default (12)
            const float titleFontSize = 32; // 2x typical ScottPlot title (16)

            _plot.Plot.FigureBackground.Color = ScottPlot.Color.FromHex("#FAFAFA");
            _plot.Plot.DataBackground.Color = ScottPlot.Color.FromHex("#FFFFFF");
            _plot.Plot.Title(_title);
            _plot.Plot.Axes.Title.Label.FontSize = titleFontSize;
            _plot.Plot.Axes.Title.Label.ForeColor = _lineColor;
            _plot.Plot.Axes.Left.Label.Text = _yLabel;
            _plot.Plot.Axes.Bottom.Label.Text = "Time";
            _plot.Plot.Axes.DateTimeTicksBottom();

            _plot.Plot.Axes.Left.Label.FontSize = axisFontSize;
            _plot.Plot.Axes.Bottom.Label.FontSize = axisFontSize;
            _plot.Plot.Axes.Left.TickLabelStyle.FontSize = axisFontSize;
            _plot.Plot.Axes.Bottom.TickLabelStyle.FontSize = axisFontSize;
        }

        private void OnPlotMouseMove(object sender, MouseEventArgs e)
        {
            _lastMousePos = e.GetPosition(_plot);
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

            _valueText.Text = $"{nearest.Y.ToString(_valueFormat)} {_unit}";
            _timeText.Text = _timestamps[nearest.Index].ToString("yyyy-MM-dd HH:mm:ss.fff");

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

        private static System.Windows.Media.Color ToWpfColor(ScottPlot.Color color)
        {
            return System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B);
        }
    }
}
