using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace QPX1200SPController
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // The ViewModelLocator is already set up in App.xaml resources
            // and MainWindow will use it through DataContext binding
            MainWindow mainWindow = new MainWindow();
            mainWindow.Show();
        }
    }
}
