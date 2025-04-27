using System.Windows.Controls;
using QPX1200SPController.ViewModels;

namespace QPX1200SPController.Views
{
    public partial class AppLogsView : UserControl
    {
        public AppLogsView()
        {
            InitializeComponent();
            DataContext = new AppLogsViewModel();
        }
    }
}