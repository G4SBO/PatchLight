namespace PatchLight.Core;

// Which Microsoft known-issues page a Windows version is judged against.
// Windows 11 versions are plain ("25H2"), as upgrade offers name them; Windows 10 ones carry a "10-" prefix
// ("10-22H2"), because both products had a 22H2.
public static class WindowsRelease
{
    private const string Windows10Prefix = "10-";

    // Windows 10 22H2 is the last Windows 10 version, still patched under Extended Security Updates.
    public const int Windows10_22H2Build = 19045;
    public const int FirstWindows11Build = 22000;

    public static bool IsSupported(int build) => build >= FirstWindows11Build || build == Windows10_22H2Build;

    public static string ForPc(int build, string displayVersion) =>
        build < FirstWindows11Build ? Windows10Prefix + displayVersion : displayVersion;

    public static bool IsWindows10(string release) => release.StartsWith(Windows10Prefix, StringComparison.Ordinal);

    public static string Version(string release) => IsWindows10(release) ? release[Windows10Prefix.Length..] : release;

    public static string Product(string release) => IsWindows10(release) ? "Windows 10" : "Windows 11";

    // "Windows 11, version 25H2"
    public static string Name(string release) => $"{Product(release)}, version {Version(release).ToUpperInvariant()}";

    // "status-windows-11-25h2"
    public static string PageSlug(string release) =>
        $"status-{Product(release).Replace(' ', '-')}-{Version(release)}".ToLowerInvariant();
}
