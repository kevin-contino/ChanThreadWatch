using System;
using System.Collections.Generic;
using System.IO;

namespace JDP {
    // The watched threads, the thread list file and the blacklist, without any user interface.
    // Work that has to run on the owner's thread (the UI thread for the main form) is passed to
    // runOnOwnerThread, which must run it synchronously. AddThread must be called on that thread.
    // Threads that a watcher auto-follows are added through postToOwnerThread, which must queue the
    // work for the owner's thread and return without waiting, so a watcher's thread never blocks
    // on the owner's thread (which waits for the watchers when the program exits).
    // The events let the owner show the threads; they are raised on the owner's thread.
    internal class WatchSession {
        private readonly Action<Action> _runOnOwnerThread;
        private readonly Action<Action> _postToOwnerThread;
        // Null to follow Settings.GetSettingsDirectory(), which can change while running
        private readonly string _settingsDirectory;
        private readonly ThreadListStore _threadListStore = new ThreadListStore();
        // Locked on itself: the load reads it on a worker thread while the owner's thread adds threads
        private readonly Dictionary<string, ThreadWatcher> _watchers = new Dictionary<string, ThreadWatcher>();
        // Not locked: the load fills it on a worker thread while auto-follow can read it (known race, unchanged by the move)
        private readonly HashSet<string> _blacklist = new HashSet<string>();
        // The page IDs read from api-threads.txt (ApiThreadsFile). An entry stays, also when no loaded thread
        // has it (fail closed), until its thread is removed or added again unguarded. Locked on itself.
        private readonly HashSet<string> _apiThreadEntries = new HashSet<string>(StringComparer.Ordinal);
        // False when api-threads.txt could not be read and could not be copied aside: it is then left as it is
        private volatile bool _canSaveApiThreads = true;
        // The page IDs of the watchers guarded for this session only (UpdateMarks): their mark is not saved. Locked on itself.
        private readonly HashSet<string> _guardedForSessionOnly = new HashSet<string>(StringComparer.Ordinal);
        // The marks of removed threads, still written to api-threads.txt until threads.txt without them is saved.
        // Locked on itself.
        private readonly HashSet<string> _pendingRemovedMarks = new HashSet<string>(StringComparer.Ordinal);
        private bool _isLoadingThreadsFromFile;

        internal WatchSession(Action<Action> runOnOwnerThread, Action<Action> postToOwnerThread, string settingsDirectory = null) {
            if (runOnOwnerThread == null) throw new ArgumentNullException("runOnOwnerThread");
            if (postToOwnerThread == null) throw new ArgumentNullException("postToOwnerThread");
            _runOnOwnerThread = runOnOwnerThread;
            _postToOwnerThread = postToOwnerThread;
            _settingsDirectory = settingsDirectory;
        }

        // A new watcher was created by AddThread, before its settings are applied and it is registered
        internal event Action<ThreadWatcher> ThreadWatcherCreated;

        // AddThread registered the watcher, before starting or stopping it
        internal event Action<ThreadWatcher> ThreadWatcherAdded;

        // The loaded threads are about to be added
        internal event Action ThreadListLoadStarting;

        // A loaded thread was linked to its parent thread
        internal event Action<ThreadWatcher> AddedFromChanged;

        // RemoveThreads is removing the watcher: its pre-remove action has run, and it is
        // unregistered right after
        internal event Action<ThreadWatcher> ThreadWatcherRemoved;

        internal bool IsLoadingThreadsFromFile {
            get { return _isLoadingThreadsFromFile; }
        }

        // Set by the load when api-threads.txt could not be read: every loaded thread is then guarded for this
        // session only (the host may say so; the log says it too)
        internal bool ApiThreadsUnreadable { get; private set; }

        // Set when the thread list has changed and should be saved
        internal bool SaveThreadListPending { get; set; }

        // A copy, so it can be enumerated while threads are added or removed
        internal List<ThreadWatcher> ThreadWatchers {
            get { lock (_watchers) { return new List<ThreadWatcher>(_watchers.Values); } }
        }

        private string SettingsDirectory {
            get { return _settingsDirectory ?? Settings.GetSettingsDirectory(); }
        }

        internal bool TryGetThreadWatcher(string pageID, out ThreadWatcher watcher) {
            lock (_watchers) {
                return _watchers.TryGetValue(pageID, out watcher);
            }
        }

        internal void UnregisterThreadWatcher(ThreadWatcher watcher) {
            lock (_watchers) {
                _watchers.Remove(watcher.PageID);
            }
        }

        // A missing download folder is replaced by the default one. A setting that is an absolute path
        // of another OS is kept, and the default folder is used for this session only.
        internal static void EnsureDownloadFolderExists() {
            FolderState state = Settings.DownloadFolder == null ? FolderState.Missing : GetFolderState(() => Settings.AbsoluteDownloadDirectory);
            if (state == FolderState.Exists) return;
            string folder = GetDefaultFolder("Watched Threads");
            if (state == FolderState.Foreign || Settings.DownloadFolderForSession != null) {
                Settings.DownloadFolderForSession = folder;
            }
            else {
                Settings.DownloadFolder = folder;
                Settings.DownloadFolderIsRelative = false;
            }
        }

        private enum FolderState { Exists, Missing, Foreign }

        // Foreign if the setting holds an absolute path of another OS, which is logged with the setting's name
        private static FolderState GetFolderState(Func<string> getFolder) {
            try {
                return Directory.Exists(getFolder()) ? FolderState.Exists : FolderState.Missing;
            }
            catch (FormatException ex) {
                Logger.Log(ex.Message + " The default folder is used for this session, and the setting is kept.");
                return FolderState.Foreign;
            }
        }

