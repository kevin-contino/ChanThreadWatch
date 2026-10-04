using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // The check interval helper is a private member of the main form, so it is reached through
    // reflection. The thread list helper tests are in ChanThreadWatch.Core.Tests.
    [TestClass]
    public class ThreadListLoadHelperTests {
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private string _dir;

        [ClassInitialize]
        public static void UseExeDirectoryForLog(TestContext context) {
            Settings.UseExeDirectoryForSettings = true;
        }

        [TestInitialize]
        public void Setup() {
            _dir = Path.Combine(Path.GetTempPath(), "ctw-migrate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Settings.Load(Path.Combine(_dir, "missing.txt"));
        }

        [TestCleanup]
        public void Cleanup() {
            Settings.Load(Path.Combine(_dir, "missing.txt"));
            Directory.Delete(_dir, true);
        }

        private static int GetCheckEveryMinutes(bool comboEnabled, string text) {
            frmChanThreadWatch form = (frmChanThreadWatch)FormatterServices.GetUninitializedObject(typeof(frmChanThreadWatch));
            GC.SuppressFinalize(form);
            using (ComboBox cbo = new ComboBox { Enabled = comboEnabled })
            using (TextBox txt = new TextBox { Text = text }) {
                typeof(frmChanThreadWatch).GetField("cboCheckEvery", PrivateInstance).SetValue(form, cbo);
                typeof(frmChanThreadWatch).GetField("txtCheckEvery", PrivateInstance).SetValue(form, txt);
                MethodInfo method = typeof(frmChanThreadWatch).GetMethod("GetCheckEveryMinutes", PrivateInstance);
                try {
                    return (int)method.Invoke(form, null);
                }
                catch (TargetInvocationException ex) {
                    throw ex.InnerException;
                }
            }
        }

        // R11
        [TestMethod]
        public void CheckEveryUsesTheTextBoxNumber() {
            Assert.AreEqual(12, GetCheckEveryMinutes(false, "12"));
        }

        [TestMethod]
        [DataRow(false, "abc")]
        [DataRow(false, "99999999999")]
        [DataRow(true, "")]
        public void CheckEveryFallsBackToTheSavedSettingInsteadOfThrowing(bool comboEnabled, string text) {
            Assert.AreEqual(3, GetCheckEveryMinutes(comboEnabled, text));
            Settings.CheckEvery = 7;
            Assert.AreEqual(7, GetCheckEveryMinutes(comboEnabled, text));
        }
    }
}
