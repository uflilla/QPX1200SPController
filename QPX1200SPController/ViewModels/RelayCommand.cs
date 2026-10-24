using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace QPX1200SPController.ViewModels
{
    public class RelayCommand : ICommand
    {
        private readonly Func<object, Task> _executeAsync;
        private readonly Predicate<object> _canExecute;
        private bool _isExecuting;

        public RelayCommand(Action<object> execute, Predicate<object> canExecute = null)
        {
            _executeAsync = parameter =>
            {
                execute(parameter);
                return Task.CompletedTask;
            };
            _canExecute = canExecute;
        }

        public RelayCommand(Func<object, Task> executeAsync, Predicate<object> canExecute = null)
        {
            _executeAsync = executeAsync;
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter)
        {
            return !_isExecuting && (_canExecute == null || _canExecute(parameter));
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public async void Execute(object parameter)
        {
            _isExecuting = true;
            CommandManager.InvalidateRequerySuggested();

            try
            {
                await _executeAsync(parameter);
            }
            catch (Exception ex)
            {
                // async void: an escaping exception would otherwise reach the dispatcher.
                Services.LoggingService.Instance.LogException(ex, nameof(RelayCommand), nameof(Execute));
            }
            finally
            {
                _isExecuting = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
}