using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal sealed class PassiveWindow : Window
{
    internal IntPtr Handle;
    private Rect _bounds = Rect.Empty;
    private IntPtr _owner;

    internal PassiveWindow(string title)
    {
        Title = title;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Focusable = false;
        SizeToContent = SizeToContent.Manual;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        UseLayoutRounding = true;
        SourceInitialized += (s, e) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            Native.SetLong(Handle, -20, new IntPtr(Native.GetLong(Handle, -20).ToInt64() | 0x08000080));
            HwndSource.FromHwnd(Handle).AddHook((IntPtr w, int msg, IntPtr a, IntPtr b, ref bool handled) =>
            {
                if (msg == 0x21) { handled = true; return new IntPtr(3); } // WM_MOUSEACTIVATE -> MA_NOACTIVATE
                return IntPtr.Zero;
            });
        };
    }

    internal void Place(IntPtr owner, Rect r)
    {
        if (Handle == IntPtr.Zero) new WindowInteropHelper(this).EnsureHandle();
        if (_owner != owner && owner != IntPtr.Zero) { Native.SetLong(Handle, -8, owner); _owner = owner; }
        if (r == _bounds) return;
        _bounds = r;
        Native.SetWindowPos(Handle, IntPtr.Zero, (int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Ceiling(r.Width), (int)Math.Ceiling(r.Height), 0x14);
    }

    internal void Reveal()
    {
        if (IsVisible) return;
        Show();
        BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(110)));
    }
}

internal sealed class ThemePalette
{
    internal Brush StripBg;
    internal Brush StripHover;
    internal Brush StripBorder;
    internal Brush InkPrimary;
    internal Brush InkMuted;
    internal Brush SepInk;
    internal Brush PanelBg;
    internal Brush PanelBorder;
    internal Brush CardBg;
    internal Brush TrackBg;
    internal Brush AccentGemini;
    internal Brush AccentClaude;
    internal Brush WarnBrush;
    internal Brush CritBrush;

    internal static ThemePalette For(bool light)
    {
        if (light)
        {
            return new ThemePalette
            {
                StripBg = Brush("#08000000"),
                StripHover = Brush("#14000000"),
                StripBorder = Brush("#1F000000"),
                InkPrimary = Brush("#1F2328"),
                InkMuted = Brush("#59636E"),
                SepInk = Brush("#C2C8D0"),
                PanelBg = Brush("#FAFFFFFF"),
                PanelBorder = Brush("#D0D7DE"),
                CardBg = Brush("#F6F8FA"),
                TrackBg = Brush("#E5E9EF"),
                AccentGemini = Brush("#1A73E8"),
                AccentClaude = Brush("#C26A2D"),
                WarnBrush = Brush("#D97706"),
                CritBrush = Brush("#DC2626")
            };
        }
        return new ThemePalette
        {
            StripBg = Brush("#12FFFFFF"),
            StripHover = Brush("#20FFFFFF"),
            StripBorder = Brush("#22FFFFFF"),
            InkPrimary = Brush("#E6EDF3"),
            InkMuted = Brush("#9BA4B0"),
            SepInk = Brush("#424A54"),
            PanelBg = Brush("#F5171A20"),
            PanelBorder = Brush("#333A45"),
            CardBg = Brush("#FF1F242D"),
            TrackBg = Brush("#FF2D333E"),
            AccentGemini = Brush("#60A5FA"),
            AccentClaude = Brush("#F59E6C"),
            WarnBrush = Brush("#F59E0B"),
            CritBrush = Brush("#F87171")
        };
    }

    internal static Brush Brush(string hex) { return (Brush)new BrushConverter().ConvertFromString(hex); }
}

internal sealed class StripItem
{
    internal string Key;
    internal string Text;
    internal Brush Ink;
}

