namespace JDP {
    // The main window's choices for a new thread (frmChanThreadWatch.AddThread), read on the UI thread: the check
    // interval (0 for a one-time download), one-time download, auto-follow and category. No login.
    internal sealed class NewThreadChoices {
        public int CheckIntervalSeconds { get; set; }
        public bool OneTimeDownload { get; set; }
        public bool AutoFollow { get; set; }
        public string Category { get; set; }

        // A thread for the local API (maintainer decision D8): the main window's current choices, from the Core's new
        // thread (NewThread.Create), never with a login or a folder
        public ThreadInfo CreateApiThread(string url) {
            ThreadInfo thread = NewThread.Create(url, null, Category);
            thread.CheckIntervalSeconds = CheckIntervalSeconds;
            thread.OneTimeDownload = OneTimeDownload;
            thread.AutoFollow = AutoFollow;
            return thread;
        }
    }
}
