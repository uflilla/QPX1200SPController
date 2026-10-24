using System;
using QPX1200SPController.Services;

namespace QPX1200SPController.ViewModels
{
    /// <summary>
    /// Creates the services and view models once (on first use) and gives the views access to them:
    /// DataContext="{Binding Main, Source={StaticResource Locator}}".
    /// </summary>
    public class ViewModelLocator
    {
        private static readonly Lazy<PowerSupplyService> _powerSupply = new Lazy<PowerSupplyService>(CreatePowerSupplyService);
        private static readonly Lazy<DatabaseService> _database = new Lazy<DatabaseService>(() => new DatabaseService());
        private static readonly Lazy<MainViewModel> _main = new Lazy<MainViewModel>(() => new MainViewModel(_powerSupply.Value, _database.Value));
        private static readonly Lazy<DataRetrievalViewModel> _dataRetrieval = new Lazy<DataRetrievalViewModel>(() => new DataRetrievalViewModel(_database.Value));
        private static readonly Lazy<HistoryViewModel> _history = new Lazy<HistoryViewModel>(() => new HistoryViewModel(_database.Value));

        private static readonly Lazy<AlarmsViewModel> _alarms = new Lazy<AlarmsViewModel>(() => new AlarmsViewModel(_main.Value, _powerSupply.Value, _database.Value));
        private static readonly Lazy<ScpiConsoleViewModel> _console = new Lazy<ScpiConsoleViewModel>(() => new ScpiConsoleViewModel(_powerSupply.Value, _main.Value));
        private static readonly Lazy<SequencerViewModel> _sequencer = new Lazy<SequencerViewModel>(() => new SequencerViewModel(_powerSupply.Value, _main.Value));

        public MainViewModel Main => _main.Value;
        public AlarmsViewModel Alarms => _alarms.Value;
        public ScpiConsoleViewModel Console => _console.Value;
        public SequencerViewModel Sequencer => _sequencer.Value;
        public HistoryViewModel History => _history.Value;
        public DataRetrievalViewModel DataRetrieval => _dataRetrieval.Value;

        private static PowerSupplyService CreatePowerSupplyService()
        {
            var service = new PowerSupplyService();
            // Every configuration command, connection event and communication error goes to the CommandLog table.
            service.ActivityRecorded += _database.Value.EnqueueActivity;
            return service;
        }
    }
}
