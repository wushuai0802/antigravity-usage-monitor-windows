using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;

internal static class Native
{
    private sealed class KnownProcess { internal long Started; internal bool Desktop; }
    private static readonly Dictionary<int, KnownProcess> Processes = new Dictionary<int, KnownProcess>();

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
        public bool Same(RECT other) { return Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    internal delegate bool EnumProc(IntPtr window, IntPtr state);

    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback, IntPtr state);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] internal static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern IntPtr SetLong(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern IntPtr GetLong(IntPtr window, int index);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] internal static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("user32.dll")] internal static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);

    internal static double IdleSeconds()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
        if (!GetLastInputInfo(ref lii)) return 0.0;
        uint elapsedMs = unchecked((uint)Environment.TickCount - lii.dwTime);
        return elapsedMs / 1000.0;
    }

    internal static bool Live(IntPtr w) { return w != IntPtr.Zero && IsWindow(w) && IsWindowVisible(w) && !IsIconic(w); }
    internal static bool IsForeground(IntPtr w) { return Live(w) && GetAncestor(GetForegroundWindow(), 2) == w; }
    internal static bool AnyMouseButtonDown()
    {
        return (GetAsyncKeyState(0x01) & 0x8000) != 0
            || (GetAsyncKeyState(0x02) & 0x8000) != 0
            || (GetAsyncKeyState(0x04) & 0x8000) != 0;
    }
    internal static bool IsPointOwnedBy(IntPtr owner, double screenX, double screenY)
    {
        if (owner == IntPtr.Zero) return false;
        IntPtr hit = WindowFromPoint(new POINT((int)Math.Round(screenX), (int)Math.Round(screenY)));
        return hit != IntPtr.Zero && GetAncestor(hit, 2) == owner;
    }
    internal static double Scale(IntPtr w) { try { return Math.Max(1.0, GetDpiForWindow(w) / 96.0); } catch (EntryPointNotFoundException) { return 1.0; } }

    internal static string GetTitle(IntPtr w)
    {
        if (w == IntPtr.Zero) return "";
        var sb = new StringBuilder(512);
        GetWindowText(w, sb, sb.Capacity);
        return sb.ToString();
    }

    internal static HashSet<int> DesktopPids()
    {
        var ids = new HashSet<int>();
        var seen = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName("Antigravity")) using (p)
        {
            try
            {
                seen.Add(p.Id);
                long started = p.StartTime.ToUniversalTime().Ticks;
                KnownProcess known;
                if (!Processes.TryGetValue(p.Id, out known) || known.Started != started)
                {
                    known = new KnownProcess { Started = started, Desktop = true };
                    Processes[p.Id] = known;
                }
                if (known.Desktop) ids.Add(p.Id);
            }
            catch { }
        }
        foreach (var key in Processes.Keys.Where(k => !seen.Contains(k)).ToArray()) Processes.Remove(key);
        return ids;
    }

    internal static string DesktopIdentity(HashSet<int> ids)
    {
        int first = ids.OrderBy(id => Processes[id].Started).FirstOrDefault();
        return first == 0 ? "" : first + ":" + Processes[first].Started;
    }

    internal static IntPtr ChooseWindow(HashSet<int> pids, IntPtr last)
    {
        var windows = new List<IntPtr>();
        EnumWindows((w, unused) =>
        {
            uint pid;
            GetWindowThreadProcessId(w, out pid);
            RECT r;
            if (pids.Contains((int)pid) && Live(w) && GetWindowRect(w, out r) && r.Width >= 420 && r.Height >= 300) windows.Add(w);
            return true;
        }, IntPtr.Zero);
        var foreground = GetAncestor(GetForegroundWindow(), 2);
        if (windows.Contains(foreground)) return foreground;
        if (windows.Contains(last)) return last;
        return windows.OrderByDescending(w => { RECT r; GetWindowRect(w, out r); return (long)r.Width * r.Height; }).FirstOrDefault();
    }

    internal static bool IsLightPixel(IntPtr owner, int x, int y, bool fallback)
    {
        if (GetAncestor(WindowFromPoint(new POINT(x, y)), 2) != owner) return fallback;
        var dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return fallback;
        try
        {
            var c = GetPixel(dc, x, y);
            if (c == 0xffffffff) return fallback;
            return 0.299 * (c & 255) + 0.587 * ((c >> 8) & 255) + 0.114 * ((c >> 16) & 255) > 128.0;
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }
}

