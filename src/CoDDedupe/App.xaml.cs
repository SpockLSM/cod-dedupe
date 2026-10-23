using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CoDDedupe;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow();
        window.Show();

        // Hidden helper for documentation: CoDDedupe.exe --screenshot out.png
        int i = Array.IndexOf(e.Args, "--screenshot");
        if (i >= 0 && i + 1 < e.Args.Length)
        {
            string target = e.Args[i + 1];
            window.ContentRendered += async (_, _) =>
            {
                await Task.Delay(15000);   // let the install scan finish so the numbers show
                SaveScreenshot(window, target);
                Shutdown();
            };
        }
    }

    static void SaveScreenshot(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(root);
        double w = root.ActualWidth + root.Margin.Left + root.Margin.Right;
        double h = root.ActualHeight + root.Margin.Top + root.Margin.Bottom;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(root.Margin.Left, root.Margin.Top, root.ActualWidth, root.ActualHeight));
        }
        var bmp = new RenderTargetBitmap((int)(w * dpi.DpiScaleX), (int)(h * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
