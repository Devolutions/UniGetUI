using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace UniGetUI.Interface.Widgets
{
    public partial class SettingsPageButton : SettingsCard
    {
        private string _text = "";
        public string Text
        {
            set
            {
                _text = CoreTools.Translate(value);
                Header = _text;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, _text);
            }
        }

        private string _underText = "";
        public string UnderText
        {
            set
            {
                _underText = CoreTools.Translate(value);
                Description = _underText;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(this, _underText);
            }
        }

        public IconType Icon
        {
            set => HeaderIcon = new LocalIcon(value);
        }

        public SettingsPageButton()
        {
            CornerRadius = new CornerRadius(8);
            HorizontalAlignment = HorizontalAlignment.Stretch;
            IsClickEnabled = true;

            Loaded += (s, e) =>
            {
                if (!string.IsNullOrEmpty(_text))
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, _text);
                if (!string.IsNullOrEmpty(_underText))
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(this, _underText);
            };
        }
    }
}
