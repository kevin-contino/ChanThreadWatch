using System;
using System.Collections.Generic;
using System.Globalization;

namespace JDP {
    // Format of api-threads.txt, kept beside the thread list file: the page IDs of the threads added through the
    // local API, whose downloads are guarded (ThreadWatcher.Guarded). The first line is the file version, then
    // one page ID per line. The mark is kept here rather than in threads.txt because the previous release loads
    // no thread from a threads.txt with an extra line or a new version; that release never opens this file, so
    // the file survives a rollback and the next start of this version marks the threads again.
    public static class ApiThreadsFile {
        public const int CurrentVersion = 1;

        // Throws FormatException for an empty file or a version this one does not know. Empty lines are ignored.
        public static HashSet<string> Parse(string[] lines) {
            if (lines.Length == 0) throw new FormatException("The file is empty.");
            if (lines[0] != CurrentVersion.ToString(CultureInfo.InvariantCulture)) throw new FormatException("Unsupported file version: " + lines[0]);
            HashSet<string> pageIDs = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 1; i < lines.Length; i++) {
                if (lines[i].Length != 0) pageIDs.Add(lines[i]);
            }
            return pageIDs;
        }

        // Sorted, so the same page IDs always give the same file
        public static string[] Serialize(IEnumerable<string> pageIDs) {
            List<string> sorted = new List<string>(pageIDs);
            sorted.Sort(StringComparer.Ordinal);
            List<string> lines = new List<string> { CurrentVersion.ToString(CultureInfo.InvariantCulture) };
            foreach (string pageID in sorted) {
                lines.Add(TextFile.ToSingleLine(pageID));
            }
            return lines.ToArray();
        }
    }
}
