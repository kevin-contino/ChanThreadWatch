using System;

namespace JDP {
    // A thread as the app's Add button creates it, from the settings that hold the main window's defaults
    // (frmChanThreadWatch.AddThread): no login, the "check every" interval unless it is a one-time download,
    // auto-follow, and no folder, stop reason or parent thread yet. Used by ctw add and the local API.
    internal static class NewThread {
        public static ThreadInfo Create(string url, string description, string category) {
            bool oneTimeDownload = Settings.OneTimeDownload == true;
            return new ThreadInfo {
                URL = url,
                PageAuth = String.Empty,
                ImageAuth = String.Empty,
                CheckIntervalSeconds = GetCheckIntervalSeconds(oneTimeDownload),
                OneTimeDownload = oneTimeDownload,
                SaveDir = String.Empty,
                Description = description ?? String.Empty,
                StopReason = null,
                ExtraData = new WatcherExtraData { AddedOn = DateTime.Now, AddedFrom = String.Empty },
                Category = category ?? String.Empty,
                AutoFollow = Settings.AutoFollow == true
            };
        }

        // The app disables "check every" for a one-time download, which then saves 0
        private static int GetCheckIntervalSeconds(bool oneTimeDownload) {
            return oneTimeDownload ? 0 : GetCheckEveryMinutes() * 60;
        }

        // 3 minutes when the setting is missing. At startup the app changes a saved 1 to 0 ("1 or <") before it
        // shows it (frmChanThreadWatch constructor), so a 1 adds threads with 0.
        private static int GetCheckEveryMinutes() {
            int minutes = Settings.CheckEvery ?? 3;
            return minutes == 1 ? 0 : minutes;
        }
    }
}
