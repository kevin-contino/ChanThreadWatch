using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;

namespace JDP {
    public static class Settings {
        private static readonly object _sync = new object();
        private static Dictionary<string, string> _settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static bool _saveBlocked;
        private static bool _checkedCopies;
        private static readonly string[] _authSettingNames = { "PageAuth", "ImageAuth" };
        // Logins set on a system that can't keep them (see StoredAuth.CanProtect): used for the
        // session only, while the file keeps what it had
        private static Dictionary<string, string> _sessionAuth = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string ApplicationName {
            get { return "Chan Thread Watch"; }
        }

        public static bool? UseCustomUserAgent {
            get { return GetBool("UseCustomUserAgent"); }
            set { SetBool("UseCustomUserAgent", value); }
        }

        public static string CustomUserAgent {
            get { return Get("CustomUserAgent"); }
            set { Set("CustomUserAgent", value); }
        }

        public static bool? UsePageAuth {
            get { return GetBool("UsePageAuth"); }
            set { SetBool("UsePageAuth", value); }
        }

        public static string PageAuth {
            get { return GetAuth("PageAuth"); }
            set { SetAuth("PageAuth", value); }
        }

        public static bool? UseImageAuth {
            get { return GetBool("UseImageAuth"); }
            set { SetBool("UseImageAuth", value); }
        }

        public static string ImageAuth {
            get { return GetAuth("ImageAuth"); }
            set { SetAuth("ImageAuth", value); }
        }

        public static bool? OneTimeDownload {
            get { return GetBool("OneTimeDownload"); }
            set { SetBool("OneTimeDownload", value); }
        }

        public static bool? AutoFollow {
            get { return GetBool("AutoFollow"); }
            set { SetBool("AutoFollow", value); }
        }

        public static int? CheckEvery {
            get { return GetInt("CheckEvery"); }
            set { SetInt("CheckEvery", value); }
        }

        public static bool? DownloadFolderIsRelative {
            get { return GetBool("DownloadFolderIsRelative"); }
            set { SetBool("DownloadFolderIsRelative", value); }
        }

        public static string DownloadFolder {
            get { return Get("DownloadFolder"); }
            set {
                Set("DownloadFolder", value);
                DownloadFolderForSession = null;
            }
        }

        // An absolute folder this session uses in place of the DownloadFolder setting, which is kept as
        // written: set at startup when the setting is an absolute path of another OS, so a portable
        // settings folder used on Windows and on Linux or macOS keeps each OS's path. Cleared when the
        // setting is changed or the settings are loaded.
        public static string DownloadFolderForSession { get; set; }

        public static bool? CompletedFolderIsRelative {
            get { return GetBool("CompletedFolderIsRelative"); }
            set { SetBool("CompletedFolderIsRelative", value); }
        }

        public static string CompletedFolder {
            get { return Get("CompletedFolder"); }
            set {
                Set("CompletedFolder", value);
                CompletedFolderForSession = null;
            }
        }

        // As DownloadFolderForSession, for the CompletedFolder setting
        public static string CompletedFolderForSession { get; set; }
        
        public static bool? MoveToCompletedFolder {
            get { return GetBool("MoveToCompletedFolder"); }
            set { SetBool("MoveToCompletedFolder", value); }
        }

        public static bool? RenameDownloadFolderWithDescription {
            get { return GetBool("RenameDownloadFolderWithDescription"); }
            set { SetBool("RenameDownloadFolderWithDescription", value); }
        }

        public static bool? RenameDownloadFolderWithCategory {
            get { return GetBool("RenameDownloadFolderWithCategory"); }
            set { SetBool("RenameDownloadFolderWithCategory", value); }
        }

        public static bool? RenameDownloadFolderWithParentThreadDescription {
            get { return GetBool("RenameDownloadFolderWithParentThreadDescription"); }
            set { SetBool("RenameDownloadFolderWithParentThreadDescription", value); }
        }

        public static string ParentThreadDescriptionFormat {
            get { return Get("ParentThreadDescriptionFormat"); }
            set { Set("ParentThreadDescriptionFormat", value); }
        }

