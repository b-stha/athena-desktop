using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Athena.Desktop.Actions;

[SupportedOSPlatform("windows")]
internal static class WindowsSleepAction
{
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint PrivilegeEnabled = 0x0002;
    private static readonly object SleepLock = new();

    public static void Sleep()
    {
        // The privilege belongs to the process, so serialize enable/restore scopes.
        lock (SleepLock)
        {
            using var process = Process.GetCurrentProcess();
            if (!OpenProcessToken(process.SafeHandle, TokenQuery | TokenAdjustPrivileges, out var token))
            {
                throw CreateWindowsError(Marshal.GetLastWin32Error(), "Could not open the process token for sleep.");
            }

            using (token)
            {
                if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var privilege))
                {
                    throw CreateWindowsError(Marshal.GetLastWin32Error(), "Could not find the Windows shutdown privilege.");
                }

                var enabledState = new TokenPrivileges
                {
                    PrivilegeCount = 1,
                    Privilege = privilege,
                    Attributes = PrivilegeEnabled
                };
                var bufferLength = (uint)Marshal.SizeOf<TokenPrivileges>();
                var adjusted = AdjustTokenPrivileges(token, false, ref enabledState, bufferLength, out var previousState, out _);
                var error = Marshal.GetLastWin32Error();
                // AdjustTokenPrivileges can succeed while reporting ERROR_NOT_ALL_ASSIGNED.
                if (!adjusted || error != 0)
                {
                    throw CreateWindowsError(error, "Could not enable the Windows shutdown privilege for sleep.");
                }

                Exception? sleepFailure = null;
                try
                {
                    // Request sleep, leave wake events enabled, and do not request hibernation.
                    if (!SetSuspendState(false, false, false))
                    {
                        throw CreateWindowsError(Marshal.GetLastWin32Error(), "Windows rejected the sleep request.");
                    }
                }
                catch (Exception exception)
                {
                    sleepFailure = exception;
                    throw;
                }
                finally
                {
                    // A count of zero means the privilege was already enabled.
                    if (previousState.PrivilegeCount != 0)
                    {
                        adjusted = AdjustTokenPrivileges(token, false, ref previousState, bufferLength, out _, out _);
                        error = Marshal.GetLastWin32Error();
                        if (!adjusted || error != 0)
                        {
                            var restoreFailure = CreateWindowsError(error, "Could not restore the Windows shutdown privilege after sleep.");
                            if (sleepFailure is not null)
                            {
                                throw new AggregateException("Sleep and privilege restoration both failed.", sleepFailure, restoreFailure);
                            }

                            throw restoreFailure;
                        }
                    }
                }
            }
        }
    }

    private static Win32Exception CreateWindowsError(int error, string message)
    {
        return new Win32Exception(error, $"{message} (Windows error {error}: {new Win32Exception(error).Message})");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    // TOKEN_PRIVILEGES with its single LUID_AND_ATTRIBUTES entry.
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Privilege;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        SafeProcessHandle processHandle,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        SafeAccessTokenHandle tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        out TokenPrivileges previousState,
        out uint returnLength);

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool force,
        [MarshalAs(UnmanagedType.U1)] bool wakeupEventsDisabled);
}
