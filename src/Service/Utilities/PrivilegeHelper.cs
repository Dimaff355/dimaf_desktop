<![CDATA[using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace RemoteDesktop.Service.Utilities;

public static class PrivilegeHelper
{
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges,
        ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const string SE_DEBUG_NAME = "SeDebugPrivilege";
    private const string SE_TCB_NAME = "SeTcbPrivilege";
    private const string SE_INCREASE_QUOTA_NAME = "SeIncreaseQuotaPrivilege"; // sometimes needed

    /// <summary>
    /// Enables a specific privilege for the current process.
    /// </summary>
    /// <param name="privilegeName">The name of the privilege (e.g., "SeDebugPrivilege").</param>
    public static void EnablePrivilege(string privilegeName)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            if (!OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle,
                    TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr tokenHandle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                if (!LookupPrivilegeValue(null, privilegeName, out LUID luid))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Privileges = new LUID_AND_ATTRIBUTES[1]
                };
                tp.Privileges[0].Luid = luid;
                tp.Privileges[0].Attributes = 0x00000002; // SE_PRIVILEGE_ENABLED

                if (!AdjustTokenPrivileges(tokenHandle, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                Marshal.FreeHGlobal(tokenHandle);
            }
        }
        catch
        {
            // Best effort; ignore if fails
        }
    }

    /// <summary>
    /// Enables all privileges needed for the service to operate across sessions and secure desktops.
    /// </summary>
    public static void EnableRequiredPrivileges()
    {
        EnablePrivilege(SE_DEBUG_NAME);
        EnablePrivilege(SE_TCB_NAME);
        // EnablePrivilege(SE_INCREASE_QUOTA_NAME); // optional
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public LUID_AND_ATTRIBUTES[] Privileges;
    }
}
]]>