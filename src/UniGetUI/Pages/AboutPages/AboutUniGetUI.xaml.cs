using Microsoft.UI.Xaml.Controls;
using UniGetUI.Core.Data;
using UniGetUI.Core.Tools;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace UniGetUI.Interface.Pages.AboutPages
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class AboutUniGetUI : Page
    {
        public AboutUniGetUI()
        {
            InitializeComponent();
            VersionText.Text = CoreTools.Translate(
                "You have installed UniGetUI Version {0}",
                CoreData.VersionName
            );
            DisclaimerBanner.Title = CoreTools.Translate("Disclaimer");
            DisclaimerBanner.Message = CoreTools.Translate(
                "UniGetUI is developed by Devolutions and is not affiliated with any of the compatible package managers."
            );
        }
    }
}
