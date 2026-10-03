using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace JDP {
    // Our own script for saved thread pages (Resources\OfflinePageScript.js): quote previews,
    // backlinks and inline image expansion that work offline. The site's scripts are never kept;
    // the page's policy allows only this script, by the hash of its exact text.
    public static class OfflinePageScript {
        // The site markup the script reads, chosen by the site helper
        public const string FourChan = "4chan";
        public const string Vichan = "vichan";
        public const string Fuuka = "fuuka";
        public const string FoolFuuka = "foolfuuka";
        public const string LynxChan = "lynxchan";

        private const string ResourceName = "OfflinePageScript.js";

        // Browsers hash the script text after turning CRLF into LF, so the text is kept with LF
        // whatever line endings the resource was built with
        public static readonly string Text = LoadText();

        public static readonly string Hash = ComputeHash(Text);

        // Like General.ActiveContentPolicyMeta, but allows this script, and no requests from it
        public static readonly string PolicyMeta = "<meta http-equiv=\"Content-Security-Policy\" content=\"script-src 'sha256-" + Hash + "'; object-src 'none'; frame-src 'none'; connect-src 'none'\">";

        public static string CreateElement(string site) {
            if (!Regex.IsMatch(site, "^[a-z0-9]+$")) throw new ArgumentException("Invalid site name: " + site, nameof(site));
            return "<script data-site=\"" + site + "\">" + Text + "</script>";
        }

        private static string LoadText() {
            using (Stream stream = typeof(OfflinePageScript).Assembly.GetManifestResourceStream(ResourceName)) {
                if (stream == null) throw new InvalidOperationException("Missing resource " + ResourceName);
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8)) {
                    return reader.ReadToEnd().Replace("\r\n", "\n");
                }
            }
        }

        private static string ComputeHash(string text) {
            using (SHA256 sha256 = SHA256.Create()) {
                return Convert.ToBase64String(sha256.ComputeHash(Encoding.UTF8.GetBytes(text)));
            }
        }
    }
}
