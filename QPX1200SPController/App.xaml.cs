using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using QPX1200SPController.Services;

namespace QPX1200SPController
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            base.OnStartup(e);

            LoggingService.Instance.LogInfo(
                $"Application started (v{Assembly.GetExecutingAssembly().GetName().Version}).",
                nameof(App), nameof(OnStartup));

            ThemeService.Instance.Apply(Models.Settings.Current.DarkTheme);

            // The ViewModelLocator is already set up in App.xaml resources
            // and MainWindow will use it through DataContext binding
            MainWindow mainWindow = new MainWindow();
            mainWindow.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            TraceService.Instance.Disable();
            LoggingService.Instance.LogInfo("Application closed.", nameof(App), nameof(OnExit));
            base.OnExit(e);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LoggingService.Instance.LogException(e.Exception, nameof(App), "UnhandledUiException");
            // Keep the application (and the connection to the supply) alive where possible.
            e.Handled = true;
            MessageBox.Show(
                "An unexpected error occurred and was logged:\n\n" + e.Exception.Message +
                "\n\nThe application will keep running. See App Logs for details.",
                "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
                LoggingService.Instance.LogException(ex, nameof(App), "UnhandledException");
        }

        private static void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            LoggingService.Instance.LogException(e.Exception, nameof(App), "UnobservedTaskException");
            e.SetObserved();
        }
    }
}
