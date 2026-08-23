using System;
using System.Collections.Generic;
using System.Linq;
using ScottPlot;
using ScottPlot.WPF;
using QPX1200SPController.Models;

namespace QPX1200SPController.Helpers
{
    /// <summary>
    /// Updates a ScottPlot 5 WpfPlot from in-memory telemetry samples.
    /// </summary>
    public sealed class LivePlotPresenter
    {
        private readonly WpfPlot _plot;
        private readonly string _title;
        private readonly string _yLabel;
        private readonly Func<TelemetrySample, double> _valueSelector;
        private readonly Color _lineColor;

        public LivePlotPresenter(
            WpfPlot plot,
            string title,
            string yLabel,
            Func<TelemetrySample, double> valueSelector,
            Color lineColor)
        {
            _plot = plot ?? throw new ArgumentNullException(nameof(plot));
            _title = title;
            _yLabel = yLabel;
            _valueSelector = valueSelector;
            _lineColor = lineColor;
            ConfigureAxes();
        }

        public void Update(IReadOnlyList<TelemetrySample> samples)
        {
            _plot.Plot.Clear();
            ConfigureAxes();

            if (samples == null || samples.Count == 0)
            {
                _plot.Refresh();
                return;
            }

            double[] xs = samples.Select(s => s.Timestamp.ToOADate()).ToArray();
            double[] ys = samples.Select(_valueSelector).ToArray();

            var scatter = _plot.Plot.Add.Scatter(xs, ys);
            scatter.Color = _lineColor;
            scatter.LineWidth = 1.5f;
            scatter.MarkerSize = 0;

            _plot.Plot.Axes.AutoScale();
            _plot.Refresh();
        }

        private void ConfigureAxes()
        {
            _plot.Plot.FigureBackground.Color = Color.FromHex("#FAFAFA");
            _plot.Plot.DataBackground.Color = Color.FromHex("#FFFFFF");
            _plot.Plot.Title(_title);
            _plot.Plot.Axes.Left.Label.Text = _yLabel;
            _plot.Plot.Axes.Bottom.Label.Text = "Time";
            _plot.Plot.Axes.DateTimeTicksBottom();
        }
    }
}
