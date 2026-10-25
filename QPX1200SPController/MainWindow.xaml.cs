using System;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using QPX1200SPController.Helpers;
using QPX1200SPController.Services;
using QPX1200SPController.ViewModels;

namespace QPX1200SPController
{
    public partial class MainWindow : Window
    {
        private const double DefaultControlWidth = 310;

        private MainViewModel _viewModel;
        private LivePlotPresenter _voltagePresenter;
        private LivePlotPresenter _currentPresenter;
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
                // Minimum Signal spans: about ten steps of the supply's resolution.
                _voltagePresenter = new LivePlotPresenter(VoltagePlot, "Voltage (V)", "Voltage", "V", "F3", 0.01,
                    s => s.Voltages, p => p.Voltage, _viewModel.VoltageYAxis);
                _currentPresenter = new LivePlotPresenter(CurrentPlot, "Current (A)", "Current", "A", "F3", 0.1,
                    s => s.Currents, p => p.Current, _viewModel.CurrentYAxis);


                // Zooming or panning one plot moves the time axis of the other.
                VoltagePlot.Plot.Axes.Link(CurrentPlot, x: true, y: false);
                CurrentPlot.Plot.Axes.Link(VoltagePlot, x: true, y: false);

                foreach (var presenter in Presenters)
                    presenter.UserNavigated += OnPlotNavigated;

                _viewModel.GraphDataUpdated += OnGraphDataUpdated;
                _viewModel.GraphYAxisChanged += OnGraphYAxisChanged;
                ThemeService.Instance.ThemeChanged += OnThemeChanged;

                // Create the alarm monitor now (it watches the readings even if its panel is never opened).
                var locator = (ViewModelLocator)FindResource("Locator");
                locator.Alarms.AlarmRaised += FlashTaskbarButton;
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

                ControlColumn.MinWidth = 260;
                ControlColumn.Width = width;
                ControlSplitterColumn.Width = new GridLength(6);
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

        /// <summary>Soft limits and Alarms open one at a time, so the control panel fits without scrolling.</summary>
        private void OnPanelExpanderExpanded(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource != sender)
                return;
            if (sender == SoftLimitsExpander)
                AlarmsExpander.IsExpanded = false;
            else if (sender == AlarmsExpander)
                SoftLimitsExpander.IsExpanded = false;
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

        private LivePlotPresenter[] Presenters => new[] { _voltagePresenter, _currentPresenter };

        private void RefreshGraphs()
        {
            if (_viewModel == null || _voltagePresenter == null)
                return;

            bool follow = _viewModel.IsFollowingLive;
            var window = _viewModel.GraphWindow;
            bool markers = _viewModel.ShowLiveMarkers;
            foreach (var presenter in Presenters)
                presenter.Update(_viewModel.LiveBuffer, follow, window, markers);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FlashInfo
        {
            public uint Size;
            public IntPtr Window;
            public uint Flags;
            public uint Count;
            public uint Timeout;
        }

        [DllImport("user32.dll")]
        private static extern bool FlashWindowEx(ref FlashInfo info);

        /// <summary>Flashes the taskbar button until the window is activated (only when it is in the background).</summary>
        private void FlashTaskbarButton()
        {
            if (IsActive)
                return;
            const uint FlashAll = 3, FlashUntilForeground = 12;
            var info = new FlashInfo
            {
                Size = (uint)Marshal.SizeOf(typeof(FlashInfo)),
                Window = new WindowInteropHelper(this).Handle,
                Flags = FlashAll | FlashUntilForeground
            };
            FlashWindowEx(ref info);
        }

        private void OnPlotNavigated()
        {
            // The user is looking at something: stop scrolling the view away from under them.
            if (_viewModel != null)
                _viewModel.IsFollowingLive = false;
        }

        private void OnGraphYAxisChanged()
        {
            if (_voltagePresenter == null)
                return;
            foreach (var presenter in Presenters)
                presenter.ApplyYAxis();
        }

        private void OnThemeChanged()
        {
            if (_voltagePresenter == null)
                return;
            foreach (var presenter in Presenters)
                presenter.ApplyTheme();
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
                _viewModel.GraphYAxisChanged -= OnGraphYAxisChanged;
                _viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
            }
            ThemeService.Instance.ThemeChanged -= OnThemeChanged;
            _voltagePresenter?.Dispose();
            _currentPresenter?.Dispose();
        }

        private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            Title = $"QPX1200SP Monitoring & Control v{version.ToString(3)}";
        }
    }
}
