using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace UniGetUI.Interface.Widgets
{
    public sealed partial class TranslatedTextBlock : UserControl
    {
        public string __text = "";
        public string Text
        {
            set => ApplyText(value);
        }

        public string __suffix = "";
        public string Suffix
        {
            set
            {
                __suffix = value;
                ApplyText(null);
            }
        }
        public string __prefix = "";
        public string Prefix
        {
            set
            {
                __prefix = value;
                ApplyText(null);
            }
        }

        public TextWrapping WrappingMode
        {
            set => _textBlock.TextWrapping = value;
        }

        public TranslatedTextBlock()
        {
            InitializeComponent();
            Loaded += (s, e) =>
            {
                if (Parent is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase parentBtn && string.IsNullOrEmpty(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(parentBtn)))
                {
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(parentBtn, _textBlock.Text);
                }
            };
        }

        public void ApplyText(string? text)
        {
            try
            {
                if (text is not null)
                    __text = CoreTools.Translate(text);
                _textBlock.Text = __prefix + __text + __suffix;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, _textBlock.Text);

                if (IsLoaded && Parent is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase parentBtn)
                {
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(parentBtn, _textBlock.Text);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }
    }
}