        // Test only: the folder that holds the default download and completed folders in place of Documents (or the
        // home folder), so a test that falls back to a default folder never writes to the user's folders. Every test
        // assembly sets it to a temporary folder. Never set by production code.
        internal static string DefaultFoldersParentForTesting { get; set; }

        // Windows uses Documents (GetFolderPath). Linux and macOS use ~/Documents, built from the home
        // folder because GetFolderPath(MyDocuments) can return the home folder itself there.
        internal static string GetDefaultFolder(string name) {
            if (DefaultFoldersParentForTesting != null) return Path.Combine(DefaultFoldersParentForTesting, name);
            bool isWindows = OperatingSystem.IsWindows();
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string documents = isWindows ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : GetUnixDocumentsFolder(home);
            return Path.Combine(GetDefaultFoldersParent(documents, home, isWindows), name);
        }

        private static string GetUnixDocumentsFolder(string home) {
            return home.Length != 0 ? Path.Combine(home, "Documents") : String.Empty;
        }

        // The folder that holds the default download and completed folders: Documents, or on Linux
        // and macOS the home folder when there is no Documents folder. Windows always uses Documents,
        // as it did before.
        internal static string GetDefaultFoldersParent(string documents, string home, bool isWindows) {
            if (isWindows || (documents.Length != 0 && Directory.Exists(documents))) return documents;
            if (home.Length == 0) throw new InvalidOperationException("No Documents or home folder was found for the default download folder.");
            return home;
        }

        // Returns true if this is the first page download for the watcher.
        internal static bool TrackPageDownload(WatcherExtraData extraData, DownloadType downloadType) {
            if (downloadType != DownloadType.Page) return false;
            bool isInitialPageDownload = false;
            if (!extraData.HasDownloadedPage) {
                extraData.HasDownloadedPage = true;
                isInitialPageDownload = true;
            }
            extraData.PreviousDownloadWasPage = true;
            return isInitialPageDownload;
        }

        // Returns true if this is the first image download since the last page download.
        internal static bool TrackImageDownload(WatcherExtraData extraData, DownloadType downloadType) {
            if (downloadType != DownloadType.Image || !extraData.PreviousDownloadWasPage) return false;
            extraData.LastImageOn = DateTime.Now;
            extraData.PreviousDownloadWasPage = false;
            return true;
        }

        internal bool AddThread(ThreadInfo thread) {
            return AddThread(thread, false);
        }

        // Threads from the file start after the load links them to their parents; any other thread starts now,
        // including one auto-followed while the load is still running.
        private bool AddThread(ThreadInfo thread, bool isFromFile) {
            ThreadWatcher watcher = null;
            ThreadWatcher parentThread = null;
            SiteHelper siteHelper = SiteHelpers.GetInstance((new Uri(thread.URL)).Host);
            siteHelper.SetURL(thread.URL);
            string pageID = siteHelper.GetPageID();
            if (IsBlacklisted(pageID)) return false;

            if (TryGetThreadWatcher(pageID, out watcher) && !CanReuseWatcher(watcher, thread)) return false;

            if (watcher == null) {
                watcher = CreateThreadWatcher(thread, isFromFile, out parentThread);
                watcher.AddThread += ThreadWatcher_AddThread;
                OnThreadWatcherCreated(watcher);
            }

            ApplyThreadInfo(watcher, thread);
            AttachExtraData(watcher, thread);
            RegisterThreadWatcher(watcher, parentThread);
            OnThreadWatcherAdded(watcher);

            StartOrStopAddedThread(watcher, thread, isFromFile);
            return true;
        }

        // A stopped watcher takes the new settings and starts again. Its Guarded mark never changes: a
        // guarded thread stays guarded, and a guarded request never takes over an unguarded watcher.
        private static bool CanReuseWatcher(ThreadWatcher watcher, ThreadInfo thread) {
            return !watcher.IsRunning && (watcher.Guarded || !thread.Guarded);
        }

        private void OnThreadWatcherCreated(ThreadWatcher watcher) {
            ThreadWatcherCreated?.Invoke(watcher);
        }

        private void OnThreadWatcherAdded(ThreadWatcher watcher) {
            ThreadWatcherAdded?.Invoke(watcher);
        }

        // A thread followed from a guarded thread is guarded, and so is one the thread list marks (MarkGuardedThreads)
        private ThreadWatcher CreateThreadWatcher(ThreadInfo thread, bool isFromFile, out ThreadWatcher parentThread) {
            parentThread = FindAddedFromThread(thread);
            ThreadWatcher watcher = new ThreadWatcher(thread.URL, thread.Guarded || IsGuarded(parentThread), !thread.ThreadNameLookedUp);
            UpdateMarks(watcher, parentThread, isFromFile);
            watcher.ThreadDownloadDirectory = thread.SaveDir;
            watcher.Description = thread.Description;
            if (_isLoadingThreadsFromFile) watcher.DoNotRename = true;
            watcher.Category = thread.Category;
            watcher.DoNotRename = false;
            watcher.ParentThread = parentThread;
            return watcher;
        }

        // Runs for a new watcher only (a stopped watcher that is added again is reused, and keeps its mark). A new
        // watcher without the mark drops an entry left for its page ID: the thread is no longer one the API added. A
        // thread from the file loaded while api-threads.txt could not be read, or one followed from such a thread, is
        // guarded for the session only: its mark is not saved. Any other guarded watcher (an API add, also one made
        // during that load) has a real mark.
        private void UpdateMarks(ThreadWatcher watcher, ThreadWatcher parentThread, bool isFromFile) {
            if (!watcher.Guarded) RemoveApiThreadEntry(watcher.PageID);
            bool sessionOnly = IsGuardedForSessionOnly(watcher, parentThread, isFromFile);
            lock (_guardedForSessionOnly) {
                if (sessionOnly) _guardedForSessionOnly.Add(watcher.PageID);
                else _guardedForSessionOnly.Remove(watcher.PageID);
            }
        }

