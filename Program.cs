using System.Reflection;

namespace ChromeRamReducer;

internal static class Program
{
    private const string MutexName = "ChromeRamReducer.SingleInstance";
    private const string ShowWindowEventName = "ChromeRamReducer.ShowWindow";

    private static Mutex? _singleInstance;

    [STAThread]
    private static void Main()
    {
        InstallExceptionHandlers();

        Log($"Starting Chrome RAM Reducer {Assembly.GetExecutingAssembly().GetName().Version} "
            + $"on {Environment.OSVersion} (.NET {Environment.Version}, "
            + $"{(Environment.Is64BitProcess ? "x64" : "x86")}).\n"
            + $"Executable: {Application.ExecutablePath}\n"
            + $"Log file: {Logger.LogFileName}", LogLevel.Info);

        _singleInstance = new Mutex(initiallyOwned: true, MutexName, out bool isFirst);

        if (!isFirst)
        {
            // A second launch must bring the running copy forward rather than exit in silence,
            // which looks exactly like the program doing nothing at all.
            Log("Another instance is already running; asking it to show its window.", LogLevel.Warning);

            try
            {
                using EventWaitHandle handle = EventWaitHandle.OpenExisting(ShowWindowEventName);
                handle.Set();
                Log("Show-window signal delivered to the running instance.", LogLevel.Info);
            }
            catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
            {
                Log($"The running instance did not expose its show-window signal: {ex.Message}", LogLevel.Error);
            }

            return;
        }

        ApplicationConfiguration.Initialize();

        MainForm form = new();
        StartShowWindowListener(form);

        Application.Run(form);

        Log("Message loop ended; shutting down.", LogLevel.Info);
        _singleInstance.Dispose();
    }

    /// <summary>Listens for a second launch and restores the window when one happens.</summary>
    private static void StartShowWindowListener(MainForm form)
    {
        EventWaitHandle handle = new(false, EventResetMode.AutoReset, ShowWindowEventName);

        new Thread(() =>
        {
            while (true)
            {
                handle.WaitOne();
                Log("Show-window signal received from another launch.", LogLevel.Info);

                try
                {
                    form.BeginInvoke(form.ShowFromAnotherInstance);
                }
                catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "ShowWindowListener",
        }.Start();
    }

    private static void InstallExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log($"UNHANDLED EXCEPTION (terminating: {e.IsTerminating}):\n{e.ExceptionObject}",
                LogLevel.Error, WaitForLogging: true);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log($"UNOBSERVED TASK EXCEPTION:\n{e.Exception}", LogLevel.Error);
            e.SetObserved();
        };

        Application.ThreadException += (_, e) =>
        {
            Log($"UI THREAD EXCEPTION:\n{e.Exception}", LogLevel.Error, WaitForLogging: true);

            MessageBox.Show(
                $"{e.Exception.Message}\n\nThe full trace is in:\n{Logger.LogFileName}",
                "Chrome RAM Reducer",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        };

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
    }
}
