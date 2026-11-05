using System.Windows;
using Demenageur.App.ViewModels;
using Demenageur.Core.Safety;
using Wpf.Ui.Controls;

namespace Demenageur.App.Dialogs;

/// <summary>Choix des deux disques de la démo (disques internes NTFS uniquement).</summary>
public partial class DemoWindow : FluentWindow
{
    public DemoWindow(IReadOnlyList<DriveViewModel> drives)
    {
        InitializeComponent();
        var eligible = drives
            .Where(d => d.Details.Volume.IsNtfs && d.Details.BusType is DiskBusType.Internal or DiskBusType.Unknown && d.Details.Volume.DriveType == Core.FileSystem.VolumeDriveType.Fixed)
            .Select(d => new DriveChoice(d))
            .ToList();
        SourceDrive.ItemsSource = eligible;
        DestinationDrive.ItemsSource = eligible;
        SourceDrive.SelectedItem = eligible.FirstOrDefault(d => !d.Drive.Details.IsSystemVolume) ?? eligible.FirstOrDefault();
        var source = SourceDrive.SelectedItem as DriveChoice;
        DestinationDrive.SelectedItem = eligible.OrderByDescending(d => d.Drive.Free).FirstOrDefault(d => d != source);
    }

    public DriveViewModel? Source => (SourceDrive.SelectedItem as DriveChoice)?.Drive;

    public DriveViewModel? Destination => (DestinationDrive.SelectedItem as DriveChoice)?.Drive;

    private void OnStart(object sender, RoutedEventArgs e)
    {
        if (Source is null || Destination is null || Source.Letter == Destination.Letter)
        {
            ErrorText.Text = "Choisis deux disques différents (internes, en NTFS).";
            return;
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed record DriveChoice(DriveViewModel Drive)
    {
        public string Display => $"{Drive.Letter} {Drive.Label} — {Drive.FreeText}";
    }
}