        private bool IsGuardedForSessionOnly(ThreadWatcher watcher, ThreadWatcher parentThread, bool isFromFile) {
            if (!watcher.Guarded) return false;
            return (isFromFile && ApiThreadsUnreadable) || IsGuardedForSessionOnly(parentThread);
        }

        private bool IsGuardedForSessionOnly(ThreadWatcher watcher) {
            if (watcher == null) return false;
            lock (_guardedForSessionOnly) {
                return _guardedForSessionOnly.Contains(watcher.PageID);
            }
        }

        private ThreadWatcher FindAddedFromThread(ThreadInfo thread) {
            ThreadWatcher parentThread = null;
            if (thread.ExtraData != null && !String.IsNullOrEmpty(thread.ExtraData.AddedFrom)) {
                TryGetThreadWatcher(thread.ExtraData.AddedFrom, out parentThread);
            }
            return parentThread;
        }

        private static bool IsGuarded(ThreadWatcher watcher) {
            return watcher != null && watcher.Guarded;
        }

        private void RemoveApiThreadEntry(string pageID) {
            lock (_apiThreadEntries) {
                _apiThreadEntries.Remove(pageID);
            }
        }

        // The removed watcher's mark stays in api-threads.txt until the thread list without the thread is saved
        // (SaveThreadList), so a failed save of threads.txt never leaves the thread there without its mark
        private void RemoveMarks(ThreadWatcher watcher) {
            if (watcher.Guarded && !IsGuardedForSessionOnly(watcher)) AddPendingRemovedMark(watcher.PageID);
            RemoveApiThreadEntry(watcher.PageID);
            lock (_guardedForSessionOnly) {
                _guardedForSessionOnly.Remove(watcher.PageID);
            }
        }

        private void AddPendingRemovedMark(string pageID) {
            lock (_pendingRemovedMarks) {
                _pendingRemovedMarks.Add(pageID);
            }
        }

        private static void ApplyThreadInfo(ThreadWatcher watcher, ThreadInfo thread) {
            watcher.PageAuth = thread.PageAuth;
            watcher.ImageAuth = thread.ImageAuth;
            watcher.CheckIntervalSeconds = thread.CheckIntervalSeconds;
            watcher.OneTimeDownload = thread.OneTimeDownload;
            watcher.AutoFollow = thread.AutoFollow;
        }

        private static void AttachExtraData(ThreadWatcher watcher, ThreadInfo thread) {
            if (thread.ExtraData == null) {
                thread.ExtraData = watcher.Tag as WatcherExtraData ?? new WatcherExtraData { AddedOn = DateTime.Now };
            }
            watcher.Tag = thread.ExtraData;
        }

        internal void RegisterThreadWatcher(ThreadWatcher watcher, ThreadWatcher parentThread) {
            if (parentThread != null) parentThread.AddChildThread(watcher);
            lock (_watchers) {
                if (!_watchers.ContainsKey(watcher.PageID)) {
                    _watchers.Add(watcher.PageID, watcher);
                }
                else {
                    _watchers[watcher.PageID] = watcher;
                }
            }
        }

        private static void StartOrStopAddedThread(ThreadWatcher watcher, ThreadInfo thread, bool isFromFile) {
            if (thread.StopReason == null && !isFromFile) {
                watcher.Start();
            }
            else if (thread.StopReason != null) {
                watcher.Stop(thread.StopReason.Value);
            }
        }

        // Called on the watcher's thread
        private void ThreadWatcher_AddThread(object sender, AddThreadEventArgs args) {
            ThreadWatcher watcher = (ThreadWatcher)sender;
            ThreadWatcher rootThread = watcher.RootThread;
            _postToOwnerThread(() => {
                try {
                    AddFollowedThread(watcher, args.PageURL);
                }
                finally {
                    // The watcher reserved room for this thread under its root before raising
                    // the event; the thread is now either added (and counted) or rejected
                    rootThread.ReleaseDescendantSlot();
                }
            });
        }

        private void AddFollowedThread(ThreadWatcher watcher, string pageURL) {
            ThreadInfo thread = watcher.CreateChildThreadInfo(pageURL, DateTime.Now, Settings.RecursiveAutoFollow != false);
            if (IsThreadWatched(thread.URL)) return;
            if (AddThread(thread)) {
                SaveThreadListPending = true;
            }
        }

        // Removes the given threads that this session watches and that are stopped and not reparsing, in the given order.
        // preRemoveAction runs on each one first; a failure is logged and the thread is removed anyway. The thread's
        // saved logins are deleted from the system's login store after the next save, if they are kept there
        // (see StoredAuth.ScheduleDelete).
        internal void RemoveThreads(IEnumerable<ThreadWatcher> threads, Action<ThreadWatcher> preRemoveAction = null) {
            foreach (ThreadWatcher watcher in threads) {
                if (IsRegistered(watcher) && ShouldRemoveThread(watcher)) {
                    RunPreRemoveAction(preRemoveAction, watcher);
                    ThreadWatcherRemoved?.Invoke(watcher);
                    UnregisterThreadWatcher(watcher);
                    RemoveMarks(watcher);
                    DeleteSavedLogins(watcher);
                }
            }
            SaveThreadListPending = true;
        }

        private static void DeleteSavedLogins(ThreadWatcher watcher) {
            WatcherExtraData extraData = watcher.Tag as WatcherExtraData;
            if (extraData == null) return;
            StoredAuth.ScheduleDelete(extraData.StoredPageAuth);
            StoredAuth.ScheduleDelete(extraData.StoredImageAuth);
            extraData.StoredPageAuth = null;
            extraData.StoredImageAuth = null;
        }

