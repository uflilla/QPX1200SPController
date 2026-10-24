/*
  In App.xaml:
  <Application.Resources>
      <vm:ViewModelLocator xmlns:vm="clr-namespace:QPX1200SPController"
                           x:Key="Locator" />
  </Application.Resources>
  
  In the View:
  DataContext="{Binding Source={StaticResource Locator}, Path=ViewModelName}"

  You can also use Blend to do all this with the tool's support.
  See http://www.galasoft.ch/mvvm
*/

using CommonServiceLocator;
using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Ioc;
using QPX1200SPController.Services;

namespace QPX1200SPController.ViewModels
{
    /// <summary>
    /// This class contains static references to all the view models in the
    /// application and provides an entry point for the bindings.
    /// </summary>
    public class ViewModelLocator
    {
        /// <summary>
        /// Initializes a new instance of the ViewModelLocator class.
        /// </summary>
        public ViewModelLocator()
        {
            ServiceLocator.SetLocatorProvider(() => SimpleIoc.Default);

            // Register services
            SimpleIoc.Default.Register<PowerSupplyService>();
            SimpleIoc.Default.Register<DatabaseService>();

            // Every configuration command, connection event and communication error goes to the CommandLog table.
            SimpleIoc.Default.GetInstance<PowerSupplyService>().ActivityRecorded +=
                SimpleIoc.Default.GetInstance<DatabaseService>().EnqueueActivity;

            // Register view models
            SimpleIoc.Default.Register<MainViewModel>();
            SimpleIoc.Default.Register<SettingsViewModel>();
            //SimpleIoc.Default.Register<LogViewModel>();
            SimpleIoc.Default.Register<DataRetrievalViewModel>();
        }

        public MainViewModel Main
        {
            get
            {
                return ServiceLocator.Current.GetInstance<MainViewModel>();
            }
        }

        public SettingsViewModel Settings
        {
            get
            {
                return ServiceLocator.Current.GetInstance<SettingsViewModel>();
            }
        }

        //public LogViewModel Log
        //{
        //    get
        //    {
        //        return ServiceLocator.Current.GetInstance<LogViewModel>();
        //    }
        //}

        public DataRetrievalViewModel DataRetrieval
        {
            get
            {
                return ServiceLocator.Current.GetInstance<DataRetrievalViewModel>();
            }
        }
        
        public static void Cleanup()
        {
            // TODO Clear the ViewModels
        }
    }
}