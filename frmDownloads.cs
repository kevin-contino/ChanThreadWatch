using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace JDP {
    public partial class frmDownloads : Form {
        private frmChanThreadWatch _parentForm;
        private Dictionary<long, ListViewItem> _items = new Dictionary<long, ListViewItem>();
        private Dictionary<long, List<DownloadedSizeSnapshot>> _snapshotLists = new Dictionary<long, List<DownloadedSizeSnapshot>>();

        public frmDownloads(frmChanThreadWatch parentForm) {
            InitializeComponent();
            GUI.SetFontAndScaling(this);
            GUI.EnableDoubleBuffering(lvDownloads);
            _parentForm = parentForm;
        }

        private void tmrUpdateList_Tick(object sender, EventArgs e) {
            HashSet<long> oldDownloadIDs = new HashSet<long>(_items.Keys);
            List<DownloadProgressInfo> downloadProgresses;
            lock (_parentForm.DownloadProgresses) {
                downloadProgresses = new List<DownloadProgressInfo>(_parentForm.DownloadProgresses.Values);
            }
            downloadProgresses.Sort((a, b) => a.StartTicks.CompareTo(b.StartTicks));
            Dictionary<long, long?> bytesPerSecByID = new Dictionary<long, long?>();
            Text = TakeSnapshots(downloadProgresses, _snapshotLists, TickCount.Now, bytesPerSecByID);
            foreach (DownloadProgressInfo info in downloadProgresses) {
                // A finished download stays listed with its result until the main form drops it
                // after the hold time (frmChanThreadWatch.FinishedDownloadHoldMilliseconds)
                UpdateDownloadProgress(info, bytesPerSecByID[info.DownloadID]);
                oldDownloadIDs.Remove(info.DownloadID);
            }
            foreach (long downloadID in oldDownloadIDs) {
                RemoveDownloadProgress(downloadID);
            }
        }

        // Adds a size snapshot to each download's list (finished downloads too, so the total speed
        // counts their last bytes), puts each download's speed in bytesPerSecByID and returns the
        // window title with the total speed
        internal static string TakeSnapshots(List<DownloadProgressInfo> downloadProgresses, Dictionary<long, List<DownloadedSizeSnapshot>> snapshotLists,
            long ticksNow, Dictionary<long, long?> bytesPerSecByID)
        {
            long totalDownloadedSize = 0;
            long minTotalDownloadedStartTicks = Int64.MaxValue;
            foreach (DownloadProgressInfo info in downloadProgresses) {
                List<DownloadedSizeSnapshot> snapshotList = GetSnapshotList(snapshotLists, info);
                RemoveExpiredSnapshots(snapshotList, ticksNow);
                snapshotList.Add(new DownloadedSizeSnapshot(ticksNow, info.DownloadedSize));
                int iLast = snapshotList.Count - 1;
                bytesPerSecByID[info.DownloadID] = GetBytesPerSecond(snapshotList);
                int iFirstForTotalWindow = GetFirstIndexForTotalWindow(snapshotList, ticksNow);
                totalDownloadedSize += snapshotList[iLast].DownloadedSize - snapshotList[iFirstForTotalWindow].DownloadedSize;
                minTotalDownloadedStartTicks = Math.Min(minTotalDownloadedStartTicks, snapshotList[iFirstForTotalWindow].Ticks);
            }
            RemoveUnlistedSnapshotLists(snapshotLists, downloadProgresses);
            long totalDownloadedTicks = ticksNow - minTotalDownloadedStartTicks;
            return GetTitle(totalDownloadedSize, totalDownloadedTicks);
        }

        // Drops the snapshots of downloads the main form no longer lists
        private static void RemoveUnlistedSnapshotLists(Dictionary<long, List<DownloadedSizeSnapshot>> snapshotLists, List<DownloadProgressInfo> downloadProgresses) {
            HashSet<long> listedIDs = new HashSet<long>();
            foreach (DownloadProgressInfo info in downloadProgresses) {
                listedIDs.Add(info.DownloadID);
            }
            List<long> unlistedIDs = new List<long>();
            foreach (long downloadID in snapshotLists.Keys) {
                if (!listedIDs.Contains(downloadID)) unlistedIDs.Add(downloadID);
            }
            foreach (long downloadID in unlistedIDs) {
                snapshotLists.Remove(downloadID);
            }
        }

        private static List<DownloadedSizeSnapshot> GetSnapshotList(Dictionary<long, List<DownloadedSizeSnapshot>> snapshotLists, DownloadProgressInfo info) {
            List<DownloadedSizeSnapshot> snapshotList;
            if (!snapshotLists.TryGetValue(info.DownloadID, out snapshotList)) {
                snapshotList = new List<DownloadedSizeSnapshot>();
                snapshotList.Add(new DownloadedSizeSnapshot(info.StartTicks, 0));
                snapshotLists[info.DownloadID] = snapshotList;
            }
            return snapshotList;
        }

        // Drops snapshots older than the 5 second speed window.
        private static void RemoveExpiredSnapshots(List<DownloadedSizeSnapshot> snapshotList, long ticksNow) {
            while (snapshotList.Count != 0 && ticksNow - snapshotList[0].Ticks > 5000) {
                snapshotList.RemoveAt(0);
            }
        }

        private static long? GetBytesPerSecond(List<DownloadedSizeSnapshot> snapshotList) {
            int iLast = snapshotList.Count - 1;
            long size = snapshotList[iLast].DownloadedSize - snapshotList[0].DownloadedSize;
            long ticks = snapshotList[iLast].Ticks - snapshotList[0].Ticks;
            if (size > 0 && ticks > 0) {
                return Convert.ToInt64(size / (ticks / 1000.0));
            }
            return null;
        }

        // Finds the first snapshot within the 2 second total speed window that has
        // less data than the latest snapshot, or the latest snapshot if none does.
        private static int GetFirstIndexForTotalWindow(List<DownloadedSizeSnapshot> snapshotList, long ticksNow) {
            int iLast = snapshotList.Count - 1;
            for (int i = 0; i < snapshotList.Count; i++) {
                if (ticksNow - snapshotList[i].Ticks <= 2000 &&
                    snapshotList[i].DownloadedSize < snapshotList[iLast].DownloadedSize)
                {
                    return i;
                }
            }
            return iLast;
        }

        private static string GetTitle(long totalDownloadedSize, long totalDownloadedTicks) {
            if (totalDownloadedSize > 0 && totalDownloadedTicks > 0) {
                return "Downloads - " + GetKilobytesString(Convert.ToInt64(
                    totalDownloadedSize / (totalDownloadedTicks / 1000.0)), "KB/s");
            }
            return "Downloads";
        }

        public void UpdateDownloadProgress(DownloadProgressInfo info, long? bytesPerSec) {
            ListViewItem item;
            if (!_items.TryGetValue(info.DownloadID, out item)) {
                item = new ListViewItem(String.Empty);
                for (int i = 1; i < lvDownloads.Columns.Count; i++) {
                    item.SubItems.Add(String.Empty);
                }
                SetSubItemText(item, ColumnIndex.URL, info.URL);
                SetSubItemText(item, ColumnIndex.Try, info.TryNumber.ToString());
                lvDownloads.Items.Add(item);
                _items[info.DownloadID] = item;
            }
            SetSubItemText(item, ColumnIndex.Size, GetSizeText(info));
            SetSubItemText(item, ColumnIndex.Percent, GetProgressText(info));
            SetSubItemText(item, ColumnIndex.Speed, GetSpeedText(info, bytesPerSec));
        }

        // The announced size, or the size so far without one; a successful download's final size
        internal static string GetSizeText(DownloadProgressInfo info) {
            return GetKilobytesString(info.TotalSize ?? info.DownloadedSize, "KB");
        }

        // A finished download shows no speed
        internal static string GetSpeedText(DownloadProgressInfo info, long? bytesPerSec) {
            return GetKilobytesString(info.EndTicks == null ? bytesPerSec : null, "KB/s");
        }

        // The Progress column: the result once the download has ended, otherwise the percent done
        // when the size is known
        internal static string GetProgressText(DownloadProgressInfo info) {
            if (info.EndTicks != null) {
                return info.IsSuccessful ? "Done" : "Failed";
            }
            if (info.TotalSize > 0) {
                return (info.DownloadedSize * 100 / info.TotalSize.Value).ToString() + "%";
            }
            return String.Empty;
        }

        private void RemoveDownloadProgress(long downloadID) {
            ListViewItem item;
            if (!_items.TryGetValue(downloadID, out item)) return;
            lvDownloads.Items.Remove(item);
            _items.Remove(downloadID);
        }

        private static string GetKilobytesString(long? byteSize, string units) {
            if (byteSize == null) return String.Empty;
            return (byteSize.Value / 1024).ToString("#,##0") + " " + units;
        }

        private void SetSubItemText(ListViewItem item, ColumnIndex columnIndex, string text) {
            var subItem = item.SubItems[(int)columnIndex];
            if (subItem.Text != text) {
                subItem.Text = text;
            }
        }

        private enum ColumnIndex {
            URL = 0,
            Size = 1,
            Percent = 2,
            Speed = 3,
            Try = 4
        }
    }
}