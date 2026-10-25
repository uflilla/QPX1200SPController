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
using QPX1200SPController.Services;

namespace QPX1200SPController.Helpers
{
    /// <summary>
    /// Shows a value popup for the data point nearest the mouse on a ScottPlot WpfPlot, across any number
    /// of series, and highlights the point with a marker. Vertical event markers (commands) show their
    /// text when the mouse is close to them.
    /// </summary>
    public sealed class PlotHover : IDisposable
    {
        private const float SnapPixels = 28f;
        private const float EventSnapPixels = 6f;

        private readonly WpfPlot _plot;
        private readonly Popup _popup;
        private readonly TextBlock _titleText;
        private readonly TextBlock _valueText;
        private readonly TextBlock _timeText;
        private readonly List<HoverSeries> _series = new List<HoverSeries>();
        private readonly List<HoverEvent> _events = new List<HoverEvent>();
        private ScottPlot.Plottables.Marker _marker;
        private Window _hostWindow;
        private Point? _lastMousePos;
        private bool _disposed;

        public PlotHover(WpfPlot plot)
        {
            _plot = plot ?? throw new ArgumentNullException(nameof(plot));

            _titleText = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold };
            _valueText = new TextBlock { FontSize = 14, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 1, 0, 0), MaxWidth = 320, TextWrapping = TextWrapping.Wrap };
            _timeText = new TextBlock { FontSize = 11, Margin = new Thickness(0, 2, 0, 0), Opacity = 0.8 };
            _valueText.SetResourceReference(TextBlock.ForegroundProperty, "MaterialDesign.Brush.Foreground");
            _timeText.SetResourceReference(TextBlock.ForegroundProperty, "MaterialDesign.Brush.Foreground");

            var stack = new StackPanel();
            stack.Children.Add(_titleText);
            stack.Children.Add(_valueText);
            stack.Children.Add(_timeText);