internal sealed class Anchor
{
    internal IntPtr Window;
    internal string WindowTitle;
    internal Native.RECT Frame;
    internal Rect ModelButton;
    internal double FreeLeft;
    internal double FreeRight;
    internal double Top;
    internal double Height;
    internal double Scale;
    internal bool MenuOpen;
    internal bool? DetectedLightTheme;
    internal string UiModelName;
    internal DateTime At;
}

internal static class OverlayLayout
{
    internal const double ControlGapDip = 6.0;

    internal static Rect FreeRangeAfterModel(Rect rowBounds, Rect modelButton, IEnumerable<Rect> rowControls, double scale)
    {
        if (rowBounds.IsEmpty || modelButton.IsEmpty || scale <= 0) return Rect.Empty;
        double start = modelButton.Right;
        double end = rowBounds.Right;
        if (end <= start) return Rect.Empty;

        double center = modelButton.Top + modelButton.Height / 2.0;
        double gap = ControlGapDip * scale;
        var occupied = (rowControls ?? Enumerable.Empty<Rect>())
            .Where(r => !r.IsEmpty && r.Width > 0 && r.Height > 0
                && r.Top <= center + 4 * scale && r.Bottom >= center - 4 * scale
                && r.Right > start && r.Left < end)
            .OrderBy(r => r.Left)
            .ToArray();

        double cursor = start;
        Rect best = Rect.Empty;
        Action<double, double> consider = (left, right) =>
        {
            double l = Math.Ceiling(left + gap);
            double r = Math.Floor(right - gap);
            if (r > l && (best.IsEmpty || (r - l) > best.Width))
            {
                best = new Rect(l, modelButton.Top, r - l, modelButton.Height);
            }
        };

        foreach (var occ in occupied)
        {
            double l = Math.Max(start, occ.Left);
            double r = Math.Min(end, occ.Right);
            if (l > cursor) consider(cursor, l);
            cursor = Math.Max(cursor, r);
        }
        if (cursor < end) consider(cursor, end);
        return best;
    }

    internal static Rect PlaceInRange(double left, double right, double top, double widthDip, double heightDip, double scale)
    {
        double w = Math.Ceiling(widthDip * scale);
        double h = Math.Ceiling(heightDip * scale);
        if (w <= 0 || h <= 0 || Math.Floor(right) - Math.Ceiling(left) < w) return Rect.Empty;
        return new Rect(Math.Floor(right) - w, Math.Round(top), w, h);
    }
}

