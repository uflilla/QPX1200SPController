namespace QPX1200SPController.ViewModels
{
    /// <summary>
    /// Y axis of one live plot. While following live data: Auto fits from 0 to the highest value
    /// shown; Signal fits the measured values only (to see ripple or drift) with a minimum span and
    /// never below 0 for non-negative readings; Manual keeps YMin to YMax.
    /// </summary>
    public sealed class PlotYAxisSettings : ViewModelBase
    {
        public const string AutoMode = "Auto";
        public const string SignalMode = "Signal";
        public const string ManualMode = "Manual";

        private string _mode = AutoMode;
        private double _yMin;
        private double _yMax;

        public PlotYAxisSettings(double defaultMin, double defaultMax)
        {
            _yMin = defaultMin;
            _yMax = defaultMax;
        }

        public string Mode
        {
            get => _mode;
            set
            {
                if (SetProperty(ref _mode, value ?? AutoMode))
                {
                    RaisePropertyChanged(nameof(IsManual));
                    RaisePropertyChanged(nameof(IsSignal));
                }
            }
        }

        public bool IsManual => Mode == ManualMode;
        public bool IsSignal => Mode == SignalMode;

        public double YMin
        {
            get => _yMin;
            set => SetProperty(ref _yMin, value);
        }

        public double YMax
        {
            get => _yMax;
            set => SetProperty(ref _yMax, value);
        }
    }
}
