using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace JDP.Api {
    // A code made by Create: shown once by the creator (the app's dialog, ctw api-pair), never saved
    internal sealed class ApiPairingCode {
        public ApiPairingCode(string code, string id, DateTimeOffset expires) {
            Code = code;
            Id = id;
            Expires = expires;
        }

        // XXXX-XXXX
        public string Code { get; }
        public string Id { get; }
        public DateTimeOffset Expires { get; }
    }

    // The pending code as api-pairing.txt holds it: never the code, only its salt and the key stretched from it
    internal sealed class ApiPendingPairing {
        public ApiPendingPairing(string id, string salt, byte[] key, DateTimeOffset expires, string pairedFamily) {
            Id = id;
            Salt = salt;
            Key = key;
            Expires = expires;
            PairedFamily = pairedFamily;
        }

        // base64url of 16 random bytes; the pairingId of the messages
        public string Id { get; }
        // base64url of 16 random bytes
        public string Salt { get; }
        public byte[] Key { get; }
        public DateTimeOffset Expires { get; }
        // Null while pending; the family once a browser paired with this code
        public string PairedFamily { get; }
    }

    // Whether the file holds a code, as read just now
    internal enum CodeInFile {
        Holds,
        // Another code, none, or content that is not trusted
        Other,
        // The file could not be read (in use past the retries, access denied)
        Unknown
    }

    // The one pending pairing code (MP-7b design E4): api-pairing.txt in the settings folder, owner-only and written as
    // api-token.txt is. The creator (the app's dialog or ctw api-pair) makes it and polls it for the result; the server
    // reads it on each pairing request, deletes it when the code burns or expires, and marks it paired on success. A
    // new code replaces the file. Six lines: "1", "id:<base64url>", "salt:<base64url>", "key:<64 hex>", "expires:<unix
    // seconds>", "state:pending" or "state:paired:<family>". The code itself is never written.
    //
    // The server and a creator both write the file, in two processes, without a lock between them. Every writer reads
    // the file again and checks the id just before it acts, which narrows the window; a write by the other side
    // between that check and the action can still be hit. So a creator compares the file's id with its own code
    // before it shows "paired" or "ended", and deletes the file only with Delete(its own id).
    internal sealed class ApiPairingFile {
        private const string ReadFailure = "The pairing code file could not be read: ";
        public const string FileName = "api-pairing.txt";
        private const string FormatVersion = "1";
        private const int MaxFileBytes = 256;
        private const string PendingState = "pending";
        private const string PairedState = "paired:";
        private const string WriteFailure = "The pairing code file could not be written: ";

        private readonly string _path;

        public ApiPairingFile(string settingsFolder) {
            if (String.IsNullOrEmpty(settingsFolder)) throw new ArgumentException("No settings folder.", nameof(settingsFolder));
            _path = System.IO.Path.Combine(settingsFolder, FileName);
        }

        public string Path {
            get { return _path; }
        }

        // Makes a new code, valid for ApiPolicy.PairingCodeLifetime from now, writes its salt and stretched key in
        // place of any older code, and returns it once. Runs PBKDF2 (600,000 iterations), so a UI calls it off its UI
        // thread. Throws ApiTokenException when the file cannot be written with owner-only access.
        public ApiPairingCode Create(DateTimeOffset now) {
            string code = ApiPairing.NewCode();
            string id = ApiPairing.Base64Url(RandomNumberGenerator.GetBytes(ApiPairing.IdBytes));
            byte[] salt = RandomNumberGenerator.GetBytes(ApiPairing.SaltBytes);
            DateTimeOffset expires = DateTimeOffset.FromUnixTimeSeconds((now + ApiPolicy.PairingCodeLifetime).ToUnixTimeSeconds());
            Write(new ApiPendingPairing(id, ApiPairing.Base64Url(salt), ApiPairing.DeriveKey(code, salt), expires, null));
            return new ApiPairingCode(ApiPairing.FormatCode(code), id, expires);
        }

        // The pending code, or null when there is none. Untrusted is set when a file is there but is a link, others
        // could read or write it, it could not be read, or it does not parse.
        public ApiPendingPairing Read(out bool untrusted) {
            bool unreadable;
            return Read(out untrusted, out unreadable);
        }

        // The same; unreadable is also set (with untrusted) when the file is there but could not be read at all (in use
        // past the retries, access denied), as opposed to content that is not trusted
        public ApiPendingPairing Read(out bool untrusted, out bool unreadable) {
            TrustedRead status;
            string text = ApiTokenStore.ReadTrustedText(_path, MaxFileBytes, out status);
            ApiPendingPairing pending = text != null ? Parse(text) : null;
            untrusted = pending == null && status != TrustedRead.Missing;
            unreadable = status == TrustedRead.Failed;
            return pending;
        }

        // Test only: runs when a writer is about to act on the file (mark it or delete it), before it reads it again
        internal static Action ActingForTesting { get; set; }

        // Marks the code used by a browser of the family, so the creator can show the result. The file is read again
        // first and written only if it still holds this code; a file that is gone (the creator cancelled) is never made
        // again. That check narrows the window for a newer code written meanwhile, it does not close it. False when
        // the file holds another code or none. Throws ApiTokenException when the file cannot be read again (the
        // caller then ends the code) or written.
        public bool MarkPaired(ApiPendingPairing pending, string family) {
            ActingForTesting?.Invoke();
            CodeInFile check = Check(pending.Id);
            if (check == CodeInFile.Unknown) throw new ApiTokenException(ReadFailure + _path);
            if (check == CodeInFile.Other) return false;
            Write(new ApiPendingPairing(pending.Id, pending.Salt, pending.Key, pending.Expires, family));
            return true;
        }

        // Ends the code with this id. The file is read again first and left when it holds another code or none, which
        // narrows the window for a newer code written meanwhile (it does not close it). A file that cannot be read is
        // deleted anyway: a newer code lost that way only costs a new code. False when the file is still there and
        // holds the code, or cannot be read (it could not be deleted).
        public bool Delete(string id) {
            ActingForTesting?.Invoke();
            if (Check(id) == CodeInFile.Other) return true;
            ApiTokenStore.TryDelete(_path);
            return !File.Exists(_path) || Check(id) == CodeInFile.Other;
        }

        internal CodeInFile Check(string id) {
            TrustedRead status;
            string text = ApiTokenStore.ReadTrustedText(_path, MaxFileBytes, out status);
            if (status == TrustedRead.Failed) return CodeInFile.Unknown;
            return text != null && Parse(text)?.Id == id ? CodeInFile.Holds : CodeInFile.Other;
        }

        private void Write(ApiPendingPairing pending) {
            string state = pending.PairedFamily == null ? PendingState : PairedState + pending.PairedFamily;
            string[] lines = {
                FormatVersion, "id:" + pending.Id, "salt:" + pending.Salt, "key:" + Convert.ToHexStringLower(pending.Key),
                "expires:" + pending.Expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), "state:" + state
            };
            ApiTokenStore.WriteOwnerOnlyText(_path, String.Join("\n", lines) + "\n", WriteFailure);
        }

        // Null unless the file is exactly the six lines, each value in its one form
        internal static ApiPendingPairing Parse(string text) {
            string[] lines = text.Split('\n');
            if (lines.Length != 7 || lines[0] != FormatVersion || lines[6].Length != 0) return null;
            return Create(Value(lines[1], "id:"), Value(lines[2], "salt:"), ApiTokenStore.ParseHash("sha256:" + Value(lines[3], "key:")),
                ParseSeconds(Value(lines[4], "expires:")), ParseState(Value(lines[5], "state:")));
        }

        private static ApiPendingPairing Create(string id, string salt, byte[] key, long? expires, string family) {
            if (!IsValid(id, salt, key, expires, family)) return null;
            return new ApiPendingPairing(id, salt, key, DateTimeOffset.FromUnixTimeSeconds(expires.Value), family.Length == 0 ? null : family);
        }

        private static bool IsValid(string id, string salt, byte[] key, long? expires, string family) {
            return ApiPairing.FromBase64Url(id, ApiPairing.IdBytes) != null && ApiPairing.FromBase64Url(salt, ApiPairing.SaltBytes) != null && key != null && expires.HasValue && family != null;
        }

        // The text after the prefix, or null
        private static string Value(string line, string prefix) {
            return line != null && line.StartsWith(prefix, StringComparison.Ordinal) ? line.Substring(prefix.Length) : null;
        }

        // 1 to 11 ASCII digits, so the value is always a valid DateTimeOffset
        private static long? ParseSeconds(string text) {
            if (text == null || text.Length == 0 || text.Length > 11 || !text.All(Char.IsAsciiDigit)) return null;
            return Int64.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        // "" for pending, the family for paired, null for anything else
        private static string ParseState(string state) {
            if (state == PendingState) return "";
            string family = Value(state, PairedState);
            return Array.IndexOf(ApiPairing.Families, family) >= 0 ? family : null;
        }
    }
}
