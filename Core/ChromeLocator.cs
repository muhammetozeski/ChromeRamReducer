using System.Diagnostics;

namespace ChromeRamReducer.Core;

/// <summary>
/// Finds Chrome on disk and works out which port its DevTools endpoint is listening on.
/// </summary>
public static class ChromeLocator
{
    private static readonly string[] ExecutableCandidates =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Google", "Chrome", "Application", "chrome.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Google", "Chrome", "Application", "chrome.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Google", "Chrome", "Application", "chrome.exe"),
    ];

    public static string DefaultUserDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Google", "Chrome", "User Data");

    public static string? FindExecutable() =>
        ExecutableCandidates.FirstOrDefault(File.Exists);

    public static bool IsChromeRunning() =>
        Process.GetProcessesByName("chrome").Length > 0;

    /// <summary>
    /// Chrome writes the live DevTools port into User Data\DevToolsActivePort whenever the endpoint
    /// is up. The file survives a crash, so the caller must still verify the port answers.
    /// </summary>
    public static int? ReadDevToolsActivePort(string userDataDirectory)
    {
        string path = Path.Combine(userDataDirectory, "DevToolsActivePort");

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new(stream);

            string? firstLine = reader.ReadLine();

            return int.TryParse(firstLine, out int port) && port is > 0 and <= 65535 ? port : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Returns the first port that actually answers as a Chrome DevTools endpoint.</summary>
    public static async Task<int?> DiscoverPortAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        List<int> candidates = [];

        int? activePort = ReadDevToolsActivePort(DefaultUserDataDirectory);
        if (activePort is int active)
        {
            candidates.Add(active);
        }

        if (!candidates.Contains(settings.DebuggingPort))
        {
            candidates.Add(settings.DebuggingPort);
        }

        foreach (int candidate in candidates)
        {
            if (await RespondsAsync(candidate, cancellationToken).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task<bool> RespondsAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using HttpClient http = new() { Timeout = TimeSpan.FromMilliseconds(1200) };
            string body = await http.GetStringAsync($"http://127.0.0.1:{port}/json/version", cancellationToken)
                .ConfigureAwait(false);

            return body.Contains("webSocketDebuggerUrl", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts Chrome with the debugging endpoint enabled. Chrome ignores the flag when another
    /// instance already owns the profile, so the caller must make sure Chrome is closed first.
    /// </summary>
    public static void LaunchWithDebugging(int port)
    {
        string? executable = FindExecutable()
            ?? throw new FileNotFoundException("chrome.exe was not found in any of the usual install locations.");

        ProcessStartInfo startInfo = new(executable)
        {
            UseShellExecute = true,
        };

        startInfo.ArgumentList.Add($"--remote-debugging-port={port}");
        startInfo.ArgumentList.Add("--restore-last-session");

        Process.Start(startInfo);
    }
}
