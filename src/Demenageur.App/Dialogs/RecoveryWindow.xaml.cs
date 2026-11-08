using System.ComponentModel;
using Wpf.Ui.Controls;

namespace Demenageur.App.Dialogs;

/// <summary>
/// Affichée au démarrage pendant la reprise d'une opération interrompue (crash, coupure de courant).
/// Ne peut pas être fermée par l'utilisateur : la reprise continue de toute façon et doit aller au bout.
/// </summary>
public partial class RecoveryWindow : FluentWindow
{
    private bool _finished;

    public RecoveryWindow()
    {
        InitializeComponent();
    }

    public void ShowSoftware(string softwareName)
        => SoftwareText.Text = $"« {softwareName} » : Déménageur termine ou annule l'opération en cours au moment de l'arrêt. Ne ferme pas l'ordinateur.";

    public void Finish()
    {
        _finished = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = !_finished;
        base.OnClosing(e);
    }
}
