using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MonitorApp.Agent.Core;

internal static partial class WindowsSystemMetrics
{
    public static CpuCounters ReadCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var idleValue = idle.ToUInt64();
        return new CpuCounters(idleValue, checked(kernel.ToUInt64() + user.ToUInt64()));
    }

    public static MemoryCounters ReadMemory()
    {
        var status = new MemoryStatusEx
        {
            Length = checked((uint)Marshal.SizeOf<MemoryStatusEx>()),
        };
        if (!GlobalMemoryStatusEx(ref status))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return new MemoryCounters(
            status.TotalPhysical,
            status.TotalPhysical - status.AvailablePhysical);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(
        out NativeFileTime idleTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;

        public readonly ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