            var border = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6),
                Child = stack,
                IsHitTestVisible = false,
                Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 1, Opacity = 0.28, Color = System.Windows.Media.Colors.Black }
            };
            border.SetResourceReference(Border.BackgroundProperty, "MaterialDesign.Brush.Card.Background");
            border.SetResourceReference(Border.BorderBrushProperty, "MaterialDesign.Brush.Separator.Background");

            _popup = new Popup
            {
                PlacementTarget = _plot,
                Placement = PlacementMode.Relative,
                AllowsTransparency = true,
                StaysOpen = true,
                PopupAnimation = PopupAnimation.None,
                IsHitTestVisible = false,
                Child = border
            };

            _plot.MouseMove += OnMouseMove;
            _plot.MouseLeave += OnMouseLeave;
            _plot.Loaded += OnLoaded;
        }

        /// <summary>Replaces the hoverable series (call after the scatters were re-added to the plot).</summary>
        public void SetSeries(IEnumerable<HoverSeries> series)
        {
            _series.Clear();
            _series.AddRange(series.Where(s => s?.Plottable != null));

            // Keep the highlight marker on top of the freshly added scatters.
            if (_marker != null)
                _plot.Plot.Remove(_marker);
            _marker = _plot.Plot.Add.Marker(0, 0, MarkerShape.FilledCircle, 9, null);
            _marker.IsVisible = false;
            Update(refresh: false);
        }

        /// <summary>Replaces the vertical event markers (time as OLE date, text shown on hover).</summary>
        public void SetEvents(IEnumerable<HoverEvent> events)
        {
            _events.Clear();
            if (events != null)
                _events.AddRange(events);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _plot.MouseMove -= OnMouseMove;
            _plot.MouseLeave -= OnMouseLeave;
            _plot.Loaded -= OnLoaded;
            if (_hostWindow != null)
                _hostWindow.Deactivated -= OnHostDeactivated;
            Hide(refresh: false);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_hostWindow != null)
                return;
            _hostWindow = Window.GetWindow(_plot);
            if (_hostWindow != null)
                _hostWindow.Deactivated += OnHostDeactivated;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            _lastMousePos = e.GetPosition(_plot);
            Update(refresh: true);
        }

        private void OnMouseLeave(object sender, MouseEventArgs e)
        {
            _lastMousePos = null;
            Hide(refresh: true);
        }

        private void OnHostDeactivated(object sender, EventArgs e)
        {
            _lastMousePos = null;
            Hide(refresh: true);
        }

        private void Update(bool refresh)
        {
            if (_disposed || !_lastMousePos.HasValue)
            {
                Hide(refresh);
                return;
            }

            Point p = _lastMousePos.Value;
            float scale = _plot.DisplayScale;
            var mousePixel = new Pixel((float)(p.X * scale), (float)(p.Y * scale));
            var lastRender = _plot.Plot.LastRender;
            if (!lastRender.DataRect.Contains(mousePixel))
            {
                Hide(refresh);
                return;
            }

            Coordinates mouse = _plot.Plot.GetCoordinates(mousePixel);

            // A command marker under the mouse wins over the data points.
            var hit = NearestEvent(mouse, mousePixel, scale);
            if (hit != null)
            {
                ShowEvent(hit, p, refresh);
                return;
            }

            HoverSeries bestSeries = null;
            DataPoint best = DataPoint.None;
            double bestDistance = double.MaxValue;
            foreach (var series in _series)
            {
                if (!series.Plottable.IsVisible)
                    continue;
                DataPoint nearest = series.Plottable.GetNearest(mouse, lastRender, SnapPixels * scale);
                if (!nearest.IsReal)
                    continue;
                Pixel px = _plot.Plot.GetPixel(nearest.Coordinates);
                double distance = Math.Sqrt(Math.Pow(px.X - mousePixel.X, 2) + Math.Pow(px.Y - mousePixel.Y, 2));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = nearest;
                    bestSeries = series;
                }
            }

            if (bestSeries == null)
            {
                Hide(refresh);
                return;
            }

            var inv = CultureInfo.InvariantCulture;
            _titleText.Text = bestSeries.Name;
            _titleText.Foreground = new SolidColorBrush(bestSeries.Accent);
            _valueText.FontSize = 14;
            _valueText.Text = $"{best.Y.ToString(bestSeries.Format, inv)} {bestSeries.Unit}";
            _timeText.Text = DateTime.FromOADate(best.X).ToString("yyyy-MM-dd HH:mm:ss.fff", inv);

            if (_marker != null)
            {
                _marker.Location = best.Coordinates;
                _marker.Color = PlotStyling.ToScottPlot(bestSeries.Accent);
                _marker.IsVisible = true;
                if (refresh)
                    _plot.Refresh();
            }

            Place(p);
        }

        private HoverEvent NearestEvent(Coordinates mouse, Pixel mousePixel, float scale)
        {
            HoverEvent best = null;
            double bestDistance = EventSnapPixels * scale;
            foreach (var ev in _events)
            {
                float x = _plot.Plot.GetPixel(new Coordinates(ev.X, mouse.Y)).X;
                double distance = Math.Abs(x - mousePixel.X);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = ev;
                }
            }
            return best;
        }

        private void ShowEvent(HoverEvent ev, Point p, bool refresh)
        {
            var inv = CultureInfo.InvariantCulture;
            _titleText.Text = ev.Title;
            _titleText.Foreground = new SolidColorBrush(ev.Accent ?? ThemeService.Instance.Palette.CommandMarker);
            _valueText.FontSize = 12;
            _valueText.Text = ev.Text;
            _timeText.Text = DateTime.FromOADate(ev.X).ToString("yyyy-MM-dd HH:mm:ss.fff", inv);
            if (_marker != null && _marker.IsVisible)
            {
                _marker.IsVisible = false;
                if (refresh)
                    _plot.Refresh();
            }
            Place(p);
        }

        private void Place(Point p)
        {
            const double popupWidth = 220;
            const double popupHeight = 70;
            double offsetX = 14;
            double offsetY = 14;
            if (p.X + offsetX + popupWidth > _plot.ActualWidth)
                offsetX = -popupWidth - 8;
            if (p.Y + offsetY + popupHeight > _plot.ActualHeight)
                offsetY = -popupHeight - 8;

            _popup.HorizontalOffset = p.X + offsetX;
            _popup.VerticalOffset = p.Y + offsetY;
            _popup.IsOpen = true;
        }

        private void Hide(bool refresh)
        {
            if (_popup.IsOpen)
                _popup.IsOpen = false;
            if (_marker != null && _marker.IsVisible)
            {
                _marker.IsVisible = false;
                if (refresh)
                    _plot.Refresh();
            }
        }
    }

    /// <summary>One hoverable line: its scatter and how its values are labelled.</summary>
    public sealed class HoverSeries
    {
        public HoverSeries(ScottPlot.Plottables.Scatter plottable, string name, string unit, string format, System.Windows.Media.Color accent)
        {
            Plottable = plottable;
            Name = name;
            Unit = unit;
            Format = format;
            Accent = accent;
        }

        public ScottPlot.Plottables.Scatter Plottable { get; }
        public string Name { get; }
        public string Unit { get; }
        public string Format { get; }
        public System.Windows.Media.Color Accent { get; }
    }

    /// <summary>A vertical event marker (for example a configuration command) at time X (OLE date).</summary>
    public sealed class HoverEvent
    {
        public HoverEvent(double x, string title, string text, System.Windows.Media.Color? accent = null)
        {
            X = x;
            Title = title;
            Text = text;
            Accent = accent;
        }

        public double X { get; }
        public string Title { get; }
        public string Text { get; }

        /// <summary>Title colour; null = the command marker colour.</summary>
        public System.Windows.Media.Color? Accent { get; }
    }
}
