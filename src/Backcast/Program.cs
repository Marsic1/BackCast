using System.Runtime.InteropServices;

namespace Backcast;

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SwRestore = 9;

    /// <summary>
    /// Single-instance via a named mutex; a second launch focuses the first
    /// instance's window instead of starting a new player.
    /// </summary>
    [STAThread]
    private static void Main()
    {
        bool createdNew;
        using var mutex = new Mutex(true, @"Local\Backcast.SingleInstance", out createdNew);
        if (!createdNew)
        {
            FocusExistingInstance();
            return;
        }

        // UI-thread and background crashes land in the diagnostic log with a
        // stack instead of a bare "Microsoft .NET" dialog.
        System.Windows.Forms.Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        System.Windows.Forms.Application.ThreadException += (_, e) =>
        {
            Log.Write($"UI crash: {e.Exception}");
            ErrorDialog(e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Write($"background crash: {e.ExceptionObject}");
        };

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(AppSettings.Load()));
        GC.KeepAlive(mutex);
    }

    private static void ErrorDialog(Exception ex)
    {
        // surface the detail — a bare "object reference" teaches nobody anything
        _ = MessageBox.Show(
            $"Something went wrong:\n\n{ex.GetType().Name}: {ex.Message}\n\nDetails were written to %TEMP%\\Backcast.log",
            "BackCast", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static void FocusExistingInstance()
    {
        // The existing window is found by its state-aware title prefix.
        foreach (var proc in System.Diagnostics.Process.GetProcessesByName("Backcast"))
        {
            if (proc.MainWindowHandle != IntPtr.Zero)
            {
                ShowWindow(proc.MainWindowHandle, SwRestore);
                SetForegroundWindow(proc.MainWindowHandle);
                return;
            }
        }
    }
}
