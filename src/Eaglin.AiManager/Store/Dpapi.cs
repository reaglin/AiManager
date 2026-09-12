using System.Security.Cryptography;
using System.Text;

namespace Eaglin.AiManager.Store;

/// <summary>
/// Windows DPAPI, CurrentUser scope, no entropy, Base64 — byte-for-byte the scheme PreseMaker
/// (and the CIATLE / AuthorPlus copies) use, so their existing blobs decrypt here. Anything
/// encrypted this way can be read by any app running as the same Windows user on the same
/// machine, which is exactly the sharing the AI Manager wants. On non-Windows (tests on CI)
/// values are Base64 only; nothing real ever runs there.
/// </summary>
internal static class Dpapi
{
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return string.Empty;
        var bytes = Encoding.UTF8.GetBytes(plain);
        if (OperatingSystem.IsWindows())
            bytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>Empty string when the value is blank, not Base64, or was encrypted by another user.</summary>
    public static string Unprotect(string base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return string.Empty;
        try
        {
            var bytes = Convert.FromBase64String(base64);
            if (OperatingSystem.IsWindows())
                bytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return string.Empty; }
    }
}
