using System.Runtime.InteropServices;

namespace ChromeRamReducer.Core;

internal static partial class NativeMethods
{
    /// <summary>
    /// Removes as many pages as possible from the process working set. This does not free
    /// committed memory; the pages move to the standby list and stay in physical RAM until
    /// Windows repurposes them. See <see cref="MemorySnapshot"/> for why both counters matter.
    /// </summary>
    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EmptyWorkingSet(nint hProcess);
}
