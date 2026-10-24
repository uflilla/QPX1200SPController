namespace QPX1200SPController.ViewModels
{
    public enum StatusFlagKind
    {
        /// <summary>Normal regulation mode (CV): green when active.</summary>
        Good,
        /// <summary>Worth noticing (CC, unregulated): amber when active.</summary>
        Notice,
        /// <summary>A protection trip: red when active.</summary>
        Trip
    }

    /// <summary>One indicator of the supply's status register (CV, CC, OVP trip, ...).</summary>
    public sealed class StatusFlag : ViewModelBase
    {
        private bool _isActive;

        public StatusFlag(string label, StatusFlagKind kind, string description)
        {
            Label = label;
            Kind = kind;
            Description = description;
        }

        public string Label { get; }
        public StatusFlagKind Kind { get; }
        public string Description { get; }

        public bool IsActive
        {
            get => _isActive;
            set => SetProperty(ref _isActive, value);
        }
    }
}
