using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace EQ2Parser.App.Services;

/// <summary>
/// At-rest storage for the EQ2Lexicon API token on macOS — the counterpart
/// of the WPF app's DPAPI TokenProtector, same surface. The token itself
/// lives in the user's login Keychain; settings.json only ever holds the
/// <see cref="KeychainMarker"/>, so a copied settings file never carries
/// the credential. Anywhere the Keychain is unavailable both calls answer
/// null, and the user is simply asked for the token again.
/// </summary>
internal static partial class TokenProtector
{
    /// <summary>What settings.json stores in place of the token.</summary>
    internal const string KeychainMarker = "macos-keychain:v1";

    private const string Account = "LexiconApiToken";

    /// <summary>The Keychain service name. A run with its own data folder
    /// (EQ2PARSER_DATA_DIR — tests, the e2e upload check) gets its own
    /// entry, named after that folder, so it can never read or overwrite
    /// the token of the real install.</summary>
    internal static string Service =>
        Environment.GetEnvironmentVariable("EQ2PARSER_DATA_DIR") is { Length: > 0 }
            ? $"EQ2Parser ({AppSettings.Directory})"
            : "EQ2Parser";

    /// <summary>Store the token; the settings value to keep, or null when
    /// it could not be stored.</summary>
    public static string? Protect(string token)
    {
        if (!OperatingSystem.IsMacOS())
            return null;
        try
        {
            return Keychain.Write(Service, Account, token) ? KeychainMarker : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The stored token; null when absent, not ours, or unreadable.</summary>
    public static string? Unprotect(string? blob)
    {
        if (blob != KeychainMarker || !OperatingSystem.IsMacOS())
            return null;
        try
        {
            return Keychain.Read(Service, Account);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Delete the stored token, if any. True when nothing is
    /// stored any more.</summary>
    public static bool Forget()
    {
        if (!OperatingSystem.IsMacOS())
            return true;
        try
        {
            return Keychain.Delete(Service, Account);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>The classic generic-password calls of Security.framework:
    /// small, stable, and enough for one secret.</summary>
    [SupportedOSPlatform("macos")]
    private static partial class Keychain
    {
        private const string Security = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const int ErrSecItemNotFound = -25300;

        public static string? Read(string service, string account)
        {
            var s = Encoding.UTF8.GetBytes(service);
            var a = Encoding.UTF8.GetBytes(account);
            var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)s.Length, s, (uint)a.Length, a,
                out var length, out var data, out var item);
            if (status != 0)
                return null;
            try
            {
                return Marshal.PtrToStringUTF8(data, (int)length);
            }
            finally
            {
                _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
                if (item != IntPtr.Zero)
                    CFRelease(item);
            }
        }

        public static bool Delete(string service, string account)
        {
            var s = Encoding.UTF8.GetBytes(service);
            var a = Encoding.UTF8.GetBytes(account);
            var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)s.Length, s, (uint)a.Length, a,
                out _, out var data, out var item);
            if (status == ErrSecItemNotFound)
                return true;
            if (status != 0)
                return false;
            _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
            try
            {
                return SecKeychainItemDelete(item) == 0;
            }
            finally
            {
                CFRelease(item);
            }
        }

        public static bool Write(string service, string account, string secret)
        {
            var s = Encoding.UTF8.GetBytes(service);
            var a = Encoding.UTF8.GetBytes(account);
            var p = Encoding.UTF8.GetBytes(secret);
            var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)s.Length, s, (uint)a.Length, a,
                out _, out var data, out var item);
            if (status == 0)
            {
                _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
                try
                {
                    return SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)p.Length, p) == 0;
                }
                finally
                {
                    CFRelease(item);
                }
            }
            if (status != ErrSecItemNotFound)
                return false;
            status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)s.Length, s, (uint)a.Length, a,
                (uint)p.Length, p, out var added);
            if (added != IntPtr.Zero)
                CFRelease(added);
            return status == 0;
        }

        [LibraryImport(Security)]
        private static partial int SecKeychainFindGenericPassword(IntPtr keychainOrArray,
            uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName,
            out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

        [LibraryImport(Security)]
        private static partial int SecKeychainAddGenericPassword(IntPtr keychain,
            uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName,
            uint passwordLength, byte[] passwordData, out IntPtr itemRef);

        [LibraryImport(Security)]
        private static partial int SecKeychainItemModifyAttributesAndData(IntPtr itemRef,
            IntPtr attrList, uint length, byte[] data);

        [LibraryImport(Security)]
        private static partial int SecKeychainItemDelete(IntPtr itemRef);

        [LibraryImport(Security)]
        private static partial int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

        [LibraryImport(CoreFoundation)]
        private static partial void CFRelease(IntPtr cf);
    }
}
