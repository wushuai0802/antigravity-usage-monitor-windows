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

    internal static bool Live(IntPtr w) { return w != IntPtr.Zero && IsWindow(w) && IsWindowVisible(w) && !IsIconic(w); }
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
            if (_webDoc != null) TryOpportunisticQuotaScan(_webDoc, window, stateDir, true);
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
            TryOpportunisticQuotaScan(_webDoc, window, stateDir, true);
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

        TryOpportunisticQuotaScan(_webDoc, window, stateDir, false);
    }

    private static readonly System.Windows.Automation.Condition QuotaAnchorCondition = new OrCondition(
        new PropertyCondition(AutomationElement.NameProperty, "5 小时限额"),
        new PropertyCondition(AutomationElement.NameProperty, "剩余五小时限制"),
        new PropertyCondition(AutomationElement.NameProperty, "周限额"),
        new PropertyCondition(AutomationElement.NameProperty, "剩余每周限制"),
        new PropertyCondition(AutomationElement.NameProperty, "5-hour limit"),
        new PropertyCondition(AutomationElement.NameProperty, "Weekly limit"));
    private string _lastSavedQuotaSummary = "";

    private void TryOpportunisticQuotaScan(AutomationElement webDoc, IntPtr window, string stateDir, bool fastMode)
    {
        if (webDoc == null || string.IsNullOrEmpty(stateDir) || DateTime.UtcNow < _nextQuotaScan) return;
        _nextQuotaScan = DateTime.UtcNow.AddMilliseconds(fastMode ? 450 : 900);
        try
        {
            Native.RECT frame;
            if (!Native.GetWindowRect(window, out frame)) return;
            double scale = Native.Scale(window);

            // Single fast native UIA query inside Electron Document only (never touches our owned WPF overlay window).
            var anchors = webDoc.FindAll(TreeScope.Descendants, QuotaAnchorCondition);
            if (anchors == null || anchors.Count == 0) return;

            var visibleAnchors = new List<AutomationElement>();
            for (int i = anchors.Count - 1; i >= 0; i--)
            {
                try
                {
                    var el = anchors[i];
                    if (!el.Current.IsOffscreen) visibleAnchors.Add(el);
                }
                catch { }
                if (visibleAnchors.Count >= 4) break;
            }
            if (visibleAnchors.Count == 0) return;

            // Climb via geometry ONLY (zero nested FindAll calls!) and keep candidates SMALLEST-FIRST
            // so the tight popover/modal card is scanned before any outer chat wrapper!
            var walker = TreeWalker.ControlViewWalker;
            var candidates = new List<AutomationElement>();
            foreach (var anchor in visibleAnchors)
            {
                AutomationElement cur = anchor;
                int addedForAnchor = 0;
                for (int d = 0; d < 12 && cur != null && addedForAnchor < 4; d++)
                {
                    if (cur == webDoc) break;
                    try
                    {
                        string aid = cur.Current.AutomationId ?? "";
                        if (string.Equals(aid, "root", StringComparison.OrdinalIgnoreCase)) break;
                        Rect r = cur.Current.BoundingRectangle;
                        if (!r.IsEmpty && r.Width >= 220 * scale && r.Height >= 150 * scale
                            && r.Width <= frame.Width * 0.92 && r.Height <= frame.Height * 0.92)
                        {
                            if (!candidates.Contains(cur))
                            {
                                candidates.Add(cur);
                                addedForAnchor++;
                            }
                        }
                    }
                    catch { break; }
                    try { cur = walker.GetParent(cur); } catch { break; }
                }
            }

            PoolQuotaState bestG = null, bestC = null;
            int bestScore = 0;
            foreach (var card in candidates)
            {
                var lines = new List<string>();
                CollectLinesFaultTolerant(card, lines, 450);
                if (lines.Count == 0) continue;
                PoolQuotaState g, c;
                if (UiaQuotaParser.TryParseFromLines(lines, DateTime.UtcNow, out g, out c))
                {
                    int score = (g != null ? (g.FiveHour != null && g.Weekly != null ? 2 : 1) : 0)
                              + (c != null ? (c.FiveHour != null && c.Weekly != null ? 2 : 1) : 0);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestG = g;
                        bestC = c;
                    }
                    if (bestScore >= 4) break;
                }
            }

            if (bestG != null || bestC != null)
            {
                DualSlotStore.WriteQuotaFromUia(stateDir, bestG, bestC, DateTime.UtcNow);
                string summary = string.Format("G={0} C={1}",
                    bestG == null ? "null" : (bestG.RemainingPct + "%(5h=" + (bestG.FiveHour != null ? bestG.FiveHour.RemainingPct.ToString() : "-") + ",7d=" + (bestG.Weekly != null ? bestG.Weekly.RemainingPct.ToString() : "-") + ")"),
                    bestC == null ? "null" : (bestC.RemainingPct + "%(5h=" + (bestC.FiveHour != null ? bestC.FiveHour.RemainingPct.ToString() : "-") + ",7d=" + (bestC.Weekly != null ? bestC.Weekly.RemainingPct.ToString() : "-") + ")"));
                if (summary != _lastSavedQuotaSummary)
                {
                    _lastSavedQuotaSummary = summary;
                    Program.Note("quota-uia-saved: " + summary);
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

    private static void CollectLinesFaultTolerant(AutomationElement root, List<string> output, int maxNodes)
    {
        var walker = TreeWalker.ControlViewWalker;
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
