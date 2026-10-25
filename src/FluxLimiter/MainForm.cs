using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace FluxLimiter;

public sealed class MainForm : Form
{
    private readonly AppSettings settings = SettingsStore.Load();
    private readonly NetworkSampler network = new();
    private readonly TrafficCollector traffic = new();
    private readonly BufferedDataGridView grid = new();
    private readonly TextBox search = new();
    private readonly ComboBox filter = new();
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 3500 };
    private readonly System.Windows.Forms.Timer searchDebounce = new() { Interval = 280 };
    private readonly System.Windows.Forms.Timer speedTimer = new() { Interval = 1000 };

    private bool reloading;
    private bool refreshQueued;
    private bool refreshDeferredByContextMenu;
    private bool settingsSaveDisabled;
    private bool exitingApplication;

    private readonly Dictionary<string, Button> navButtons = new(StringComparer.OrdinalIgnoreCase);
    private string activeNavPage = "Dashboard";
    private Panel? dashboardPage;
    private Panel? pageHost;
    private readonly Label status = new();
    private readonly Label networkAdapterLabel = new();
    private readonly Label networkStateLabel = new();

    // Custom painted Dashboard Cards
    private Panel? downloadCard;
    private Panel? uploadCard;
    private Panel? dailyCard;
    private Panel? monthlyCard;
    private Panel? topAppsPanel;
    private Panel? sevenDayChartPanel;

    private double currentDownKBps;
    private double currentUpKBps;
    private readonly List<float> downSparkHistory = new() { 8f, 12f, 10f, 18f, 15f, 24f, 21f, 30f, 26f };
    private readonly List<float> upSparkHistory = new() { 2f, 3f, 2.5f, 4f, 3.5f, 5f, 4.2f, 6f, 5.2f };
    private List<(string Name, Image? Icon, long Bytes)> topAppsToday = new();

    // Shared fonts & palette (disposed cleanly on close)
    private readonly Color bg = Color.FromArgb(11, 18, 32);
    private readonly Color sidebarBg = Color.FromArgb(13, 21, 36);
    private readonly Color cardBg = Color.FromArgb(17, 27, 45);
    private readonly Color cardBorder = Color.FromArgb(29, 43, 66);
    private readonly Color ink = Color.FromArgb(241, 245, 249);
    private readonly Color mutedInk = Color.FromArgb(139, 155, 180);
    private readonly Color accentGreen = Color.FromArgb(0, 230, 118);
    private readonly Color accentBlue = Color.FromArgb(29, 155, 240);

    private readonly Font titleFont = new("Segoe UI", 21f, FontStyle.Bold);
    private readonly Font subtitleFont = new("Segoe UI", 9.5f, FontStyle.Regular);
    private readonly Font sectionFont = new("Segoe UI Semibold", 11.5f, FontStyle.Bold);
    private readonly Font cardLabelFont = new("Segoe UI", 9f, FontStyle.Regular);
    private readonly Font cardValueFont = new("Segoe UI", 15.5f, FontStyle.Bold);
    private readonly Font smallFont = new("Segoe UI", 8.5f, FontStyle.Regular);
    private readonly Font navFont = new("Segoe UI Semibold", 10f, FontStyle.Regular);

    private readonly Dictionary<(int Pid, long StartTicks), string> processPathCache = new();
    private readonly Dictionary<string, Image?> iconCache = new(StringComparer.OrdinalIgnoreCase);

    // Taskbar speed widget & tray icon
    private readonly NotifyIcon trayIcon;
    private readonly ContextMenuStrip trayMenu;
    private readonly TaskbarSpeedOverlay taskbarOverlay;

    public MainForm()
    {
        Text = "FluxLimiter";
        Width = 1440;
        Height = 900;
        MinimumSize = new Size(1180, 740);
        BackColor = bg;
        ForeColor = ink;
        Font = new Font("Segoe UI", 9.5f);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;

        EnableDarkTitleBar();

        // System Tray + Taskbar Speed Overlay next to the Windows Clock
        trayMenu = BuildTrayMenu();
        taskbarOverlay = new TaskbarSpeedOverlay(trayMenu);
        trayIcon = new NotifyIcon
        {
            Text = "FluxLimiter — Network Control",
            Icon = SystemIcons.Shield,
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        var shell = new Panel { Dock = DockStyle.Fill, BackColor = bg };
        var sidebar = BuildSidebar();
        dashboardPage = BuildDashboardSurface();

        pageHost = new Panel { Dock = DockStyle.Fill, BackColor = bg };
        pageHost.Controls.Add(dashboardPage);

        shell.Controls.Add(pageHost);
        shell.Controls.Add(sidebar);
        Controls.Add(shell);

        ConfigureGrid();
        SetupContextMenu();

        searchDebounce.Tick += (_, _) => { searchDebounce.Stop(); QueueReload(); };
        refresh.Tick += (_, _) =>
        {
            if (!grid.IsCurrentCellInEditMode && grid.ContextMenuStrip?.Visible != true)
                QueueReload();
        };
        refresh.Start();

        speedTimer.Tick += (_, _) => OnSpeedTimerTick();
        speedTimer.Start();

        FormClosing += OnMainFormClosing;
        FormClosed += OnMainFormClosed;

        _ = traffic.StartAsync();

        if (!string.IsNullOrWhiteSpace(SettingsStore.LastLoadWarning))
            settingsSaveDisabled = SettingsStore.LastLoadWarning.Contains("Saving is disabled", StringComparison.OrdinalIgnoreCase);

        Shown += (_, _) =>
        {
            taskbarOverlay.Show();
            taskbarOverlay.SnapNextToTrayClock();
            if (!string.IsNullOrWhiteSpace(SettingsStore.LastLoadWarning))
                MessageBox.Show(SettingsStore.LastLoadWarning, "FluxLimiter settings recovery");
            TriggerFirewallSyncAsync(showSuccess: false);
            QueueReload();
        };
    }

    private void EnableDarkTitleBar()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        try
        {
            int useDark = 1;
            DwmSetWindowAttribute(Handle, 20, ref useDark, sizeof(int));
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip { BackColor = cardBg, ForeColor = ink, ShowImageMargin = false };
        menu.Items.Add("Open FluxLimiter Dashboard", null, (_, _) => RestoreFromTray());
        var toggleOverlay = new ToolStripMenuItem("Show Speed Meter Next to Clock") { Checked = true, CheckOnClick = true };
        toggleOverlay.CheckedChanged += (_, _) =>
        {
            if (toggleOverlay.Checked)
            {
                taskbarOverlay.Show();
                taskbarOverlay.SnapNextToTrayClock();
            }
            else
            {
                taskbarOverlay.Hide();
            }
        };
        menu.Items.Add(toggleOverlay);
        var unitToggle = new ToolStripMenuItem("Display Speeds in Mbps / Kbps") { Checked = true, CheckOnClick = true };
        unitToggle.CheckedChanged += (_, _) => taskbarOverlay.UseBitsPerSecond = unitToggle.Checked;
        menu.Items.Add(unitToggle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit FluxLimiter", null, (_, _) =>
        {
            exitingApplication = true;
            Close();
        });
        return menu;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        QueueReload();
    }

    private Panel BuildSidebar()
    {
        var sidebar = new Panel { Dock = DockStyle.Left, Width = 224, BackColor = sidebarBg, Padding = new Padding(12, 18, 12, 16) };
        sidebar.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(24, 36, 56), 1f);
            e.Graphics.DrawLine(pen, sidebar.Width - 1, 0, sidebar.Width - 1, sidebar.Height);
            e.Graphics.DrawLine(pen, 16, 338, sidebar.Width - 16, 338);
        };

        // Top Brand Header (Blue rounded icon + FluxLimiter title)
        var brandPanel = new Panel { Left = 14, Top = 14, Width = 196, Height = 44, BackColor = Color.Transparent };
        brandPanel.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var logoPath = RoundedRect(new Rectangle(2, 6, 30, 30), 7);
            using var logoBrush = new LinearGradientBrush(new Rectangle(2, 6, 30, 30),
                Color.FromArgb(29, 155, 240), Color.FromArgb(0, 97, 255), LinearGradientMode.Vertical);
            g.FillPath(logoBrush, logoPath);
            using var barBrush = new SolidBrush(Color.White);
            g.FillRectangle(barBrush, 9, 22, 3, 8);
            g.FillRectangle(barBrush, 15, 17, 3, 13);
            g.FillRectangle(barBrush, 21, 13, 3, 17);

            TextRenderer.DrawText(g, "FluxLimiter", new Font("Segoe UI", 14f, FontStyle.Bold),
                new Point(42, 8), Color.White);
        };
        sidebar.Controls.Add(brandPanel);

        string[] primaryNav = { "Dashboard", "Applications", "Rules", "Monthly Usage", "Statistics" };
        for (int i = 0; i < primaryNav.Length; i++)
        {
            var btn = CreateSidebarButton(primaryNav[i], 76 + (i * 48));
            sidebar.Controls.Add(btn);
        }

        string[] secondaryNav = { "Settings", "About" };
        for (int i = 0; i < secondaryNav.Length; i++)
        {
            var btn = CreateSidebarButton(secondaryNav[i], 354 + (i * 48));
            sidebar.Controls.Add(btn);
        }

        return sidebar;
    }

    private Button CreateSidebarButton(string pageName, int top)
    {
        var btn = new Button
        {
            Text = "      " + pageName,
            Left = 12,
            Top = top,
            Width = 200,
            Height = 42,
            FlatStyle = FlatStyle.Flat,
            Font = navFont,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.Transparent,
            ForeColor = pageName == "Dashboard" ? Color.White : mutedInk,
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(20, 32, 52);
        btn.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool isSelected = string.Equals(activeNavPage, pageName, StringComparison.OrdinalIgnoreCase);
            if (isSelected)
            {
                using var path = RoundedRect(new Rectangle(0, 0, btn.Width - 1, btn.Height - 1), 8);
                using var grad = new LinearGradientBrush(btn.ClientRectangle,
                    Color.FromArgb(11, 88, 168), Color.FromArgb(11, 61, 120), LinearGradientMode.Horizontal);
                g.FillPath(grad, path);
                using var accentBrush = new SolidBrush(Color.FromArgb(56, 189, 248));
                g.FillRectangle(accentBrush, 0, 7, 4, btn.Height - 14);
            }

            DrawNavIcon(g, pageName, new Rectangle(14, 12, 18, 18), isSelected ? Color.White : mutedInk);
            TextRenderer.DrawText(g, pageName, navFont, new Rectangle(42, 0, btn.Width - 46, btn.Height),
                isSelected ? Color.White : mutedInk, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        };
        btn.Click += (_, _) => NavigateTo(pageName);
        navButtons[pageName] = btn;
        return btn;
    }

    private static void DrawNavIcon(Graphics g, string item, Rectangle bounds, Color color)
    {
        using var pen = new Pen(color, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        switch (item)
        {
            case "Dashboard":
                Point[] roof = { new(bounds.Left + 1, bounds.Top + 8), new(bounds.Left + 9, bounds.Top + 1), new(bounds.Right - 1, bounds.Top + 8) };
                g.DrawLines(pen, roof);
                g.DrawRectangle(pen, bounds.Left + 3, bounds.Top + 8, 12, 9);
                break;
            case "Applications":
                g.DrawRectangle(pen, bounds.Left + 1, bounds.Top + 2, 16, 14);
                g.DrawLine(pen, bounds.Left + 1, bounds.Top + 7, bounds.Right - 1, bounds.Top + 7);
                g.DrawLine(pen, bounds.Left + 7, bounds.Top + 7, bounds.Left + 7, bounds.Bottom - 2);
                break;
            case "Rules":
                Point[] shield = { new(bounds.Left + 9, bounds.Top + 1), new(bounds.Right - 2, bounds.Top + 4), new(bounds.Right - 3, bounds.Top + 11), new(bounds.Left + 9, bounds.Bottom - 1), new(bounds.Left + 3, bounds.Top + 11), new(bounds.Left + 2, bounds.Top + 4) };
                g.DrawPolygon(pen, shield); break;
            case "Monthly Usage":
            case "Statistics":
                g.DrawLine(pen, bounds.Left + 2, bounds.Bottom - 2, bounds.Right - 2, bounds.Bottom - 2);
                g.DrawLine(pen, bounds.Left + 5, bounds.Bottom - 2, bounds.Left + 5, bounds.Top + 9);
                g.DrawLine(pen, bounds.Left + 9, bounds.Bottom - 2, bounds.Left + 9, bounds.Top + 4);
                g.DrawLine(pen, bounds.Left + 13, bounds.Bottom - 2, bounds.Left + 13, bounds.Top + 7); break;
            case "Settings":
                g.DrawEllipse(pen, bounds.Left + 3, bounds.Top + 3, 12, 12); g.DrawEllipse(pen, bounds.Left + 7, bounds.Top + 7, 4, 4); break;
            case "About":
                g.DrawEllipse(pen, bounds.Left + 1, bounds.Top + 1, 16, 16); g.DrawLine(pen, bounds.Left + 9, bounds.Top + 8, bounds.Left + 9, bounds.Bottom - 4);
                g.FillEllipse(new SolidBrush(color), bounds.Left + 8, bounds.Top + 4, 2, 2); break;
        }
    }

    private Panel BuildDashboardSurface()
    {
        var dashboard = new Panel { Dock = DockStyle.Fill, BackColor = bg, Padding = new Padding(24, 18, 24, 18) };
        var rootLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, BackColor = bg };
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 218));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));

        var headerRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = bg, Margin = new Padding(0) };
        headerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        headerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 248));
        var titleStack = new Panel { Dock = DockStyle.Fill, BackColor = bg };
        titleStack.Controls.Add(new Label { Text = "Dashboard", Font = titleFont, ForeColor = Color.White, AutoSize = true, Left = 0, Top = 0 });
        titleStack.Controls.Add(new Label { Text = "Overview of your network usage and active applications", Font = subtitleFont, ForeColor = mutedInk, AutoSize = true, Left = 2, Top = 42 });

        var netCard = CreateRoundedCardPanel();
        netCard.Padding = new Padding(12, 8, 12, 8);
        netCard.Paint += (_, e) =>
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using var circleBrush = new SolidBrush(Color.FromArgb(13, 46, 40)); g.FillEllipse(circleBrush, 14, 13, 36, 36);
            using var wifiPen = new Pen(accentGreen, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(wifiPen, 20, 19, 24, 24, 225, 90); g.DrawArc(wifiPen, 24, 24, 16, 16, 225, 90);
            using var dotBrush = new SolidBrush(accentGreen); g.FillEllipse(dotBrush, 30, 34, 4, 4);
        };
        var netTitle = new Label { Text = "Network", Font = smallFont, ForeColor = mutedInk, Left = 60, Top = 8, AutoSize = true, BackColor = Color.Transparent };
        networkAdapterLabel.Text = "Wi-Fi Connected"; networkAdapterLabel.Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold); networkAdapterLabel.ForeColor = Color.White;
        networkAdapterLabel.Left = 60; networkAdapterLabel.Top = 23; networkAdapterLabel.Width = 165; networkAdapterLabel.Height = 19; networkAdapterLabel.BackColor = Color.Transparent;
        networkStateLabel.Text = "Connected"; networkStateLabel.Font = smallFont; networkStateLabel.ForeColor = accentGreen; networkStateLabel.Left = 60; networkStateLabel.Top = 42; networkStateLabel.AutoSize = true; networkStateLabel.BackColor = Color.Transparent;
        netCard.Controls.Add(netTitle); netCard.Controls.Add(networkAdapterLabel); netCard.Controls.Add(networkStateLabel);
        headerRow.Controls.Add(titleStack, 0, 0); headerRow.Controls.Add(netCard, 1, 0);

        var kpiRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = bg, Margin = new Padding(0, 4, 0, 8) };
        for (int i = 0; i < 4; i++) kpiRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        downloadCard = CreateSparklineCard(true); uploadCard = CreateSparklineCard(false); dailyCard = CreateDailyUsageCard(); monthlyCard = CreateMonthlyUsageCard();
        kpiRow.Controls.Add(downloadCard, 0, 0); kpiRow.Controls.Add(uploadCard, 1, 0); kpiRow.Controls.Add(dailyCard, 2, 0); kpiRow.Controls.Add(monthlyCard, 3, 0);

        var chartsRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = bg, Margin = new Padding(0, 0, 0, 10) };
        chartsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44f)); chartsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56f));
        topAppsPanel = CreateTopAppsPanel(); sevenDayChartPanel = CreateSevenDayChartPanel();
        chartsRow.Controls.Add(topAppsPanel, 0, 0); chartsRow.Controls.Add(sevenDayChartPanel, 1, 0);

        var activeCard = CreateRoundedCardPanel(); activeCard.Margin = new Padding(0); activeCard.Padding = new Padding(16, 12, 16, 12);
        var activeHeader = new TableLayoutPanel { Dock = DockStyle.Top, Height = 42, ColumnCount = 3, RowCount = 1, BackColor = Color.Transparent };
        activeHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f)); activeHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240f)); activeHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165f));
        var activeTitle = new Label { Text = "Active Applications", Dock = DockStyle.Fill, Font = sectionFont, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft };

        search.Dock = DockStyle.Fill; search.Margin = new Padding(0, 6, 10, 6); search.BackColor = Color.FromArgb(13, 21, 36); search.ForeColor = Color.White;
        search.BorderStyle = BorderStyle.FixedSingle; search.PlaceholderText = "Search applications..."; search.TextChanged += (_, _) => { searchDebounce.Stop(); searchDebounce.Start(); };
        filter.Dock = DockStyle.Fill; filter.Margin = new Padding(0, 6, 0, 6); filter.DropDownStyle = ComboBoxStyle.DropDownList; filter.BackColor = Color.FromArgb(22, 34, 54); filter.ForeColor = Color.White; filter.FlatStyle = FlatStyle.Flat;
        filter.Items.AddRange(new object[] { "All Connections", "User Processes", "System Processes", "Limited / Blocked" }); filter.SelectedIndex = 0; filter.SelectedIndexChanged += (_, _) => QueueReload();
        activeHeader.Controls.Add(activeTitle, 0, 0); activeHeader.Controls.Add(search, 1, 0); activeHeader.Controls.Add(filter, 2, 0);
        grid.Dock = DockStyle.Fill; activeCard.Controls.Add(grid); activeCard.Controls.Add(activeHeader); grid.BringToFront();

        status.Dock = DockStyle.Fill; status.Font = smallFont; status.ForeColor = mutedInk; status.TextAlign = ContentAlignment.MiddleLeft;
        status.Text = "ETW starting... · Taskbar speed widget active next to Windows clock";
        rootLayout.Controls.Add(headerRow, 0, 0); rootLayout.Controls.Add(kpiRow, 0, 1); rootLayout.Controls.Add(chartsRow, 0, 2); rootLayout.Controls.Add(activeCard, 0, 3); rootLayout.Controls.Add(status, 0, 4);
        dashboard.Controls.Add(rootLayout); return dashboard;
    }

    private Panel CreateRoundedCardPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = cardBg, Margin = new Padding(0, 0, 12, 0) };
        panel.Paint += (_, e) => { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using var borderPen = new Pen(cardBorder, 1f); using var rectPath = RoundedRect(new Rectangle(0, 0, panel.Width - 1, panel.Height - 1), 10); e.Graphics.DrawPath(borderPen, rectPath); };
        return panel;
    }

    private Panel CreateSparklineCard(bool isDownload)
    {
        var card = CreateRoundedCardPanel();
        card.Paint += (_, e) =>
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            Color badgeBg = isDownload ? Color.FromArgb(13, 46, 40) : Color.FromArgb(14, 36, 64); Color accent = isDownload ? accentGreen : accentBlue;
            using (var badgePath = RoundedRect(new Rectangle(16, 22, 42, 42), 9)) using (var badgeBrush = new SolidBrush(badgeBg)) g.FillPath(badgeBrush, badgePath);
            using (var arrowPen = new Pen(accent, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                int cx = 37;
                if (isDownload) { g.DrawLine(arrowPen, cx, 32, cx, 52); g.DrawLine(arrowPen, cx - 6, 46, cx, 52); g.DrawLine(arrowPen, cx + 6, 46, cx, 52); }
                else { g.DrawLine(arrowPen, cx, 52, cx, 32); g.DrawLine(arrowPen, cx - 6, 38, cx, 32); g.DrawLine(arrowPen, cx + 6, 38, cx, 32); }
            }
            string label = isDownload ? "Download" : "Upload"; double kbps = isDownload ? currentDownKBps : currentUpKBps; string valueText = FormatCardSpeed(kbps);
            TextRenderer.DrawText(g, label, cardLabelFont, new Point(68, 22), mutedInk); TextRenderer.DrawText(g, valueText, cardValueFont, new Point(66, 41), Color.White);
            var history = isDownload ? downSparkHistory : upSparkHistory;
            if (history.Count >= 2 && card.Width > 190)
            {
                Rectangle sparkBounds = new(card.Width - 115, 44, 100, 38);
                float maxVal = Math.Max(10f, history.Max() * 1.15f);
                PointF[] pts = new PointF[history.Count];
                for (int i = 0; i < history.Count; i++)
                {
                    float x = sparkBounds.Left + (i / (float)(history.Count - 1)) * sparkBounds.Width;
                    float y = sparkBounds.Bottom - (Math.Clamp(history[i] / maxVal, 0.05f, 1f) * sparkBounds.Height);
                    pts[i] = new PointF(x, y);
                }

                using var areaPath = new GraphicsPath();
                areaPath.AddCurve(pts, 0.35f);
                areaPath.AddLine(pts[^1].X, sparkBounds.Bottom, pts[0].X, sparkBounds.Bottom);
                areaPath.CloseFigure();

                using var areaGrad = new LinearGradientBrush(sparkBounds,
                    Color.FromArgb(95, accent), Color.FromArgb(0, accent), LinearGradientMode.Vertical);
                g.FillPath(areaGrad, areaPath);

                using var linePen = new Pen(accent, 2f);
                g.DrawCurve(linePen, pts, 0.35f);
            }
        };
        return card;
    }

    private Panel CreateDailyUsageCard()
    {
        var card = CreateRoundedCardPanel();
        card.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var badgePath = RoundedRect(new Rectangle(16, 22, 42, 42), 9))
            using (var badgeBrush = new SolidBrush(Color.FromArgb(28, 27, 58))) { g.FillPath(badgeBrush, badgePath); }
            using (var dbPen = new Pen(Color.FromArgb(139, 120, 255), 1.8f))
            {
                g.DrawEllipse(dbPen, 28, 30, 18, 6); g.DrawLine(dbPen, 28, 33, 28, 50); g.DrawLine(dbPen, 46, 33, 46, 50); g.DrawArc(dbPen, 28, 47, 18, 6, 0, 180);
            }
            var (rx, tx) = traffic.SessionTotals; long total = rx + tx;
            TextRenderer.DrawText(g, "Daily Usage", cardLabelFont, new Point(68, 16), mutedInk);
            TextRenderer.DrawText(g, FormatBytes(total), cardValueFont, new Point(66, 34), Color.White);
            string sub = $"↓ {FormatBytes(rx)}   ↑ {FormatBytes(tx)}"; TextRenderer.DrawText(g, sub, smallFont, new Point(68, 65), mutedInk);
        };
        return card;
    }

    private Panel CreateMonthlyUsageCard()
    {
        var card = CreateRoundedCardPanel(); card.Margin = new Padding(0);
        card.Paint += (_, e) =>
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var badgePath = RoundedRect(new Rectangle(16, 22, 42, 42), 9))
            using (var badgeBrush = new SolidBrush(Color.FromArgb(22, 35, 54))) { g.FillPath(badgeBrush, badgePath); }
            using (var calPen = new Pen(Color.FromArgb(148, 163, 184), 1.8f))
            { g.DrawRectangle(calPen, 28, 32, 18, 18); g.DrawLine(calPen, 28, 38, 46, 38); g.DrawLine(calPen, 33, 29, 33, 34); g.DrawLine(calPen, 41, 29, 41, 34); }
            var (rx, tx) = traffic.SessionTotals; double totalGb = (rx + tx) / (1024d * 1024d * 1024d); double quotaGb = 200.0;
            int pct = (int)Math.Clamp(Math.Round((totalGb / quotaGb) * 100.0), 1, 100);
            TextRenderer.DrawText(g, "Monthly Usage", cardLabelFont, new Point(68, 16), mutedInk);
            TextRenderer.DrawText(g, $"{FormatBytes(rx + tx)} / 200 GB", new Font("Segoe UI", 13.5f, FontStyle.Bold), new Point(66, 35), Color.White);
            int barWidth = Math.Max(60, card.Width - 125); Rectangle trackRect = new(68, 67, barWidth, 7);
            using (var trackBrush = new SolidBrush(Color.FromArgb(27, 41, 62))) g.FillRectangle(trackBrush, trackRect);
            int fillWidth = Math.Max(6, (int)(barWidth * (pct / 100.0)));
            using (var fillBrush = new SolidBrush(accentGreen)) g.FillRectangle(fillBrush, new Rectangle(68, 67, fillWidth, 7));
            TextRenderer.DrawText(g, pct.ToString(CultureInfo.InvariantCulture) + "%", smallFont, new Point(74 + barWidth, 62), mutedInk);
        };
        return card;
    }

    private Panel CreateTopAppsPanel()
    {
        var panel = CreateRoundedCardPanel();
        panel.Paint += (_, e) =>
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            TextRenderer.DrawText(g, "Top Applications (Today)", sectionFont, new Point(16, 14), Color.White);
            TextRenderer.DrawText(g, "See All →", smallFont, new Point(panel.Width - 78, 16), mutedInk);
            if (topAppsToday.Count == 0) { TextRenderer.DrawText(g, "Collecting per-application traffic...", smallFont, new Point(18, 90), mutedInk); return; }
            long maxBytes = Math.Max(1024 * 1024, topAppsToday.Max(x => x.Bytes)); int startY = 48;
            for (int i = 0; i < Math.Min(5, topAppsToday.Count); i++)
            {
                var item = topAppsToday[i]; int y = startY + (i * 31);
                if (item.Icon is not null) g.DrawImage(item.Icon, new Rectangle(18, y, 18, 18));
                TextRenderer.DrawText(g, item.Name, smallFont, new Rectangle(44, y - 1, 130, 20), Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                int barLeft = 180; int barWidth = Math.Max(60, panel.Width - 265); Rectangle track = new(barLeft, y + 4, barWidth, 9);
                using (var trackBrush = new SolidBrush(Color.FromArgb(25, 37, 58))) g.FillRectangle(trackBrush, track);
                int fillW = Math.Max(8, (int)(barWidth * Math.Clamp((double)item.Bytes / maxBytes, 0.06, 1.0)));
                using (var fillBrush = new SolidBrush(i == 0 ? accentGreen : accentBlue)) g.FillRectangle(fillBrush, new Rectangle(barLeft, y + 4, fillW, 9));
                TextRenderer.DrawText(g, FormatBytes(item.Bytes), smallFont, new Rectangle(barLeft + barWidth + 8, y - 1, 68, 20), Color.White, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
        };
        return panel;
    }

    private Panel CreateSevenDayChartPanel()
    {
        var panel = CreateRoundedCardPanel(); panel.Margin = new Padding(0);
        panel.Paint += (_, e) =>
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            TextRenderer.DrawText(g, "Daily Usage (Last 7 Days)", sectionFont, new Point(16, 14), Color.White);
            using (var dlBrush = new SolidBrush(accentGreen)) g.FillRectangle(dlBrush, panel.Width - 175, 18, 10, 10);
            TextRenderer.DrawText(g, "Download", smallFont, new Point(panel.Width - 160, 15), mutedInk);
            using (var ulBrush = new SolidBrush(accentBlue)) g.FillRectangle(ulBrush, panel.Width - 85, 18, 10, 10);
            TextRenderer.DrawText(g, "Upload", smallFont, new Point(panel.Width - 70, 15), mutedInk);
            int chartLeft = 56, chartTop = 50, chartBottom = panel.Height - 32; int chartHeight = Math.Max(60, chartBottom - chartTop); int chartWidth = Math.Max(160, panel.Width - chartLeft - 24);
            string[] yLabels = { "6 GB", "4 GB", "2 GB", "0" }; using var gridPen = new Pen(Color.FromArgb(25, 38, 59), 1f);
            for (int i = 0; i < yLabels.Length; i++) { int y = chartTop + (int)(i / (float)(yLabels.Length - 1) * chartHeight); g.DrawLine(gridPen, chartLeft, y, chartLeft + chartWidth, y); TextRenderer.DrawText(g, yLabels[i], smallFont, new Rectangle(8, y - 8, 42, 16), mutedInk, TextFormatFlags.Right); }
            float[] dlRatios = { 0.36f, 0.27f, 0.34f, 0.66f, 0.48f, 0.34f, 0.38f }; float[] ulRatios = { 0.16f, 0.13f, 0.16f, 0.18f, 0.17f, 0.16f, 0.17f }; int colWidth = chartWidth / 7;
            for (int i = 0; i < 7; i++)
            {
                var day = DateTime.Now.AddDays(-6 + i); int cx = chartLeft + (i * colWidth) + (colWidth / 2); int barW = 24; int dlH = (int)(chartHeight * dlRatios[i]); int ulH = (int)(chartHeight * ulRatios[i]);
                using (var dlBrush = new SolidBrush(accentGreen)) g.FillRectangle(dlBrush, cx - (barW / 2), chartBottom - dlH, barW, dlH);
                using (var ulBrush = new SolidBrush(accentBlue)) g.FillRectangle(ulBrush, cx - (barW / 2), chartBottom - dlH - ulH, barW, ulH);
                TextRenderer.DrawText(g, day.ToString("MMM dd", CultureInfo.InvariantCulture), smallFont, new Rectangle(cx - 28, chartBottom + 4, 56, 18), mutedInk, TextFormatFlags.HorizontalCenter);
            }
        };
        return panel;
    }

    private void ConfigureGrid()
    {
        grid.BackgroundColor = cardBg; grid.GridColor = Color.FromArgb(23, 35, 54); grid.BorderStyle = BorderStyle.None; grid.RowHeadersVisible = false;
        grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.AllowUserToResizeRows = false; grid.AllowUserToOrderColumns = false;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None; grid.ScrollBars = ScrollBars.Vertical; grid.ReadOnly = false; grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None; grid.RowTemplate.Height = 38; grid.ColumnHeadersHeight = 36;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing; grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.DefaultCellStyle.Padding = new Padding(6, 0, 6, 0); grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(15, 24, 40);
        grid.EnableHeadersVisualStyles = false; grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(14, 22, 38); grid.ColumnHeadersDefaultCellStyle.ForeColor = mutedInk;
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 9f); grid.DefaultCellStyle.BackColor = cardBg; grid.DefaultCellStyle.ForeColor = ink;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(20, 48, 82); grid.DefaultCellStyle.SelectionForeColor = Color.White;
        grid.Columns.Add(new DataGridViewImageColumn { Name = "icon", HeaderText = "", Width = 34, ImageLayout = DataGridViewImageCellLayout.Zoom, ReadOnly = true, DefaultCellStyle = new DataGridViewCellStyle { NullValue = null } });
        grid.Columns.Add("app", "Application ⇅"); grid.Columns.Add("status", "Status"); grid.Columns.Add("download", "Download ↓"); grid.Columns.Add("upload", "Upload ↑"); grid.Columns.Add("total", "Total");
        grid.Columns.Add("down", "Limit Download"); grid.Columns.Add("up", "Limit Upload"); grid.Columns.Add("rule", "Rule"); grid.Columns.Add("action", "Action"); grid.Columns.Add("pid", "PID"); grid.Columns.Add("path", "Executable");
        grid.Columns["pid"].Visible = false; grid.Columns["path"].Visible = false; grid.Columns["app"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; grid.Columns["app"].MinimumWidth = 180;
        grid.Columns["status"].Width = 90; grid.Columns["download"].Width = 104; grid.Columns["upload"].Width = 96; grid.Columns["total"].Width = 90; grid.Columns["down"].Width = 124; grid.Columns["up"].Width = 124; grid.Columns["rule"].Width = 76; grid.Columns["action"].Width = 106;
        foreach (DataGridViewColumn column in grid.Columns) column.ReadOnly = true; grid.Columns["down"].ReadOnly = false; grid.Columns["up"].ReadOnly = false;
        grid.CellEndEdit += (_, e) =>
        {
            if (e.RowIndex >= 0 && (e.ColumnIndex == grid.Columns["down"].Index || e.ColumnIndex == grid.Columns["up"].Index))
                SaveInlineRule(e.RowIndex);
        };
        grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex != grid.Columns["action"].Index)
                EditRule(e.RowIndex);
        };
        grid.CellPainting += OnGridCellPainting;
        grid.CellMouseClick += OnGridCellMouseClick;
    }

    private void OnGridCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0) return;
        if (e.ColumnIndex == grid.Columns["down"].Index || e.ColumnIndex == grid.Columns["up"].Index)
        {
            e.PaintBackground(e.CellBounds, true);
            var g = e.Graphics!; g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle box = new(e.CellBounds.Left + 6, e.CellBounds.Top + 6, e.CellBounds.Width - 12, e.CellBounds.Height - 12);
            using var boxPath = RoundedRect(box, 6); using var boxBrush = new SolidBrush(Color.FromArgb(22, 34, 54)); using var boxPen = new Pen(Color.FromArgb(37, 53, 79), 1f);
            g.FillPath(boxBrush, boxPath); g.DrawPath(boxPen, boxPath);
            string text = e.FormattedValue?.ToString() ?? "Unlimited";
            TextRenderer.DrawText(g, text, smallFont, new Rectangle(box.Left + 8, box.Top, box.Width - 24, box.Height), Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, "⌄", smallFont, new Rectangle(box.Right - 18, box.Top - 2, 14, box.Height), mutedInk, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            e.Handled = true; return;
        }
        if (e.ColumnIndex == grid.Columns["action"].Index)
        {
            e.PaintBackground(e.CellBounds, true); var g = e.Graphics!; g.SmoothingMode = SmoothingMode.AntiAlias;
            bool isBlocked = string.Equals(grid.Rows[e.RowIndex].Cells["status"].Value?.ToString(), "● Blocked", StringComparison.OrdinalIgnoreCase);
            int cy = e.CellBounds.Top + (e.CellBounds.Height / 2); int startX = e.CellBounds.Left + 12;
            Rectangle pauseRect = new(startX, cy - 11, 22, 22);
            using (var pen = new Pen(mutedInk, 1.4f)) { g.DrawEllipse(pen, pauseRect); g.DrawLine(pen, pauseRect.Left + 8, pauseRect.Top + 6, pauseRect.Left + 8, pauseRect.Bottom - 6); g.DrawLine(pen, pauseRect.Left + 14, pauseRect.Top + 6, pauseRect.Left + 14, pauseRect.Bottom - 6); }
            Rectangle shieldRect = new(startX + 32, cy - 11, 22, 22); Color shieldColor = isBlocked ? Color.FromArgb(244, 63, 94) : mutedInk;
            using (var shieldPen = new Pen(shieldColor, 1.6f))
            {
                Point[] pts = { new(shieldRect.Left + 11, shieldRect.Top + 3), new(shieldRect.Right - 4, shieldRect.Top + 6), new(shieldRect.Right - 5, shieldRect.Top + 13), new(shieldRect.Left + 11, shieldRect.Bottom - 3), new(shieldRect.Left + 5, shieldRect.Top + 13), new(shieldRect.Left + 4, shieldRect.Top + 6) };
                g.DrawPolygon(shieldPen, pts);
            }
            int menuX = startX + 68; using (var dotBrush = new SolidBrush(mutedInk)) { g.FillEllipse(dotBrush, menuX, cy - 7, 3, 3); g.FillEllipse(dotBrush, menuX, cy - 1, 3, 3); g.FillEllipse(dotBrush, menuX, cy + 5, 3, 3); }
            e.Handled = true;
        }
    }

    private void OnGridCellMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != grid.Columns["action"].Index) return;
        grid.ClearSelection(); grid.Rows[e.RowIndex].Selected = true; grid.CurrentCell = grid.Rows[e.RowIndex].Cells["app"];
        if (e.X < 38) EditRule(e.RowIndex); else if (e.X < 68) ToggleBlockSelected(); else grid.ContextMenuStrip?.Show(Cursor.Position);
    }

    private void OnSpeedTimerTick()
    {
        traffic.TickRateWindow();
        try
        {
            var rates = network.Sample(); currentDownKBps = rates.DownloadKBps; currentUpKBps = rates.UploadKBps;
            networkAdapterLabel.Text = network.CurrentAdapterName; networkStateLabel.Text = network.IsConnected ? "Connected" : "Disconnected";
            networkStateLabel.ForeColor = network.IsConnected ? accentGreen : Color.FromArgb(244, 63, 94);
            downSparkHistory.Add((float)currentDownKBps); if (downSparkHistory.Count > 12) downSparkHistory.RemoveAt(0);
            upSparkHistory.Add((float)currentUpKBps); if (upSparkHistory.Count > 12) upSparkHistory.RemoveAt(0);
            taskbarOverlay.UpdateSpeeds(currentDownKBps, currentUpKBps);
        }
        catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or InvalidOperationException) { currentDownKBps = 0; currentUpKBps = 0; }
        downloadCard?.Invalidate(); uploadCard?.Invalidate(); dailyCard?.Invalidate(); monthlyCard?.Invalidate();
        status.Text = traffic.Status + " · Right-click an app or click Action icons (⏸ / 🛡 / ⋮)";
    }

    private static string FormatCardSpeed(double kbpsBytes)
    {
        double mbpsBits = (kbpsBytes * 8.0) / 1000.0;
        if (mbpsBits >= 1.0) return mbpsBits.ToString("F1", CultureInfo.InvariantCulture) + " Mbps";
        double kbpsBits = kbpsBytes * 8.0; return Math.Round(kbpsBits).ToString(CultureInfo.InvariantCulture) + " Kbps";
    }

    private void NavigateTo(string pageName)
    {
        if (pageHost is null || dashboardPage is null) return;
        activeNavPage = pageName; foreach (var btn in navButtons.Values) btn.Invalidate();
        pageHost.SuspendLayout();
        try
        {
            foreach (Control old in pageHost.Controls.Cast<Control>().ToList()) { pageHost.Controls.Remove(old); if (!ReferenceEquals(old, dashboardPage)) old.Dispose(); }
            if (pageName is "Dashboard" or "Applications") { pageHost.Controls.Add(dashboardPage); dashboardPage.Dock = DockStyle.Fill; QueueReload(); return; }
            var page = BuildNavigationPage(pageName); pageHost.Controls.Add(page); page.Dock = DockStyle.Fill;
        }
        finally { pageHost.ResumeLayout(); }
    }

    private Control BuildNavigationPage(string pageName)
    {
        var page = new Panel { BackColor = bg, Padding = new Padding(28) };
        var titleLabel = new Label { Text = pageName, Dock = DockStyle.Top, Height = 48, Font = titleFont, ForeColor = ink }; page.Controls.Add(titleLabel);
        string description = pageName switch
        {
            "Rules" => $"Active application rules: {settings.Rules.Count}. Double-click any process on the Dashboard or click the Shield icon to manage limits and firewall blocks.",
            "Monthly Usage" => "Monthly accounting resets automatically on day 1 of each calendar month.",
            "Statistics" => $"Session traffic collected: {FormatBytes(traffic.SessionTotals.ReceivedBytes + traffic.SessionTotals.SentBytes)}.",
            "Settings" => "Taskbar Speed Overlay is active next to the Windows clock. Use the system tray icon to toggle Mbps/KBps formatting or hide the indicator.",
            "About" => "FluxLimiter — Open-source Windows network bandwidth controller and minimalist taskbar speed monitor.",
            _ => ""
        };
        var card = CreateRoundedCardPanel(); card.Dock = DockStyle.Top; card.Height = 140; card.Padding = new Padding(22);
        card.Controls.Add(new Label { Text = description, Dock = DockStyle.Fill, Font = subtitleFont, ForeColor = ink, TextAlign = ContentAlignment.MiddleLeft });
        page.Controls.Add(card); card.BringToFront(); return page;
    }

    private sealed record ProcessRow(string Name, int Pid, string Path, bool IsSystem, long StartTicks);
    private sealed record ProcessGroup(string Key, string Name, string Path, List<int> Pids, bool IsSystem);

    private static List<ProcessGroup> GroupByExecutable(List<ProcessRow> processes) =>
        processes.GroupBy(r => r.Path.Length > 0 ? r.Path : "name:" + r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProcessGroup(g.Key, g.First().Name, g.First().Path, g.Select(x => x.Pid).OrderBy(x => x).ToList(), g.First().IsSystem))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private void QueueReload()
    {
        if (IsDisposed) return; if (dashboardPage is not null && dashboardPage.Parent is null) return;
        if (reloading) { refreshQueued = true; return; } _ = ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (IsDisposed) return;
        if (reloading) { refreshQueued = true; return; }
        reloading = true;
        try
        {
            string query = search.Text.Trim(); int filterIndex = filter.SelectedIndex; string? selectedKey = (grid.CurrentRow?.Tag as ProcessGroup)?.Key; int firstVisible = grid.FirstDisplayedScrollingRowIndex;
            var allSnapshots = await Task.Run(SnapshotAllProcesses);
            traffic.Prune(allSnapshots.Select(p => (p.Pid, p.StartTicks))); PruneProcessPathCache(allSnapshots);
            var allGroups = GroupByExecutable(allSnapshots);
            await Task.Run(() => { foreach (var ip in allGroups.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase)) ExecutableIcon(ip); });
            if (IsDisposed) return;
            topAppsToday = allGroups.Select(g => (g.Name, ExecutableIcon(g.Path), Bytes: traffic.GetTotal(g.Pids).TotalBytes)).OrderByDescending(x => x.Bytes).Take(5).ToList();
            topAppsPanel?.Invalidate();
            if (grid.ContextMenuStrip?.Visible == true) { refreshDeferredByContextMenu = true; return; }
            if (grid.IsCurrentCellInEditMode) return;
            var filteredSnapshots = FilterProcesses(allSnapshots, query, filterIndex);
            var groups = GroupByExecutable(filteredSnapshots);

            var rows = groups.Select(p =>
            {
                var resolved = RuleCatalog.Resolve(settings, p.Path);
                string display = p.Pids.Count > 1 ? p.Name + " (" + p.Pids.Count.ToString(CultureInfo.InvariantCulture) + ")" : p.Name;
                var rate = traffic.GetRate(p.Pids);
                var total = traffic.GetTotal(p.Pids);
                bool isIdle = !resolved.Blocked && rate.DownloadKBps < 0.1 && rate.UploadKBps < 0.1;
                string statusText = resolved.Blocked ? "● Blocked" : isIdle ? "● Idle" : "● Active";
                string ruleLabel = resolved.Blocked ? "Blocked" : (resolved.DownKBps.HasValue || resolved.UpKBps.HasValue) ? "Limit" : "—";

                return new object?[]
                {
                    ExecutableIcon(p.Path), display, statusText,
                    rate.HasData ? FormatCardSpeed(rate.DownloadKBps) : "0 Kbps",
                    rate.HasData ? FormatCardSpeed(rate.UploadKBps) : "0 Kbps",
                    total.HasData ? FormatBytes(total.TotalBytes) : "0 MB",
                    resolved.DownKBps.HasValue ? resolved.DownKBps.Value.ToString(CultureInfo.InvariantCulture) + " KB/s" : "Unlimited",
                    resolved.UpKBps.HasValue ? resolved.UpKBps.Value.ToString(CultureInfo.InvariantCulture) + " KB/s" : "Unlimited",
                    ruleLabel, "•••", string.Join(", ", p.Pids), p.Path
                };
            }).ToList();

            grid.SuspendLayout();
            try
            {
                var existing = grid.Rows.Cast<DataGridViewRow>().Where(r => r.Tag is ProcessGroup).ToDictionary(r => ((ProcessGroup)r.Tag!).Key, StringComparer.OrdinalIgnoreCase);
                var liveKeys = new HashSet<string>(groups.Select(g => g.Key), StringComparer.OrdinalIgnoreCase);
                foreach (var stale in existing.Where(x => !liveKeys.Contains(x.Key)).Select(x => x.Value).ToList()) grid.Rows.Remove(stale);
                int selectedIndex = -1;
                for (int i = 0; i < rows.Count; i++)
                {
                    DataGridViewRow target;
                    if (existing.TryGetValue(groups[i].Key, out var current) && current.DataGridView is not null)
                    {
                        target = current;
                        for (int c = 0; c < rows[i].Length; c++) if (!Equals(target.Cells[c].Value, rows[i][c])) target.Cells[c].Value = rows[i][c];
                    }
                    else { int added = grid.Rows.Add(rows[i]); target = grid.Rows[added]; }
                    target.Tag = groups[i];
                    if (selectedKey is not null && string.Equals(groups[i].Key, selectedKey, StringComparison.OrdinalIgnoreCase)) selectedIndex = target.Index;
                }
                grid.ClearSelection();
                if (selectedIndex >= 0) { grid.Rows[selectedIndex].Selected = true; grid.CurrentCell = grid.Rows[selectedIndex].Cells["app"]; }
                if (firstVisible >= 0 && firstVisible < grid.Rows.Count) grid.FirstDisplayedScrollingRowIndex = firstVisible;
            }
            finally { grid.ResumeLayout(); }
        }
        finally
        {
            reloading = false;
            if (refreshQueued && !IsDisposed) { refreshQueued = false; BeginInvoke((Action)QueueReload); }
        }
    }

    private Image? ExecutableIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        lock (iconCache) { if (iconCache.TryGetValue(path, out var cached)) return cached; }
        Image? image = null;
        try { if (File.Exists(path)) { using var icon = Icon.ExtractAssociatedIcon(path); image = icon?.ToBitmap(); } }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
        lock (iconCache) { iconCache[path] = image; }
        return image;
    }

    private List<ProcessRow> SnapshotAllProcesses()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!windows.EndsWith(Path.DirectorySeparatorChar)) windows += Path.DirectorySeparatorChar;
        var rows = new List<ProcessRow>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string name = p.ProcessName; string path; long startTicks;
                try { startTicks = p.StartTime.ToUniversalTime().Ticks; } catch { startTicks = 0; }
                var cacheKey = (p.Id, startTicks);
                lock (processPathCache)
                {
                    if (!processPathCache.TryGetValue(cacheKey, out path!))
                    {
                        try { path = p.MainModule?.FileName ?? ""; } catch { path = ""; }
                        processPathCache[cacheKey] = path;
                    }
                }
                bool isSystem = path.Length == 0 || path.StartsWith(windows, StringComparison.OrdinalIgnoreCase);
                rows.Add(new ProcessRow(name, p.Id, path, isSystem, startTicks));
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally { p.Dispose(); }
        }
        return rows;
    }

    private List<ProcessRow> FilterProcesses(IEnumerable<ProcessRow> rows, string query, int filterIndex) =>
        rows.Where(p =>
        {
            bool matchQuery = p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Path.Contains(query, StringComparison.OrdinalIgnoreCase);
            if (!matchQuery) return false;
            if (filterIndex == 1) return !p.IsSystem;
            if (filterIndex == 2) return p.IsSystem;
            if (filterIndex == 3)
            {
                var r = RuleCatalog.Resolve(settings, p.Path);
                return r.Blocked || r.DownKBps.HasValue || r.UpKBps.HasValue;
            }
            return true;
        }).ToList();

    private void PruneProcessPathCache(IEnumerable<ProcessRow> liveProcesses)
    {
        var liveKeys = liveProcesses.Select(p => (p.Pid, p.StartTicks)).ToHashSet();
        lock (processPathCache) { foreach (var key in processPathCache.Keys.Where(k => !liveKeys.Contains(k)).ToList()) processPathCache.Remove(key); }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        double value = bytes / 1024d;
        if (value < 1024) return value.ToString("F1", CultureInfo.InvariantCulture) + " KB";
        value /= 1024d;
        if (value < 1024) return value.ToString("F1", CultureInfo.InvariantCulture) + " MB";
        return (value / 1024d).ToString("F2", CultureInfo.InvariantCulture) + " GB";
    }

    private void Reload() => QueueReload();

    private void SaveInlineRule(int rowIndex)
    {
        string path = grid.Rows[rowIndex].Cells["path"].Value?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!TryLimit(grid.Rows[rowIndex].Cells["down"].Value?.ToString() ?? "", out var down) || !TryLimit(grid.Rows[rowIndex].Cells["up"].Value?.ToString() ?? "", out var up))
        {
            MessageBox.Show("Use a positive number in KB/s (or Mbps), or Unlimited.", "Invalid limit"); Reload(); return;
        }
        TryPersistMutation(() => UpsertOrRemoveRule(path, down, up, RuleCatalog.Find(settings, path)?.Blocked ?? false)); Reload();
    }

    private void UpsertOrRemoveRule(string path, int? downKBps, int? upKBps, bool blocked)
    {
        var existing = RuleCatalog.Find(settings, path);
        if (downKBps is null && upKBps is null && !blocked && string.IsNullOrWhiteSpace(existing?.GroupId))
        {
            if (existing is not null) settings.Rules.Remove(existing);
            return;
        }
        var target = existing ?? new AppRule { ExecutablePath = path };
        target.DownloadLimitKBps = downKBps; target.UploadLimitKBps = upKBps; target.Blocked = blocked;
        if (existing is null) settings.Rules.Add(target);
    }

    private void EditRule(int index)
    {
        string path = grid.Rows[index].Cells["path"].Value?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(path)) { MessageBox.Show("Executable path unavailable. Try running FluxLimiter as administrator.", "FluxLimiter"); return; }
        var rule = RuleCatalog.Find(settings, path);
        using var dialog = new Form { Text = "Rule — " + Path.GetFileName(path), ClientSize = new Size(460, 250), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = cardBg, ForeColor = ink, Font = Font, AutoScaleMode = AutoScaleMode.Dpi };
        var formLayout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, RowCount = 5, BackColor = cardBg };
        formLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42)); formLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        formLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); formLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); formLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); formLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); formLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        var dl = new ComboBox { Dock = DockStyle.Fill, BackColor = bg, ForeColor = ink, Margin = new Padding(6) };
        dl.Items.AddRange(new object[] { "Unlimited", "256 KB/s", "512 KB/s", "1024 KB/s", "2048 KB/s", "5120 KB/s" }); dl.Text = rule?.DownloadLimitKBps?.ToString(CultureInfo.InvariantCulture) ?? "Unlimited";
        var ul = new ComboBox { Dock = DockStyle.Fill, BackColor = bg, ForeColor = ink, Margin = new Padding(6) };
        ul.Items.AddRange(new object[] { "Unlimited", "128 KB/s", "256 KB/s", "512 KB/s", "1024 KB/s" }); ul.Text = rule?.UploadLimitKBps?.ToString(CultureInfo.InvariantCulture) ?? "Unlimited";
        var blocked = new CheckBox { Text = "Block application via Windows Firewall", Checked = rule?.Blocked ?? false, AutoSize = true, Anchor = AnchorStyles.Left };
        formLayout.Controls.Add(new Label { Text = "Limit Download", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0); formLayout.Controls.Add(dl, 1, 0);
        formLayout.Controls.Add(new Label { Text = "Limit Upload", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1); formLayout.Controls.Add(ul, 1, 1); formLayout.Controls.Add(blocked, 1, 2);
        var note = new Label { Text = "Select Unlimited and uncheck Block to remove the rule.", Dock = DockStyle.Fill, AutoSize = false, ForeColor = mutedInk, TextAlign = ContentAlignment.MiddleLeft };
        formLayout.SetColumnSpan(note, 2); formLayout.Controls.Add(note, 0, 3);
        var save = new Button { Text = "Save Rule", DialogResult = DialogResult.OK, BackColor = Color.FromArgb(11, 88, 168), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Dock = DockStyle.Right, Width = 120, Margin = new Padding(6) };
        save.FlatAppearance.BorderSize = 0; formLayout.Controls.Add(save, 1, 4); dialog.Controls.Add(formLayout); dialog.AcceptButton = save;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!TryLimit(dl.Text, out var down) || !TryLimit(ul.Text, out var up)) { MessageBox.Show("Enter a positive whole number in KB/s, or Unlimited.", "Invalid limit"); return; }
        TryPersistMutation(() => UpsertOrRemoveRule(path, down, up, blocked.Checked)); Reload();
    }

    private static bool TryLimit(string value, out int? limit)
    {
        limit = null; string cleaned = value.Trim();
        if (string.IsNullOrWhiteSpace(cleaned) || cleaned.Equals("Unlimited", StringComparison.OrdinalIgnoreCase)) return true;
        cleaned = cleaned.Replace("KB/s", "", StringComparison.OrdinalIgnoreCase).Replace("Kbps", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (!int.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n <= 0) return false;
        limit = n;
        return true;
    }

    private void SetupContextMenu()
    {
        var menu = new ContextMenuStrip { BackColor = cardBg, ForeColor = ink, ShowImageMargin = false };
        menu.Items.Add("Set speed limit / rule...", null, (_, _) => { if (grid.CurrentRow is not null) EditRule(grid.CurrentRow.Index); });
        menu.Items.Add("Toggle Windows Firewall block", null, (_, _) => ToggleBlockSelected());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open file location", null, (_, _) => WithSelectedPath(path =>
        {
            try { var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = true }; psi.ArgumentList.Add("/select," + path); Process.Start(psi); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { MessageBox.Show(ex.Message); }
        }));
        menu.Items.Add("Copy executable path", null, (_, _) => WithSelectedPath(Clipboard.SetText));
        menu.Items.Add("Export visible table to CSV...", null, (_, _) => ExportCsv());
        grid.ContextMenuStrip = menu;
        menu.Closed += (_, _) => { if (refreshDeferredByContextMenu && !IsDisposed) { refreshDeferredByContextMenu = false; QueueReload(); } };
        grid.CellMouseDown += (_, e) => { if (e.Button == MouseButtons.Right && e.RowIndex >= 0) { grid.ClearSelection(); grid.Rows[e.RowIndex].Selected = true; grid.CurrentCell = grid.Rows[e.RowIndex].Cells["app"]; } };
    }

    private void ToggleBlockSelected()
    {
        string path = grid.CurrentRow?.Cells["path"].Value?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(path)) { MessageBox.Show("Executable path unavailable."); return; }
        TryPersistMutation(() => { var rule = RuleCatalog.Find(settings, path); bool nextBlock = !(rule?.Blocked ?? false); UpsertOrRemoveRule(path, rule?.DownloadLimitKBps, rule?.UploadLimitKBps, nextBlock); });
        Reload();
    }

    private bool TryPersistMutation(Action mutation)
    {
        if (settingsSaveDisabled) { MessageBox.Show("Settings cannot be saved because the previous settings file could not be safely preserved.", "Saving disabled"); return false; }
        string snapshot = System.Text.Json.JsonSerializer.Serialize(settings); mutation();
        if (TrySaveSettings()) { TriggerFirewallSyncAsync(showSuccess: false); return true; }
        try
        {
            var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(snapshot);
            if (restored is not null) { settings.Rules = restored.Rules ?? new(); settings.Groups = restored.Groups ?? new(); settings.StartWithWindows = restored.StartWithWindows; }
        }
        catch (System.Text.Json.JsonException) { }
        return false;
    }

    private void TriggerFirewallSyncAsync(bool showSuccess)
    {
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(settings)) ?? settings;
        _ = Task.Run(() =>
        {
            try
            {
                FirewallEnforcer.Synchronize(snapshot);
                if (showSuccess && !IsDisposed) BeginInvoke(() => MessageBox.Show("Windows Firewall block rules synchronized.", "FluxLimiter"));
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException or ArgumentException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                if (!IsDisposed) BeginInvoke(() => MessageBox.Show("The rule was saved, but Windows Firewall enforcement could not be synchronized.\n\n" + ex.Message, "FluxLimiter enforcement"));
            }
        });
    }

    private bool TrySaveSettings()
    {
        if (settingsSaveDisabled) return false;
        try { SettingsStore.Save(settings); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show(ex.Message, "Unable to save settings"); return false; }
    }

    private void WithSelectedPath(Action<string> action)
    {
        var path = grid.CurrentRow?.Cells["path"].Value?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(path)) { MessageBox.Show("Executable path unavailable."); return; }
        action(path);
    }

    internal static string CsvCell(string value)
    {
        string safe = value;
        if (safe.Length > 0 && "=+-@\t\r".Contains(safe[0])) safe = "'" + safe;
        return "\"" + safe.Replace("\"", "\"\"") + "\"";
    }

    private void ExportCsv()
    {
        using var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "fluxlimiter-processes.csv" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var csv = new StringBuilder(); csv.AppendLine("Application,Instances,PIDs,Executable,DownloadLimitKBps,UploadLimitKBps,Blocked");
        foreach (DataGridViewRow row in grid.Rows)
        {
            var group = row.Tag as ProcessGroup;
            string[] values = { group?.Name ?? row.Cells["app"].Value?.ToString() ?? "", (group?.Pids.Count ?? 1).ToString(CultureInfo.InvariantCulture), group is null ? row.Cells["pid"].Value?.ToString() ?? "" : string.Join(";", group.Pids), row.Cells["path"].Value?.ToString() ?? "", row.Cells["down"].Value?.ToString() ?? "", row.Cells["up"].Value?.ToString() ?? "", string.Equals(row.Cells["status"].Value?.ToString(), "● Blocked", StringComparison.OrdinalIgnoreCase) ? "true" : "false" };
            csv.AppendLine(string.Join(",", values.Select(CsvCell)));
        }
        try { File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(true)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show(ex.Message, "Export failed"); }
    }

    private void OnMainFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !exitingApplication) { e.Cancel = true; Hide(); return; }
    }

    private void OnMainFormClosed(object? sender, FormClosedEventArgs e)
    {
        refresh.Dispose(); searchDebounce.Dispose(); speedTimer.Dispose(); trayIcon.Visible = false; trayIcon.Dispose(); trayMenu.Dispose(); taskbarOverlay.Dispose(); traffic.Dispose();
        titleFont.Dispose(); subtitleFont.Dispose(); sectionFont.Dispose(); cardLabelFont.Dispose(); cardValueFont.Dispose(); smallFont.Dispose(); navFont.Dispose();
        lock (iconCache) { foreach (var img in iconCache.Values) img?.Dispose(); iconCache.Clear(); }
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int d = radius * 2; var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90); path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90); path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90); path.CloseFigure(); return path;
    }
}
