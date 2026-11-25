using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace AorusPanel;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--upload", var path])
        {
            try
            {
                Upload(PanelMedia.ReadStaged(path), null);
                return 0;
            }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, "AORUS Panel upload", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }

        if (args is ["--status"])
        {
            try
            {
                PanelState state = WithPanel(panel => panel.Status());
                Console.WriteLine($"Firmware {state.Firmware} | {(state.On ? "On" : "Off")} | {state.Mode} | Metrics 0x{state.Metrics:X2} | Interval {state.Interval}");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }

        if (args.Length != 0)
            return 2;
        using var instance = new Mutex(true, @"LocalAorusPanel.Window", out bool first);
        if (!first)
        {
            MessageBox.Show("AORUS Panel is already open.", "AORUS Panel");
            return 0;
        }

        var app = new Application();
        return app.Run(new MainWindow());
    }

    internal static T WithPanel<T>(Func<Panel, T> action) =>
        Exclusive(() =>
        {
            using var panel = new Panel();
            return action(panel);
        });

    private static T Exclusive<T>(Func<T> action)
    {
        using var bus = new Mutex(false, @"LocalAorusPanel.Display");
        try
        {
            if (!bus.WaitOne(TimeSpan.FromSeconds(2)))
                throw new IOException("Another AORUS Panel operation is in progress.");
        }
        catch (AbandonedMutexException) { }

        try { return action(); }
        finally { bus.ReleaseMutex(); }
    }

    private static bool Elevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static async Task<PanelState> SendMedia(PanelMedia media, Action<double> progress, Action waitingForAdmin)
    {
        bool direct = await Task.Run(() =>
        {
            using var service = new LcdService(allowChanges: false);
            return !service.NeedsPause || Elevated();
        });

        if (direct)
            return await Task.Run(() => Upload(media, progress));

        waitingForAdmin();
        string directory = Path.Combine(Path.GetTempPath(), "AorusPanel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "media.bin");
        try
        {
            media.Stage(file);
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            start.ArgumentList.Add("--upload");
            start.ArgumentList.Add(file);
            using var worker = Process.Start(start) ?? throw new IOException("Windows could not start the uploader.");
            await worker.WaitForExitAsync();
            if (worker.ExitCode != 0)
                throw new IOException("The upload did not complete. Check the uploader’s error message.");
            return await Task.Run(() => WithPanel(panel => panel.Status()));
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            throw new IOException("The upload was canceled at the Windows approval prompt.");
        }
        finally
        {
            File.Delete(file);
            Directory.Delete(directory);
        }
    }

    private static PanelState Upload(PanelMedia media, Action<double>? progress) => Exclusive(() =>
    {
        using var panel = new Panel();
        bool elevated = Elevated();
        using var service = new LcdService(allowChanges: elevated);
        if (!elevated && service.NeedsPause)
            throw new IOException("The AORUS LCD service started. Retry the upload to pause it first.");

        return service.WhileStopped(() => panel.Upload(media, progress));
    });
}
