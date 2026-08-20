namespace ChromeRamReducer;

internal static class Program
{
    private static Mutex? _singleInstance;

    [STAThread]
    private static void Main()
    {
        _singleInstance = new Mutex(initiallyOwned: true, "ChromeRamReducer.SingleInstance", out bool isFirst);

        if (!isFirst)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());

        _singleInstance.Dispose();
    }
}