        private bool IsRegistered(ThreadWatcher watcher) {
            ThreadWatcher registered;
            return TryGetThreadWatcher(watcher.PageID, out registered) && registered == watcher;
        }

        private static bool ShouldRemoveThread(ThreadWatcher watcher) {
            return !watcher.IsRunning && !watcher.IsReparsing;
        }

        private static void RunPreRemoveAction(Action<ThreadWatcher> preRemoveAction, ThreadWatcher watcher) {
            if (preRemoveAction == null) return;
            try { preRemoveAction(watcher); }
            catch (Exception ex) {
                Logger.Log(ex.ToString());
            }
        }

        // Removes the given threads like RemoveThreads, first moving their folders to the
        // completed folder if the settings ask for it
        internal void RemoveCompletedThreads(IEnumerable<ThreadWatcher> threads) {
            if (Settings.MoveToCompletedFolder != true) {
                RemoveThreads(threads);
            }
            else {
                EnsureCompletedFolderExists();
                RemoveThreads(threads, MoveThreadToCompletedFolder);
            }
        }

        // As EnsureDownloadFolderExists, for the completed folder
        private static void EnsureCompletedFolderExists() {
            FolderState state = GetFolderState(() => Settings.AbsoluteCompletedDirectory);
            if (state == FolderState.Exists) return;
            string folder = GetDefaultFolder("Completed Threads");
            if (state == FolderState.Foreign || Settings.CompletedFolderForSession != null) {
                Settings.CompletedFolderForSession = folder;
            }
            else {
                Settings.CompletedFolder = folder;
                Settings.CompletedFolderIsRelative = false;
            }
        }

        internal static void MoveThreadToCompletedFolder(ThreadWatcher watcher) {
            string destDir = Path.Combine(Settings.AbsoluteCompletedDirectory, GetCompletedFolderName(watcher));
            if (Directory.Exists(watcher.ThreadDownloadDirectory)) {
                if (Directory.Exists(destDir)) {
                    Directory.Delete(destDir);
                }
                if (watcher.Category.Length != 0) {
                    Directory.CreateDirectory(General.RemoveLastDirectory(destDir));
                }
                Directory.Move(watcher.ThreadDownloadDirectory, destDir);
            }
            DeleteCategoryFolderIfEmpty(watcher);
        }

        // A thread folder on another root (another drive or network share) has no relative path, and
        // combining the absolute path would make the destination the thread folder itself
        private static string GetCompletedFolderName(ThreadWatcher watcher) {
            string relativeDir = General.GetRelativeDirectoryPath(watcher.ThreadDownloadDirectory, watcher.MainDownloadDirectory);
            return Path.IsPathRooted(relativeDir) ? General.GetLastDirectory(watcher.ThreadDownloadDirectory) : relativeDir;
        }

        private static void DeleteCategoryFolderIfEmpty(ThreadWatcher watcher) {
            string categoryPath = General.RemoveLastDirectory(watcher.ThreadDownloadDirectory);
            if (categoryPath != watcher.MainDownloadDirectory && Directory.GetFiles(categoryPath).Length == 0 && Directory.GetDirectories(categoryPath).Length == 0) {
                Directory.Delete(categoryPath);
            }
        }

        internal static void DeleteThreadFolder(ThreadWatcher watcher) {
            if (Directory.Exists(watcher.ThreadDownloadDirectory)) Directory.Delete(watcher.ThreadDownloadDirectory, true);
            DeleteCategoryFolderIfEmpty(watcher);
        }

        private string ThreadListPath {
            get { return Path.Combine(SettingsDirectory, Settings.ThreadsFileName); }
        }

        private string ApiThreadsPath {
            get { return Path.Combine(SettingsDirectory, Settings.ApiThreadsFileName); }
        }

        // Returns false if the list was not saved (still loading, or the save failed), so the
        // caller can try again later
        internal bool SaveThreadList() {
            if (_isLoadingThreadsFromFile) return false;
            try {
                List<string> pendingRemoved = GetPendingRemovedMarks();
                // The marks of the threads added through the API go first, those of removed threads
                // included: a thread list saved without a thread's mark, or one that failed to save
                // after the mark of a thread still in it was dropped, would let the thread run
                // unguarded after the next start
                if (_threadListStore.CanSave) SaveApiThreads(pendingRemoved);
                // The thread list store refuses to save until the load has finished, and
                // writes atomically so a failure can't leave a partially written file.
                bool saved = _threadListStore.Save(ThreadListPath, GetSavedThreadInfos());
                if (saved) OnSaveSucceeded(pendingRemoved);
                return saved;
            }
            catch (Exception ex) {
                LogSaveFailed(ex);
                return false;
            }
        }

        // Writes api-threads.txt atomically. No file is made until a thread is added through the API.
        private void SaveApiThreads(List<string> pendingRemoved) {
            if (!_canSaveApiThreads) return;
            List<string> pageIDs = GetApiThreadPageIDs();
            pageIDs.AddRange(pendingRemoved.FindAll(pageID => !pageIDs.Contains(pageID)));
            string path = ApiThreadsPath;
            if (pageIDs.Count == 0 && !File.Exists(path)) return;
            TextFile.WriteAllLinesAtomic(path, ApiThreadsFile.Serialize(pageIDs));
            MarkApiThreadsFileWritten(SettingsDirectory);
        }

