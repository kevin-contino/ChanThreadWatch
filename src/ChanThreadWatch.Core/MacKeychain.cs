using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace JDP {
    // macOS: each login is a generic password item in the default keychain, with the service
    // name and the id as its account. Reading an item that another build of the app wrote can
    // show an "allow access" dialog (see KeyringStoredAuthProtector for the timeout). Security.framework and CoreFoundation are called directly.
    // Every CoreFoundation object created here, and the one SecItemCopyMatching returns, is
    // released before the call returns (see CFScope); the constants are not owned and never
    // released. The login's bytes are cleared once CoreFoundation has copied them.
    internal sealed class MacKeychain : ILoginKeyring {
        private const string SecurityPath = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundationPath = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const int ErrSecSuccess = 0;
        private const int ErrSecItemNotFound = -25300;
        private const int ErrSecDuplicateItem = -25299;
        private const string Label = "Chan Thread Watch login";

        private static readonly Dictionary<int, string> _statusNames = new Dictionary<int, string> {
            { -25308, "errSecInteractionNotAllowed: user interaction is not allowed (locked keychain or access prompt)" },
            { -25293, "errSecAuthFailed" },
            { -25294, "errSecNoSuchKeychain" },
            { -25291, "errSecNotAvailable: no keychain is available" },
            { -25299, "errSecDuplicateItem" },
            { -128, "errSecUserCanceled" }
        };

        private static readonly object _constantsSync = new object();
        private static Constants _constants;

        private readonly string _service;

        public MacKeychain(string service) {
            if (String.IsNullOrEmpty(service)) throw new ArgumentException("A service name is required.", "service");
            _service = service;
        }

        public string Prefix {
            get { return StoredAuth.KeychainPrefix; }
        }

        public string Name {
            get { return "the macOS Keychain"; }
        }

        public void Store(string id, string login) {
            byte[] secret = Encoding.UTF8.GetBytes(login);
            try {
                using (CFScope cf = new CFScope(GetConstants())) {
                    Check(StoreData(cf, id, cf.Data(secret)));
                }
            }
            finally {
                Array.Clear(secret, 0, secret.Length);
            }
        }

        // Updates the item, or adds it. If another process added it in between, the update is tried once more.
        private int StoreData(CFScope cf, string id, IntPtr data) {
            int status = Update(cf, id, data);
            if (status == ErrSecItemNotFound) status = SecItemAdd(NewItem(cf, id, data), IntPtr.Zero);
            return status == ErrSecDuplicateItem ? Update(cf, id, data) : status;
        }

        private int Update(CFScope cf, string id, IntPtr data) {
            return SecItemUpdate(ItemQuery(cf, id), cf.Dictionary(new[] { cf.C.ValueData }, new[] { data }));
        }

        public string Lookup(string id) {
            using (CFScope cf = new CFScope(GetConstants())) {
                Constants c = cf.C;
                IntPtr query = cf.Dictionary(new[] { c.Class, c.AttrService, c.AttrAccount, c.ReturnData, c.MatchLimit },
                    new[] { c.ClassGenericPassword, cf.String(_service), cf.String(id), c.BooleanTrue, c.MatchLimitOne });
                IntPtr result;
                int status = SecItemCopyMatching(query, out result);
                if (status == ErrSecItemNotFound) return null;
                Check(status);
                // Only set on success; a copied object is the caller's to release
                cf.OwnIfAny(result);
                return ReadUtf8(result);
            }
        }

        public void Clear(string id) {
            using (CFScope cf = new CFScope(GetConstants())) {
                int status = SecItemDelete(ItemQuery(cf, id));
                if (status != ErrSecItemNotFound) Check(status);
            }
        }

        private IntPtr ItemQuery(CFScope cf, string id) {
            Constants c = cf.C;
            return cf.Dictionary(new[] { c.Class, c.AttrService, c.AttrAccount }, new[] { c.ClassGenericPassword, cf.String(_service), cf.String(id) });
        }

        private IntPtr NewItem(CFScope cf, string id, IntPtr data) {
            Constants c = cf.C;
            return cf.Dictionary(new[] { c.Class, c.AttrService, c.AttrAccount, c.AttrLabel, c.ValueData },
                new[] { c.ClassGenericPassword, cf.String(_service), cf.String(id), cf.String(Label), data });
        }

        private static void Check(int status) {
            if (status == ErrSecSuccess) return;
            string name;
            throw new KeyringException("OSStatus " + status + (_statusNames.TryGetValue(status, out name) ? " (" + name + ")" : String.Empty));
        }

        private static string ReadUtf8(IntPtr data) {
            if (data == IntPtr.Zero) throw new KeyringException("The keychain returned no data for the item.");
            nint length = CFDataGetLength(data);
            if (length == 0) return String.Empty;
            byte[] bytes = new byte[checked((int)length)];
            Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, bytes.Length);
            try {
                return Encoding.UTF8.GetString(bytes);
            }
            finally {
                Array.Clear(bytes, 0, bytes.Length);
            }
        }

        private static Constants GetConstants() {
            lock (_constantsSync) {
                if (_constants == null) _constants = new Constants();
                return _constants;
            }
        }

        // The Security and CoreFoundation constants, read from the frameworks once. The frameworks
        // stay loaded for the life of the process.
        private sealed class Constants {
            public readonly IntPtr Class, ClassGenericPassword, AttrService, AttrAccount, AttrLabel, ValueData, ReturnData, MatchLimit, MatchLimitOne;
            public readonly IntPtr BooleanTrue, KeyCallBacks, ValueCallBacks;

            public Constants() {
                IntPtr security = NativeLibrary.Load(SecurityPath);
                IntPtr coreFoundation = NativeLibrary.Load(CoreFoundationPath);
                Class = ReadPointer(security, "kSecClass");
                ClassGenericPassword = ReadPointer(security, "kSecClassGenericPassword");
                AttrService = ReadPointer(security, "kSecAttrService");
                AttrAccount = ReadPointer(security, "kSecAttrAccount");
                AttrLabel = ReadPointer(security, "kSecAttrLabel");
                ValueData = ReadPointer(security, "kSecValueData");
                ReturnData = ReadPointer(security, "kSecReturnData");
                MatchLimit = ReadPointer(security, "kSecMatchLimit");
                MatchLimitOne = ReadPointer(security, "kSecMatchLimitOne");
                BooleanTrue = ReadPointer(coreFoundation, "kCFBooleanTrue");
                // The callbacks are structs: their address is what CFDictionaryCreate takes
                KeyCallBacks = NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryKeyCallBacks");
                ValueCallBacks = NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryValueCallBacks");
            }

            private static IntPtr ReadPointer(IntPtr library, string name) {
                IntPtr value = Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));
                if (value == IntPtr.Zero) throw new KeyringException(name + " is null.");
                return value;
            }
        }

        // Owns the CoreFoundation objects of one call and releases them all on Dispose, whether
        // the call succeeded or threw
        private sealed class CFScope : IDisposable {
            private readonly List<IntPtr> _owned = new List<IntPtr>();

            public CFScope(Constants constants) {
                C = constants;
            }

            public Constants C { get; private set; }

            public IntPtr String(string value) {
                return Own(CFStringCreateWithCharacters(IntPtr.Zero, value, value.Length));
            }

            public IntPtr Data(byte[] bytes) {
                return Own(CFDataCreate(IntPtr.Zero, bytes, bytes.Length));
            }

            // The dictionary retains its keys and values, so the scope still releases its own references
            public IntPtr Dictionary(IntPtr[] keys, IntPtr[] values) {
                return Own(CFDictionaryCreate(IntPtr.Zero, keys, values, keys.Length, C.KeyCallBacks, C.ValueCallBacks));
            }

            public void OwnIfAny(IntPtr cf) {
                if (cf != IntPtr.Zero) _owned.Add(cf);
            }

            private IntPtr Own(IntPtr cf) {
                if (cf == IntPtr.Zero) throw new KeyringException("CoreFoundation could not create an object.");
                _owned.Add(cf);
                return cf;
            }

            public void Dispose() {
                for (int i = _owned.Count - 1; i >= 0; i--) {
                    CFRelease(_owned[i]);
                }
                _owned.Clear();
            }
        }

        [DllImport(CoreFoundationPath, CharSet = CharSet.Unicode)]
        private static extern IntPtr CFStringCreateWithCharacters(IntPtr allocator, string chars, nint numChars);

        [DllImport(CoreFoundationPath)]
        private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);

        [DllImport(CoreFoundationPath)]
        private static extern nint CFDataGetLength(IntPtr data);

        [DllImport(CoreFoundationPath)]
        private static extern IntPtr CFDataGetBytePtr(IntPtr data);

        [DllImport(CoreFoundationPath)]
        private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint numValues, IntPtr keyCallBacks, IntPtr valueCallBacks);

        [DllImport(CoreFoundationPath)]
        private static extern void CFRelease(IntPtr cf);

        [DllImport(SecurityPath)]
        private static extern int SecItemAdd(IntPtr attributes, IntPtr result);

        [DllImport(SecurityPath)]
        private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);

        [DllImport(SecurityPath)]
        private static extern int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);

        [DllImport(SecurityPath)]
        private static extern int SecItemDelete(IntPtr query);
    }
}
