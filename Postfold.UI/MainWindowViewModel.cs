using CommunityToolkit.Mvvm.ComponentModel;

namespace Postfold.UI;

public sealed class MainWindowViewModel : ObservableObject
{
    private string _title = "Postfold";

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }
}
