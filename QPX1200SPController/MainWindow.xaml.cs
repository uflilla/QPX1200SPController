using System;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using QPX1200SPController.Helpers;
using QPX1200SPController.ViewModels;
using ScottPlot;

namespace QPX1200SPController
{
    public partial class MainWindow : Window
    {
        private const double DefaultControlWidth = 300;

        private MainViewModel _viewModel;
        private LivePlotPresenter _voltagePresenter;
        private LivePlotPresenter _currentPresenter;
        private LivePlotPresenter _powerPresenter;
        private GridLength _savedControlWidth = new GridLength(DefaultControlWidth);
        private bool _closeConfirmed;
        private bool _closeInProgress;

        public MainWindow()
        {
            // Subscribe before InitializeComponent so the XAML DataContext is captured.
            DataContextChanged += MainWindow_DataContextChanged;
            InitializeComponent();
            Closing += MainWindow_Closing;
            Loaded += OnWindowLoaded;
        }

        private void MainWindow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            AttachViewModel(e.NewValue as MainViewModel);
        }

        private void AttachViewModel(MainViewModel viewModel)
        {
            if (_viewModel != null)
                _viewModel.PropertyChanged -= ViewModelOnPropertyChanged;

            _viewModel = viewModel;
            if (_viewModel == null)
                return;

            _viewModel.PropertyChanged += ViewModelOnPropertyChanged;

            // Controls are not ready yet if DataContextChanged fires mid-InitializeComponent.
            if (ControlColumn != null)
                ApplyControlPanelVisibility(_viewModel.IsControlPanelVisible);
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            // Ensure we are attached even if DataContextChanged was missed.
            if (_viewModel == null)
                AttachViewModel(DataContext as MainViewModel);

            if (_viewModel == null)
                return;

            if (_voltagePresenter == null)
            {
                _voltagePresenter = new LivePlotPresenter(
                    VoltagePlot, "Voltage", "V", "V", "F3",
                    s => s.Voltage, Colors.OrangeRed, _viewModel.VoltageYAxis);
                _currentPresenter = new LivePlotPresenter(
                    CurrentPlot, "Current", "A", "A", "F3",
                    s => s.Current, Colors.DodgerBlue, _viewModel.CurrentYAxis);
                _powerPresenter = new LivePlotPresenter(
                    PowerPlot, "Power", "W", "W", "F2",
                    s => s.Power, Colors.ForestGreen, _viewModel.PowerYAxis);

                _viewModel.GraphDataUpdated += OnGraphDataUpdated;
            }

            ApplyControlPanelVisibility(_viewModel.IsControlPanelVisible);
            RefreshGraphs();
        }

        private void ViewModelOnPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.IsControlPanelVisible))
                ApplyControlPanelVisibility(_viewModel.IsControlPanelVisible);
        }

        /// <summary>
        /// Only shows/hides the control column (and its splitter).
        /// Does not reset digital/graph widths so GridSplitter adjustments are kept.
        /// </summary>
        private void ApplyControlPanelVisibility(bool visible)
        {
            if (ControlColumn == null || ControlSplitterColumn == null
                || ControlSplitter == null || ControlPanelBorder == null)
                return;

            if (visible)
            {
                var width = _savedControlWidth.IsAbsolute && _savedControlWidth.Value > 0
                    ? _savedControlWidth
                    : new GridLength(DefaultControlWidth);

                ControlColumn.MinWidth = 180;
                ControlColumn.Width = width;
                ControlSplitterColumn.Width = new GridLength(5);
                ControlSplitter.Visibility = Visibility.Visible;
                ControlPanelBorder.Visibility = Visibility.Visible;
            }
            else
            {
                if (ControlColumn.Width.IsAbsolute && ControlColumn.Width.Value > 0)
                    _savedControlWidth = ControlColumn.Width;

                ControlColumn.MinWidth = 0;
                ControlColumn.Width = new GridLength(0);
                ControlSplitterColumn.Width = new GridLength(0);
                ControlSplitter.Visibility = Visibility.Collapsed;
                ControlPanelBorder.Visibility = Visibility.Collapsed;
            }
        }

        private void OnGraphDataUpdated()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RefreshGraphs));
                return;
            }
            RefreshGraphs();
        }

        private void RefreshGraphs()
        {
            if (_viewModel == null || _voltagePresenter == null)
                return;

            var samples = _viewModel.GetGraphSamples();
            _voltagePresenter.Update(samples);
            _currentPresenter.Update(samples);
            _powerPresenter.Update(samples);
        }

        private async void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (_closeConfirmed || _viewModel == null)
            {
                ReleasePlots();
                return;
            }

            // Closing is synchronous, so cancel it, run the async exit sequence, then close again.
            e.Cancel = true;
            if (_closeInProgress)
                return;

            _closeInProgress = true;
            try
            {
                if (!await _viewModel.PrepareShutdownAsync(this))
                    return;
            }
            catch (Exception ex)
            {
                Services.LoggingService.Instance.LogException(ex, nameof(MainWindow), nameof(MainWindow_Closing));
            }
            finally
            {
                _closeInProgress = false;
            }

            _closeConfirmed = true;
            // Close() cannot be called from inside a Closing handler, so queue it.
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }

        private void ReleasePlots()
        {
            if (_viewModel != null)
            {
                _viewModel.GraphDataUpdated -= OnGraphDataUpdated;
                _viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
            }
            _voltagePresenter?.Dispose();
            _currentPresenter?.Dispose();
            _powerPresenter?.Dispose();
        }

        private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            Title = $"QPX1200SP Monitoring & Control v{version}";
        }
    }
}
