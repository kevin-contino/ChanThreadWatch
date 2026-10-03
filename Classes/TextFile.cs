using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace JDP {
    // Helpers for the line-based files kept in the settings folder.
    public static class TextFile {
        // Writes the lines to a temporary file in the same folder and then swaps it in,
        // so an interrupted write leaves either the old or the new content, never a mix.
        public static void WriteAllLinesAtomic(string path, IEnumerable<string> lines) {
            string tempPath = path + ".tmp";
            using (FileStream fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (StreamWriter sw = new StreamWriter(fs, new UTF8Encoding(false))) {
                foreach (string line in lines) {
                    sw.WriteLine(line);
                }
                sw.Flush();
                fs.Flush(true);
            }
            ReplaceWithTempFile(tempPath, path);
        }

        // Like WriteAllLinesAtomic, for content that has to be kept byte for byte. The
        // temporary file's name starts differently from the file's, so a search for recovery
        // copies (GetCopySearchPattern) never finds it, and it is deleted if the write fails.
        public static void WriteAllBytesAtomic(string path, byte[] content) {
            string tempPath = Path.Combine(Path.GetDirectoryName(path), "~" + Path.GetFileName(path) + ".tmp");
            try {
                using (FileStream fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None)) {
                    fs.Write(content, 0, content.Length);
                    fs.Flush(true);
                }
                ReplaceWithTempFile(tempPath, path);
            }
            catch {
                TryDelete(tempPath);
                throw;
            }
        }

        private static void TryDelete(string path) {
            try {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                // Left behind; the error that made the write fail is what gets reported
            }
        }

        private static void ReplaceWithTempFile(string tempPath, string path) {
            if (File.Exists(path)) {
                File.Replace(tempPath, path, null, true);
            }
            else {
                File.Move(tempPath, path);
            }
        }

        // Copies the file to "<path>.corrupt-<timestamp>" so that later saves can't destroy
        // it. Returns the path of the copy. Throws if the copy can't be made.
        public static string PreserveCopy(string path) {
            string copyPath = GetCopyPath(path);
            File.Copy(path, copyPath, false);
            return copyPath;
        }

        // Like PreserveCopy, but the copy holds the file's bytes as changed by the filter
        // (e.g. with logins removed). A copy that can't be fully written is deleted.
        public static string PreserveCopy(string path, Func<byte[], byte[]> filter) {
            string copyPath = GetCopyPath(path);
            using (FileStream fs = new FileStream(copyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                try {
                    byte[] content = filter(File.ReadAllBytes(path));
                    fs.Write(content, 0, content.Length);
                    fs.Flush(true);
                }
                catch {
                    fs.Dispose();
                    TryDelete(copyPath);
                    throw;
                }
            }
            return copyPath;
        }

        // The pattern that finds the copies PreserveCopy made of the file
        public static string GetCopySearchPattern(string path) {
            return Path.GetFileName(path) + ".corrupt-*";
        }

        private static string GetCopyPath(string path) {
            return path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        }

        // Copies larger than this are not read again by RewriteCopies
        public const long MaxRewrittenCopySize = 64 * 1024 * 1024;

        // Runs the filter over every copy PreserveCopy made of the file, e.g. to remove logins
        // from copies made by a version that copied byte for byte. A copy the filter changes
        // is replaced atomically and its file name (never its content) is logged. A copy that
        // fails is left unchanged and logged, and the others are still done.
        public static void RewriteCopies(string path, Func<byte[], byte[]> filter) {
            try {
                foreach (string copyPath in Directory.GetFiles(Path.GetDirectoryName(path), GetCopySearchPattern(path))) {
                    TryRewriteCopy(copyPath, filter);
                }
            }
            catch (Exception ex) {
                Logger.Log("The recovery copies of " + Path.GetFileName(path) + " could not be listed to remove plaintext logins; the next session tries again. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void TryRewriteCopy(string copyPath, Func<byte[], byte[]> filter) {
            try {
                RewriteCopy(copyPath, filter);
            }
            catch (Exception ex) {
                Logger.Log("Plaintext logins could not be removed from " + Path.GetFileName(copyPath) + "; the next session tries again. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void RewriteCopy(string copyPath, Func<byte[], byte[]> filter) {
            if (new FileInfo(copyPath).Length > MaxRewrittenCopySize) {
                Logger.Log(Path.GetFileName(copyPath) + " was not checked for plaintext logins because it is larger than " + (MaxRewrittenCopySize / (1024 * 1024)) + " MB.");
                return;
            }
            byte[] content = File.ReadAllBytes(copyPath);
            byte[] filtered = filter(content);
            // Removing anything always shortens the content
            if (filtered.Length == content.Length) return;
            WriteAllBytesAtomic(copyPath, filtered);
            Logger.Log("Plaintext logins were removed from " + Path.GetFileName(copyPath));
        }

        // Returns the content with the end of some lines removed: getKeptLengths gets the
        // lines (split as File.ReadAllLines splits them) and returns for each one how many
        // characters to keep, or -1 to keep all. A file without a byte order mark or with
        // UTF-8's keeps every other byte; a UTF-16 or UTF-32 file is decoded and written again
        // in the same encoding with the same byte order mark. Removing anything always makes
        // the content shorter, and if nothing is removed the same array is returned.
        public static byte[] CutLineEnds(byte[] content, Func<string[], int[]> getKeptLengths) {
            using (StreamReader sr = new StreamReader(new MemoryStream(content), new UTF8Encoding(false), true)) {
                sr.Peek();
                if (sr.CurrentEncoding is UTF8Encoding) return CutUtf8LineEnds(content, getKeptLengths);
                return CutDecodedLineEnds(content, sr.ReadToEnd(), sr.CurrentEncoding, getKeptLengths);
            }
        }

        // The text is cut as UTF-8 (which holds any decoded text) and encoded back
        private static byte[] CutDecodedLineEnds(byte[] content, string text, Encoding encoding, Func<string[], int[]> getKeptLengths) {
            byte[] utf8 = new UTF8Encoding(false).GetBytes(text);
            byte[] cut = CutUtf8LineEnds(utf8, getKeptLengths);
            if (cut == utf8) return content;
            byte[] preamble = encoding.GetPreamble();
            byte[] body = encoding.GetBytes(Encoding.UTF8.GetString(cut));
            byte[] result = new byte[preamble.Length + body.Length];
            Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
            Buffer.BlockCopy(body, 0, result, preamble.Length, body.Length);
            return result;
        }

        private static byte[] CutUtf8LineEnds(byte[] content, Func<string[], int[]> getKeptLengths) {
            List<LineSpan> spans = SplitLines(content, HasUtf8ByteOrderMark(content) ? 3 : 0);
            string[] lines = spans.ConvertAll(span => Encoding.UTF8.GetString(content, span.Start, span.Length)).ToArray();
            List<LineSpan> cuts = GetCuts(spans, lines, getKeptLengths(lines));
            return cuts.Count != 0 ? RemoveSpans(content, cuts) : content;
        }

        private static List<LineSpan> GetCuts(List<LineSpan> spans, string[] lines, int[] keptLengths) {
            List<LineSpan> cuts = new List<LineSpan>();
            for (int i = 0; i < lines.Length; i++) {
                if (keptLengths[i] < 0 || keptLengths[i] >= lines[i].Length) continue;
                // The kept part decodes from valid UTF-8, so its length in bytes is exact
                int keptBytes = Encoding.UTF8.GetByteCount(lines[i].Substring(0, keptLengths[i]));
                cuts.Add(new LineSpan(spans[i].Start + keptBytes, spans[i].Start + spans[i].Length));
            }
            return cuts;
        }

        private static bool HasUtf8ByteOrderMark(byte[] content) {
            return content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF;
        }

        // Lines end at CR, LF or CRLF, and a last line break doesn't start another line
        private static List<LineSpan> SplitLines(byte[] content, int start) {
            List<LineSpan> lines = new List<LineSpan>();
            while (start < content.Length) {
                int end = FindLineEnd(content, start);
                lines.Add(new LineSpan(start, end));
                start = SkipLineBreak(content, end);
            }
            return lines;
        }

        private static int FindLineEnd(byte[] content, int start) {
            int end = start;
            while (end < content.Length && content[end] != '\r' && content[end] != '\n') {
                end++;
            }
            return end;
        }

        private static int SkipLineBreak(byte[] content, int end) {
            bool isCRLF = end + 1 < content.Length && content[end] == '\r' && content[end + 1] == '\n';
            return end + (isCRLF ? 2 : 1);
        }

        private static byte[] RemoveSpans(byte[] content, List<LineSpan> spans) {
            using (MemoryStream ms = new MemoryStream(content.Length)) {
                int copied = 0;
                foreach (LineSpan span in spans) {
                    ms.Write(content, copied, span.Start - copied);
                    copied = span.Start + span.Length;
                }
                ms.Write(content, copied, content.Length - copied);
                return ms.ToArray();
            }
        }

        private struct LineSpan {
            public readonly int Start;
            public readonly int Length;

            public LineSpan(int start, int end) {
                Start = start;
                Length = end - start;
            }
        }

        // The files hold one value per line, so line breaks inside a value are replaced
        // by spaces. Null becomes an empty string.
        public static string ToSingleLine(string value) {
            if (value == null) return String.Empty;
            return value.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}
