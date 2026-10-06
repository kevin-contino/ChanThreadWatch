using System;
using System.Globalization;

namespace JDP {
    internal enum LocalApiSaveResult {
        // Nothing changed, so nothing was written
        Unchanged,
        Saved,
        // The values are set for this session, but settings.txt could not be written (Settings.Save logs why)
        NotSaved
    }

    // The Local API dialog's settings, apart from the window: the values it shows, the check of the port, and the save,
    // which sets only the values that changed. The keys are the ones ctw watch reads: ApiEnabled (only "1" is on),
    // ApiPort and ApiAllowUnknownHosts.
    internal sealed class LocalApiSettings {
        public bool Enabled { get; set; }
        public int Port { get; set; }
        public bool AllowUnknownHosts { get; set; }

        // A port that is missing or not valid shows as the default one, which the API then uses
        public static LocalApiSettings Load() {
            return new LocalApiSettings {
                Enabled = Settings.ApiEnabled == true,
                Port = Settings.ApiPort.Value,
                AllowUnknownHosts = Settings.ApiAllowUnknownHosts == true
            };
        }

        // Null when the text is a port from 1024 to 65535 (in port); otherwise the message to show
        public static string ParsePort(string text, out int port) {
            int value;
            bool isNumber = Int32.TryParse((text ?? String.Empty).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value);
            port = isNumber && Settings.IsValidApiPort(value) ? value : 0;
            return port != 0 ? null : "Enter a port from " + Settings.MinimumApiPort + " to " + Settings.MaximumApiPort + ".";
        }

        // Sets the values that differ from loaded, and a value that is not valid in the file (ApiEnabled, ApiPort), which
        // then holds what the dialog showed, and saves the settings if any was set
        public LocalApiSaveResult SaveChanges(LocalApiSettings loaded) {
            bool changed = SaveEnabled(loaded);
            changed |= SavePort(loaded);
            changed |= SaveAllowUnknownHosts(loaded);
            if (!changed) return LocalApiSaveResult.Unchanged;
            return Settings.Save() ? LocalApiSaveResult.Saved : LocalApiSaveResult.NotSaved;
        }

        private bool SaveEnabled(LocalApiSettings loaded) {
            if (Enabled == loaded.Enabled && !Settings.ApiEnabledIsNotValid) return false;
            Settings.ApiEnabled = Enabled;
            return true;
        }

        private bool SavePort(LocalApiSettings loaded) {
            if (Port == loaded.Port && !Settings.ApiPortIsNotValid) return false;
            Settings.ApiPort = Port;
            return true;
        }

        private bool SaveAllowUnknownHosts(LocalApiSettings loaded) {
            if (AllowUnknownHosts == loaded.AllowUnknownHosts) return false;
            Settings.ApiAllowUnknownHosts = AllowUnknownHosts;
            return true;
        }
    }

    internal enum LocalApiOkAction {
        Wait,
        Close,
        // The start failed: the dialog stays open with the reason in its status line
        StayOpen
    }

    // What the Local API dialog does after OK applied the settings: it waits for the start's result for up to MaxWait,
    // then closes when the API listens or is off, and stays open when the start failed. A start still running after
    // MaxWait goes on in the background, and the dialog closes. It never closes once a token was shown after OK was
    // pressed (the dialog turns "New token..." off while it waits), so a token is never closed away unseen.
    internal static class LocalApiOkWait {
        public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(3);

        public static LocalApiOkAction Decide(LocalApiState state, TimeSpan waited, bool tokenShownSinceOk) {
            if (state == LocalApiState.Failed || tokenShownSinceOk) return LocalApiOkAction.StayOpen;
            return state == LocalApiState.Starting && waited < MaxWait ? LocalApiOkAction.Wait : LocalApiOkAction.Close;
        }
    }
}