internal sealed class AnchorFinder
{
    private static readonly Regex ModelRegex = new Regex(@"(Select\s+model|模型|Gemini|Claude|Opus|Sonnet|Neon|Argon|Carbon|Hydrogen|Flash|GPT-OSS|Orchestrator)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CurrentModelExtract = new Regex(@"(?:current|当前)\s*[:：]\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private AutomationElement _root;
    private AutomationElement _webDoc;
    private AutomationElement _modelButton;
    private AutomationElement _composerInput;
    private readonly List<AutomationElement> _rowControls = new List<AutomationElement>();
    private IntPtr _window;
    private DateTime _nextSearch = DateTime.MinValue;
    private DateTime _nextQuotaScan = DateTime.MinValue;
    private bool? _cachedLightTheme;
    internal string Stage = "waiting";

    internal void Invalidate() { _nextSearch = DateTime.MinValue; }

    internal Anchor Find(IntPtr window, string stateDir)
    {
        if (!Native.Live(window)) { _modelButton = null; Stage = "host-hidden"; return null; }
        try
        {
            if (window != _window || _modelButton == null || DateTime.UtcNow >= _nextSearch)
            {
                _window = window;
                Search(window, stateDir);
            }
            if (_webDoc != null && Native.IsForeground(window)) TryOpportunisticQuotaScan(_webDoc, window, stateDir, true);
            if (_modelButton == null) { Stage = "model-not-found"; return null; }
            if (_modelButton.Current.IsOffscreen) { Invalidate(); Stage = "offscreen"; return null; }

            Rect modelRect = _modelButton.Current.BoundingRectangle;
            Native.RECT frame;
            Native.GetWindowRect(window, out frame);
            if (modelRect.IsEmpty || modelRect.Width <= 0 || modelRect.Left < frame.Left || modelRect.Right > frame.Right || modelRect.Top < frame.Top || modelRect.Bottom > frame.Bottom)
            {
                Invalidate();
                return null;
            }

            bool menuOpen = _rowControls.Any(IsExpanded);
            if (!menuOpen && _root != null)
            {
                var menu = _root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu));
                if (menu != null && !menu.Current.IsOffscreen && !menu.Current.BoundingRectangle.IsEmpty) menuOpen = true;
            }

            double scale = Native.Scale(window);
            Rect inputRect = _composerInput != null && !_composerInput.Current.IsOffscreen ? _composerInput.Current.BoundingRectangle : Rect.Empty;
            double rowLeft = !inputRect.IsEmpty ? inputRect.Left : modelRect.Left;
            double rowRight = !inputRect.IsEmpty ? inputRect.Right : frame.Right - 24 * scale;
            Rect rowBounds = new Rect(rowLeft, modelRect.Top, Math.Max(0, rowRight - rowLeft), modelRect.Height);

            var occupied = new List<Rect>();
            foreach (var c in _rowControls)
            {
                try
                {
                    if (!c.Current.IsOffscreen)
                    {
                        var r = c.Current.BoundingRectangle;
                        if (!r.IsEmpty && r != modelRect) occupied.Add(r);
                    }
                }
                catch { }
            }

            Rect free = OverlayLayout.FreeRangeAfterModel(rowBounds, modelRect, occupied, scale);
            string uiModel = ExtractModelName(_modelButton.Current.Name);

            Stage = menuOpen ? "menu-open" : (free.IsEmpty ? "no-room" : "located");
            return new Anchor
            {
                Window = window,
                WindowTitle = Native.GetTitle(window),
                Frame = frame,
                ModelButton = modelRect,
                FreeLeft = free.IsEmpty ? modelRect.Right : free.Left,
                FreeRight = free.IsEmpty ? modelRect.Right : free.Right,
                Top = modelRect.Top,
                Height = modelRect.Height,
                Scale = scale,
                MenuOpen = menuOpen,
                DetectedLightTheme = _cachedLightTheme,
                UiModelName = uiModel,
                At = DateTime.UtcNow
            };
        }
        catch (ElementNotAvailableException) { _modelButton = null; Invalidate(); Stage = "element-rebuilt"; return null; }
        catch (InvalidOperationException) { _modelButton = null; Invalidate(); Stage = "uia-unavailable"; return null; }
    }

    private void Search(IntPtr window, string stateDir)
    {
        _nextSearch = DateTime.UtcNow.AddSeconds(4);
        _modelButton = null;
        _composerInput = null;
        _rowControls.Clear();

        _root = AutomationElement.FromHandle(window);
        _webDoc = null;
        if (_root == null) return;

        // Always scope UIA searches to Electron's main RootWebArea Document, isolating our own owned WPF overlay window.
        var docs = FindWebDocuments(_root);
        _webDoc = docs.Count > 0 ? docs[docs.Count - 1] : _root;

        Native.RECT frame;
        Native.GetWindowRect(window, out frame);
        double scale = Native.Scale(window);

        // Detect theme from Antigravity root group ClassName (e.g., "theme-standalone dark dark-theme theme-dark")
        _cachedLightTheme = DetectTheme(_webDoc);

        // Locate composer input ("Message input" ComboBox or Edit near lower half)
        var inputCond = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        var inputs = _webDoc.FindAll(TreeScope.Descendants, inputCond);
        var inputBoxes = new List<KeyValuePair<AutomationElement, Rect>>();
        foreach (AutomationElement el in inputs)
        {
            try
            {
                if (el.Current.IsOffscreen) continue;
                Rect r = el.Current.BoundingRectangle;
                if (r.IsEmpty || r.Width < 200 * scale || r.Top < frame.Top + frame.Height * 0.4) continue;
                inputBoxes.Add(new KeyValuePair<AutomationElement, Rect>(el, r));
            }
            catch { }
        }

        // Locate model picker button directly below the composer input
        var buttons = _webDoc.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
        double bestBottom = double.MinValue;
        AutomationElement bestModel = null;
        Rect bestInputRect = Rect.Empty;
        AutomationElement bestInputEl = null;

        foreach (AutomationElement b in buttons)
        {
            try
            {
                if (b.Current.IsOffscreen) continue;
                string name = b.Current.Name ?? "";
                if (!ModelRegex.IsMatch(name)) continue;
                Rect r = b.Current.BoundingRectangle;
                if (r.IsEmpty || r.Width < 40 * scale || r.Height > 48 * scale) continue;

                foreach (var pair in inputBoxes)
                {
                    Rect ir = pair.Value;
                    if (r.Top >= ir.Bottom - 16 * scale && r.Top <= ir.Bottom + 90 * scale
                        && r.Left >= ir.Left - 12 * scale && r.Right <= ir.Right + 24 * scale)
                    {
                        if (r.Bottom > bestBottom)
                        {
                            bestBottom = r.Bottom;
                            bestModel = b;
                            bestInputRect = ir;
                            bestInputEl = pair.Key;
                        }
                    }
                }
            }
            catch { }
        }

        if (bestModel == null)
        {
            _nextSearch = DateTime.UtcNow.AddSeconds(1);
            if (Native.IsForeground(window)) TryOpportunisticQuotaScan(_webDoc, window, stateDir, true);
            return;
        }

        _modelButton = bestModel;
        _composerInput = bestInputEl;
        Rect mb = bestModel.Current.BoundingRectangle;
        double center = mb.Top + mb.Height / 2.0;

        foreach (AutomationElement b in buttons)
        {
            try
            {
                if (b.Current.IsOffscreen) continue;
                Rect r = b.Current.BoundingRectangle;
                if (r.IsEmpty || r.Top > center + 4 * scale || r.Bottom < center - 4 * scale) continue;
                if (!bestInputRect.IsEmpty && (r.Right < bestInputRect.Left - 20 * scale || r.Left > bestInputRect.Right + 20 * scale)) continue;
                _rowControls.Add(b);
            }
            catch { }
        }

        if (Native.IsForeground(window)) TryOpportunisticQuotaScan(_webDoc, window, stateDir, false);
    }

    private static readonly System.Windows.Automation.Condition UsageMenuItemCondition = new AndCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
        new OrCondition(
            new PropertyCondition(AutomationElement.NameProperty, "查看用量"),
            new PropertyCondition(AutomationElement.NameProperty, "View usage"),
            new PropertyCondition(AutomationElement.NameProperty, "Usage")));
    private static readonly System.Windows.Automation.Condition UsageMenuCondition = new AndCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu),
        new OrCondition(
            new PropertyCondition(AutomationElement.NameProperty, "查看用量"),
            new PropertyCondition(AutomationElement.NameProperty, "使用量"),
            new PropertyCondition(AutomationElement.NameProperty, "View usage"),
            new PropertyCondition(AutomationElement.NameProperty, "Usage")));

    // Zero-cursor-move UIA quota sync: Expand model picker -> post WM_MOUSEMOVE over "查看用量" -> read RawView -> Collapse.
    // When requireForeground is true (automatic background timer), never run unless Antigravity is the active
    // foreground window, unobstructed at the model button, with no mouse buttons pressed; abort immediately on user activity.
    internal bool TriggerQuotaRefresh(IntPtr window, string stateDir, bool requireForeground)
    {
        if (!Native.Live(window) || string.IsNullOrEmpty(stateDir)) return false;
        if (requireForeground && (!Native.IsForeground(window) || Native.AnyMouseButtonDown())) return false;
        try
        {
            if (window != _window || _modelButton == null || _webDoc == null)
            {
                _window = window;
                Search(window, stateDir);
            }
            if (_modelButton == null || _webDoc == null || _modelButton.Current.IsOffscreen) return false;
            if (requireForeground)
            {
                if (!Native.IsForeground(window) || Native.AnyMouseButtonDown() || Native.IdleSeconds() < 45.0) return false;
                Rect mb = _modelButton.Current.BoundingRectangle;
                if (mb.IsEmpty || mb.Width <= 0 || mb.Height <= 0) return false;
                if (!Native.IsPointOwnedBy(window, mb.Left + mb.Width / 2.0, mb.Top + mb.Height / 2.0)) return false;
            }

            object patObj;
            if (!_modelButton.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out patObj)) return false;
            var ep = (ExpandCollapsePattern)patObj;
            if (ep.Current.ExpandCollapseState == ExpandCollapseState.Expanded) return false; // User already has menu open

            ep.Expand();
            try
            {
                System.Threading.Thread.Sleep(160);
                if (requireForeground && (!Native.IsForeground(window) || Native.AnyMouseButtonDown() || Native.IdleSeconds() < 1.5)) return false;

                var usageItem = _webDoc.FindFirst(TreeScope.Descendants, UsageMenuItemCondition);
                if (usageItem == null) return false;

                Rect r = usageItem.Current.BoundingRectangle;
                if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) return false;

                // Post WM_MOUSEMOVE directly to the Electron window without moving the user's actual mouse cursor.
                IntPtr renderHwnd = Native.FindWindowEx(window, IntPtr.Zero, "Chrome_RenderWidgetHostHWND", null);
                IntPtr targetHwnd = renderHwnd != IntPtr.Zero ? renderHwnd : window;
                var pt = new Native.POINT((int)(r.X + r.Width / 2.0), (int)(r.Y + r.Height / 2.0));
                Native.ScreenToClient(targetHwnd, ref pt);
                IntPtr lparam = new IntPtr(((pt.Y & 0xFFFF) << 16) | (pt.X & 0xFFFF));
                Native.PostMessage(targetHwnd, 0x0200, IntPtr.Zero, lparam);

                // Wait for submenu mount and fresh values (finish early as soon as "正在刷新" clears).
                List<string> bestLines = null;
                for (int t = 0; t < 15; t++)
                {
                    System.Threading.Thread.Sleep(120);
                    if (requireForeground && (!Native.IsForeground(window) || Native.AnyMouseButtonDown() || Native.IdleSeconds() < 1.5))
                    {
                        break;
                    }
                    var usageMenu = _webDoc.FindFirst(TreeScope.Descendants, UsageMenuCondition);
                    if (usageMenu == null) continue;

                    var lines = new List<string>();
                    CollectLinesFaultTolerant(usageMenu, lines, 120, true);
                    PoolQuotaState g, c;
                    if (UiaQuotaParser.TryParseFromLines(lines, DateTime.UtcNow, out g, out c))
                    {
                        bestLines = lines;
                        bool stillRefreshing = lines.Any(l => l.Contains("正在刷新") || l.IndexOf("refreshing", StringComparison.OrdinalIgnoreCase) >= 0);
                        if (!stillRefreshing && t >= 2) break;
                    }
                }
                if (bestLines != null)
                {
                    return TryCommitQuotaLines(bestLines, stateDir);
                }
            }
            finally
            {
                try { ep.Collapse(); } catch { }
            }
        }
        catch (Exception ex) { Program.Note("quota-auto-refresh-err: " + ex.GetType().Name); }
        return false;
    }
    private static readonly System.Windows.Automation.Condition ModalAnchorCondition = new OrCondition(
        new PropertyCondition(AutomationElement.NameProperty, "剩余五小时限制"),
        new PropertyCondition(AutomationElement.NameProperty, "剩余每周限制"),
        new PropertyCondition(AutomationElement.NameProperty, "模型额度"),
        new PropertyCondition(AutomationElement.NameProperty, "Model quotas"));
    private string _lastSavedQuotaSummary = "";

    private void TryOpportunisticQuotaScan(AutomationElement webDoc, IntPtr window, string stateDir, bool fastMode)
    {
        if (webDoc == null || string.IsNullOrEmpty(stateDir) || DateTime.UtcNow < _nextQuotaScan) return;
        _nextQuotaScan = DateTime.UtcNow.AddMilliseconds(fastMode ? 350 : 800);
        try
        {
            // Fast path 1 (verified via FromPoint): model-menu "查看用量" submenu mounts as ControlType.Menu
            // whose children are flagged IsOffscreen=True by Chromium due to portal wrapper geometry.
            // Scanning its ~20 raw nodes takes < 5ms and never touches chat content.
            var usageMenu = webDoc.FindFirst(TreeScope.Descendants, UsageMenuCondition);
            if (usageMenu != null)
            {
                var lines = new List<string>();
                CollectLinesFaultTolerant(usageMenu, lines, 120, true);
                if (TryCommitQuotaLines(lines, stateDir)) return;
            }

            // Also scan any open ControlType.Menu under composer box (covers localized labels).
            var composerBox = webDoc.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "antigravity.agentSidePanelInputBox"));
            if (composerBox != null)
            {
                var menus = composerBox.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu));
                for (int i = menus.Count - 1; i >= 0; i--)
                {
                    var lines = new List<string>();
                    CollectLinesFaultTolerant(menus[i], lines, 200, true);
                    if (TryCommitQuotaLines(lines, stateDir)) return;
                }
            }

            // Fast path 2: settings "模型额度" modal (climb from visible modal anchor smallest-first).
            Native.RECT frame;
            if (!Native.GetWindowRect(window, out frame)) return;
            double scale = Native.Scale(window);
            var anchors = webDoc.FindAll(TreeScope.Descendants, ModalAnchorCondition);
            if (anchors == null || anchors.Count == 0) return;

            var walker = TreeWalker.ControlViewWalker;
            for (int i = anchors.Count - 1; i >= 0; i--)
            {
                AutomationElement el = anchors[i];
                try { if (el.Current.IsOffscreen) continue; } catch { continue; }
                AutomationElement cur = el;
                int tried = 0;
                for (int d = 0; d < 10 && cur != null && tried < 3; d++)
                {
                    if (cur == webDoc) break;
                    try
                    {
                        string aid = cur.Current.AutomationId ?? "";
                        if (string.Equals(aid, "root", StringComparison.OrdinalIgnoreCase)) break;
                        Rect r = cur.Current.BoundingRectangle;
                        if (!r.IsEmpty && r.Width >= 240 * scale && r.Height >= 180 * scale
                            && r.Width <= frame.Width * 0.92 && r.Height <= frame.Height * 0.92)
                        {
                            tried++;
                            var lines = new List<string>();
                            CollectLinesFaultTolerant(cur, lines, 360, true);
                            if (TryCommitQuotaLines(lines, stateDir)) return;
                        }
                    }
                    catch { break; }
                    try { cur = walker.GetParent(cur); } catch { break; }
                }
            }
        }
        catch { }
    }

    private static List<AutomationElement> FindWebDocuments(AutomationElement root)
    {
        var docs = new List<AutomationElement>();
        var walker = TreeWalker.ControlViewWalker;
        try
        {
            AutomationElement child = walker.GetFirstChild(root);
            int guard = 0;
            while (child != null && guard++ < 16)
            {
                try
                {
                    // Skip owned WPF overlay windows (ControlType.Window); descend only into Electron Chrome Pane/RootView.
                    if (child.Current.ControlType != ControlType.Window)
                    {
                        var found = child.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
                        for (int i = 0; i < found.Count; i++) docs.Add(found[i]);
                    }
                }
                catch { }
                try { child = walker.GetNextSibling(child); } catch { break; }
            }
        }
        catch { }
        return docs;
    }

    private bool TryCommitQuotaLines(List<string> lines, string stateDir)
    {
        PoolQuotaState g, c;
        if (!UiaQuotaParser.TryParseFromLines(lines, DateTime.UtcNow, out g, out c)) return false;
        DualSlotStore.WriteQuotaFromUia(stateDir, g, c, DateTime.UtcNow);
        string summary = string.Format("G={0} C={1}",
            g == null ? "null" : (g.RemainingPct + "%(5h=" + (g.FiveHour != null ? g.FiveHour.RemainingPct.ToString() : "-") + ",7d=" + (g.Weekly != null ? g.Weekly.RemainingPct.ToString() : "-") + ")"),
            c == null ? "null" : (c.RemainingPct + "%(5h=" + (c.FiveHour != null ? c.FiveHour.RemainingPct.ToString() : "-") + ",7d=" + (c.Weekly != null ? c.Weekly.RemainingPct.ToString() : "-") + ")"));
        if (summary != _lastSavedQuotaSummary)
        {
            _lastSavedQuotaSummary = summary;
            Program.Note("quota-uia-saved: " + summary);
        }
        return true;
    }

    private static void CollectLinesFaultTolerant(AutomationElement root, List<string> output, int maxNodes, bool useRawWalker = false)
    {
        var walker = useRawWalker ? TreeWalker.RawViewWalker : TreeWalker.ControlViewWalker;
        var stack = new Stack<AutomationElement>();
        stack.Push(root);
        int visited = 0;
        while (stack.Count > 0 && visited < maxNodes)
        {
            AutomationElement cur = stack.Pop();
            visited++;
            try
            {
                string n = cur.Current.Name;
                if (!string.IsNullOrWhiteSpace(n) && n.Length <= 80)
                {
                    foreach (string part in n.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string t = part.Trim();
                        if (t.Length > 0 && t.Length <= 48) output.Add(t);
                    }
                }
            }
            catch { }

            // Push children in reverse so DFS visits them in exact top-to-bottom visual DOM order.
            var children = new List<AutomationElement>();
            try
            {
                AutomationElement child = walker.GetFirstChild(cur);
                int guard = 0;
                while (child != null && guard++ < 80)
                {
                    children.Add(child);
                    try { child = walker.GetNextSibling(child); } catch { break; }
                }
            }
            catch { }
            for (int i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
        }
    }

    private static AutomationElement FindQuotaDialogScope(AutomationElement root)
    {
        try
        {
            var cond = new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group));
            var all = root.FindAll(TreeScope.Descendants, cond);
            for (int i = all.Count - 1; i >= 0; i--)
            {
                try
                {
                    var el = all[i];
                    if (el.Current.IsOffscreen) continue;
                    string localized = (el.Current.LocalizedControlType ?? "").ToLowerInvariant();
                    string cls = (el.Current.ClassName ?? "").ToLowerInvariant();
                    if (localized.Contains("dialog") || localized.Contains("对话框") || cls.Contains("dialog") || cls.Contains("modal"))
                    {
                        return el;
                    }
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private static bool? DetectTheme(AutomationElement root)
    {
        try
        {
            var groups = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group));
            int maxCheck = Math.Min(groups.Count, 12);
            for (int i = 0; i < maxCheck; i++)
            {
                string cls = (groups[i].Current.ClassName ?? "").ToLowerInvariant();
                if (cls.Contains("theme-dark") || cls.Contains("dark-theme")) return false;
                if (cls.Contains("theme-light") || cls.Contains("light-theme")) return true;
            }
        }
        catch { }
        return null;
    }

    private static bool IsExpanded(AutomationElement e)
    {
        try
        {
            object p;
            return e.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out p)
                && ((ExpandCollapsePattern)p).Current.ExpandCollapseState == ExpandCollapseState.Expanded;
        }
        catch { return false; }
    }

    internal static string ExtractModelName(string buttonName)
    {
        if (string.IsNullOrWhiteSpace(buttonName)) return "";
        Match m = CurrentModelExtract.Match(buttonName);
        return m.Success ? m.Groups[1].Value.Trim() : buttonName.Trim();
    }
}
