using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using QPX1200SPController.Helpers;
using QPX1200SPController.Models;
using QPX1200SPController.ViewModels;

namespace QPX1200SPController.Views
{
    public partial class HistoryView : UserControl
    {
        private HistoryViewModel _viewModel;
        private HistoryPlotPresenter _presenter;

        public HistoryView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Loaded fires again each time the tab is re-selected; the presenter (and the plots)
            // live as long as the window.
            if (_presenter != null)
                return;

            _viewModel = DataContext as HistoryViewModel;
            if (_viewModel == null)
                return;

            _presenter = new HistoryPlotPresenter(VoltagePlot, CurrentPlot, PowerPlot,
                _viewModel.IsMarkerVisible, HistoryViewModel.IsError);
            _presenter.VisibleRangeChanged += OnVisibleRangeChanged;
            _presenter.CursorChanged += text => _viewModel.CursorText = text;
            _presenter.MarkerClicked += record => _viewModel.SelectEventFromPlot(record);
            _presenter.MarkerNoteChanged += note => _viewModel.MarkerNote = note;
            AutoYCheckBox.Checked += (s, a) => _presenter.AutoScaleY = true;
            AutoYCheckBox.Unchecked += (s, a) => _presenter.AutoScaleY = false;

            _viewModel.DataLoaded += OnDataLoaded;
            _viewModel.MarkersChanged += _presenter.RebuildMarkers;
            _viewModel.CenterOnRequested += _presenter.CenterOn;
            _viewModel.ExportPngRequested += OnExportPng;
            Services.ThemeService.Instance.ThemeChanged += _presenter.ApplyTheme;

            // The tab content is loaded at start-up; load the last hour when the tab is first shown,
            // so "the last hour" means the hour before the user looked.
            IsVisibleChanged += OnFirstShown;
            if (IsVisible)
                OnFirstShown(this, default(DependencyPropertyChangedEventArgs));
        }

        private void OnFirstShown(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsVisible)
                return;
            IsVisibleChanged -= OnFirstShown;
            if (_viewModel.PresetCommand.CanExecute("1 hour"))
                _viewModel.PresetCommand.Execute("1 hour");
        }

        private void OnDataLoaded(HistorySeries series, IReadOnlyList<ActivityRecord> markers, bool resetAxes)
        {
            _presenter?.SetData(series, markers, resetAxes, _viewModel.VisibleFrom, _viewModel.VisibleTo);
        }

        private async void OnVisibleRangeChanged(DateTime from, DateTime to)
        {
            await _viewModel.OnVisibleRangeChangedAsync(from, to);
        }

        private void OnExportPng()
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PNG image (*.png)|*.png",
                DefaultExt = "png",
                FileName = $"PowerSupplyHistory_{_viewModel.VisibleFrom:yyyyMMdd_HHmmss}-{_viewModel.VisibleTo:yyyyMMdd_HHmmss}.png"
            };
            if (dialog.ShowDialog() != true)
                return;

            try
            {
                _presenter.SavePng(PlotsContainer, dialog.FileName);
                _viewModel.ReportPngExported(dialog.FileName, null);
            }
            catch (Exception ex)
            {
                _viewModel.ReportPngExported(dialog.FileName, ex);
            }
        }
    }
}
