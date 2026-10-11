using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Postfold.UI;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