internal static class StripBuilder
{
    internal static List<StripItem> BuildItems(MonitorSnapshot snap, SessionStats session, string uiModel, OverlaySettings settings, ThemePalette pal)
    {
        var items = new List<StripItem>();
        string activeFamily = session != null && session.ModelFamily != "unknown"
            ? session.ModelFamily
            : MetricFormatter.ClassifyModelFamily(uiModel);

        foreach (string key in OverlaySettings.AllItems)
        {
            bool enabled;
            if (!settings.Visible.TryGetValue(key, out enabled) || !enabled) continue;

            if (key == "rate")
            {
                string txt = session != null ? MetricFormatter.FormatRate(session.TokPerSec, session.TokPerSecEstimated) : "-- tok/s";
                items.Add(new StripItem { Key = key, Text = txt, Ink = pal.InkPrimary });
            }
            else if (key == "today")
            {
                string txt = snap != null && snap.Today != null ? MetricFormatter.FormatTokens(snap.Today.TotalTokens) : "--";
                items.Add(new StripItem { Key = key, Text = txt, Ink = pal.InkMuted });
            }
            else if (key == "cache")
            {
                double? hr = (session != null && session.CacheHitRate.HasValue)
                    ? session.CacheHitRate
                    : (snap != null && snap.Today != null ? snap.Today.CacheHitRate : null);
                items.Add(new StripItem { Key = key, Text = MetricFormatter.FormatHitRate(hr), Ink = pal.InkMuted });
            }
            else if (key == "quota")
            {
                string qtxt = snap != null ? MetricFormatter.FormatCompactQuota(snap.Quota, activeFamily) : null;
                if (!string.IsNullOrEmpty(qtxt))
                {
                    Brush qink = pal.InkPrimary;
                    QuotaAlertLevel worst = WorstAlert(snap.Quota);
                    if (worst == QuotaAlertLevel.Critical7d) qink = pal.CritBrush;
                    else if (worst == QuotaAlertLevel.Warn5h) qink = pal.WarnBrush;
                    items.Add(new StripItem { Key = key, Text = qtxt, Ink = qink });
                }
            }
            else if (key == "context")
            {
                string txt = session != null && session.ContextTokens > 0
                    ? string.Format(CultureInfo.InvariantCulture, "上下文 {0:0}%", session.ContextPct)
                    : "上下文 --";
                Brush cink = (session != null && session.ContextPct >= 80.0) ? pal.WarnBrush : pal.InkMuted;
                items.Add(new StripItem { Key = key, Text = txt, Ink = cink });
            }
        }
        return items;
    }

    internal static List<StripItem> FitToWidth(List<StripItem> allItems, List<string> priority, double maxWidthDip, Func<List<StripItem>, double> measureDip)
    {
        var current = new List<StripItem>(allItems);
        if (current.Count == 0) return current;
        if (measureDip(current) <= maxWidthDip) return current;

        // Drop lowest-priority items first while keeping visual order of remaining items
        var dropOrder = new List<string>(priority);
        dropOrder.Reverse();
        foreach (string key in dropOrder)
        {
            if (current.Count <= 1) break;
            int idx = current.FindIndex(x => x.Key == key);
            if (idx >= 0)
            {
                current.RemoveAt(idx);
                if (measureDip(current) <= maxWidthDip) return current;
            }
        }
        return measureDip(current) <= maxWidthDip ? current : new List<StripItem>();
    }

    private static QuotaAlertLevel WorstAlert(QuotaSnapshot q)
    {
        if (q == null) return QuotaAlertLevel.None;
        QuotaAlertLevel a = q.Gemini != null ? q.Gemini.Alert : QuotaAlertLevel.None;
        QuotaAlertLevel b = q.Claude != null ? q.Claude.Alert : QuotaAlertLevel.None;
        if (a == QuotaAlertLevel.Critical7d || b == QuotaAlertLevel.Critical7d) return QuotaAlertLevel.Critical7d;
        if (a == QuotaAlertLevel.Warn5h || b == QuotaAlertLevel.Warn5h) return QuotaAlertLevel.Warn5h;
        return QuotaAlertLevel.None;
    }
}

internal sealed class OverlayController
{
    private static readonly FontFamily UiFont = new FontFamily("Segoe UI, Microsoft YaHei UI");
    private readonly string _stateDir;
    private readonly string _settingsPath;
    private readonly Action _requestRefresh;
    private readonly Dispatcher _dispatcher;
    private readonly PassiveWindow _strip;
    private readonly PassiveWindow _panel;
    private readonly DispatcherTimer _tick;