        public const string DefaultParentThreadDescriptionFormat = " ({Parent})";

        public static bool? ChildThreadsAreNewFormat {
            get { return GetBool("ChildThreadsAreNewFormat"); }
            set { SetBool("ChildThreadsAreNewFormat", value); }
        }

        public static bool? SortImagesByPoster {
            get { return GetBool("SortImagesByPoster"); }
            set { SetBool("SortImagesByPoster", value); }
        }

        public static bool? RecursiveAutoFollow {
            get { return GetBool("RecursiveAutoFollow"); }
            set { SetBool("RecursiveAutoFollow", value); }
        }

        public static bool? InterBoardAutoFollow {
            get { return GetBool("InterBoardAutoFollow"); }
            set { SetBool("InterBoardAutoFollow", value); }
        }

        public static bool? SaveThumbnails {
            get { return GetBool("SaveThumbnails"); }
            set { SetBool("SaveThumbnails", value); }
        }

        public static bool? UseOriginalFileNames {
            get { return GetBool("UseOriginalFileNames"); }
            set { SetBool("UseOriginalFileNames", value); }
        }

        public static bool? VerifyImageHashes {
            get { return GetBool("VerifyImageHashes"); }
            set { SetBool("VerifyImageHashes", value); }
        }

        public static bool? UseSlug {
            get { return GetBool("UseSlug"); }
            set { SetBool("UseSlug", value); }
        }

        public static SlugType SlugType {
            get {
                string value = Get("SlugType") ?? String.Empty;
                if (String.IsNullOrEmpty(value)) return SlugType.Last;
                SlugType valueSlug;
                try {
                    valueSlug = (SlugType)Enum.Parse(typeof (SlugType), value);
                }
                catch (ArgumentException) {
                    valueSlug = SlugType.Last;
                }
                return valueSlug;
            }
            set { Set("SlugType", value.ToString()); }
        }

        public static bool? CheckForUpdates {
            get { return GetBool("CheckForUpdates"); }
            set { SetBool("CheckForUpdates", value); }
        }

        public static DateTime? LastUpdateCheck {
            get { return GetDate("LastUpdateCheck"); }
            set { SetDate("LastUpdateCheck", value); }
        }

        public static string LatestUpdateVersion {
            get { return Get("LatestUpdateVersion"); }
            set { Set("LatestUpdateVersion", value); }
        }

        public static bool? BlacklistWildcards {
            get { return GetBool("BlacklistWildcards"); }
            set { SetBool("BlacklistWildcards", value); }
        }

        public static bool? MinimizeToTray {
            get { return GetBool("MinimizeToTray"); }
            set {  SetBool("MinimizeToTray", value); }
        }

        public static bool? BackupThreadList {
            get { return GetBool("BackupThreadList"); }
            set { SetBool("BackupThreadList", value); }
        }

        public static int? BackupEvery {
            get { return GetInt("BackupEvery"); }
            set { SetInt("BackupEvery", value); }
        }

        public static bool? BackupCheckSize {
            get { return GetBool("BackupCheckSize"); }
            set { SetBool("BackupCheckSize", value); }
        }

        public static long? MaximumBytesPerSecond {
            get { return GetLong("MaximumBytesPerSecond"); }
            set { SetLong("MaximumBytesPerSecond", value); }
        }
        
        public static string WindowTitle {
            get { return Get("WindowTitle"); }
            set { Set("WindowTitle", value); }
        }

        public static bool? UseExeDirectoryForSettings { get; set; }

        public static string ExeDirectory {
            // The folder of the app's exe. A single-file app has no assembly file paths, so this does not use Assembly.Location.
            get { return Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory); }
        }

