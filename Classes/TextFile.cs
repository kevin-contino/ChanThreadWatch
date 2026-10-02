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
            string copyPath = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            File.Copy(path, copyPath, false);
            return copyPath;
        }

        // The files hold one value per line, so line breaks inside a value are replaced
        // by spaces. Null becomes an empty string.
        public static string ToSingleLine(string value) {
            if (value == null) return String.Empty;
            return value.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}