        // After a write of api-threads.txt, so a later load treats a missing one as lost (IsMissingAfterWrite). The file
        // is written first: if the settings then fail to save, a missing file only fails open as it did before the
        // setting, so that is logged and the save goes on. Also used by ctw.
        internal static void MarkApiThreadsFileWritten(string settingsDirectory) {
            if (Settings.ApiThreadsFileWritten == true) return;
            Settings.ApiThreadsFileWritten = true;
            if (!Settings.Save(Path.Combine(settingsDirectory, Settings.SettingsFileName))) {
                Logger.Log(ApiThreadsFileWrittenSetting + "=1 could not be saved in " + Settings.SettingsFileName + " after " + Settings.ApiThreadsFileName +
                    " was written; until it is, a missing " + Settings.ApiThreadsFileName + " is read as no thread added through the local API.");
            }
        }

        private List<string> GetPendingRemovedMarks() {
            lock (_pendingRemovedMarks) {
                return new List<string>(_pendingRemovedMarks);
            }
        }

        // threads.txt no longer has the removed threads, so their marks go too. If writing api-threads.txt
        // fails, they stay there as entries without a thread (kept, as any such entry) until the next save.
        private void DropPendingRemovedMarks(List<string> pendingRemoved) {
            if (pendingRemoved.Count == 0) return;
            lock (_pendingRemovedMarks) {
                _pendingRemovedMarks.ExceptWith(pendingRemoved);
            }
            try {
                SaveApiThreads(new List<string>());
            }
            catch (Exception ex) {
                Logger.Log("The marks of removed threads could not be dropped from " + Settings.ApiThreadsFileName + "; the next save tries again." + Environment.NewLine + ex);
            }
        }

        // The guarded watchers but those guarded for this session only, and the entries read from the file
        // whose thread is not watched (for example one whose entry in the thread list did not load)
        private List<string> GetApiThreadPageIDs() {
            HashSet<string> pageIDs = new HashSet<string>(StringComparer.Ordinal);
            foreach (ThreadWatcher watcher in ThreadWatchers) {
                if (watcher.Guarded && !IsGuardedForSessionOnly(watcher)) pageIDs.Add(watcher.PageID);
            }
            foreach (string pageID in GetApiThreadEntries()) {
                ThreadWatcher watcher;
                if (!TryGetThreadWatcher(pageID, out watcher)) pageIDs.Add(pageID);
            }
            return new List<string>(pageIDs);
        }

        private List<string> GetApiThreadEntries() {
            lock (_apiThreadEntries) {
                return new List<string>(_apiThreadEntries);
            }
        }

        // Failed saves since the last one that succeeded. A failed save is tried again on every
        // timer tick, so only the first failure is logged in full.
        private int _failedSaves;

        private void LogSaveFailed(Exception ex) {
            if (_failedSaves++ == 0) Logger.Log(ex.ToString());
        }

        // Items of removed threads and cleared logins go once the list without them is saved,
        // unless a thread still uses them
        private void OnSaveSucceeded(List<string> pendingRemoved) {
            LogSaveSucceeded();
            DropPendingRemovedMarks(pendingRemoved);
            StoredAuthDeletes.Flush(SettingsDirectory, GetLiveStoredAuth());
        }

        private List<string> GetLiveStoredAuth() {
            List<string> values = new List<string>();
            foreach (ThreadWatcher watcher in ThreadWatchers) {
                WatcherExtraData extraData = watcher.Tag as WatcherExtraData;
                if (extraData != null) values.AddRange(new[] { extraData.StoredPageAuth, extraData.StoredImageAuth, extraData.UndecryptablePageAuth, extraData.UndecryptableImageAuth });
            }
            return values;
        }

        private void LogSaveSucceeded() {
            if (_failedSaves == 0) return;
            Logger.Log("The thread list was saved after " + _failedSaves + " failed saves.");
            _failedSaves = 0;
        }

        private List<ThreadInfo> GetSavedThreadInfos() {
            List<ThreadInfo> threads = new List<ThreadInfo>();
            foreach (ThreadWatcher watcher in ThreadWatchers) {
                threads.Add(GetSavedThreadInfo(watcher));
            }
            return threads;
        }

        private static ThreadInfo GetSavedThreadInfo(ThreadWatcher watcher) {
            return new ThreadInfo {
                URL = watcher.PageURL,
                PageAuth = watcher.PageAuth,
                ImageAuth = watcher.ImageAuth,
                CheckIntervalSeconds = watcher.CheckIntervalSeconds,
                OneTimeDownload = watcher.OneTimeDownload,
                SaveDir = GetSaveDirLine(watcher),
                StopReason = GetSavedStopReason(watcher),
                Description = watcher.Description,
                ExtraData = (WatcherExtraData)watcher.Tag,
                Category = watcher.Category,
                AutoFollow = watcher.AutoFollow,
                Guarded = watcher.Guarded
            };
        }

        private static string GetSaveDirLine(ThreadWatcher watcher) {
            return watcher.ThreadDownloadDirectory != null ? General.GetRelativeDirectoryPath(watcher.ThreadDownloadDirectory, watcher.MainDownloadDirectory) : String.Empty;
        }

        private static StopReason? GetSavedStopReason(ThreadWatcher watcher) {
            return (watcher.IsStopping && watcher.StopReason != StopReason.Exiting) ? watcher.StopReason : (StopReason?)null;
        }

        // Called on a worker thread
        internal void LoadThreadList() {
            bool loadedFully = false;
            try {
                loadedFully = LoadThreadListFile();
            }
            catch (Exception ex) {
                Logger.Log(ex.ToString());
            }
            _isLoadingThreadsFromFile = false;
            // If anything failed, the original file is copied aside before saving is allowed.
            _threadListStore.EndLoad(ThreadListPath, loadedFully);
        }

