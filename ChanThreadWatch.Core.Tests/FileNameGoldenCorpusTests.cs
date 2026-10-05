using System;
using System.Collections.Generic;
using System.Reflection;
using System.Web;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // Golden outputs of the functions that turn names, titles and URLs into the file and folder
    // names written to disk. The expected values were recorded from the code before the classes
    // moved to ChanThreadWatch.Core; any difference means saved file names would change. Inputs
    // are made up. Non-ASCII characters are written as \u escapes so NFC and NFD forms stay visible.
    [TestClass]
    public class FileNameGoldenCorpusTests {
        private static readonly string LongA = new string('a', 300) + ".jpg";
        private static readonly string LongE = new string('\u00E9', 120);
        private static readonly string LongB = new string('b', 260) + ".png";

        // Input, General.CleanFileName(input)
        private static readonly string[][] CleanFileNameCases = {
            new[] { "Anonymous", "Anonymous" },
            new[] { "Sample Poster", "Sample Poster" },
            new[] { "name/with\\slashes", "namewithslashes" },
            new[] { "a:b*c?d\"e<f>g|h", "abcdefgh" },
            new[] { "tab\u0009here", "tabhere" },
            new[] { "nul\u0000char", "nulchar" },
            new[] { "ctl\u0001\u001F\u007Fend", "ctl\u007Fend" },
            new[] { "CON", "_CON" },
            new[] { "con", "_con" },
            new[] { "NUL.txt", "_NUL.txt" },
            new[] { "nul .txt", "_nul .txt" },
            new[] { "COM1", "_COM1" },
            new[] { "com1.jpg", "_com1.jpg" },
            new[] { "LPT9 ", "_LPT9" },
            new[] { "COM\u00B9", "_COM\u00B9" },
            new[] { "LPT\u00B3.png", "_LPT\u00B3.png" },
            new[] { "CONIN$", "_CONIN$" },
            new[] { "conout$.log", "_conout$.log" },
            new[] { "COM0", "COM0" },
            new[] { "LPT10", "LPT10" },
            new[] { "CONSOLE", "CONSOLE" },
            new[] { "AUX_backup", "AUX_backup" },
            new[] { "PRN.tar.gz", "_PRN.tar.gz" },
            new[] { "file...", "file" },
            new[] { "file. . ", "file" },
            new[] { "...", "" },
            new[] { ". .", "" },
            new[] { "..", "" },
            new[] { ".", "" },
            new[] { "", "" },
            new[] { " leading", " leading" },
            new[] { "trailing ", "trailing" },
            new[] { ".hidden", ".hidden" },
            new[] { "a.b.c.", "a.b.c" },
            new[] { LongA, LongA },
            new[] { LongE, LongE },
            new[] { "Caf\u00E9 Poster", "Caf\u00E9 Poster" },
            new[] { "Cafe\u0301 Poster", "Cafe\u0301 Poster" },
            new[] { "\uAC00\uB098", "\uAC00\uB098" },
            new[] { "\u1100\u1161\u1102\u1161", "\u1100\u1161\u1102\u1161" },
            new[] { "\u65E5\u672C\u8A9E\u306E\u540D\u524D", "\u65E5\u672C\u8A9E\u306E\u540D\u524D" },
            new[] { "smile\U0001F600face", "smile\U0001F600face" },
            new[] { "lone\uD800half", "lone\uD800half" },
            new[] { "rtl\u202Etxt.exe", "rtl\u202Etxt.exe" },
            new[] { "zero\u200Bwidth", "zero\u200Bwidth" },
            new[] { "nbsp\u00A0name", "nbsp\u00A0name" },
            new[] { "fullwidth\uFF0F\uFF1A\uFF1F", "fullwidth\uFF0F\uFF1A\uFF1F" }
        };

        // URL, General.URLFileName(url), ImageInfo.FileName and ThumbnailInfo.FileName for that URL
        private static readonly string[][] URLFileNameCases = {
            new[] { "http://media.test/w1/1234567890.jpg", "1234567890.jpg", "1234567890.jpg" },
            new[] { "http://media.test/w1/file%20name%2Fx.png", "file%20name%2Fx.png", "file%20name%2Fx.png" },
            new[] { "http://media.test/w1/img.jpg?size=large&x=1", "img.jpg?size=large&x=1", "img.jpgsize=large&x=1" },
            new[] { "http://media.test/w1/img.jpg?a=b|c<d>", "img.jpg?a=b|c<d>", "img.jpga=bcd" },
            new[] { "http://media.test/w1/", "", "" },
            new[] { "no-slash-at-all", "", "" },
            new[] { "http://media.test/w1/%E6%97%A5%E6%9C%AC.webm", "%E6%97%A5%E6%9C%AC.webm", "%E6%97%A5%E6%9C%AC.webm" },
            new[] { "http://media.test/w1/img.jpg#frag", "img.jpg#frag", "img.jpg#frag" },
            new[] { "http://media.test/w1/caf\u00E9.jpg", "caf\u00E9.jpg", "caf\u00E9.jpg" },
            new[] { "http://media.test/w1/cafe\u0301.jpg", "cafe\u0301.jpg", "cafe\u0301.jpg" },
            new[] { "http://media.test/w1/CON", "CON", "_CON" },
            new[] { "http://media.test/w1/nul.jpg", "nul.jpg", "_nul.jpg" },
            new[] { "http://media.test/w1/trailing.", "trailing.", "trailing" },
            new[] { "http://media.test/w1/..", "..", "" },
            new[] { "http://media.test/w1/a%2e%2e", "a%2e%2e", "a%2e%2e" },
            new[] { "http://media.test/w1/" + LongB, LongB, LongB },
            new[] { "http://media.test/w1/x?q=a/b.jpg", "b.jpg", "b.jpg" }
        };

        // Entity-encoded title or poster name, HttpUtility.HtmlDecode(input), and
        // General.CleanFileName(HttpUtility.HtmlDecode(input)) as the site helpers do. The decoded column
        // (MP-4a) was recorded from System.Web.HttpUtility.HtmlDecode in an app targeting .NET Framework 4.8;
        // the cleaned column is the corpus recorded before the move to ChanThreadWatch.Core.
        private static readonly string[][] DecodedNameCases = {
            new[] { "Tom &amp; Jerry", "Tom & Jerry", "Tom & Jerry" },
            new[] { "it&#x27;s here", "it's here", "it's here" },
            new[] { "it&#39;s here", "it's here", "it's here" },
            new[] { "&lt;b&gt;bold&lt;/b&gt;", "<b>bold</b>", "bboldb" },
            new[] { "&quot;quoted&quot;", "\"quoted\"", "quoted" },
            new[] { "&#47;slash&#47;", "/slash/", "slash" },
            new[] { "&nbsp;spaced&nbsp;", "\u00A0spaced\u00A0", "\u00A0spaced\u00A0" },
            new[] { "a&#58;b", "a:b", "ab" },
            new[] { "&#x2F;&#x2E;&#x2E;", "/..", "" },
            new[] { "&amp;amp; double", "&amp; double", "&amp; double" },
            new[] { "caf&eacute;", "caf\u00E9", "caf\u00E9" },
            new[] { "cafe&#x301;", "cafe\u0301", "cafe\u0301" },
            new[] { "&#x1F600; grin", "\U0001F600 grin", "\U0001F600 grin" },
            new[] { "&bogus; entity", "&bogus; entity", "&bogus; entity" },
            new[] { "&#0; zero", "\u0000 zero", " zero" },
            new[] { "CON&period;txt", "CON&period;txt", "CON&period;txt" },
            new[] { "&#67;&#79;&#78;", "CON", "_CON" },
            new[] { "dots&hellip;", "dots\u2026", "dots\u2026" },
            new[] { "end&#46;&#46;&#46;", "end...", "end" }
        };

        // G4 deltas. Each OS removes its own invalid file name characters (Path.GetInvalidFileNameChars),
        // so Linux and macOS keep < > : " | ? * and control characters other than NUL. Every OS removes
        // '/', '\' and NUL, trims trailing dots and spaces, and prefixes Windows device names, so no
        // other value differs. The columns above hold the Windows values, which are unchanged.
        //
        //   Function                          Input                         Windows                  Linux and macOS
        //   CleanFileName                     a:b*c?d"e<f>g|h               abcdefgh                 a:b*c?d"e<f>g|h
        //   CleanFileName                     tab\u0009here                 tabhere                  tab\u0009here
        //   CleanFileName                     ctl\u0001\u001F\u007Fend      ctl\u007Fend             ctl\u0001\u001F\u007Fend
        //   ImageInfo/ThumbnailInfo.FileName  .../img.jpg?size=large&x=1    img.jpgsize=large&x=1    img.jpg?size=large&x=1
        //   ImageInfo/ThumbnailInfo.FileName  .../img.jpg?a=b|c<d>          img.jpga=bcd             img.jpg?a=b|c<d>
        //   CleanFileName(HtmlDecode)         &lt;b&gt;bold&lt;/b&gt;      bboldb                   <b>bold<b>
        //   CleanFileName(HtmlDecode)         &quot;quoted&quot;            quoted                   "quoted"
        //   CleanFileName(HtmlDecode)         a&#58;b                       ab                       a:b
        //
        // Input, Linux and macOS value
        private static readonly Dictionary<string, string> UnixDeltas = new Dictionary<string, string>(StringComparer.Ordinal) {
            { "a:b*c?d\"e<f>g|h", "a:b*c?d\"e<f>g|h" },
            { "tab\u0009here", "tab\u0009here" },
            { "ctl\u0001\u001F\u007Fend", "ctl\u0001\u001F\u007Fend" },
            { "http://media.test/w1/img.jpg?size=large&x=1", "img.jpg?size=large&x=1" },
            { "http://media.test/w1/img.jpg?a=b|c<d>", "img.jpg?a=b|c<d>" },
            { "&lt;b&gt;bold&lt;/b&gt;", "<b>bold<b>" },
            { "&quot;quoted&quot;", "\"quoted\"" },
            { "a&#58;b", "a:b" }
        };

        // The recorded Windows value, or off Windows the G4 delta for the input where there is one
        private static string ExpectedOnThisOS(string input, string windowsValue) {
            string unixValue;
            return !OperatingSystem.IsWindows() && UnixDeltas.TryGetValue(input, out unixValue) ? unixValue : windowsValue;
        }

        // Thread name, page index, ThreadWatcher.GetPageFileName(threadName, pageIndex)
        private static readonly object[][] PageFileNameCases = {
            new object[] { "7770000004", 0, "7770000004.html" },
            new object[] { "7770000004", 2, "7770000004_3.html" },
            new object[] { "CON", 0, "_CON.html" },
            new object[] { "CON", 2, "_CON_3.html" },
            new object[] { "thread/7770000004", 0, "thread7770000004.html" },
            new object[] { "thread/7770000004", 2, "thread7770000004_3.html" },
            new object[] { "name...", 0, "name.html" },
            new object[] { "name...", 2, "name_3.html" }
        };

        [TestMethod]
        public void CleanFileNameMatchesGoldenCorpus() {
            var failures = new List<string>();
            foreach (string[] c in CleanFileNameCases) {
                Check(failures, "CleanFileName", c[0], ExpectedOnThisOS(c[0], c[1]), General.CleanFileName(c[0]));
            }
            AssertNoFailures(failures);
        }

        [TestMethod]
        public void URLFileNamesMatchGoldenCorpus() {
            var failures = new List<string>();
            foreach (string[] c in URLFileNameCases) {
                Check(failures, "URLFileName", c[0], c[1], General.URLFileName(c[0]));
                Check(failures, "ImageInfo.FileName", c[0], ExpectedOnThisOS(c[0], c[2]), new ImageInfo { URL = c[0] }.FileName);
                Check(failures, "ThumbnailInfo.FileName", c[0], ExpectedOnThisOS(c[0], c[2]), new ThumbnailInfo { URL = c[0] }.FileName);
            }
            AssertNoFailures(failures);
        }

        [TestMethod]
        public void DecodedNamesMatchGoldenCorpus() {
            var failures = new List<string>();
            foreach (string[] c in DecodedNameCases) {
                Check(failures, "CleanFileName(HtmlDecode)", c[0], ExpectedOnThisOS(c[0], c[2]), General.CleanFileName(HttpUtility.HtmlDecode(c[0])));
            }
            AssertNoFailures(failures);
        }

        // Decoding alone does not depend on the OS, so unlike DecodedNamesMatchGoldenCorpus it has no G4 deltas
        [TestMethod]
        public void HtmlDecodeMatchesRecordedNetFrameworkOutput() {
            var failures = new List<string>();
            foreach (string[] c in DecodedNameCases) {
                Check(failures, "HtmlDecode", c[0], c[1], HttpUtility.HtmlDecode(c[0]));
            }
            AssertNoFailures(failures);
        }

        [TestMethod]
        public void PageFileNamesMatchGoldenCorpus() {
            MethodInfo method = typeof(ThreadWatcher).GetMethod("GetPageFileName", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "Missing helper: GetPageFileName");
            var failures = new List<string>();
            foreach (object[] c in PageFileNameCases) {
                string actual = (string)method.Invoke(null, new[] { c[0], c[1] });
                Check(failures, "GetPageFileName", c[0] + " #" + c[1], (string)c[2], actual);
            }
            AssertNoFailures(failures);
        }

        private static void Check(List<string> failures, string function, string input, string expected, string actual) {
            if (!String.Equals(expected, actual, StringComparison.Ordinal)) {
                failures.Add(function + "(" + Escape(input) + "): expected " + Escape(expected) + ", got " + Escape(actual));
            }
        }

        private static void AssertNoFailures(List<string> failures) {
            Assert.IsEmpty(failures, String.Join(Environment.NewLine, failures));
        }

        private static string Escape(string s) {
            var sb = new System.Text.StringBuilder("\"");
            foreach (char c in s) {
                if (c >= 0x20 && c <= 0x7E) sb.Append(c);
                else sb.Append("\\u").Append(((int)c).ToString("X4"));
            }
            return sb.Append('"').ToString();
        }
    }
}
