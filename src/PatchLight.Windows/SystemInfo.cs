using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using PatchLight.Core;

namespace PatchLight.Windows;

public sealed record WindowsInfo(string? DisplayVersion, Version Build)
{
    // Windows 11, or Windows 10 22H2 (the only Windows 10 version still getting updates).
    public bool IsSupported => DisplayVersion is not null && WindowsRelease.IsSupported(Build.Major);

    // The known-issues page this PC is judged against: "25H2" on Windows 11, "10-22H2" on Windows 10.
    public string Release => WindowsRelease.ForPc(Build.Major, DisplayVersion
        ?? throw new PatchLightException(NotSupportedMessage));

    public const string NotSupportedMessage = "PatchLight works on Windows 11 and Windows 10 version 22H2.";
}

[SupportedOSPlatform("windows")]
public static class SystemInfo
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PatchLight");

    // ProductName says "Windows 10" on Windows 11, so go by the build number.
    // Windows 10 22H2 reports DisplayVersion "22H2"; older Windows 10 versions have none.
    public static WindowsInfo Read()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")
            ?? throw new PatchLightException("Couldn't read the Windows version on this PC.");
        var build = int.Parse((string)key.GetValue("CurrentBuild", "0")!);
        var ubr = Convert.ToInt32(key.GetValue("UBR", 0));
        return new WindowsInfo(key.GetValue("DisplayVersion") as string, new Version(build, ubr));
    }

    // Windows Home ("Core", "CoreSingleLanguage", ...) ignores Windows Update policies like the deferral one.
    public static bool IsHomeEdition()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return key?.GetValue("EditionID") is string edition && edition.StartsWith("Core", StringComparison.OrdinalIgnoreCase);
    }

    // Joined to a company domain or to Microsoft Entra (work or school account set up as the PC's owner).
    // Such PCs see every known issue; others set aside issues that usually only hit business PCs.
    public static bool IsWorkPc()
    {
        const int NetSetupDomainName = 3;
        if (NetGetJoinInformation(null, out var name, out var status) == 0)
        {
            NetApiBufferFree(name);
            if (status == NetSetupDomainName) return true;
        }
        using var entra = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo");
        return entra?.SubKeyCount > 0;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetGetJoinInformation(string? server, out IntPtr name, out int status);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
