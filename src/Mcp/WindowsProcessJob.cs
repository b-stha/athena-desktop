using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Athena.Desktop.Mcp;

/// <summary>Closing this private job handle terminates all owned descendants, even if the launcher exited.</summary>
internal sealed class WindowsProcessJob : IDisposable
{
    private readonly SafeFileHandle handle;

    public WindowsProcessJob()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create Windows-MCP process ownership job.");
        var limits = new ExtendedLimitInformation { BasicLimitInformation = new BasicLimitInformation { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        var size = Marshal.SizeOf<ExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, false);
            if (!SetInformationJobObject(handle, 9, buffer, (uint)size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not configure Windows-MCP process ownership.");
        }
        catch { handle.Dispose(); throw; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.Handle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not assign Windows-MCP process ownership.");
    }

    public void Dispose() => handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, IntPtr information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
