using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // The Local API dialog's Copy puts the token on the clipboard with the formats that keep it out of the Windows
    // clipboard history and the cloud clipboard. The data is read back from the data object only: the test never sets
    // the system clipboard, which belongs to the user of the computer.
    [TestClass]
    public class LocalApiClipboardTests {
        [TestMethod]
        public void TheTokenDataIsTextMarkedForNoHistoryAndNoCloud() {
            DataObject data = frmLocalApi.CreateTokenData("ctw_test");

            Assert.AreEqual("ctw_test", data.GetText(TextDataFormat.UnicodeText));
            CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0 }, ReadFormat(data, "CanIncludeInClipboardHistory"));
            CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0 }, ReadFormat(data, "CanUploadToCloudClipboard"));
            Assert.IsTrue(data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing"));
        }

        private static byte[] ReadFormat(DataObject data, string format) {
            MemoryStream stream;
            Assert.IsTrue(data.TryGetData(format, out stream), format);
            return stream.ToArray();
        }
    }
}
