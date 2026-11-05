using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Demenageur.Windows.AppData;

namespace Demenageur.App;

/// <summary>
/// Version aperçu uniquement : dessine une fenêtre HORS ÉCRAN dans une image PNG (aucune capture de l'écran,
/// aucune fenêtre au premier plan), pour vérifier l'interface pendant le développement.
/// </summary>
internal static class PreviewCapture
{
    public static void PrepareOffscreen(Window window)
    {
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
    }

    public static async Task CaptureAsync(Window window, string path, int delayMs)
    {
        await Task.Delay(delayMs);
        var root = (FrameworkElement)window.Content;
        if (root is System.Windows.Controls.Panel panel)
        {
            panel.Background = (Brush)Application.Current.FindResource("ApplicationBackgroundBrush");
        }

        root.UpdateLayout();
        var width = (int)Math.Ceiling(root.ActualWidth);
        var height = (int)Math.Ceiling(root.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var memory = new MemoryStream();
        encoder.Save(memory);
        PreviewFiles.Save(path, memory.ToArray());
    }
}
