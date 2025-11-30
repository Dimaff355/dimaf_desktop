using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace RemoteDesktop.Service.Services;

internal static class AclHelper
{
    public static void HardenDirectory(string directoryPath, ILogger? logger = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var info = new DirectoryInfo(directoryPath);
            if (!info.Exists)
            {
                info.Create();
            }

            var security = info.GetAccessControl();
            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.ResetAccessRule(new FileSystemAccessRule(systemSid, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(adminsSid, FileSystemRights.FullControl, AccessControlType.Allow));

            info.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to harden directory permissions for {Directory}", directoryPath);
        }
    }

    public static void HardenFile(string filePath, ILogger? logger = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
            {
                return;
            }

            var security = info.GetAccessControl();
            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.ResetAccessRule(new FileSystemAccessRule(systemSid, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(adminsSid, FileSystemRights.FullControl, AccessControlType.Allow));

            info.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to harden file permissions for {File}", filePath);
        }
    }
}
