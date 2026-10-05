using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace JDP {
    // Linux: each login is a Secret Service item in the default collection (the login keyring),
    // which can ask the user to unlock it (see KeyringStoredAuthProtector for the timeout),
    // stored through libsecret's synchronous, non-variadic password functions. The schema
    // "org.chanthreadwatch.Login" has two string attributes: "service" (the service name, so
    // tests use their own) and "id".
    // Memory: the schema and its strings are allocated once and kept for the life of the
    // process. Each call allocates its attribute table and strings and frees them before it
    // returns (see Attributes and NativeText); the login's copy is cleared before it is freed.
    // A password libsecret returns is freed with secret_password_free, and a GError with
    // g_error_free.
    internal sealed class LibSecretKeyring : ILoginKeyring {
        private const string LibSecret = "libsecret-1.so.0";
        private const string LibGLib = "libglib-2.0.so.0";
        private const string SchemaName = "org.chanthreadwatch.Login";
        private const string Label = "Chan Thread Watch login";
        // SECRET_COLLECTION_DEFAULT: the default collection's alias (the login keyring). The docs
        // say NULL also means the default collection; the alias is named for clarity.
        private const string DefaultCollection = "default";

        private static readonly object _nativeSync = new object();
        private static IntPtr _schema;
        private static IntPtr _strHash;
        private static IntPtr _strEqual;

        private readonly string _service;

        public LibSecretKeyring(string service) {
            if (String.IsNullOrEmpty(service)) throw new ArgumentException("A service name is required.", "service");
            _service = service;
        }

        public string Prefix {
            get { return StoredAuth.SecretServicePrefix; }
        }

        public string Name {
            get { return "the Secret Service (login keyring)"; }
        }

        public void Store(string id, string login) {
            using (Attributes attributes = new Attributes(_service, id))
            using (NativeText collection = new NativeText(DefaultCollection, false))
            using (NativeText label = new NativeText(Label, false))
            using (NativeText password = new NativeText(login, true)) {
                IntPtr error = IntPtr.Zero; // GLib requires *error to be NULL on entry
                int stored = secret_password_storev_sync(GetSchema(), attributes.Table, collection.Pointer, label.Pointer, password.Pointer, IntPtr.Zero, ref error);
                ThrowIfError(error);
                if (stored == 0) throw new KeyringException("The Secret Service did not store the login.");
            }
        }

        public string Lookup(string id) {
            using (Attributes attributes = new Attributes(_service, id)) {
                IntPtr error = IntPtr.Zero; // GLib requires *error to be NULL on entry
                IntPtr password = secret_password_lookupv_sync(GetSchema(), attributes.Table, IntPtr.Zero, ref error);
                try {
                    ThrowIfError(error);
                    return password != IntPtr.Zero ? Marshal.PtrToStringUTF8(password) : null;
                }
                finally {
                    // Clears the password before freeing it; null is allowed
                    secret_password_free(password);
                }
            }
        }

        // FALSE without an error means there was no such item
        public void Clear(string id) {
            using (Attributes attributes = new Attributes(_service, id)) {
                IntPtr error = IntPtr.Zero; // GLib requires *error to be NULL on entry
                secret_password_clearv_sync(GetSchema(), attributes.Table, IntPtr.Zero, ref error);
                ThrowIfError(error);
            }
        }

        // GError: guint32 domain, gint code, gchar *message
        private static void ThrowIfError(IntPtr error) {
            if (error == IntPtr.Zero) return;
            string message;
            try {
                message = "GError " + Marshal.ReadInt32(error, 4) + ": " + Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8));
            }
            finally {
                g_error_free(error);
            }
            throw new KeyringException(message);
        }

        private static IntPtr GetSchema() {
            lock (_nativeSync) {
                if (_schema == IntPtr.Zero) _schema = CreateSchema();
                return _schema;
            }
        }

        // SecretSchema as declared in libsecret's secret-schema.h (0.18 and later):
        //   const gchar *name; SecretSchemaFlags flags; SecretSchemaAttribute attributes[32];
        //   gint reserved; gpointer reserved1 ... reserved7;
        // with SecretSchemaAttribute { const gchar *name; SecretSchemaAttributeType type; }.
        // Each attribute takes two pointer sizes (the enum is padded), the attribute array starts
        // at two pointer sizes, and a zeroed attribute ends the list: 74 pointer sizes in all.
        // Flags 0 is SECRET_SCHEMA_NONE (the schema name is matched) and type 0 is
        // SECRET_SCHEMA_ATTRIBUTE_STRING. Zeroed memory also fills the reserved fields.
        private static IntPtr CreateSchema() {
            int p = IntPtr.Size;
            int size = 74 * p;
            IntPtr schema = Marshal.AllocHGlobal(size);
            Marshal.Copy(new byte[size], 0, schema, size);
            Marshal.WriteIntPtr(schema, 0, Marshal.StringToCoTaskMemUTF8(SchemaName));
            Marshal.WriteInt32(schema, p, 0);
            WriteStringAttribute(schema, 0, "service");
            WriteStringAttribute(schema, 1, "id");
            return schema;
        }

        private static void WriteStringAttribute(IntPtr schema, int index, string name) {
            int p = IntPtr.Size;
            int offset = 2 * p + index * 2 * p;
            Marshal.WriteIntPtr(schema, offset, Marshal.StringToCoTaskMemUTF8(name));
            Marshal.WriteInt32(schema, offset + p, 0);
        }

        // g_str_hash and g_str_equal, for the attribute table. GLib stays loaded for the life of the process.
        private static void GetStringFunctions(out IntPtr strHash, out IntPtr strEqual) {
            lock (_nativeSync) {
                if (_strHash == IntPtr.Zero) LoadStringFunctions();
                strHash = _strHash;
                strEqual = _strEqual;
            }
        }

        private static void LoadStringFunctions() {
            IntPtr glib = NativeLibrary.Load(LibGLib);
            _strEqual = NativeLibrary.GetExport(glib, "g_str_equal");
            _strHash = NativeLibrary.GetExport(glib, "g_str_hash");
        }

        // A GHashTable of the item's attributes. libsecret doesn't take ownership of the table
        // or its strings, so Dispose frees them all.
        private sealed class Attributes : IDisposable {
            private readonly List<NativeText> _strings = new List<NativeText>();

            public Attributes(string service, string id) {
                IntPtr strHash, strEqual;
                GetStringFunctions(out strHash, out strEqual);
                Table = g_hash_table_new(strHash, strEqual);
                if (Table == IntPtr.Zero) throw new KeyringException("GLib could not create a hash table.");
                try {
                    Insert("service", service);
                    Insert("id", id);
                }
                catch {
                    Dispose();
                    throw;
                }
            }

            public IntPtr Table { get; private set; }

            private void Insert(string key, string value) {
                NativeText keyText = Add(key);
                g_hash_table_insert(Table, keyText.Pointer, Add(value).Pointer);
            }

            private NativeText Add(string text) {
                NativeText nativeText = new NativeText(text, false);
                _strings.Add(nativeText);
                return nativeText;
            }

            public void Dispose() {
                if (Table != IntPtr.Zero) g_hash_table_unref(Table);
                Table = IntPtr.Zero;
                foreach (NativeText text in _strings) {
                    text.Dispose();
                }
                _strings.Clear();
            }
        }

        // A NUL-terminated UTF-8 copy of a string in unmanaged memory. A secret one is cleared before it is freed.
        private sealed class NativeText : IDisposable {
            private readonly int _length;
            private readonly bool _isSecret;

            public NativeText(string text, bool isSecret) {
                byte[] bytes = Encoding.UTF8.GetBytes(text + "\0");
                _length = bytes.Length;
                _isSecret = isSecret;
                Pointer = Marshal.AllocHGlobal(_length);
                Marshal.Copy(bytes, 0, Pointer, _length);
                Array.Clear(bytes, 0, bytes.Length);
            }

            public IntPtr Pointer { get; private set; }

            public void Dispose() {
                if (Pointer == IntPtr.Zero) return;
                if (_isSecret) Marshal.Copy(new byte[_length], 0, Pointer, _length);
                Marshal.FreeHGlobal(Pointer);
                Pointer = IntPtr.Zero;
            }
        }

        [DllImport(LibSecret)]
        private static extern int secret_password_storev_sync(IntPtr schema, IntPtr attributes, IntPtr collection, IntPtr label, IntPtr password, IntPtr cancellable, ref IntPtr error);

        [DllImport(LibSecret)]
        private static extern IntPtr secret_password_lookupv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, ref IntPtr error);

        [DllImport(LibSecret)]
        private static extern int secret_password_clearv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, ref IntPtr error);

        [DllImport(LibSecret)]
        private static extern void secret_password_free(IntPtr password);

        [DllImport(LibGLib)]
        private static extern IntPtr g_hash_table_new(IntPtr hashFunc, IntPtr keyEqualFunc);

        // Returns gboolean since GLib 2.40 and void before; the result is not used
        [DllImport(LibGLib)]
        private static extern void g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);

        [DllImport(LibGLib)]
        private static extern void g_hash_table_unref(IntPtr table);

        [DllImport(LibGLib)]
        private static extern void g_error_free(IntPtr error);
    }
}
