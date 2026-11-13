using System.Runtime.InteropServices;
using System.Text;

namespace Quartermaster.Render;

/// <summary>
/// The macOS login Keychain through the Security.framework generic-password C API: one item per account under the service
/// <see cref="SecretStoreProtector.Service"/>. Never throws; never logs the secret.
/// </summary>
public sealed class MacKeychainStore : ISecretStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const int ErrSecSuccess = 0;
    private const int ErrSecDuplicateItem = -25299;

    private static readonly byte[] ServiceBytes = Encoding.UTF8.GetBytes(SecretStoreProtector.Service);

    // OSStatus SecKeychainAddGenericPassword(SecKeychainRef keychain, UInt32 serviceNameLength, const char *serviceName,
    //     UInt32 accountNameLength, const char *accountName, UInt32 passwordLength, const void *passwordData, SecKeychainItemRef *itemRef);
    [DllImport(Security)]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, uint passwordLength, byte[] passwordData, out IntPtr itemRef);

    // OSStatus SecKeychainFindGenericPassword(CFTypeRef keychainOrArray, UInt32 serviceNameLength, const char *serviceName,
    //     UInt32 accountNameLength, const char *accountName, UInt32 *passwordLength, void **passwordData, SecKeychainItemRef *itemRef);
    [DllImport(Security)]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

    // The same call for the item only: NULL passwordLength / passwordData, so the data is not decrypted (no Keychain access prompt).
    [DllImport(Security, EntryPoint = "SecKeychainFindGenericPassword")]
    private static extern int SecKeychainFindGenericPasswordItem(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, IntPtr passwordLength, IntPtr passwordData, out IntPtr itemRef);

    // OSStatus SecKeychainItemModifyAttributesAndData(SecKeychainItemRef itemRef, const SecKeychainAttributeList *attrList,
    //     UInt32 length, const void *data);
    [DllImport(Security)]
    private static extern int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

    // OSStatus SecKeychainItemDelete(SecKeychainItemRef itemRef);
    [DllImport(Security)]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    // OSStatus SecKeychainItemFreeContent(SecKeychainAttributeList *attrList, void *data);
    [DllImport(Security)]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    // void CFRelease(CFTypeRef cf);
    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);

    public bool TrySet(string account, string secret)
    {
        if (account == null || secret == null || !OperatingSystem.IsMacOS()) return false;
        var acc = Encoding.UTF8.GetBytes(account);
        var pw = Encoding.UTF8.GetBytes(secret);
        var item = IntPtr.Zero;
        try
        {
            int status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)ServiceBytes.Length, ServiceBytes, (uint)acc.Length, acc,
                (uint)pw.Length, pw, out item);
            if (status == ErrSecSuccess) return true;
            if (status != ErrSecDuplicateItem) return false;
            // Already there: replace its data.
            Release(ref item);
            status = FindItem(acc, out item);
            if (status != ErrSecSuccess || item == IntPtr.Zero) return false;
            return SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)pw.Length, pw) == ErrSecSuccess;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
        finally
        {
            Release(ref item);
            Array.Clear(pw);
        }
    }

    public string? Get(string account)
    {
        if (account == null || !OperatingSystem.IsMacOS()) return null;
        var acc = Encoding.UTF8.GetBytes(account);
        var data = IntPtr.Zero;
        var item = IntPtr.Zero;
        byte[]? copy = null;
        try
        {
            int status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)ServiceBytes.Length, ServiceBytes, (uint)acc.Length, acc,
                out uint length, out data, out item);
            if (status != ErrSecSuccess || (data == IntPtr.Zero && length > 0) || length > int.MaxValue) return null;
            copy = new byte[length];
            if (length > 0) Marshal.Copy(data, copy, 0, (int)length);
            return Encoding.UTF8.GetString(copy);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
        finally
        {
            if (copy != null) Array.Clear(copy);
            try { if (data != IntPtr.Zero) SecKeychainItemFreeContent(IntPtr.Zero, data); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
            Release(ref item);
        }
    }

    public void Remove(string account)
    {
        if (account == null || !OperatingSystem.IsMacOS()) return;
        var acc = Encoding.UTF8.GetBytes(account);
        var item = IntPtr.Zero;
        try
        {
            if (FindItem(acc, out item) == ErrSecSuccess && item != IntPtr.Zero) SecKeychainItemDelete(item);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        finally { Release(ref item); }
    }

    /// <summary>Finds the item of an account without reading its data (the caller releases <paramref name="item"/>).</summary>
    private static int FindItem(byte[] account, out IntPtr item)
    {
        item = IntPtr.Zero;
        return SecKeychainFindGenericPasswordItem(IntPtr.Zero, (uint)ServiceBytes.Length, ServiceBytes, (uint)account.Length, account,
            IntPtr.Zero, IntPtr.Zero, out item);
    }

    private static void Release(ref IntPtr cf)
    {
        if (cf == IntPtr.Zero) return;
        try { CFRelease(cf); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        cf = IntPtr.Zero;
    }
}
