using System;
using System.IO;
using JDP.Api;

namespace JDP.Cli {
    // ctw api-token: makes a new token for the local API (MP-7a) and prints it once on stdout, the only place a token
    // is ever shown; only its hash is saved, in api-token.txt, which only the current user can read (ApiTokenStore).
    // A new token replaces the previous one, which stops working at once, also in a ctw watch that runs (each request
    // reads the file again). It takes no lock and writes no other file, so it works while ctw watch or the app runs.
    internal static class ApiTokenCommand {
        public static int Run(CliContext context) {
            // The API does not run as root (ApiServer), and as root the file's mode would prove nothing
            if (OwnerOnlyFile.IsRootOnUnix()) throw new CliException("ctw api-token does not run as root, since the local API does not. Run it as the user that runs ctw watch.");
            SettingsFolder folder = context.GetSettingsFolder();
            // The app creates its folder in the application data the same way
            if (folder.IsAppData) Directory.CreateDirectory(folder.Path);
            ApiTokenStore store = new ApiTokenStore(folder.Path);
            bool replacing = File.Exists(store.Path);
            WriteToken(context, Generate(store));
            // The token is out: a note that can't be written (a closed pipe) is dropped and does not fail the command
            WatchStatusOutput notes = new WatchStatusOutput(context.Error);
            notes.WriteLine("ctw: note: This is the local API's token. It is shown only this once: only its hash is saved, in " + ApiTokenStore.FileName +
                " in the settings folder " + ConsoleText.Clean(folder.Path) + ". Send it as \"Authorization: Bearer <token>\".");
            if (replacing) notes.WriteLine("ctw: note: It replaces the previous token, which no longer works, also in a ctw watch that runs now.");
            notes.WriteLine("ctw: note: ctw watch runs the local API when " + Settings.SettingsFileName + " has ApiEnabled=1.");
            return CliApp.ExitSuccess;
        }

        private static string Generate(ApiTokenStore store) {
            try {
                return store.Generate();
            }
            catch (ApiTokenException ex) {
                throw new CliException(ex.Message);
            }
        }

        // The hash is already saved, so a token that can't be shown leaves no working token
        private static void WriteToken(CliContext context, string token) {
            try {
                context.Output.WriteLine(token);
                context.Output.Flush();
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException) {
                throw new CliException("The new token could not be written to stdout, and the previous token, if any, no longer works. Run 'ctw api-token' again.");
            }
        }
    }
}
