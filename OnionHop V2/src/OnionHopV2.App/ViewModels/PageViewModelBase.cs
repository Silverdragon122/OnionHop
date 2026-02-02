using Material.Icons;

namespace OnionHopV2.App.ViewModels;

public abstract class PageViewModelBase : ViewModelBase
{
    protected PageViewModelBase(string displayName, MaterialIconKind icon, AppStateViewModel state)
    {
        DisplayName = displayName;
        Icon = icon;
        State = state;
    }

    public string DisplayName { get; }
    public MaterialIconKind Icon { get; }
    public AppStateViewModel State { get; }
}
