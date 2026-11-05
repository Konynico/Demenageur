using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Demenageur.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        // Suit automatiquement le thème clair/sombre de Windows.
        SystemThemeWatcher.Watch(this);
    }
}
