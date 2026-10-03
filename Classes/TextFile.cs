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

        // Like WriteAllLinesAtomic, for content that has to be kept byte for byte.
        public static void WriteAllBytesAtomic(string path, byte[] content) {
            string tempPath = path + ".tmp";
            using (FileStream fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None)) {
                fs.Write(content, 0, content.Length);
                fs.Flush(true);
            }
            ReplaceWithTempFile(tempPath, path);
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
        // (e.g. with logins removed).
        public static string PreserveCopy(string path, Func<byte[], byte[]> filter) {
            string copyPath = GetCopyPath(path);
            byte[] content = filter(File.ReadAllBytes(path));
            using (FileStream fs = new FileStream(copyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                fs.Write(content, 0, content.Length);
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

        // The files hold one value per line, so line breaks inside a value are replaced
        // by spaces. Null becomes an empty string.
        public static string ToSingleLine(string value) {
            if (value == null) return String.Empty;
            return value.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}