        // Returns false if any part of the file could not be loaded.
        private bool LoadThreadListFile() {
            // Before the thread list, so the marks are kept even if the thread list does not load
            bool allGuarded = !ReadApiThreads();
            ApiThreadsUnreadable = allGuarded;
            ThreadListData data = _threadListStore.Read(ThreadListPath);
            if (data == null) return true;
            // Plaintext logins are protected by the next save, before the periodic backup can copy them
            if (data.HasPlaintextAuth) SaveThreadListPending = true;
            MarkGuardedThreads(data.Threads, GetApiThreadEntries(), allGuarded);
            bool allThreadsAdded = data.Threads.Count == 0 || AddLoadedThreads(data.Threads);
            return allThreadsAdded && data.TrailingLineCount == 0;
        }

        // A missing file marks no thread. Returns false if the file could not be used: every loaded thread is then
        // guarded for this session only. A file that can't be read (after the retries of SharedFile.ReadAllLines,
        // e.g. one another program holds) is left as it is and not saved this session. A file that is not valid or has
        // a version this one does not know is copied aside, and the next save writes only the marks this session makes
        // (if the copy aside fails, the file is left as it is too).
        private bool ReadApiThreads() {
            string path = ApiThreadsPath;
            string[] lines;
            try {
                // A missing file costs no retries; one that goes away before the read is missing too (null)
                lines = SharedFile.ReadAllLinesIfPresent(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                _canSaveApiThreads = false;
                Logger.Log(Settings.ApiThreadsFileName + " could not be read, so every loaded thread is treated as added through the local API for this session (it never connects to a local or private address). " +
                    "The file is left as it is and is not saved this session, so no thread can be added through the API." + Environment.NewLine + ex);
                return false;
            }
            return lines != null ? TryAddApiThreadEntries(path, lines) : !IsMissingAfterWrite();
        }

        // A file that is missing although it was written before (Settings.ApiThreadsFileWritten) may have been deleted
        // or lost, so it is treated like one that can't be read. It can still be written: the next save writes the
        // marks this session makes. Without the setting, a missing file just means no thread was added through the API.
        private static bool IsMissingAfterWrite() {
            if (Settings.ApiThreadsFileWritten != true) return false;
            Logger.Log(Settings.ApiThreadsFileName + " is missing although it was written before (" + ApiThreadsFileWrittenSetting + " in " + Settings.SettingsFileName + "), " +
                "so every loaded thread is treated as added through the local API for this session (it never connects to a local or private address); that is not saved.");
            return true;
        }

        private const string ApiThreadsFileWrittenSetting = "ApiThreadsFileWritten";

        private bool TryAddApiThreadEntries(string path, string[] lines) {
            try {
                HashSet<string> pageIDs = ApiThreadsFile.Parse(lines);
                lock (_apiThreadEntries) {
                    _apiThreadEntries.UnionWith(pageIDs);
                }
                return true;
            }
            catch (FormatException ex) {
                PreserveApiThreadsFile(path, ex);
                return false;
            }
        }

        // False while api-threads.txt can't be written this session (see ReadApiThreads): a thread added through the
        // API would lose its mark, so the API refuses to add one
        internal bool CanSaveApiThreadMarks {
            get { return _canSaveApiThreads; }
        }

        // A file that can't be copied aside is not written this session, so it is read again at the next start
        private void PreserveApiThreadsFile(string path, Exception ex) {
            string problem = Settings.ApiThreadsFileName + " is not valid or has a version this one does not know, so every loaded thread is treated as added through the local API for this session (it never connects to a local or private address); that is not saved.";
            try {
                Logger.Log(problem + " The original file was kept as " + TextFile.PreserveCopy(path) + Environment.NewLine + ex);
            }
            catch (Exception copyEx) {
                _canSaveApiThreads = false;
                Logger.Log(problem + " It could not be copied aside either, so it is not saved this session." + Environment.NewLine + ex + Environment.NewLine + copyEx);
            }
        }

        // Before the watchers are made, so a guarded thread's first request (the 4chan slug lookup in the
        // watcher's constructor) is guarded. A thread is guarded if its page ID or that of a thread it was
        // followed from (the AddedFrom chain) is an entry, so a thread that a previous release followed from a
        // guarded thread, without marking it, is marked again.
        internal static void MarkGuardedThreads(List<ThreadInfo> threads, ICollection<string> entries, bool allGuarded) {
            List<string> pageIDs = threads.ConvertAll(thread => TryGetPageID(thread.URL));
            Dictionary<string, ThreadInfo> byPageID = IndexByPageID(threads, pageIDs);
            for (int i = 0; i < threads.Count; i++) {
                threads[i].Guarded = allGuarded || IsMarkedOrFollowedFromMarked(threads[i], pageIDs[i], byPageID, entries, threads.Count + 1);
            }
        }

        // The first thread of each page ID (a second one fails the load)
        private static Dictionary<string, ThreadInfo> IndexByPageID(List<ThreadInfo> threads, List<string> pageIDs) {
            Dictionary<string, ThreadInfo> byPageID = new Dictionary<string, ThreadInfo>(StringComparer.Ordinal);
            for (int i = 0; i < threads.Count; i++) {
                if (pageIDs[i] != null) byPageID.TryAdd(pageIDs[i], threads[i]);
            }
            return byPageID;
        }

        // The step limit stops the walk even if the AddedFrom links form a cycle (as IsSelfOrAncestor). A
        // thread the chain leads to that is not in the list is still checked against the entries.
        private static bool IsMarkedOrFollowedFromMarked(ThreadInfo thread, string pageID, Dictionary<string, ThreadInfo> byPageID, ICollection<string> entries, int stepsLeft) {
            while (pageID != null && stepsLeft-- > 0) {
                if (entries.Contains(pageID)) return true;
                pageID = GetAddedFrom(thread);
                byPageID.TryGetValue(pageID ?? String.Empty, out thread);
            }
            return false;
        }

        // Null when the thread is not in the list or was not followed from another one
        private static string GetAddedFrom(ThreadInfo thread) {
            string addedFrom = thread != null && thread.ExtraData != null ? thread.ExtraData.AddedFrom : null;
            return String.IsNullOrEmpty(addedFrom) ? null : addedFrom;
        }

        // Null for an entry whose page ID can't be found (e.g. its URL is not a valid address); adding the
        // thread then fails that entry alone, and logs why
        private static string TryGetPageID(string url) {
            try {
                SiteHelper siteHelper = SiteHelpers.GetInstance(new Uri(url).Host);
                siteHelper.SetURL(url);
                return siteHelper.GetPageID();
            }
            catch (Exception) {
                return null;
            }
        }

        private bool AddLoadedThreads(List<ThreadInfo> threads) {
            _isLoadingThreadsFromFile = true;
            _runOnOwnerThread(() => {
                ThreadListLoadStarting?.Invoke();
            });
            int failedCount = 0;
            foreach (ThreadInfo thread in threads) {
                if (!TryAddLoadedThread(thread)) failedCount++;
            }
            LinkLoadedThreadsToParents();
            MigrateChildThreadsToNewFormat();
            _isLoadingThreadsFromFile = false;
            return failedCount == 0;
        }

        // A SaveDir written on another OS is read in this OS's form; one that is an absolute path of
        // another OS fails this thread only, like any other entry that doesn't load
        private bool TryAddLoadedThread(ThreadInfo thread) {
            try {
                thread.SaveDir = thread.SaveDir.Length != 0 ? GetLoadedThreadDirectory(thread.SaveDir) : null;
                _runOnOwnerThread(() => {
                    // A second entry for the same thread fails the load, so the first one is kept and the file is copied aside
                    if (IsThreadWatched(thread.URL)) throw new InvalidOperationException("Duplicate entry in the thread list");
                    AddThread(thread, true);
                });
                return true;
            }
            catch (Exception ex) {
                Logger.Log("Unable to load thread " + thread.URL + Environment.NewLine + ex);
                return false;
            }
        }

        // On Linux a folder written on Windows or macOS can differ in case from the one on disk
        private static string GetLoadedThreadDirectory(string saveDir) {
            string dir = General.GetAbsoluteDirectoryPath(General.ToLocalDirectoryPath(saveDir, "SaveDir"), Settings.AbsoluteDownloadDirectory);
            return OperatingSystem.IsWindows() ? dir : General.FindDirectoryIgnoringCase(dir);
        }

        internal bool IsThreadWatched(string url) {
            SiteHelper siteHelper = SiteHelpers.GetInstance((new Uri(url)).Host);
            siteHelper.SetURL(url);
            ThreadWatcher watcher;
            return TryGetThreadWatcher(siteHelper.GetPageID(), out watcher);
        }

        private void LinkLoadedThreadsToParents() {
            foreach (ThreadWatcher threadWatcher in ThreadWatchers) {
                LinkToParentThread(threadWatcher);
                ThreadWatcher watcher = threadWatcher;
                _runOnOwnerThread(() => {
                    AddedFromChanged?.Invoke(watcher);
                });
                if (Settings.ChildThreadsAreNewFormat == true && IsRestartableAfterLoad(threadWatcher)) {
                    threadWatcher.Start();
                }
            }
        }

        internal void LinkToParentThread(ThreadWatcher threadWatcher) {
            ThreadWatcher parentThread = FindLoadedParentThread(threadWatcher);
            threadWatcher.ParentThread = parentThread;
            if (parentThread != null) {
                parentThread.AddChildThread(threadWatcher);
            }
        }

        // Returns null if the thread has no (known) parent, or if linking it to its parent
        // would make a thread its own ancestor.
        private ThreadWatcher FindLoadedParentThread(ThreadWatcher threadWatcher) {
            string addedFrom = ((WatcherExtraData)threadWatcher.Tag).AddedFrom;
            ThreadWatcher parentThread;
            if (String.IsNullOrEmpty(addedFrom) || !TryGetThreadWatcher(addedFrom, out parentThread)) return null;
            return IsSelfOrAncestor(threadWatcher, parentThread) ? null : parentThread;
        }

        private bool IsSelfOrAncestor(ThreadWatcher threadWatcher, ThreadWatcher descendantThread) {
            // The step limit stops the walk even if the existing parent links already form a cycle.
            int stepsLeft;
            lock (_watchers) {
                stepsLeft = _watchers.Count + 1;
            }
            for (ThreadWatcher thread = descendantThread; thread != null && stepsLeft-- > 0; thread = thread.ParentThread) {
                if (thread == threadWatcher) return true;
            }
            return false;
        }

        private static bool IsRestartableAfterLoad(ThreadWatcher threadWatcher) {
            return threadWatcher.StopReason != StopReason.PageNotFound && threadWatcher.StopReason != StopReason.UserRequest;
        }

        private void MigrateChildThreadsToNewFormat() {
            if (Settings.ChildThreadsAreNewFormat == true) return;
            foreach (ThreadWatcher threadWatcher in ThreadWatchers) {
                if (threadWatcher.ChildThreads.Count == 0 || threadWatcher.ParentThread != null) continue;
                foreach (ThreadWatcher descendantThread in threadWatcher.DescendantThreads.Values) {
                    MoveDescendantThreadDirectory(threadWatcher, descendantThread);
                }
            }
            Settings.ChildThreadsAreNewFormat = true;
            Settings.Save();

            foreach (ThreadWatcher threadWatcher in ThreadWatchers) {
                if (IsRestartableAfterLoad(threadWatcher)) threadWatcher.Start();
            }
        }

        internal static void MoveDescendantThreadDirectory(ThreadWatcher threadWatcher, ThreadWatcher descendantThread) {
            descendantThread.DoNotRename = true;
            try {
                TryMoveDescendantThreadDirectory(threadWatcher, descendantThread);
            }
            finally {
                descendantThread.DoNotRename = false;
            }
        }

        private static void TryMoveDescendantThreadDirectory(ThreadWatcher threadWatcher, ThreadWatcher descendantThread) {
            string sourceDir = descendantThread.ThreadDownloadDirectory;
            if (!CanMoveDescendantThreadDirectory(threadWatcher, sourceDir)) return;
            string destDir = GetDescendantThreadDestDir(threadWatcher, descendantThread, sourceDir);
            if (String.Equals(destDir, sourceDir, StringComparison.Ordinal)) return;
            try {
                MoveThreadDirectory(sourceDir, destDir);
                descendantThread.ThreadDownloadDirectory = destDir;
            }
            catch (Exception ex) {
                Logger.Log(ex.ToString());
            }
        }

        private static bool CanMoveDescendantThreadDirectory(ThreadWatcher threadWatcher, string sourceDir) {
            return !String.IsNullOrEmpty(sourceDir) && !String.IsNullOrEmpty(threadWatcher.ThreadDownloadDirectory) && Directory.Exists(sourceDir);
        }

        internal static string GetDescendantThreadDestDir(ThreadWatcher threadWatcher, ThreadWatcher descendantThread, string sourceDir) {
            // A folder directly inside the download folder is already in the new layout.
            if (General.RemoveLastDirectory(sourceDir) == descendantThread.MainDownloadDirectory) {
                return sourceDir;
            }
            return Path.Combine(General.RemoveLastDirectory(threadWatcher.ThreadDownloadDirectory),
                General.GetRelativeDirectoryPath(descendantThread.ThreadDownloadDirectory, threadWatcher.ThreadDownloadDirectory));
        }

        private static void MoveThreadDirectory(string sourceDir, string destDir) {
            if (String.Equals(destDir, sourceDir, StringComparison.OrdinalIgnoreCase)) {
                Directory.Move(sourceDir, destDir + " Temp");
                sourceDir = destDir + " Temp";
            }
            if (!Directory.Exists(General.RemoveLastDirectory(destDir))) Directory.CreateDirectory(General.RemoveLastDirectory(destDir));
            Directory.Move(sourceDir, destDir);
        }

        private string BlacklistPath {
            get { return Path.Combine(SettingsDirectory, Settings.BlacklistFileName); }
        }

        // Called on a worker thread
        internal void LoadBlacklist() {
            try {
                string path = BlacklistPath;
                if (!File.Exists(path)) return;
                string[] lines = File.ReadAllLines(path);
                if (lines.Length < 1) return;
                AddBlacklistRules(lines);
            }
            catch (Exception ex) {
                Logger.Log(ex.ToString());
            }
        }

        // Also used by the command line, which reads the file itself
        internal void AddBlacklistRules(string[] lines) {
            for (int i = 0; i < lines.Length; i++) {
                string rule = lines[i];
                if (rule.Split('/').Length == 3) {
                    _blacklist.Add(rule);
                }
            }
        }

        // Writes the blacklist file with the threads added; they are only blacklisted if it was written
        internal void AddToBlacklist(IEnumerable<ThreadWatcher> watchers) {
            List<string> lines = new List<string>();
            foreach (string rule in _blacklist) {
                lines.Add(rule);
            }
            HashSet<string> blacklist = new HashSet<string>();
            foreach (ThreadWatcher watcher in watchers) {
                if (!_blacklist.Contains(watcher.PageID) && blacklist.Add(watcher.PageID)) {
                    lines.Add(watcher.PageID);
                }
            }
            try {
                string path = BlacklistPath;
                File.WriteAllLines(path, lines.ToArray());
                foreach (string pageID in blacklist) {
                    _blacklist.Add(pageID);
                }
            }
            catch (Exception ex) {
                Logger.Log(ex.ToString());
            }
        }

        internal bool IsBlacklisted(string pageID) {
            if (_blacklist.Contains(pageID)) return true;
            if (Settings.BlacklistWildcards != true) return false;
            string[] pageIDSplit = pageID.Split('/');
            if (pageIDSplit.Length != 3) return false;
            foreach (string rule in _blacklist) {
                if (MatchesWildcardRule(rule, pageIDSplit)) return true;
            }
            return false;
        }

        private static bool MatchesWildcardRule(string rule, string[] pageIDSplit) {
            string[] ruleSplit = rule.Split('/');
            if (ruleSplit.Length != 3) return false;
            for (int i = 0; i < 3; i++) {
                if (!MatchesWildcardRulePart(ruleSplit[i], pageIDSplit[i])) return false;
            }
            return true;
        }

        private static bool MatchesWildcardRulePart(string rulePart, string pageIDPart) {
            return rulePart == "*" || rulePart == pageIDPart;
        }

        internal MonitoringInfo GetMonitoringInfo() {
            int running = 0;
            int dead = 0;
            int stopped = 0;
            List<ThreadWatcher> watchers = ThreadWatchers;
            foreach (ThreadWatcher watcher in watchers) {
                if (watcher.IsRunning || watcher.IsWaiting) {
                    running++;
                }
                else if (watcher.StopReason == StopReason.PageNotFound) {
                    dead++;
                }
                else {
                    stopped++;
                }
            }
            return new MonitoringInfo {
                TotalThreads = watchers.Count,
                RunningThreads = running,
                DeadThreads = dead,
                StoppedThreads = stopped
            };
        }
    }
}
