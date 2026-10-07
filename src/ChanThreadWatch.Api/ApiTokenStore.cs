using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace JDP.Api {
    // The token file could not be written with owner-only access, or could not be written at all
    internal sealed class ApiTokenException : Exception {
        public ApiTokenException(string message, Exception innerException = null, bool ownerOnlyNotSupported = false)
            : base(message, innerException) {
            OwnerOnlyNotSupported = ownerOnlyNotSupported;
        }

        // The folder's file system does not keep owner-only access (FAT or exFAT on Windows, a mount without file
        // modes elsewhere), so no token file can be written there; not set for any other failure (access denied, a
        // sharing violation, a full disk)
        public bool OwnerOnlyNotSupported { get; }
    }

    // The API's one bearer token (maintainer decisions D2, D3; security item 15). Only its SHA-256 hash is kept, in
    // api-token.txt in the settings folder: one line "sha256:<64 lowercase hex>". The file is written to a temporary
    // file that only the current user can read (OwnerOnlyFile), then moved into place; if that access cannot be set,
    // nothing is written. The file is read again on every check, so a token made by another process (ctw api-token)
    // takes effect at once and the old one stops working. A file that is a link, or that others could read or write,
    // is not trusted: no token passes and the server does not start.
    internal sealed class ApiTokenStore {
        public const string FileName = "api-token.txt";
        public const string TokenPrefix = "ctw_";
        private const string HashPrefix = "sha256:";
        private const int TokenBytes = 32;
        private const int HexLength = 64;
        // More than the one valid line, so a longer file fails the parse
        private const int MaxFileBytes = 128;
        private const int Attempts = 10;
        // A reader holds the file only for a moment, but on Windows the replace fails for as long as any reader has it
        // open, so the replace is tried more often (up to about half a second in all)
        private const int MoveAttempts = 50;
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(10);

        private readonly string _path;

        public ApiTokenStore(string settingsFolder) {
            if (String.IsNullOrEmpty(settingsFolder)) throw new ArgumentException("No settings folder.", nameof(settingsFolder));
            _path = System.IO.Path.Combine(settingsFolder, FileName);
        }

        public string Path {
            get { return _path; }
        }

        // True when the file holds a valid hash and only the current user can read or write it
        public bool IsConfigured() {
            return ReadHash() != null;
        }

        // Makes a new token, saves its hash and returns the token; the caller shows it once and never saves it
        public string Generate() {
            string token = TokenPrefix + Base64Url(RandomNumberGenerator.GetBytes(TokenBytes));
            WriteHashFile(HashPrefix + Convert.ToHexStringLower(Hash(token)));
            return token;
        }

        // Writes this file's hash to the other store's file, which only the current user can read, as Generate writes it
        // (the app's settings folder move). Returns false, and writes nothing, when this file is missing or not trusted:
        // a token that does not pass here would not pass in the other folder either. Throws ApiTokenException when the
        // copy cannot be written with owner-only access.
        public bool CopyTo(ApiTokenStore destination) {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            byte[] hash = ReadHash();
            if (hash == null) return false;
            destination.WriteHashFile(HashPrefix + Convert.ToHexStringLower(hash));
            return true;
        }

        // A value longer than the limit is refused before it is hashed
        public bool Verify(string presented) {
            if (String.IsNullOrEmpty(presented) || presented.Length > ApiPolicy.MaxAuthorizationLength) return false;
            byte[] expected = ReadHash();
            return expected != null && CryptographicOperations.FixedTimeEquals(Hash(presented), expected);
        }

        private static byte[] Hash(string token) {
            return SHA256.HashData(Encoding.UTF8.GetBytes(token));
        }

        private static string Base64Url(byte[] bytes) {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        // Null when the file is missing, unreadable, not trusted or not one valid line
        private byte[] ReadHash() {
            string text = ReadTrustedText();
            return text != null ? ParseHash(text.TrimEnd('\r', '\n')) : null;
        }

        // A file being replaced by Generate (here or in another process) is read again after a short wait
        private string ReadTrustedText() {
            for (int attempt = 1; ; attempt++) {
                try {
                    return TryReadTrustedText();
                }
                catch (IOException ex) when (IsRetryable(ex, attempt)) {
                    Thread.Sleep(RetryDelay);
                }
                catch (Exception ex) when (IsUntrustedFailure(ex)) {
                    return null;
                }
            }
        }

        // Only a sharing violation (another program has the file open for a moment) is read again; anything else
        // fails the check at once
        internal static bool IsRetryable(IOException ex, int attempt) {
            return attempt < Attempts && IsSharingViolation(ex);
        }

        private static bool IsFileFailure(Exception ex) {
            return ex is IOException || ex is UnauthorizedAccessException;
        }

        // Also a file system without ACLs (NotSupportedException) or an ACL that cannot be read
        // (PrivilegeNotHeldException, an UnauthorizedAccessException): the file is not trusted
        private static bool IsUntrustedFailure(Exception ex) {
            return IsFileFailure(ex) || ex is NotSupportedException;
        }

        // Test only: runs between the link check and the open
        internal static Action OpeningForTesting { get; set; }

        // The path is checked for a link again once the file is open, so a link put in its place between the first check
        // and the open (which follows it on Unix) is refused
        private string TryReadTrustedText() {
            if (OwnerOnlyFile.IsLink(_path)) return null;
            OpeningForTesting?.Invoke();
            using (FileStream stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                if (OwnerOnlyFile.IsLink(_path) || !OwnerOnlyFile.IsOwnerOnly(stream)) return null;
                byte[] buffer = new byte[MaxFileBytes];
                int length = stream.ReadAtLeast(buffer, buffer.Length, false);
                return Encoding.ASCII.GetString(buffer, 0, length);
            }
        }

        // ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION (Windows)
        private static bool IsSharingViolation(IOException ex) {
            int code = ex.HResult & 0xFFFF;
            return code == 32 || code == 33;
        }

        internal static byte[] ParseHash(string line) {
            if (line.Length != HashPrefix.Length + HexLength || !line.StartsWith(HashPrefix, StringComparison.Ordinal)) return null;
            string hex = line.Substring(HashPrefix.Length);
            return IsLowerHex(hex) ? Convert.FromHexString(hex) : null;
        }

        private static bool IsLowerHex(string text) {
            foreach (char c in text) {
                if (!Char.IsAsciiHexDigitLower(c)) return false;
            }
            return true;
        }

        // Also used by the tests to write other content with the same access
        internal void WriteHashFile(string line) {
            string tempPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (FileStream stream = OwnerOnlyFile.Create(tempPath)) {
                    byte[] bytes = Encoding.ASCII.GetBytes(line + "\n");
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                MoveIntoPlace(tempPath);
            }
            catch (ApiTokenException) {
                TryDelete(tempPath);
                throw;
            }
            catch (Exception ex) when (IsWriteFailure(ex)) {
                TryDelete(tempPath);
                // An access check that throws NotSupportedException means a file system without ACLs
                throw new ApiTokenException("The API token file could not be written: " + _path, ex, ex is NotSupportedException);
            }
        }

        // Also an access check that throws on a file system without ACLs (NotSupportedException)
        private static bool IsWriteFailure(Exception ex) {
            return IsFileFailure(ex) || ex is InvalidOperationException || ex is NotSupportedException;
        }

        // A reader that has the file open for a moment (Verify, here or in the running program) makes the replace fail
        // on Windows, as access denied (UnauthorizedAccessException) or a sharing violation; it is tried again for a
        // short while
        private void MoveIntoPlace(string tempPath) {
            for (int attempt = 1; ; attempt++) {
                try {
                    File.Move(tempPath, _path, true);
                    return;
                }
                catch (Exception ex) when (attempt < MoveAttempts && IsFileFailure(ex)) {
                    Thread.Sleep(RetryDelay);
                }
            }
        }

        private static void TryDelete(string path) {
            try {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
            }
        }
    }
}
