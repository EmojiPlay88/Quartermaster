using System.Runtime.InteropServices;
using System.Text;

namespace Quartermaster.Render;

/// <summary>
/// The freedesktop Secret Service (GNOME Keyring, KWallet) through libsecret's non-variadic <c>*v_sync</c> calls: one item per account
/// (schema <see cref="SchemaName"/>, string attribute <c>account</c>) in the default collection. Never throws; never logs the secret.
/// </summary>
public sealed class LinuxSecretServiceStore : ISecretStore
{
    private const string LibSecret = "libsecret-1.so.0";
    private const string LibGlib = "libglib-2.0.so.0";

    public const string SchemaName = "org.quartermaster.TurnMailbox";
    private const string AttributeName = "account";
    private const string Collection = "default";   // SECRET_COLLECTION_DEFAULT

    // gboolean secret_password_storev_sync(const SecretSchema *schema, GHashTable *attributes, const gchar *collection,
    //     const gchar *label, const gchar *password, GCancellable *cancellable, GError **error);
    [DllImport(LibSecret)]
    private static extern int secret_password_storev_sync(IntPtr schema, IntPtr attributes, byte[] collection, byte[] label,
        byte[] password, IntPtr cancellable, ref IntPtr error);

    // gchar *secret_password_lookupv_sync(const SecretSchema *schema, GHashTable *attributes, GCancellable *cancellable, GError **error);
    [DllImport(LibSecret)]
    private static extern IntPtr secret_password_lookupv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, ref IntPtr error);

    // gboolean secret_password_clearv_sync(const SecretSchema *schema, GHashTable *attributes, GCancellable *cancellable, GError **error);
    [DllImport(LibSecret)]
    private static extern int secret_password_clearv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, ref IntPtr error);

    // void secret_password_free(gchar *password);  (clears the memory, then g_free)
    [DllImport(LibSecret)]
    private static extern void secret_password_free(IntPtr password);

    // GHashTable *g_hash_table_new(GHashFunc hash_func, GEqualFunc key_equal_func);
    [DllImport(LibGlib)]
    private static extern IntPtr g_hash_table_new(IntPtr hashFunc, IntPtr keyEqualFunc);

    // gboolean g_hash_table_insert(GHashTable *hash_table, gpointer key, gpointer value);
    [DllImport(LibGlib)]
    private static extern int g_hash_table_insert(IntPtr hashTable, IntPtr key, IntPtr value);

    // void g_hash_table_unref(GHashTable *hash_table);
    [DllImport(LibGlib)]
    private static extern void g_hash_table_unref(IntPtr hashTable);

    // void g_error_free(GError *error);
    [DllImport(LibGlib)]
    private static extern void g_error_free(IntPtr error);

    private static readonly object InitLock = new();
    private static bool _initTried;
    private static IntPtr _schema;       // the SecretSchema; lives for the process
    private static IntPtr _strHash;      // g_str_hash
    private static IntPtr _strEqual;     // g_str_equal

    private static readonly byte[] CollectionBytes = Utf8z(Collection);
    private static readonly byte[] LabelBytes = Utf8z(SecretStoreProtector.Service);

    /// <summary>True when libsecret loads and a probe lookup does not fail (a D-Bus session with a Secret Service is reachable).</summary>
    public static bool IsAvailable()
    {
        if (!OperatingSystem.IsLinux() || !Init()) return false;
        try
        {
            Lookup("qm-probe-" + Guid.NewGuid().ToString("N"), out bool failed);   // finds nothing; only a GError matters
            return !failed;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    public bool TrySet(string account, string secret)
    {
        if (account == null || secret == null || !OperatingSystem.IsLinux() || !Init()) return false;
        var pw = Utf8z(secret);
        try
        {
            return WithAttributes(account, table =>
            {
                var error = IntPtr.Zero;   // GError** must point at NULL
                int ok = secret_password_storev_sync(_schema, table, CollectionBytes, LabelBytes, pw, IntPtr.Zero, ref error);
                bool failed = FreeError(error);
                return ok != 0 && !failed;
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
        finally { Array.Clear(pw); }
    }

    public string? Get(string account)
    {
        if (account == null || !OperatingSystem.IsLinux() || !Init()) return null;
        try { return Lookup(account, out _); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    public void Remove(string account)
    {
        if (account == null || !OperatingSystem.IsLinux() || !Init()) return;
        try
        {
            WithAttributes(account, table =>
            {
                var error = IntPtr.Zero;
                secret_password_clearv_sync(_schema, table, IntPtr.Zero, ref error);
                FreeError(error);
                return true;
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    /// <summary>The stored password of <paramref name="account"/> (null when none); <paramref name="failed"/> = the call raised a GError.</summary>
    private static string? Lookup(string account, out bool failed)
    {
        bool raised = false;
        string? result = null;
        WithAttributes(account, table =>
        {
            var error = IntPtr.Zero;
            var pw = secret_password_lookupv_sync(_schema, table, IntPtr.Zero, ref error);
            raised = FreeError(error);
            if (pw != IntPtr.Zero)
            {
                try { result = Marshal.PtrToStringUTF8(pw); }
                finally { secret_password_free(pw); }
            }
            return true;
        });
        failed = raised;
        return raised ? null : result;
    }

    /// <summary>Runs <paramref name="call"/> with a GHashTable {"account": account}; keys and values stay alive until after it.</summary>
    private static bool WithAttributes(string account, Func<IntPtr, bool> call)
    {
        var key = IntPtr.Zero;
        var value = IntPtr.Zero;
        var table = IntPtr.Zero;
        try
        {
            key = Marshal.StringToCoTaskMemUTF8(AttributeName);
            value = Marshal.StringToCoTaskMemUTF8(account);
            table = g_hash_table_new(_strHash, _strEqual);
            if (table == IntPtr.Zero) return false;
            g_hash_table_insert(table, key, value);   // no destroy functions: the table does not own them
            return call(table);
        }
        finally
        {
            if (table != IntPtr.Zero) g_hash_table_unref(table);
            if (value != IntPtr.Zero) Marshal.FreeCoTaskMem(value);
            if (key != IntPtr.Zero) Marshal.FreeCoTaskMem(key);
        }
    }

    /// <summary>Frees a GError when one was set; true when there was one.</summary>
    private static bool FreeError(IntPtr error)
    {
        if (error == IntPtr.Zero) return false;
        g_error_free(error);
        return true;
    }

    /// <summary>Loads glib's string hash functions and builds the schema once; false when the libraries are missing.</summary>
    private static bool Init()
    {
        lock (InitLock)
        {
            if (_initTried) return _schema != IntPtr.Zero;
            _initTried = true;
            try
            {
                if (!NativeLibrary.TryLoad(LibSecret, out _)) return false;
                if (!NativeLibrary.TryLoad(LibGlib, out var glib)) return false;
                if (!NativeLibrary.TryGetExport(glib, "g_str_hash", out var hash)) return false;
                if (!NativeLibrary.TryGetExport(glib, "g_str_equal", out var equal)) return false;
                _strHash = hash;
                _strEqual = equal;
                _schema = BuildSchema();
                return true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Writes a SecretSchema into unmanaged memory that is never freed (it must outlive every call):
    /// <c>{ const gchar *name; SecretSchemaFlags flags; SecretSchemaAttribute attributes[32]; gint reserved; gpointer reserved1..7; }</c>
    /// with <c>SecretSchemaAttribute { const gchar *name; SecretSchemaAttributeType type; }</c>. Pointers are pointer-aligned, so on a
    /// 64-bit process: name 0, flags 8, attributes 16 (16 bytes each), reserved 528, reserved1 536, size 592. The unused attributes
    /// stay zero: a NULL name ends the list.
    /// </summary>
    internal static IntPtr BuildSchema()
    {
        int ptr = IntPtr.Size;
        int attrSize = 2 * ptr;                        // pointer + enum, padded to pointer alignment
        int attrsOffset = 2 * ptr;                     // after name + flags (padded)
        int reservedOffset = attrsOffset + 32 * attrSize;
        int size = reservedOffset + ptr + 7 * ptr;     // gint reserved (padded) + reserved1..7
        var mem = Marshal.AllocHGlobal(size);
        for (int i = 0; i < size; i++) Marshal.WriteByte(mem, i, 0);
        Marshal.WriteIntPtr(mem, 0, Marshal.StringToCoTaskMemUTF8(SchemaName));             // name (never freed)
        Marshal.WriteInt32(mem, ptr, 0);                                                     // SECRET_SCHEMA_NONE
        Marshal.WriteIntPtr(mem, attrsOffset, Marshal.StringToCoTaskMemUTF8(AttributeName)); // attributes[0].name (never freed)
        Marshal.WriteInt32(mem, attrsOffset + ptr, 0);                                       // SECRET_SCHEMA_ATTRIBUTE_STRING
        return mem;
    }

    private static byte[] Utf8z(string s)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(s) + 1];
        Encoding.UTF8.GetBytes(s, 0, s.Length, bytes, 0);
        return bytes;
    }
}
