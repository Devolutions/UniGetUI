using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.Interface.Widgets
{
    public partial class PackageItemContainer : ItemContainer
    {
        public IPackage? Package { get; set; }

        private PackageWrapper _wrapper = null!;
        public PackageWrapper Wrapper
        {
            get => _wrapper;
            set
            {
                if (_wrapper != null)
                {
                    _wrapper.PropertyChanged -= Wrapper_PropertyChanged;
                }
                _wrapper = value;
                if (_wrapper != null)
                {
                    _wrapper.PropertyChanged += Wrapper_PropertyChanged;
                }
            }
        }

        private void Wrapper_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PackageWrapper.IsChecked))
            {
                var peer = FrameworkElementAutomationPeer.FromElement(this) as PackageItemContainerAutomationPeer;
                if (peer != null)
                {
                    ToggleState oldState = !Wrapper.IsChecked ? ToggleState.On : ToggleState.Off;
                    ToggleState newState = Wrapper.IsChecked ? ToggleState.On : ToggleState.Off;
                    peer.RaiseToggleStatePropertyChanged(oldState, newState);
                }
            }
        }

        protected override AutomationPeer OnCreateAutomationPeer()
        {
            return new PackageItemContainerAutomationPeer(this);
        }
    }

    public partial class PackageItemContainerAutomationPeer : FrameworkElementAutomationPeer, IToggleProvider
    {
        private readonly PackageItemContainer _owner;

        public PackageItemContainerAutomationPeer(PackageItemContainer owner) : base(owner)
        {
            _owner = owner;
        }

        protected override AutomationControlType GetAutomationControlTypeCore()
        {
            return AutomationControlType.CheckBox;
        }

        protected override object GetPatternCore(PatternInterface patternInterface)
        {
            if (patternInterface == PatternInterface.Toggle)
            {
                return this;
            }
            return base.GetPatternCore(patternInterface);
        }

        public ToggleState ToggleState => (_owner.Wrapper != null && _owner.Wrapper.IsChecked) ? ToggleState.On : ToggleState.Off;

        public void Toggle()
        {
            _owner.Wrapper?.IsChecked = !_owner.Wrapper.IsChecked;
        }

        public void RaiseToggleStatePropertyChanged(ToggleState oldValue, ToggleState newValue)
        {
            RaisePropertyChangedEvent(TogglePatternIdentifiers.ToggleStateProperty, oldValue, newValue);
        }
    }
}