    private OverlaySettings _settings;
    private MonitorSnapshot _snapshot;
    private Anchor _anchor;
    private Native.RECT _lastFrame;
    private DateTime _stableAt = DateTime.MinValue;
    private string _hiddenSignature = "";
    private string _hostSignature = "";
    private bool _panelOpen;
    private bool _settingsTab;
    private bool _hoverStrip;
    private bool _lightTheme;

    internal volatile bool IsMoving;

    internal OverlayController(string stateDir, Action requestRefresh)
    {
        _stateDir = stateDir;
        _settingsPath = Path.Combine(stateDir, "settings.json");
        _requestRefresh = requestRefresh;
        _settings = OverlaySettings.Load(_settingsPath);
        _dispatcher = Dispatcher.CurrentDispatcher;

        _strip = new PassiveWindow("Antigravity Usage Strip");
        _panel = new PassiveWindow("Antigravity Usage Details");
        _panel.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.28, Color = Colors.Black };

        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _tick.Tick += (s, e) => OnTick();
        _tick.Start();
    }

    internal void HostState(string sig)
    {
        _dispatcher.BeginInvoke((Action)(() =>
        {
            _hostSignature = sig ?? "";
            if (string.IsNullOrEmpty(_hostSignature))
            {
                _panelOpen = false;
                _strip.Hide();
                _panel.Hide();
            }
        }));
    }

    internal void PostAnchor(Anchor anchor)
    {
        _dispatcher.BeginInvoke((Action)(() =>
        {
            _anchor = anchor;
            if (anchor != null) UpdateTheme(anchor);
            RenderAndPlace();
        }));
    }

    internal void PostSnapshot(MonitorSnapshot snap)
    {
        _dispatcher.BeginInvoke((Action)(() =>
        {
            _snapshot = snap;
            RenderAndPlace();
        }));
    }

    private void UpdateTheme(Anchor a)
    {
        if (_settings.ThemeMode == "light") { _lightTheme = true; return; }
        if (_settings.ThemeMode == "dark") { _lightTheme = false; return; }
        if (a.DetectedLightTheme.HasValue) { _lightTheme = a.DetectedLightTheme.Value; return; }
        _lightTheme = Native.IsLightPixel(a.Window, (int)(a.FreeLeft + 4), (int)(a.Top + a.Height / 2), _lightTheme);
    }

    private void OnTick()
    {
        if (_anchor == null || !Native.Live(_anchor.Window))
        {
            if (_strip.IsVisible) _strip.Hide();
            if (_panel.IsVisible) { _panelOpen = false; _panel.Hide(); }
            return;
        }

        Native.RECT nowFrame;
        if (Native.GetWindowRect(_anchor.Window, out nowFrame))
        {
            if (!_lastFrame.Same(nowFrame))
            {
                _lastFrame = nowFrame;
                _stableAt = DateTime.UtcNow.AddMilliseconds(260);
                IsMoving = true;
                _strip.Hide();
                if (_panelOpen) { _panelOpen = false; _panel.Hide(); }
                return;
            }
            if (IsMoving && DateTime.UtcNow >= _stableAt)
            {
                IsMoving = false;
                RenderAndPlace();
            }
        }

        // Collapse details panel when clicking outside both strip and panel
        if (_panelOpen && (Native.GetAsyncKeyState(0x01) & 0x8000) != 0)
        {
            Native.POINT pt;
            if (Native.GetCursorPos(out pt))
            {
                IntPtr hit = Native.GetAncestor(Native.WindowFromPoint(pt), 2);
                if (hit != _panel.Handle && hit != _strip.Handle)
                {
                    _panelOpen = false;
                    _panel.Hide();
                }
            }
        }
    }

    private void RenderAndPlace()
    {
        if (_anchor == null || _anchor.MenuOpen || IsMoving || _hiddenSignature == _hostSignature && !string.IsNullOrEmpty(_hostSignature))
        {
            _strip.Hide();
            if (_panelOpen) { _panelOpen = false; _panel.Hide(); }
            return;
        }

        ThemePalette pal = ThemePalette.For(_lightTheme);
        SessionStats session = _snapshot != null ? _snapshot.ResolveSession(_anchor.WindowTitle, _anchor.UiModelName) : null;
        var allItems = StripBuilder.BuildItems(_snapshot, session, _anchor.UiModelName, _settings, pal);
        double maxDip = Math.Max(0, (_anchor.FreeRight - _anchor.FreeLeft) / _anchor.Scale);
        var fitted = StripBuilder.FitToWidth(allItems, _settings.Priority, maxDip, list => MeasureStripDip(list, pal));

        if (fitted.Count == 0)
        {
            _strip.Hide();
            return;
        }

        FrameworkElement stripContent = BuildStripElement(fitted, pal);
        stripContent.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double wDip = Math.Ceiling(stripContent.DesiredSize.Width);
        double hDip = Math.Min(26.0, Math.Max(22.0, _anchor.Height / _anchor.Scale));
        double topPx = _anchor.Top + Math.Max(0, (_anchor.Height - hDip * _anchor.Scale) / 2.0);
        Rect place = OverlayLayout.PlaceInRange(_anchor.FreeLeft, _anchor.FreeRight, topPx, wDip, hDip, _anchor.Scale);
        if (place.IsEmpty)
        {
            _strip.Hide();
            return;
        }

        _strip.Content = stripContent;
        _strip.Place(_anchor.Window, place);
        _strip.Reveal();

        if (_panelOpen)
        {
            FrameworkElement panelContent = BuildPanelElement(session, pal);
            panelContent.Measure(new Size(560, 620));
            double pw = Math.Ceiling(panelContent.DesiredSize.Width) + 12;
            double ph = Math.Ceiling(panelContent.DesiredSize.Height) + 12;
            double px = Math.Max(_anchor.Frame.Left + 8 * _anchor.Scale, Math.Min(place.Right - pw * _anchor.Scale, _anchor.Frame.Right - pw * _anchor.Scale - 8 * _anchor.Scale));
            double py = Math.Max(_anchor.Frame.Top + 36 * _anchor.Scale, place.Top - ph * _anchor.Scale - 8 * _anchor.Scale);
            _panel.Content = panelContent;
            _panel.Place(_anchor.Window, new Rect(px, py, pw * _anchor.Scale, ph * _anchor.Scale));
            _panel.Reveal();
        }
        else if (_panel.IsVisible)
        {
            _panel.Hide();
        }
    }

    private double MeasureStripDip(List<StripItem> items, ThemePalette pal)
    {
        var el = BuildStripElement(items, pal);
        el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Ceiling(el.DesiredSize.Width);
    }

    private FrameworkElement BuildStripElement(List<StripItem> items, ThemePalette pal)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                sp.Children.Add(new TextBlock
                {
                    Text = " | ",
                    FontFamily = UiFont,
                    FontSize = 11.5,
                    Foreground = pal.SepInk,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            sp.Children.Add(new TextBlock
            {
                Text = items[i].Text,
                FontFamily = UiFont,
                FontSize = 11.5,
                FontWeight = i == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = items[i].Ink,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        var border = new Border
        {
            Background = _hoverStrip ? pal.StripHover : pal.StripBg,
            BorderBrush = pal.StripBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 2, 8, 2),
            Cursor = Cursors.Hand,
            Child = sp
        };
        border.MouseEnter += (s, e) => { _hoverStrip = true; border.Background = pal.StripHover; };
        border.MouseLeave += (s, e) => { _hoverStrip = false; border.Background = pal.StripBg; };
        border.MouseLeftButtonUp += (s, e) =>
        {
            _panelOpen = !_panelOpen;
            if (!_panelOpen) _settingsTab = false;
            RenderAndPlace();
            e.Handled = true;
        };
        border.MouseRightButtonUp += (s, e) =>
        {
            ShowContextMenu();
            e.Handled = true;
        };
        return border;
    }

    private void ShowContextMenu()
    {
        var menu = new ContextMenu();
        var refresh = new MenuItem { Header = "立即刷新本地统计" };
        refresh.Click += (s, e) => { if (_requestRefresh != null) _requestRefresh(); };
        var hideRun = new MenuItem { Header = "本次运行暂时隐藏" };
        hideRun.Click += (s, e) => { _hiddenSignature = _hostSignature; _panelOpen = false; _strip.Hide(); _panel.Hide(); };
        var quit = new MenuItem { Header = "退出 Antigravity Usage Monitor" };
        quit.Click += (s, e) => Application.Current.Shutdown();
        menu.Items.Add(refresh);
        menu.Items.Add(hideRun);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);
        menu.IsOpen = true;
    }

    internal FrameworkElement BuildPanelElement(SessionStats session, ThemePalette pal)
    {
        var rootStack = new StackPanel { Orientation = Orientation.Vertical };

        // Header bar
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var titleBlock = new TextBlock
        {
            Text = "Antigravity Usage Monitor",
            FontFamily = UiFont,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = pal.InkPrimary,
            VerticalAlignment = VerticalAlignment.Center
        };
        var tabBtn = MakeSmallButton(_settingsTab ? "返回详情" : "显示设置", pal, () =>
        {
            _settingsTab = !_settingsTab;
            RenderAndPlace();
        });
        DockPanel.SetDock(tabBtn, Dock.Right);
        header.Children.Add(tabBtn);
        header.Children.Add(titleBlock);
        rootStack.Children.Add(header);

        if (_settingsTab)
        {
            rootStack.Children.Add(BuildSettingsView(pal));
        }
        else
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });

            var leftCard = BuildSessionCard(session, pal);
            var rightCard = BuildQuotaAndTodayCard(pal);
            Grid.SetColumn(leftCard, 0);
            Grid.SetColumn(rightCard, 2);
            grid.Children.Add(leftCard);
            grid.Children.Add(rightCard);
            rootStack.Children.Add(grid);
        }

        return new Border
        {
            Background = pal.PanelBg,
            BorderBrush = pal.PanelBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
            Margin = new Thickness(6),
            Child = rootStack
        };
    }

    private Border BuildSessionCard(SessionStats s, ThemePalette pal)
    {
        var sp = new StackPanel();
        sp.Children.Add(SectionHeading("本会话", pal));
        if (s == null)
        {
            sp.Children.Add(MakeKv("状态", "等待会话数据", pal, pal.InkMuted));
        }
        else
        {
            string modelLabel = string.IsNullOrEmpty(s.Model) ? (_anchor != null ? _anchor.UiModelName : "--") : s.Model;
            sp.Children.Add(MakeKv("当前模型", modelLabel, pal, s.ModelFamily == "claude" ? pal.AccentClaude : pal.AccentGemini));
            sp.Children.Add(MakeKv("生成速率", MetricFormatter.FormatRate(s.TokPerSec, s.TokPerSecEstimated), pal, pal.InkPrimary));
            sp.Children.Add(MakeKv("本会话累计", MetricFormatter.FormatTokens(s.SessionTokens), pal, pal.InkPrimary));
            sp.Children.Add(MakeKv("上次回答", MetricFormatter.FormatTokens(s.LastTurnTokens) + " (" + MetricFormatter.FormatDurationMs(s.LastTurnDurationMs) + ")", pal, pal.InkMuted));
            sp.Children.Add(MakeKv("缓存命中率", MetricFormatter.FormatHitRate(s.CacheHitRate), pal, pal.InkPrimary));
            sp.Children.Add(MakeKv("上下文占用", string.Format(CultureInfo.InvariantCulture, "{0:0.0}% ({1})", s.ContextPct, MetricFormatter.FormatTokens(s.ContextTokens)), pal, s.ContextPct >= 80 ? pal.WarnBrush : pal.InkPrimary));
            sp.Children.Add(MakeProgressBar(s.ContextPct, s.ContextPct >= 80 ? pal.WarnBrush : pal.AccentGemini, pal));
            if (s.SubagentCount > 0)
            {
                sp.Children.Add(MakeKv("子代理拆分", string.Format(CultureInfo.InvariantCulture, "{0} 个 · {1}", s.SubagentCount, MetricFormatter.FormatTokens(s.SubagentTokens)), pal, pal.AccentClaude));
                sp.Children.Add(MakeKv("含子代理合计", MetricFormatter.FormatTokens(s.TotalWithSubagentsTokens), pal, pal.InkMuted));
            }
            if (s.ExecutionDurationMs > 0)
            {
                sp.Children.Add(MakeKv("执行总耗时", MetricFormatter.FormatDurationMs(s.ExecutionDurationMs), pal, pal.InkMuted));
            }
        }
        return WrapCard(sp, pal);
    }

    private Border BuildQuotaAndTodayCard(ThemePalette pal)
    {
        var sp = new StackPanel();
        sp.Children.Add(SectionHeading("官方双池配额", pal));
        QuotaSnapshot q = _snapshot != null ? _snapshot.Quota : null;
        if (q == null || (q.Gemini == null && q.Claude == null))
        {
            sp.Children.Add(new TextBlock
            {
                Text = "暂未获取到云端配额快照。\n打开设置配额页或使用 agy statusLine 同步后将自动显示。",
                FontFamily = UiFont,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = pal.InkMuted,
                Margin = new Thickness(0, 2, 0, 8)
            });
        }
        else
        {
            if (q.Gemini != null) sp.Children.Add(BuildPoolBlock("Gemini 池", q.Gemini, pal.AccentGemini, pal));
            if (q.Claude != null) sp.Children.Add(BuildPoolBlock("Claude / 第三方池", q.Claude, pal.AccentClaude, pal));
            sp.Children.Add(MakeKv("数据来源", string.IsNullOrEmpty(q.Source) ? "--" : q.Source, pal, pal.InkMuted));
        }

        sp.Children.Add(new Border { Height = 1, Background = pal.PanelBorder, Margin = new Thickness(0, 8, 0, 8) });
        sp.Children.Add(SectionHeading("今日汇总", pal));
        TodayStats t = _snapshot != null ? _snapshot.Today : null;
        if (t == null)
        {
            sp.Children.Add(MakeKv("今日 Token", "--", pal, pal.InkMuted));
        }
        else
        {
            sp.Children.Add(MakeKv("今日总 Token", MetricFormatter.FormatTokens(t.TotalTokens), pal, pal.InkPrimary));
            sp.Children.Add(MakeKv("今日缓存命中", MetricFormatter.FormatHitRate(t.CacheHitRate), pal, pal.InkPrimary));
            sp.Children.Add(MakeKv("活跃会话数", t.SessionsCount.ToString(CultureInfo.InvariantCulture), pal, pal.InkMuted));
        }
        return WrapCard(sp, pal);
    }

    private StackPanel BuildPoolBlock(string title, PoolQuotaState p, Brush accent, ThemePalette pal)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        if (p.FiveHour != null && p.Weekly != null)
        {
            sp.Children.Add(new TextBlock
            {
                Text = title,
                FontFamily = UiFont,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = pal.InkPrimary,
                Margin = new Thickness(0, 0, 0, 2)
            });
            sp.Children.Add(BuildSingleWindowBar("5h 限制", p.FiveHour, accent, pal));
            sp.Children.Add(BuildSingleWindowBar("7d 限制", p.Weekly, accent, pal));
            return sp;
        }
        string badge = string.IsNullOrEmpty(p.WindowKind) ? "" : (" [" + p.WindowKind + "]");
        sp.Children.Add(BuildSingleWindowBar(title + badge, p, accent, pal));
        return sp;
    }

    private StackPanel BuildSingleWindowBar(string label, PoolQuotaState p, Brush accent, ThemePalette pal)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 3) };
        Brush barBrush = accent;
        if (p.Alert == QuotaAlertLevel.Critical7d) barBrush = pal.CritBrush;
        else if (p.Alert == QuotaAlertLevel.Warn5h) barBrush = pal.WarnBrush;

        string rightVal = p.Alert == QuotaAlertLevel.ResetPassed
            ? "已重置"
            : string.Format(CultureInfo.InvariantCulture, "{0:0}% · {1}", p.RemainingPct, p.CountdownText);
        sp.Children.Add(MakeKv(label, rightVal, pal, barBrush));
        sp.Children.Add(MakeProgressBar(p.RemainingPct, barBrush, pal));
        if (!string.IsNullOrEmpty(p.AlertHint))
        {
            sp.Children.Add(new TextBlock
            {
                Text = p.AlertHint,
                FontFamily = UiFont,
                FontSize = 10.5,
                Foreground = barBrush,
                Margin = new Thickness(0, 1, 0, 0)
            });
        }
        return sp;
    }

    private StackPanel BuildSettingsView(ThemePalette pal)
    {
        var sp = new StackPanel { Width = 492 };
        sp.Children.Add(SectionHeading("主题模式", pal));
        var themeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 10) };
        string[] modes = new[] { "auto", "dark", "light" };
        string[] labels = new[] { "跟随 Antigravity", "深色", "浅色" };
        for (int i = 0; i < modes.Length; i++)
        {
            string m = modes[i];
            var btn = MakeSmallButton((_settings.ThemeMode == m ? "● " : "") + labels[i], pal, () =>
            {
                _settings.ThemeMode = m;
                _settings.Save(_settingsPath);
                if (_anchor != null) UpdateTheme(_anchor);
                RenderAndPlace();
            });
            btn.Margin = new Thickness(0, 0, 8, 0);
            themeRow.Children.Add(btn);
        }
        sp.Children.Add(themeRow);

        sp.Children.Add(SectionHeading("折叠条显示项与窄窗口保留顺序（越靠上越优先保留）", pal));
        var names = new Dictionary<string, string>
        {
            { "rate", "生成速率 (tok/s)" },
            { "quota", "官方双池配额 (G · C)" },
            { "context", "上下文占用 (%)" },
            { "today", "今日总 Token" },
            { "cache", "缓存命中率 (%)" }
        };

        for (int i = 0; i < _settings.Priority.Count; i++)
        {
            int idx = i;
            string key = _settings.Priority[i];
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var down = MakeSmallButton("↓", pal, () => MovePriority(idx, 1));
            var up = MakeSmallButton("↑", pal, () => MovePriority(idx, -1));
            up.Margin = new Thickness(0, 0, 4, 0);
            DockPanel.SetDock(down, Dock.Right);
            DockPanel.SetDock(up, Dock.Right);
            row.Children.Add(down);
            row.Children.Add(up);

            var cb = new CheckBox
            {
                Content = names.ContainsKey(key) ? names[key] : key,
                IsChecked = _settings.Visible.ContainsKey(key) && _settings.Visible[key],
                Foreground = pal.InkPrimary,
                FontFamily = UiFont,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            cb.Click += (s, e) =>
            {
                _settings.Visible[key] = cb.IsChecked == true;
                _settings.Save(_settingsPath);
                RenderAndPlace();
            };
            row.Children.Add(cb);
            sp.Children.Add(row);
        }
        return sp;
    }

    private void MovePriority(int index, int delta)
    {
        int next = index + delta;
        if (next < 0 || next >= _settings.Priority.Count) return;
        string item = _settings.Priority[index];
        _settings.Priority.RemoveAt(index);
        _settings.Priority.Insert(next, item);
        _settings.Save(_settingsPath);
        RenderAndPlace();
    }

    private static TextBlock SectionHeading(string text, ThemePalette pal)
    {
        return new TextBlock
        {
            Text = text,
            FontFamily = UiFont,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = pal.InkMuted,
            Margin = new Thickness(0, 0, 0, 6)
        };
    }

    private static DockPanel MakeKv(string label, string value, ThemePalette pal, Brush valueInk)
    {
        var dp = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var val = new TextBlock
        {
            Text = value ?? "--",
            FontFamily = UiFont,
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            Foreground = valueInk,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 145
        };
        DockPanel.SetDock(val, Dock.Right);
        dp.Children.Add(val);
        dp.Children.Add(new TextBlock
        {
            Text = label,
            FontFamily = UiFont,
            FontSize = 11.5,
            Foreground = pal.InkMuted
        });
        return dp;
    }

    private static Border MakeProgressBar(double pct, Brush fill, ThemePalette pal)
    {
        double clamped = Math.Max(0.0, Math.Min(100.0, pct));
        var grid = new Grid { Height = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.01, clamped), GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.01, 100.0 - clamped), GridUnitType.Star) });
        var bar = new Border { Background = fill, CornerRadius = new CornerRadius(2) };
        Grid.SetColumn(bar, 0);
        grid.Children.Add(bar);
        return new Border
        {
            Background = pal.TrackBg,
            CornerRadius = new CornerRadius(2),
            Height = 4,
            Margin = new Thickness(0, 2, 0, 4),
            Child = grid
        };
    }

    private static Border WrapCard(UIElement child, ThemePalette pal)
    {
        return new Border
        {
            Background = pal.CardBg,
            BorderBrush = pal.PanelBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Child = child
        };
    }

    private static Border MakeSmallButton(string text, ThemePalette pal, Action onClick)
    {
        var b = new Border
        {
            Background = pal.CardBg,
            BorderBrush = pal.PanelBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 3, 8, 3),
            Cursor = Cursors.Hand,
            Child = new TextBlock { Text = text, FontFamily = UiFont, FontSize = 11, Foreground = pal.InkPrimary }
        };
        b.MouseLeftButtonUp += (s, e) => { onClick(); e.Handled = true; };
        return b;
    }

    private OverlayController(string stateDir, bool previewOnly)
    {
        _stateDir = stateDir;
        _settingsPath = Path.Combine(stateDir, "settings.json");
        _settings = OverlaySettings.CreateDefault();
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    internal static void RenderPreview(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var now = DateTime.UtcNow;
        var sample = new MonitorSnapshot
        {
            ActiveConversationId = "demo",
            Today = new TodayStats { Date = now.ToString("yyyy-MM-dd"), TotalTokens = 1845200, CacheHitRate = 0.846, SessionsCount = 6 },
            Sessions = new Dictionary<string, SessionStats>
            {
                {
                    "demo", new SessionStats
                    {
                        ConversationId = "demo",
                        Title = "Antigravity Usage Monitor",
                        Model = "gemini-4-neon-high",
                        ModelFamily = "gemini",
                        SessionTokens = 248100,
                        CacheHitRate = 0.846,
                        LastTurnTokens = 52100,
                        LastTurnDurationMs = 11200,
                        TokPerSec = 48.2,
                        ContextTokens = 142000,
                        ContextWindowSize = 1048576,
                        ContextPct = 13.5,
                        SubagentCount = 2,
                        SubagentTokens = 64300,
                        TotalWithSubagentsTokens = 312400,
                        ExecutionDurationMs = 98000
                    }
                }
            },
            Quota = new QuotaSnapshot
            {
                Source = "agy-statusline",
                PlanTier = "Pro",
                Gemini = QuotaEvaluator.Evaluate("gemini", 25.0, now.AddHours(3).AddMinutes(15), now),
                Claude = QuotaEvaluator.Evaluate("claude", 8.0, now.AddHours(3).AddMinutes(40), now)
            }
        };

        var ctrl = new OverlayController(outputDir, true);
        ctrl._snapshot = sample;
        foreach (bool light in new[] { false, true })
        {
            var pal = ThemePalette.For(light);
            var panel = ctrl.BuildPanelElement(sample.Sessions["demo"], pal);
            panel.Measure(new Size(560, 620));
            panel.Arrange(new Rect(new Point(0, 0), panel.DesiredSize));
            panel.UpdateLayout();
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(panel.DesiredSize.Width * 2), (int)Math.Ceiling(panel.DesiredSize.Height * 2), 192, 192, PixelFormats.Pbgra32);
            rtb.Render(panel);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(Path.Combine(outputDir, light ? "preview-light.png" : "preview-dark.png"))) enc.Save(fs);
        }
    }
}
