using System.Windows;
using Wpf.Ui.Controls;

namespace Demenageur.App.Dialogs;

/// <summary>Avertissement du premier lancement : il faut cocher « J'ai compris » pour continuer.</summary>
public partial class FirstRunWindow : FluentWindow
{
    public FirstRunWindow()
    {
        InitializeComponent();
    }

    /// <summary>L'utilisateur a demandé la démo depuis cet écran.</summary>
    public bool DemoRequested { get; private set; }

    private void OnUnderstoodChanged(object sender, RoutedEventArgs e) => ContinueButton.IsEnabled = Understood.IsChecked == true;

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void OnQuit(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnDemo(object sender, RoutedEventArgs e)
    {
        if (Understood.IsChecked != true)
        {
            System.Windows.MessageBox.Show(this, "Coche d'abord « J'ai lu et compris ces informations ».", "Déménageur");
            return;
        }

        DemoRequested = true;
        DialogResult = true;
    }
}
