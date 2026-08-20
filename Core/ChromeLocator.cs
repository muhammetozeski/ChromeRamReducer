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

    public static string? FindExecutable()
    {
        foreach (string candidate in ExecutableCandidates)
        {
            if (File.Exists(candidate))
            {
                Log($"chrome.exe found at: {candidate}", LogLevel.Info);
                return candidate;
            }
        }

        Log($"chrome.exe not found. Looked in:\n  {string.Join("\n  ", ExecutableCandidates)}", LogLevel.Error);
        return null;
    }

    public static int CountChromeProcesses()
    {
        Process[] processes = Process.GetProcessesByName("chrome");

        foreach (Process process in processes)
        {
            process.Dispose();
        }

        return processes.Length;
    }

    public static bool IsChromeRunning() => CountChromeProcesses() > 0;

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
                Log($"DevToolsActivePort file is absent: {path}", LogLevel.Info);
                return null;
            }

            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new(stream);

            string? firstLine = reader.ReadLine();

            if (int.TryParse(firstLine, out int port) && port is > 0 and <= 65535)
            {
                Log($"DevToolsActivePort file reports port {port}.", LogLevel.Info);
                return port;
            }

            Log($"DevToolsActivePort file holds an unusable first line: '{firstLine}'.", LogLevel.Warning);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"DevToolsActivePort file could not be read: {ex}", LogLevel.Warning);
            return null;
        }
    }

    /// <summary>Returns the first port that actually answers as a Chrome DevTools endpoint.</summary>
    public static async Task<int?> DiscoverPortAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        Log($"Port discovery started. Chrome processes running: {CountChromeProcesses()}.", LogLevel.Info);

        List<int> candidates = [];

        if (ReadDevToolsActivePort(DefaultUserDataDirectory) is int active)
        {
            candidates.Add(active);
        }

        if (!candidates.Contains(settings.DebuggingPort))
        {
            candidates.Add(settings.DebuggingPort);
        }

        Log($"Probing ports: {string.Join(", ", candidates)}", LogLevel.Info);

        foreach (int candidate in candidates)
        {
            if (await RespondsAsync(candidate, cancellationToken).ConfigureAwait(false))
            {
                Log($"Port {candidate} answered as a DevTools endpoint.", LogLevel.Info);
                return candidate;
            }
        }

        Log("No port answered. Chrome is not exposing a DevTools endpoint.", LogLevel.Warning);
        return null;
    }

    private static async Task<bool> RespondsAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using HttpClient http = new() { Timeout = TimeSpan.FromMilliseconds(1500) };
            string body = await http.GetStringAsync($"http://127.0.0.1:{port}/json/version", cancellationToken)
                .ConfigureAwait(false);

            bool usable = body.Contains("webSocketDebuggerUrl", StringComparison.Ordinal);
            Log($"Port {port} replied {body.Length} bytes, usable endpoint: {usable}.", LogLevel.Debug);

            return usable;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            Log($"Port {port} did not answer: {ex.GetType().Name} - {ex.Message}", LogLevel.Debug);
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

        Log($"Starting: \"{executable}\" --remote-debugging-port={port} --restore-last-session", LogLevel.Info);
        Process.Start(startInfo);
    }

    /// <summary>
    /// Asks every Chrome window to close and waits for the processes to go away, so the profile is
    /// released and the debugging flag will be honoured on the next start.
    /// </summary>
    /// <returns>True when no Chrome process is left.</returns>
    public static async Task<bool> CloseChromeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Log("Asking Chrome to close its windows.", LogLevel.Info);

        foreach (Process process in Process.GetProcessesByName("chrome"))
        {
            using (process)
            {
                try
                {
                    if (process.MainWindowHandle != nint.Zero)
                    {
                        process.CloseMainWindow();
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    Log($"Could not signal Chrome process {process.Id}: {ex.Message}", LogLevel.Debug);
                }
            }
        }

        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            int remaining = CountChromeProcesses();

            if (remaining == 0)
            {
                Log("Chrome has exited.", LogLevel.Info);
                return true;
            }

            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        }

        Log($"Chrome is still running after {timeout.TotalSeconds:F0}s ({CountChromeProcesses()} processes).",
            LogLevel.Warning);

        return false;
    }
}
