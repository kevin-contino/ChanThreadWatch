using System;
using System.Collections.Generic;

namespace JDP {
    // The status texts shown for a watcher in the thread list
    internal static class WatcherStatusText {
        private static readonly Dictionary<StopReason, string> _stopReasonTexts = new Dictionary<StopReason, string> {
            { StopReason.UserRequest, "User requested" },
            { StopReason.Exiting, "Exiting" },
            { StopReason.PageNotFound, "Page not found" },
            { StopReason.DownloadComplete, "Download complete" },
            { StopReason.IOError, "Error writing to disk" }
        };

        // E.g. "Waiting 60 seconds", "Error: HTTP 403 Forbidden, waiting 60 seconds",
        // "Rate limited by i.4cdn.org until 14:32:05" or
        // "2 files failed, waiting 60 seconds"
        internal static string FormatWaitStatus(int remainingSeconds, string checkError, int failedFileCount, string rateLimitedHost = null, DateTime rateLimitResumeTime = default(DateTime)) {
            if (checkError != null) return String.Format("Error: {0}, waiting {1} seconds", checkError, remainingSeconds);
            if (rateLimitedHost != null) return String.Format("Rate limited by {0} until {1:HH:mm:ss}", rateLimitedHost, rateLimitResumeTime);
            if (failedFileCount > 0) return String.Format("{0}, waiting {1} seconds", FormatFailedFileCount(failedFileCount), remainingSeconds);
            return String.Format("Waiting {0} seconds", remainingSeconds);
        }

        // E.g. "Stopped: Download complete", "Stopped: Download complete, 2 files failed" or
        // "Stopped: Error: HTTP 403 Forbidden"
        internal static string FormatStopStatus(StopReason stopReason, string stopError, int failedFileCount) {
            if (stopError != null && IsStopWithoutKnownReason(stopReason)) return "Stopped: Error: " + stopError;
            string reasonText = GetStopReasonText(stopReason);
            if (stopReason == StopReason.DownloadComplete && failedFileCount > 0) {
                reasonText += ", " + FormatFailedFileCount(failedFileCount);
            }
            return "Stopped: " + reasonText;
        }

        // E.g. "Stopped: User requested, reparse failed: Access to the path is denied"
        internal static string AppendReparseError(string stopStatus, string reparseError) {
            return !String.IsNullOrEmpty(reparseError) ? stopStatus + ", reparse failed: " + reparseError : stopStatus;
        }

        private static string GetStopReasonText(StopReason stopReason) {
            string reasonText;
            return _stopReasonTexts.TryGetValue(stopReason, out reasonText) ? reasonText : "Unknown error";
        }

        // stopError is only set for a stop it caused (a one-time download whose page failed stops with Other)
        private static bool IsStopWithoutKnownReason(StopReason stopReason) {
            return stopReason == StopReason.Other || stopReason == StopReason.DownloadComplete;
        }

        private static string FormatFailedFileCount(int failedFileCount) {
            return String.Format("{0} file{1} failed", failedFileCount, failedFileCount != 1 ? "s" : String.Empty);
        }
    }
}
