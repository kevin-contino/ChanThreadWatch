using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace JDP.Api {
    // How a read of an owner-only file ended (ApiTokenStore.ReadTrustedText)
    internal enum TrustedRead {
        Read,
        // No file (or no folder)
        Missing,
        // A link, access for others, a file system without ACLs, or more than the limit
        Untrusted,
        // An I/O or access failure: a sharing violation after the retries, access denied, an ACL that can't be read
        Failed
    }

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
        // More than the one valid line. A longer file is refused as a whole, never cut: the line followed by blank lines
        // or other padding past this size is not trusted (stricter than a parse of the first bytes would be).
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

        internal static byte[] Hash(string token) {
            return SHA256.HashData(Encoding.UTF8.GetBytes(token));
        }

        private static string Base64Url(byte[] bytes) {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        // The token's SHA-256 hash (also the key of the script's proof, POST /api/v1/proof). Null when the file is
        // missing, unreadable, not trusted or not one valid line.
        internal byte[] ReadHash() {
            string text = ReadTrustedText(_path, MaxFileBytes);
            return text != null ? ParseHash(text.TrimEnd('\r', '\n')) : null;
        }

        // The text of an owner-only file of at most that many bytes (ASCII), or null when it is missing, unreadable,
        // longer, a link, or others could read or write it. Also used for api-clients.txt and api-pairing.txt. A file
        // being replaced (here or in another process) is read again after a short wait.
        internal static string ReadTrustedText(string path, int maxBytes) {
            TrustedRead status;
            return ReadTrustedText(path, maxBytes, out status);
        }

        // The same, and how the read ended, so a writer can tell a file it may replace (missing or untrusted) from one
        // it could not read
        internal static string ReadTrustedText(string path, int maxBytes, out TrustedRead status) {
            for (int attempt = 1; ; attempt++) {
                try {
                    string text = TryReadTrustedText(path, maxBytes);
                    status = text != null ? TrustedRead.Read : TrustedRead.Untrusted;
                    return text;
                }
                catch (IOException ex) when (IsRetryable(ex, attempt)) {
                    Thread.Sleep(RetryDelay);
                }
                catch (Exception ex) when (IsUntrustedFailure(ex)) {
                    status = Classify(ex);
                    return null;
                }
            }
        }

        private static TrustedRead Classify(Exception ex) {
            if (ex is FileNotFoundException || ex is DirectoryNotFoundException) return TrustedRead.Missing;
            return ex is NotSupportedException ? TrustedRead.Untrusted : TrustedRead.Failed;
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

        // Test only: runs between the link check and the open, with the file's path
        internal static Action<string> OpeningForTesting { get; set; }

        // The path is checked for a link again once the file is open, so a link put in its place between the first check
        // and the open (which follows it on Unix) is refused
        private static string TryReadTrustedText(string path, int maxBytes) {
            if (OwnerOnlyFile.IsLink(path)) return null;
            OpeningForTesting?.Invoke(path);
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                if (OwnerOnlyFile.IsLink(path) || !OwnerOnlyFile.IsOwnerOnly(stream)) return null;
                return ReadAtMost(stream, maxBytes);
            }
        }

        // Null when the file is longer
        private static string ReadAtMost(FileStream stream, int maxBytes) {
            byte[] buffer = new byte[maxBytes + 1];
            int length = stream.ReadAtLeast(buffer, buffer.Length, false);
            return length <= maxBytes ? Encoding.ASCII.GetString(buffer, 0, length) : null;
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
            WriteOwnerOnlyText(_path, line + "\n", "The API token file could not be written: ");
        }

        // Writes the text (ASCII) to a temporary file that only the current user can read, then moves it into place;
        // if that access cannot be set, nothing is written. Also used for api-clients.txt and api-pairing.txt. Throws
        // ApiTokenException with the failure text and the path.
        internal static void WriteOwnerOnlyText(string path, string text, string failure) {
            string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (FileStream stream = OwnerOnlyFile.Create(tempPath)) {
                    byte[] bytes = Encoding.ASCII.GetBytes(text);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                MoveIntoPlace(tempPath, path);
            }
            catch (ApiTokenException) {
                TryDelete(tempPath);
                throw;
            }
            catch (Exception ex) when (IsWriteFailure(ex)) {
                TryDelete(tempPath);
                // An access check that throws NotSupportedException means a file system without ACLs
                throw new ApiTokenException(failure + path, ex, ex is NotSupportedException);
            }
        }

        // Also an access check that throws on a file system without ACLs (NotSupportedException)
        private static bool IsWriteFailure(Exception ex) {
            return IsFileFailure(ex) || ex is InvalidOperationException || ex is NotSupportedException;
        }

        // A reader that has the file open for a moment (Verify, here or in the running program) makes the replace fail
        // on Windows, as access denied (UnauthorizedAccessException) or a sharing violation; it is tried again for a
        // short while
        private static void MoveIntoPlace(string tempPath, string path) {
            for (int attempt = 1; ; attempt++) {
                try {
                    File.Move(tempPath, path, true);
                    return;
                }
                catch (Exception ex) when (attempt < MoveAttempts && IsFileFailure(ex)) {
                    Thread.Sleep(RetryDelay);
                }
            }
        }

        internal static void TryDelete(string path) {
            try {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
            }
        }
    }
}
