using System.Diagnostics;

namespace ChromeRamReducer.Core;

/// <summary>
/// A point-in-time reading of every Chrome process on the machine.
/// </summary>
/// <param name="ProcessCount">Number of chrome.exe processes that were readable.</param>
/// <param name="WorkingSetBytes">
/// Physical pages currently charged to Chrome. This is the number Task Manager shows by default,
/// and it drops when pages are merely evicted to the standby list, so it does not prove a release.
/// </param>
/// <param name="PrivateBytes">
/// Private committed bytes. Memory is only genuinely handed back to the operating system when
/// this number falls.
/// </param>
public readonly record struct MemorySnapshot(int ProcessCount, long WorkingSetBytes, long PrivateBytes)
{
    public static MemorySnapshot Empty => new(0, 0, 0);

    public double WorkingSetMb => WorkingSetBytes / 1024d / 1024d;

    public double PrivateMb => PrivateBytes / 1024d / 1024d;

    public bool HasChrome => ProcessCount > 0;

    public static MemorySnapshot Capture()
    {
        long workingSet = 0;
        long priv = 0;
        int count = 0;

        foreach (Process process in Process.GetProcessesByName("chrome"))
        {
            using (process)
            {
                try
                {
                    workingSet += process.WorkingSet64;
                    priv += process.PrivateMemorySize64;
                    count++;
                }
                catch (InvalidOperationException)
                {
                    // The process exited between enumeration and the read.
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception)
                {
                    // Access denied for an elevated Chrome process; skip it.
                }
            }
        }

        return new MemorySnapshot(count, workingSet, priv);
    }

    /// <summary>
    /// Calls EmptyWorkingSet on every Chrome process. Cosmetic by design: it lowers
    /// <see cref="WorkingSetBytes"/> without lowering <see cref="PrivateBytes"/>.
    /// </summary>
    /// <returns>How many processes accepted the call.</returns>
    public static int EmptyAllChromeWorkingSets()
    {
        int trimmed = 0;

        foreach (Process process in Process.GetProcessesByName("chrome"))
        {
            using (process)
            {
                try
                {
                    if (NativeMethods.EmptyWorkingSet(process.Handle))
                    {
                        trimmed++;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Process exited or is not accessible.
                }
            }
        }

        return trimmed;
    }
}
