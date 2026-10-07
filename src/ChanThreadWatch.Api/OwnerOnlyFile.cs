using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace JDP.Api {
    // A file that only the current user can read: on Windows a protected DACL with one allow rule for the user, who
    // also owns the file; elsewhere mode 0600. Create makes such a file and reads its access back; IsOwnerOnly checks an
    // open one, so a file that another user or program could have written or read (an inherited ACL, another allow
    // rule, mode 0644, a link) is not trusted.
    internal static class OwnerOnlyFile {
        private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        private const UnixFileMode GroupAndOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                                   UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

        // Root on Linux or macOS: it can open every file, so a successful open with mode 0600 does not prove that the
        // file is its own. The API refuses to run then. The tests put another check in its place.
        internal static Func<bool> IsRootOnUnix { get; set; } = () => !OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess;

        // Test only: stands in for the check of a new file's access, as on a volume that keeps no ACL (FAT)
        internal static Func<FileStream, bool> NewFileCheckForTesting { get; set; }

        // A new file (CreateNew), open for writing. Throws ApiTokenException when the file system does not keep the
        // access (the caller deletes the file). The file is closed before anything is thrown, so the caller can delete it.
        public static FileStream Create(string path) {
            FileStream stream = OperatingSystem.IsWindows() ? CreateOnWindows(path) : CreateOnUnix(path);
            if (!IsNewFileOwnerOnly(stream)) {
                stream.Dispose();
                throw new ApiTokenException(OperatingSystem.IsWindows() ?
                    "The settings folder does not support owner-only files; move it to an NTFS folder." :
                    "The settings folder does not keep file permissions (mode 0600); move it to a local folder.", null, true);
            }
            return stream;
        }

        // An access check that throws (an ACL that can't be read) closes the file too
        private static bool IsNewFileOwnerOnly(FileStream stream) {
            try {
                return (NewFileCheckForTesting ?? IsOwnerOnly)(stream);
            }
            catch {
                stream.Dispose();
                throw;
            }
        }

        // True for a symbolic link or other reparse point, which is never followed (checked before the open, since on
        // Unix the open follows a link)
        public static bool IsLink(string path) {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).LinkTarget != null;
        }

        // The open file: not a reparse point, and only the current user has access
        public static bool IsOwnerOnly(FileStream stream) {
            if ((File.GetAttributes(stream.SafeFileHandle) & FileAttributes.ReparsePoint) != 0) return false;
            return OperatingSystem.IsWindows() ? IsOwnerOnlyOnWindows(stream) : IsOwnerOnlyOnUnix(stream);
        }

        [SupportedOSPlatform("windows")]
        private static SecurityIdentifier CurrentUser() {
            return WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("The current user has no security identifier.");
        }

        // Opened with ReadPermissions too, so the new file's DACL can be read back
        [SupportedOSPlatform("windows")]
        private static FileStream CreateOnWindows(string path) {
            SecurityIdentifier user = CurrentUser();
            FileSecurity security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(user);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            FileSystemRights rights = FileSystemRights.Write | FileSystemRights.ReadPermissions | FileSystemRights.Synchronize;
            return new FileInfo(path).Create(FileMode.CreateNew, rights, FileShare.None, 4096, FileOptions.None, security);
        }

        // Owned by the current user, not inheriting, and no allow rule for anyone else (what CreateOnWindows makes)
        [SupportedOSPlatform("windows")]
        private static bool IsOwnerOnlyOnWindows(FileStream stream) {
            SecurityIdentifier user = CurrentUser();
            FileSecurity security = stream.GetAccessControl();
            return user.Equals(security.GetOwner(typeof(SecurityIdentifier))) && security.AreAccessRulesProtected && AllowsOnly(security, user);
        }

        [SupportedOSPlatform("windows")]
        private static bool AllowsOnly(FileSecurity security, SecurityIdentifier user) {
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier))) {
                if (rule.AccessControlType == AccessControlType.Allow && !user.Equals(rule.IdentityReference)) return false;
            }
            return true;
        }

        // Created with mode 0600 (the umask can only remove bits); Create checks it
        [UnsupportedOSPlatform("windows")]
        private static FileStream CreateOnUnix(string path) {
            return new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = OwnerReadWrite });
        }

        // No access for group or others, and not root. With that mode only the owner (or root) can open the file, so
        // a file this process could open is its own; .NET has no managed call for the owner's uid.
        [UnsupportedOSPlatform("windows")]
        private static bool IsOwnerOnlyOnUnix(FileStream stream) {
            return !IsRootOnUnix() && (File.GetUnixFileMode(stream.SafeFileHandle) & GroupAndOther) == 0;
        }
    }
}