        public static string AppDataDirectory {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ApplicationName); }
        }

        public static string SettingsFileName {
            get { return "settings.txt"; }
        }

        public static string ThreadsFileName {
            get { return "threads.txt"; }
        }

        public static string LogFileName {
            get { return "log.txt"; }
        }

        public static string BlacklistFileName {
            get { return "blacklist.txt"; }
        }

        public static string DebugFolderName {
            get { return "Debug"; }
        }

        public static ThreadDoubleClickAction? OnThreadDoubleClick {
            get {
                int x = GetInt("OnThreadDoubleClick") ?? -1;
                return Enum.IsDefined(typeof (ThreadDoubleClickAction), x) ?
                    (ThreadDoubleClickAction?)x : null;
            }
            set { SetInt("OnThreadDoubleClick", value.HasValue ? (int?)value.Value : null); }
        }

        public static string GetSettingsDirectory() {
            if (UseExeDirectoryForSettings == null) {
                #if DEBUG
                    UseExeDirectoryForSettings = File.Exists(Path.Combine(Path.Combine(ExeDirectory, DebugFolderName), SettingsFileName));
                #else
                    UseExeDirectoryForSettings = File.Exists(Path.Combine(ExeDirectory, SettingsFileName));
                #endif
            }
            return GetSettingsDirectory(UseExeDirectoryForSettings.Value);
        }

        public static string GetSettingsDirectory(bool useExeDirForSettings) {
            if (useExeDirForSettings) {
                #if DEBUG
                    return Path.Combine(ExeDirectory, DebugFolderName);
                #else
                    return ExeDirectory;
                #endif
            }
            else {
                #if DEBUG
                    string dir = Path.Combine(AppDataDirectory, DebugFolderName);
                #else
                    string dir = AppDataDirectory;
                #endif
                if (!Directory.Exists(dir)) {
                    Directory.CreateDirectory(dir);
                }
                return dir;
            }
        }

        // Throws FormatException if the folder is an absolute path of another OS (see General.ToLocalDirectoryPath)
        public static string AbsoluteDownloadDirectory {
            get { return GetAbsoluteDirectory("DownloadFolder", DownloadFolderForSession, DownloadFolderIsRelative); }
        }

        // Throws FormatException if the folder is an absolute path of another OS (see General.ToLocalDirectoryPath)
        public static string AbsoluteCompletedDirectory {
            get { return GetAbsoluteDirectory("CompletedFolder", CompletedFolderForSession, CompletedFolderIsRelative); }
        }

        private static string GetAbsoluteDirectory(string settingName, string folderForSession, bool? isRelative) {
            if (folderForSession != null) return WithDebugFolder(folderForSession);
            string dir = WithDebugFolder(General.ToLocalDirectoryPath(Get(settingName), settingName));
            if (!String.IsNullOrEmpty(dir) && (isRelative == true)) {
                dir = General.GetAbsoluteDirectoryPath(dir, ExeDirectory);
            }
            return dir;
        }

        private static string WithDebugFolder(string folder) {
            #if DEBUG
                return Path.Combine(folder, DebugFolderName);
            #else
                return folder;
            #endif
        }

        public static Size? ClientSize {
            get {
                int[] size = GetIntArray("ClientSize");
                if (size.Length != 2 || size[0] < 1 || size[1] < 1) return null;
                return new Size(size[0], size[1]);
            }
            set { Set("ClientSize", value.HasValue ? value.Value.Width + "," + value.Value.Height : null); }
        }

        public static int[] ColumnWidths {
            get { return GetIntArray("ColumnWidths"); }
            set { SetIntArray("ColumnWidths", value); }
        }

        public static int[] DefaultColumnWidths {
            get { return new[] { 110, 150, 115, 115, 110, 75 }; }
        }

        public static int[] ColumnIndices {
            get { return GetIntArray("ColumnIndices"); }
            set { SetIntArray("ColumnIndices", value); }
        }
        
        public static int? SortColumn {
            get { return GetInt("SortColumn"); }
            set { SetInt("SortColumn", value); }
        }

        public static bool? SortAscending {
            get { return GetBool("SortAscending"); }
            set { SetBool("SortAscending", value); }
        }

        private static string Get(string name) {
            lock (_sync) {
                string value;
                return _settings.TryGetValue(name, out value) ? value : null;
            }
        }

        // Logins are kept in their on-disk form (see StoredAuth) and decrypted when read.
        private static string GetAuth(string name) {
            string value;
            lock (_sync) {
                if (_sessionAuth.TryGetValue(name, out value)) return value;
            }
            value = Get(name);
            return value != null ? StoredAuth.Unprotect(value) : null;
        }

        private static bool? GetBool(string name) {
            string value = Get(name);
            if (value == null) return null;
            return value != "0";
        }

        private static int? GetInt(string name) {
            string value = Get(name);
            if (value == null) return null;
            int x;
            return Int32.TryParse(value, out x) ? x : (int?)null;
        }

        private static long? GetLong(string name) {
            string value = Get(name);
            if (value == null) return null;
            long x;
            return Int64.TryParse(value, out x) ? x : (long?)null;
        }

        private static DateTime? GetDate(string name) {
            string value = Get(name);
            if (value == null) return null;
            DateTime x;
            return DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out x) ? x : (DateTime?)null;
        }

        private static int[] GetIntArray(string name) {
            string value = Get(name);
            if (value == null) return new int[0];
            string[] array = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            int[] values = new int[array.Length];
            for (int i = 0; i < array.Length; i++) {
                Int32.TryParse(array[i], out values[i]);
            }
            return values;
        }

        // The settings file holds one "name=value" per line, so line breaks in a value
        // are replaced by spaces.
        private static void Set(string name, string value) {
            lock (_sync) {
                if (value == null) {
                    _settings.Remove(name);
                }
                else {
                    _settings[name] = TextFile.ToSingleLine(value);
                }
            }
        }

        // Setting the login that is already read back keeps the stored value, so a saved login
        // that can't be decrypted (read as empty) survives until a different login is set.
        // A login this system can't keep is used for the session only.
        private static void SetAuth(string name, string value) {
            lock (_sync) {
                if (value == GetAuth(name)) return;
                if (ClearsSessionLoginOnly(name, value)) {
                    _sessionAuth.Remove(name);
                    return;
                }
                string stored = value != null ? StoredAuth.Protect(value) : null;
                _sessionAuth.Remove(name);
                if (IsNotKept(value, stored)) {
                    _sessionAuth[name] = TextFile.ToSingleLine(value);
                }
                else {
                    Set(name, stored);
                }
            }
        }

        // Clearing a session login keeps an encrypted value the file still holds (e.g. one
        // from Windows), as the login read back is then empty anyway
        private static bool ClearsSessionLoginOnly(string name, string value) {
            return String.IsNullOrEmpty(value) && _sessionAuth.ContainsKey(name) && StoredAuth.IsProtected(Get(name));
        }

        // True for a login that Protect gave back nothing for
        private static bool IsNotKept(string value, string stored) {
            return stored != null && stored.Length == 0 && TextFile.ToSingleLine(value).Length != 0;
        }

        private static void SetBool(string name, bool? value) {
            Set(name, value.HasValue ? (value.Value ? "1" : "0") : null);
        }

        private static void SetInt(string name, int? value) {
            Set(name, value.HasValue ? value.Value.ToString() : null);
        }

        private static void SetLong(string name, long? value) {
            Set(name, value.HasValue ? value.Value.ToString() : null);
        }

        private static void SetDate(string name, DateTime? value) {
            Set(name, value.HasValue ? value.Value.ToString("yyyyMMdd") : null);
        }

        private static void SetIntArray(string name, int[] value) {
            Set(name, value.Length > 0 ? String.Join(",", Array.ConvertAll(value, Convert.ToString)) : null);
        }

        public static void Load() {
            Load(Path.Combine(GetSettingsDirectory(), SettingsFileName));
        }

        // Replaces the current settings with the file's. If the file exists but can't be
        // read, it is copied aside before saving is allowed; if that copy fails too,
        // saving stays disabled so the file isn't overwritten.
        public static void Load(string path) {
            Dictionary<string, string> settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool saveBlocked = false;
            try {
                ReadSettingsFile(path, settings);
            }
            catch (Exception ex) {
                Logger.Log(ex.ToString());
                saveBlocked = !TryPreserveSettingsFile(path);
            }
            lock (_sync) {
                _settings = settings;
                _sessionAuth = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _saveBlocked = saveBlocked;
                _checkedCopies = false;
            }
            DownloadFolderForSession = null;
            CompletedFolderForSession = null;
        }

        private static void ReadSettingsFile(string path, Dictionary<string, string> settings) {
            if (!File.Exists(path)) {
                return;
            }
            using (StreamReader sr = File.OpenText(path)) {
                string line;

                while ((line = sr.ReadLine()) != null) {
                    LoadSettingLine(settings, line);
                }
            }
        }

        // Adds a "name=value" line to the settings. Lines without '=' are ignored,
        // and the first occurrence of a duplicate name wins.
        private static void LoadSettingLine(Dictionary<string, string> settings, string line) {
            int pos = line.IndexOf('=');

            if (pos == -1) {
                return;
            }

            string name = line.Substring(0, pos);
            string val = line.Substring(pos + 1);

            if (!settings.ContainsKey(name)) {
                settings.Add(name, val);
            }
        }

        private static bool TryPreserveSettingsFile(string path) {
            try {
                Logger.Log("The settings file could not be loaded. The original file was kept as " + TextFile.PreserveCopy(path, BlankPlaintextAuth));
                return true;
            }
            catch (Exception ex) {
                Logger.Log("The settings file could not be loaded or copied aside, so settings will not be saved this session." + Environment.NewLine + ex);
                return false;
            }
        }

        public static void Save() {
            Save(Path.Combine(GetSettingsDirectory(), SettingsFileName));
        }

        public static void Save(string path) {
            try {
                // Copies made aside by a version that kept them byte for byte can hold plaintext
                // logins, so after the first save following a load they are written again without
                // them. That reads every copy, so it runs outside the lock, which the getters
                // also take. A copy that fails is left unchanged and tried again next session.
                if (WriteSettingsFile(path)) {
                    TextFile.RewriteCopies(path, BlankPlaintextAuth);
                }
            }
            catch (Exception ex) {
                Logger.Log(ex.ToString());
            }
        }

        // Returns true if the file was written and the copies have yet to be checked since
        // the last load (only one caller gets true).
        private static bool WriteSettingsFile(string path) {
            lock (_sync) {
                if (_saveBlocked) return false;
                TextFile.WriteAllLinesAtomic(path, GetSettingLines());
                bool checkCopies = !_checkedCopies;
                _checkedCopies = true;
                return checkCopies;
            }
        }

        // Returns the file's content with the value of every plaintext login setting removed
        // (the name and '=' stay) and everything else kept (see TextFile.CutLineEnds), for a
        // copy of a settings file. Lines are matched as Load reads them, and every line for a
        // login setting is blanked, not only the first one that Load uses.
        public static byte[] BlankPlaintextAuth(byte[] content) {
            return TextFile.CutLineEnds(content, lines => Array.ConvertAll(lines, GetPlaintextAuthStart));
        }

        // Returns where a plaintext login value starts in the line, or -1
        private static int GetPlaintextAuthStart(string line) {
            int pos = line.IndexOf('=');
            if (pos == -1 || !IsAuthSettingName(line.Substring(0, pos))) return -1;
            return StoredAuth.IsPlaintext(line.Substring(pos + 1)) ? pos + 1 : -1;
        }

        private static bool IsAuthSettingName(string name) {
            return Array.Exists(_authSettingNames, authName => String.Equals(authName, name, StringComparison.OrdinalIgnoreCase));
        }

        private static List<string> GetSettingLines() {
            List<string> lines = new List<string>();
            foreach (KeyValuePair<string, string> kvp in _settings) {
                lines.Add(kvp.Key + "=" + ToStoredValue(kvp.Key, kvp.Value));
            }
            return lines;
        }

        // Plaintext logins loaded from a file written by an older version are encrypted
        // when the settings are written back, or written empty on a system that can't keep
        // them (see StoredAuth.CanProtect).
        private static string ToStoredValue(string name, string value) {
            return IsAuthSettingName(name) && !StoredAuth.IsProtected(value) ? StoredAuth.Protect(value) : value;
        }
    }
}