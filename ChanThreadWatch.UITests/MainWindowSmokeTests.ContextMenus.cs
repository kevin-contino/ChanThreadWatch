using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Exceptions;
using JDP.Tests.Integration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.UITests {
    // The thread list, its column headers and the tray icon each have a context menu. These tests
    // open each menu in the real app and check what it offers: the item texts in order, where the
    // separators are, and which items are enabled or checked.
    //
    // The tests do not use SendInput (FlaUI's Mouse class): it needs the app in the foreground and
    // an unlocked desktop. They move the cursor and post the right-click messages to the window
    // under it instead, which is what the system delivers for a real right-click, so the app runs
    // the same code.
    //
    // UI Automation finds the open menu, but the items are read through Active Accessibility: on
    // .NET Framework, the UI Automation provider of a WinForms menu fails (E_FAIL) when it steps
    // past a hidden item, and the thread menu hides the items that do not apply.
    public partial class MainWindowSmokeTests {
        private const string Separator = "---";
        private const string ThreadPage = "<html><head><title>Menu</title></head><body><p>Menu test thread</p></body></html>";
        // A right-click the app was not ready for is sent again after this long
        private static readonly TimeSpan ClickRetryInterval = TimeSpan.FromSeconds(2);

        // The app is per-monitor DPI aware. The test process must be too, or the cursor position it
        // sets and the element positions UI Automation reports use different scales above 100%.
        [AssemblyInitialize]
        public static void MakeTestProcessDpiAware(TestContext context) {
            try {
                if (!SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) {
                    context.WriteLine("SetProcessDpiAwarenessContext failed with error " + Marshal.GetLastWin32Error() + "; the test process keeps its DPI awareness");
                }
            }
            catch (EntryPointNotFoundException) {
                // Windows 10 before version 1703, where the click point check in RightClickAt shows any mismatch
            }
        }

        [TestMethod]
        [TestCategory("UI")]
        public void ThreadMenuForStoppedThreadOffersStartAndRemove() {
            _server.Route(ThreadPath, LoopbackResponse.Html(ThreadPage));
            Window window = LaunchApp();
            AutomationElement threadList = AddThread(window, _server.URL(ThreadPath), true);
            WaitUntil(() => RowHasCell(threadList, "Stopped: Download complete"), "the thread to stop after its one-time download");

            AutomationElement menu = RightClickFirstRow(threadList);

            CollectionAssert.AreEqual(new[] {
                "Edit", "Open Folder", "Open URL", "Start", "Copy URL", "Remove", "Remove and Delete Folder", "Blacklist", "Reparse"
            }, DescribeItems(menu));
        }

        [TestMethod]
        [TestCategory("UI")]
        public void ThreadMenuForRunningThreadOffersStopAndCheckEvery() {
            _server.Route(ThreadPath, LoopbackResponse.Html(ThreadPage));
            Window window = LaunchApp();
            AutomationElement threadList = AddThread(window, _server.URL(ThreadPath), false);
            // After its first check a watched thread waits for the next one and keeps running
            WaitUntil(() => RowHasCellStartingWith(threadList, "Waiting "), "the thread to wait for its next check");

            AutomationElement menu = RightClickFirstRow(threadList);

            CollectionAssert.AreEqual(new[] {
                "Edit", "Open Folder", "Open URL", "Stop", "Copy URL", "Blacklist", "Check Now", "Check Every"
            }, DescribeItems(menu));

            // The interval submenu is built from the Add Thread interval list at startup
            FindItem(menu, "Check Every").DoDefaultAction();
            AutomationElement submenu = WaitForMenu("2 Minutes", "the Check Every submenu");
            CollectionAssert.AreEqual(new[] {
                "1 Minute or <", "2 Minutes", "3 Minutes", "5 Minutes", "10 Minutes", "60 Minutes"
            }, DescribeItems(submenu));
        }

        [TestMethod]
        [TestCategory("UI")]
        public void ColumnHeaderMenuChecksVisibleColumnsAndHidesOne() {
            Window window = LaunchApp();
            // The app measures where the rows start once the thread list has loaded
            WaitUntilReady(window);
            AutomationElement threadList = FindById(window, "lvThreads");
            string[] allChecked = { "[x] Description", "[x] Status", "[x] Last Image On", "[x] Added On", "[x] Added From", "[x] Category" };

            AutomationElement menu = RightClickHeader(threadList, "Description");
            CollectionAssert.AreEqual(allChecked, DescribeItems(menu));

            // Clicking a checked column hides it, and the menu shows it unchecked the next time it opens
            FindItem(menu, "[x] Category").DoDefaultAction();
            WaitUntil(() => ProcessMenus().Length == 0, "the column menu to close after the click");
            menu = RightClickHeader(threadList, "Description");
            CollectionAssert.AreEqual(allChecked.Take(5).Concat(new[] { "Category" }).ToArray(), DescribeItems(menu));
        }

        // The tray menu opens when the shell sends the icon's callback message for a right-click.
        // The test sends that same message to the app's tray icon window, because the notification
        // area itself (and its overflow flyout) is not reliably reachable through UI Automation.
        [TestMethod]
        [TestCategory("UI")]
        public void TrayMenuShowsThreadCountsAndQuickLinks() {
            // The tray icon only shows when minimize to tray is on
            File.AppendAllLines(Path.Combine(SettingsDir, "settings.txt"), new[] { "MinimizeToTray=1" });
            _server.Route(ThreadPath, LoopbackResponse.Html(ThreadPage));
            Window window = LaunchApp();
            AutomationElement threadList = AddThread(window, _server.URL(ThreadPath), true);
            WaitUntil(() => RowHasCell(threadList, "Stopped: Download complete"), "the thread to stop after its one-time download");
            IntPtr trayWindow = WaitForTrayIconWindow(_process.Id);

            // The app updates the counts once a second, also while the menu is open
            AutomationElement menu = OpenMenu(() => PostTrayRightClick(trayWindow), "(disabled)     1 stopped", "the tray icon menu to count the stopped thread");

            // The counts are indented under the total on purpose, so the spaces are compared too
            CollectionAssert.AreEqual(new[] {
                "(disabled) Watching 1 thread", "(disabled)     0 running", "(disabled)     0 dead", "(disabled)     1 stopped",
                Separator,
                "Add From Clipboard", "Downloads", "Settings", "About", "Help",
                Separator,
                "Exit"
            }, DescribeItems(menu));
        }

        private static void WaitUntilReady(Window window) {
            Button addButton = FindById(window, "btnAdd").AsButton();
            WaitUntil(() => addButton.IsEnabled, "the Add Thread button to be enabled after the thread list loads");
        }

        private AutomationElement AddThread(Window window, string threadURL, bool oneTime) {
            WaitUntilReady(window);
            FindById(window, "chkOneTime").AsCheckBox().IsChecked = oneTime;
            FindById(window, "txtPageURL").AsTextBox().Text = threadURL;
            FindById(window, "btnAdd").AsButton().Invoke();
            AutomationElement threadList = FindById(window, "lvThreads");
            WaitUntil(() => threadList.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem)).Length == 1, "the added thread to show in the thread list");
            return threadList;
        }

        // A right-click selects the row under the mouse, and the app then shows the thread menu
        private AutomationElement RightClickFirstRow(AutomationElement threadList) {
            return OpenMenu(() => {
                AutomationElement row = threadList.FindFirstChild(cf => cf.ByControlType(ControlType.ListItem));
                Rectangle bounds = row.BoundingRectangle;
                RightClickAt(threadList, new Point(bounds.Left + 20, bounds.Top + bounds.Height / 2));
            }, "Edit", "the thread menu");
        }

        // The app shows the column menu only while the mouse is above the first row
        private AutomationElement RightClickHeader(AutomationElement threadList, string columnName) {
            AutomationElement header = null;
            AutomationElement headerItem = null;
            WaitUntil(() => {
                header = threadList.FindFirstChild(cf => cf.ByControlType(ControlType.Header));
                headerItem = header?.FindFirstChild(cf => cf.ByControlType(ControlType.HeaderItem).And(cf.ByName(columnName)));
                return headerItem != null;
            }, "the " + columnName + " column header");
            return OpenMenu(() => {
                Rectangle bounds = headerItem.BoundingRectangle;
                RightClickAt(header, new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2));
            }, "[x] Added From", "the column menu");
        }

        // Runs rightClick, then waits for a menu with the given item. While none is open, the
        // right-click is sent again every ClickRetryInterval, in case the app was not ready for it.
        private AutomationElement OpenMenu(Action rightClick, string itemDescription, string description) {
            AutomationElement menu = null;
            Stopwatch sinceClick = null;
            WaitUntil(() => {
                menu = FindOpenMenu(itemDescription);
                if (menu != null) return true;
                if (sinceClick == null || sinceClick.Elapsed > ClickRetryInterval) {
                    rightClick();
                    sinceClick = Stopwatch.StartNew();
                }
                return false;
            }, description);
            return menu;
        }

        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_RBUTTONUP = 0x0205;
        private const int MK_RBUTTON = 0x0002;

        private static void RightClickAt(AutomationElement control, Point screenPoint) {
            IntPtr hwnd = control.Properties.NativeWindowHandle.Value;
            Assert.AreNotEqual(IntPtr.Zero, hwnd, control.ControlType + " has no window handle");
            Assert.IsTrue(SetCursorPos(screenPoint.X, screenPoint.Y), "could not move the cursor");
            Assert.IsTrue(GetCursorPos(out NativePoint cursor), "could not read the cursor position");
            Assert.AreEqual(screenPoint, new Point(cursor.X, cursor.Y), "the cursor is not where the test put it (DPI scaling mismatch?)");
            var clientPoint = new NativePoint { X = screenPoint.X, Y = screenPoint.Y };
            Assert.IsTrue(ScreenToClient(hwnd, ref clientPoint), "could not convert the click point");
            IntPtr lParam = (IntPtr)((clientPoint.Y << 16) | (clientPoint.X & 0xFFFF));
            Assert.IsTrue(PostMessage(hwnd, WM_RBUTTONDOWN, (IntPtr)MK_RBUTTON, lParam), "could not post the right-click");
            Assert.IsTrue(PostMessage(hwnd, WM_RBUTTONUP, IntPtr.Zero, lParam), "could not post the right-click");
        }

        // Waits for an open menu that has an item with the given description (see MenuEntry)
        private AutomationElement WaitForMenu(string itemDescription, string description) {
            AutomationElement menu = null;
            WaitUntil(() => (menu = FindOpenMenu(itemDescription)) != null, description);
            return menu;
        }

        private AutomationElement FindOpenMenu(string itemDescription) {
            return ProcessMenus().FirstOrDefault(menu => TryDescribeItems(menu)?.Contains(itemDescription) == true);
        }

        // An open menu is a top-level window, which UI Automation shows as a child of the window
        // that owns it (the main window) or of the desktop. Only menus of the app this test started
        // count, so a ChanThreadWatch the user has open on the same desktop is never touched.
        private AutomationElement[] ProcessMenus() {
            try {
                AutomationElement[] processWindows = _automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(_process.Id));
                AutomationElement[] menus = processWindows.Concat(processWindows.SelectMany(w => w.FindAllChildren())).Where(IsMenu).ToArray();
                // On .NET 10 an open submenu shows under the item it opened from (menu > item > submenu)
                return menus.Concat(menus.SelectMany(m => m.FindAllDescendants(cf => cf.ByControlType(ControlType.Menu)))).ToArray();
            }
            catch (Exception e) when (IsFromClosingMenu(e)) {
                return new AutomationElement[0];
            }
        }

        // A WinForms context menu reports itself as a tool bar
        private static bool IsMenu(AutomationElement element) {
            return element.ControlType == ControlType.Menu || element.ControlType == ControlType.ToolBar;
        }

        // Null while the menu is closing or not fully there
        private static string[] TryDescribeItems(AutomationElement menu) {
            try {
                return menu.Properties.NativeWindowHandle.ValueOrDefault == IntPtr.Zero ? null : DescribeItems(menu);
            }
            catch (Exception e) when (IsFromClosingMenu(e)) {
                return null;
            }
        }

        // What UI Automation and Active Accessibility throw for a menu window that is going away
        private static bool IsFromClosingMenu(Exception e) {
            return e is COMException || e is ArgumentException || e is InvalidCastException
                || e is PropertyNotSupportedException || e is ElementNotAvailableException;
        }

        private static MenuEntry FindItem(AutomationElement menu, string itemDescription) {
            MenuEntry item = ReadMenu(menu).FirstOrDefault(entry => entry.Description == itemDescription);
            Assert.IsNotNull(item, "menu item " + itemDescription + " in " + string.Join(", ", DescribeItems(menu)));
            return item;
        }

        // One entry per shown item, in order (see MenuEntry.Description)
        private static string[] DescribeItems(AutomationElement menu) {
            return ReadMenu(menu).Select(entry => entry.Description).ToArray();
        }

        private const int OBJID_CLIENT = -4;
        private const int CHILDID_SELF = 0;
        private const int ROLE_SYSTEM_SEPARATOR = 0x15;
        private const int STATE_SYSTEM_UNAVAILABLE = 0x1;
        private const int STATE_SYSTEM_CHECKED = 0x10;
        private const int STATE_SYSTEM_INVISIBLE = 0x8000;
        private static readonly Guid IID_IAccessible = typeof(IAccessible).GUID;

        // The shown items of an open menu
        private static MenuEntry[] ReadMenu(AutomationElement menu) {
            Guid iid = IID_IAccessible;
            Marshal.ThrowExceptionForHR(AccessibleObjectFromWindow(menu.Properties.NativeWindowHandle.Value, OBJID_CLIENT, ref iid, out object menuObject));
            var menuAccessible = (IAccessible)menuObject;
            var children = new object[menuAccessible.get_accChildCount()];
            Marshal.ThrowExceptionForHR(AccessibleChildren(menuAccessible, 0, children.Length, children, out int obtained));
            return children.Take(obtained).Select(child => new MenuEntry(menuAccessible, child)).Where(entry => !entry.IsHidden).ToArray();
        }

        // A menu item as Active Accessibility reports it. A child is either an object of its own
        // or a child id of its parent.
        private sealed class MenuEntry {
            private readonly IAccessible _accessible;
            private readonly object _childId;

            public MenuEntry(IAccessible parent, object child) {
                _accessible = child as IAccessible ?? parent;
                _childId = child is IAccessible ? CHILDID_SELF : child;
            }

            private int State => Convert.ToInt32(_accessible.get_accState(_childId));

            public bool IsHidden => (State & STATE_SYSTEM_INVISIBLE) != 0;

            // The exact text, "---" for a separator, and "(disabled)" or "[x]" before a disabled
            // or checked item
            public string Description {
                get {
                    if (Convert.ToInt32(_accessible.get_accRole(_childId)) == ROLE_SYSTEM_SEPARATOR) return Separator;
                    int state = State;
                    string disabled = (state & STATE_SYSTEM_UNAVAILABLE) != 0 ? "(disabled) " : "";
                    string checkMark = (state & STATE_SYSTEM_CHECKED) != 0 ? "[x] " : "";
                    return disabled + checkMark + _accessible.get_accName(_childId);
                }
            }

            public void DoDefaultAction() {
                _accessible.accDoDefaultAction(_childId);
            }
        }

        // The Active Accessibility interface, declared here so the tests need no reference to the
        // Accessibility assembly. The methods must stay in vtable order, so the ones before
        // accDoDefaultAction are declared even where the tests do not call them.
        [ComImport]
        [Guid("618736E0-3C3D-11CF-810C-00AA00389B71")]
        [InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface IAccessible {
            [return: MarshalAs(UnmanagedType.IDispatch)]
            object get_accParent();
            int get_accChildCount();
            [return: MarshalAs(UnmanagedType.IDispatch)]
            object get_accChild(object childId);
            string get_accName(object childId);
            string get_accValue(object childId);
            string get_accDescription(object childId);
            object get_accRole(object childId);
            object get_accState(object childId);
            string get_accHelp(object childId);
            int get_accHelpTopic(out string helpFile, object childId);
            string get_accKeyboardShortcut(object childId);
            object get_accFocus();
            object get_accSelection();
            string get_accDefaultAction(object childId);
            void accSelect(int flags, object childId);
            void accLocation(out int left, out int top, out int width, out int height, object childId);
            object accNavigate(int direction, object start);
            object accHitTest(int left, int top);
            void accDoDefaultAction(object childId);
        }

        private static bool RowHasCellStartingWith(AutomationElement list, string prefix) {
            return list.FindAllChildren().Any(row => row.FindAllChildren().Any(cell => cell.Name.StartsWith(prefix, StringComparison.Ordinal)));
        }

        // WinForms gives the tray icon a hidden top-level window that receives the shell's mouse
        // callbacks as WM_USER + 1024, with the mouse message in lParam
        private const int WM_TRAYMOUSEMESSAGE = 0x0400 + 1024;

        private static void PostTrayRightClick(IntPtr trayWindow) {
            Assert.IsTrue(PostMessage(trayWindow, WM_TRAYMOUSEMESSAGE, IntPtr.Zero, (IntPtr)WM_RBUTTONUP), "could not post the tray right-click");
        }

        private static IntPtr WaitForTrayIconWindow(int processId) {
            List<IntPtr> trayWindows = null;
            WaitUntil(() => (trayWindows = FindTrayIconWindows(processId)).Count == 1, "exactly one hidden tray icon window of the app");
            return trayWindows[0];
        }

        // Hidden, untitled WinForms windows of the process. Timers and the parking window are
        // message-only windows, so EnumWindows does not list them.
        private static List<IntPtr> FindTrayIconWindows(int processId) {
            var windows = new List<IntPtr>();
            EnumWindows((hwnd, lParam) => {
                GetWindowThreadProcessId(hwnd, out uint windowProcessId);
                if (windowProcessId == processId && !IsWindowVisible(hwnd) && GetWindowTextLength(hwnd) == 0 && GetClassName(hwnd).StartsWith("WindowsForms10.Window.0.", StringComparison.Ordinal)) {
                    windows.Add(hwnd);
                }
                return true;
            }, IntPtr.Zero);
            return windows;
        }

        private static string GetClassName(IntPtr hwnd) {
            var className = new StringBuilder(256);
            GetClassName(hwnd, className, className.Capacity);
            return className.ToString();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint {
            public int X;
            public int Y;
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NativePoint point);

        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hwnd, ref NativePoint point);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("oleacc.dll")]
        private static extern int AccessibleObjectFromWindow(IntPtr hwnd, int objectId, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object accessible);

        [DllImport("oleacc.dll")]
        private static extern int AccessibleChildren([MarshalAs(UnmanagedType.Interface)] IAccessible container, int childStart, int childCount, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] object[] children, out int obtained);
    }
}
