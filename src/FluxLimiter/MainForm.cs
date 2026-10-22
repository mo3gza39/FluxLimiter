using System.Diagnostics;
using System.Drawing;
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
    private readonly Label speed = new();
    private readonly Label uploadSpeed = new();
    private readonly BufferedDataGridView grid = new();
    private readonly TextBox search = new();
    private readonly ComboBox filter = new();
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 5000 };
    private bool reloading;
    private bool refreshQueued;
    private bool refreshDeferredByContextMenu;
    private bool settingsSaveDisabled;
    private readonly System.Windows.Forms.Timer searchDebounce = new() { Interval = 300 };
    private readonly System.Windows.Forms.Timer speedTimer = new() { Interval = 1000 };
    private readonly Label status = new();
    private readonly Label processCount = new();
    private readonly Label ruleCount = new();
    private readonly Label dailyUsage = new();
    private readonly Label monthlyUsage = new();
    private readonly Dictionary<string, Button> navButtons = new(StringComparer.OrdinalIgnoreCase);
    private Panel? dashboardPage;
    private Panel? pageHost;
    private readonly Color bg = Color.FromArgb(16, 22, 34);
    private readonly Color panel = Color.FromArgb(25, 34, 49);
    private readonly Color ink = Color.FromArgb(229, 237, 246);
    private readonly Dictionary<(int Pid, long StartTicks), string> processPathCache = new();

    public MainForm()
    {
        Text = "FluxLimiter — Network Control";
        Width = 1480;
        Height = 900;
        MinimumSize = new Size(1180, 720);
        BackColor = bg;
        ForeColor = ink;
        Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;

        var top = new Panel { Dock = DockStyle.Top, Height = 88, BackColor = bg, Padding = new Padding(0, 6, 0, 6) };
        var headerLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, BackColor = bg };
        headerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var headingRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = bg };
        headingRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        headingRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        headingRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        var headingText = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = bg };
        var title = new Label { Text = "FluxLimiter", Font = new Font("Segoe UI Semibold", 26), ForeColor = Color.FromArgb(91, 225, 184), AutoSize = true, Margin = new Padding(0) };
        var subtitle = new Label { Text = "Network control dashboard", AutoSize = true, ForeColor = Color.FromArgb(155, 171, 190), Margin = new Padding(2, 4, 0, 0) };
        headingText.Controls.Add(title); headingText.Controls.Add(subtitle);
        processCount.Dock = DockStyle.Fill; processCount.Margin = new Padding(8); processCount.Font = new Font("Segoe UI Semibold", 13); processCount.ForeColor = ink; processCount.TextAlign = ContentAlignment.MiddleCenter; processCount.BackColor = bg;
        ruleCount.Dock = DockStyle.Fill; ruleCount.Margin = new Padding(8); ruleCount.Font = new Font("Segoe UI Semibold", 13); ruleCount.ForeColor = ink; ruleCount.TextAlign = ContentAlignment.MiddleCenter; ruleCount.BackColor = bg;
        headingRow.Controls.Add(headingText, 0, 0); headingRow.Controls.Add(processCount, 1, 0); headingRow.Controls.Add(ruleCount, 2, 0);

        var toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = panel };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
        search.Dock = DockStyle.Fill; search.Margin = new Padding(0, 8, 12, 8);
        search.PlaceholderText = "Search application or executable...";
        search.TextChanged += (_, _) => { searchDebounce.Stop(); searchDebounce.Start(); };
        filter.Dock = DockStyle.Fill; filter.Margin = new Padding(0, 8, 12, 8);
        filter.DropDownStyle = ComboBoxStyle.DropDownList;
        filter.Items.AddRange(new object[] { "All processes", "User processes", "System processes" });
        filter.SelectedIndex = 0;
        filter.SelectedIndexChanged += (_, _) => QueueReload();
        var export = Button("Export CSV", 0, 0, 0, (_, _) => ExportCsv()); export.Dock = DockStyle.Fill; export.Margin = new Padding(0, 8, 12, 8);
        var reload = Button("Refresh", 0, 0, 0, (_, _) => Reload()); reload.Dock = DockStyle.Fill; reload.Margin = new Padding(0, 8, 0, 8);
        toolbar.Controls.Add(search, 0, 0); toolbar.Controls.Add(filter, 1, 0); toolbar.Controls.Add(export, 2, 0); toolbar.Controls.Add(reload, 3, 0);
        headerLayout.Controls.Add(headingRow, 0, 0);
        top.Controls.Add(headerLayout);

        grid.Dock = DockStyle.Fill;
        grid.BackgroundColor = bg;
        grid.GridColor = Color.FromArgb(45, 58, 74);
        grid.BorderStyle = BorderStyle.None;
        grid.RowHeadersVisible = false;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.AllowUserToOrderColumns = false;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        grid.ScrollBars = ScrollBars.Vertical;
        grid.ReadOnly = false;
        grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        grid.RowTemplate.Height = 34;
        grid.ColumnHeadersHeight = 38;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.DefaultCellStyle.Padding = new Padding(8, 0, 8, 0);
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(19, 27, 40);
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = panel;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = ink;
        grid.DefaultCellStyle.BackColor = bg;
        grid.DefaultCellStyle.ForeColor = ink;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(38, 99, 100);
        grid.DefaultCellStyle.SelectionForeColor = Color.White;
        grid.Columns.Add(new DataGridViewImageColumn { Name = "icon", HeaderText = "", Width = 34, ImageLayout = DataGridViewImageCellLayout.Zoom, ReadOnly = true, DefaultCellStyle = new DataGridViewCellStyle { NullValue = null } });
        grid.Columns.Add("app", "Application");
        grid.Columns.Add("status", "Status");
        grid.Columns.Add("download", "Download ↓");
        grid.Columns.Add("upload", "Upload ↑");
        grid.Columns.Add("total", "Total");
        grid.Columns.Add("down", "Limit ↓ (saved)");
        grid.Columns.Add("up", "Limit ↑ (saved)");
        grid.Columns.Add("rule", "Rule");
        grid.Columns.Add("action", "Action");
        grid.Columns.Add("pid", "PID");
        grid.Columns.Add("path", "Executable");
        grid.Columns["pid"].Visible = false;
        grid.Columns["path"].Visible = false;
        grid.Columns["app"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        grid.Columns["app"].MinimumWidth = 190;
        grid.Columns["status"].Width = 76;
        grid.Columns["download"].Width = 100;
        grid.Columns["upload"].Width = 92;
        grid.Columns["total"].Width = 86;
        grid.Columns["down"].Width = 132;
        grid.Columns["up"].Width = 132;
        grid.Columns["down"].DefaultCellStyle.ForeColor = Color.FromArgb(160, 174, 190);
        grid.Columns["up"].DefaultCellStyle.ForeColor = Color.FromArgb(160, 174, 190);
        grid.Columns["rule"].Width = 82;
        grid.Columns["action"].Width = 78;
        foreach (DataGridViewColumn column in grid.Columns) column.ReadOnly = true;
        grid.Columns["down"].ReadOnly = false;
        grid.Columns["up"].ReadOnly = false;
        grid.CellEndEdit += (_, e) => { if (e.RowIndex >= 0 && (e.ColumnIndex == grid.Columns["down"].Index || e.ColumnIndex == grid.Columns["up"].Index)) SaveInlineRule(e.RowIndex); };
        grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) EditRule(e.RowIndex); };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 68, BackColor = panel };
        speed.SetBounds(28, 37, 900, 24);
        speed.ForeColor = Color.FromArgb(91, 225, 184);
        speed.Text = "Sampling...";
        uploadSpeed.Text = "Sampling...";
        bottom.Controls.Add(speed);
        status.AutoSize = true;
        status.Left = 28;
        status.Top = 12;
        status.ForeColor = Color.FromArgb(170, 185, 200);
        bottom.Controls.Add(status);
        // Approved product shell: persistent navigation + dashboard composition.
        var shell = new Panel { Dock = DockStyle.Fill, BackColor = bg };
        var sidebar = new Panel { Dock = DockStyle.Left, Width = 218, BackColor = Color.FromArgb(13, 20, 30) };
        sidebar.Controls.Add(new Label { Text = "FluxLimiter", Left = 22, Top = 24, Width = 180, Height = 40, Font = new Font("Segoe UI Semibold", 22), ForeColor = Color.FromArgb(91, 225, 184) });
        sidebar.Controls.Add(new Label { Text = "NETWORK CONTROL", Left = 24, Top = 65, Width = 170, Height = 20, Font = new Font("Segoe UI Semibold", 8), ForeColor = Color.FromArgb(115, 132, 151) });
        string[] nav = { "Dashboard", "Applications", "Rules", "Monthly Usage", "Statistics", "Settings", "About" };
        for (int i = 0; i < nav.Length; i++)
        {
            var n = new Button { Text = nav[i], Image = UiIcon(nav[i]), ImageAlign = ContentAlignment.MiddleLeft, TextImageRelation = TextImageRelation.ImageBeforeText, Left = 16, Top = 112 + (i * 50), Width = 186, Height = 40, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16, 0, 0, 0), BackColor = i == 0 ? Color.FromArgb(27, 37, 52) : sidebar.BackColor, ForeColor = i == 0 ? ink : Color.FromArgb(145, 159, 177) };
            n.FlatAppearance.BorderSize = 0;
            string pageName = nav[i];
            n.Click += (_, _) => NavigateTo(pageName);
            navButtons[pageName] = n;
            sidebar.Controls.Add(n);
        }

        var dashboard = new Panel { Dock = DockStyle.Fill, BackColor = bg, AutoScroll = true, Padding = new Padding(22, 16, 22, 18) };
        dashboardPage = dashboard;
        top.Dock = DockStyle.Fill; top.Margin = new Padding(0, 0, 0, 16);
        title.Text = "Dashboard"; title.ForeColor = ink;
        subtitle.Text = "Monitor and control application network activity";
        processCount.Text = "Applications"; ruleCount.Text = "Saved rules";
        dailyUsage.Text = "—"; monthlyUsage.Text = "—";

        var summary = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = bg, Margin = new Padding(0), Padding = new Padding(0) };
        for (int i = 0; i < 4; i++) summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        summary.Controls.Add(DashboardCard("DOWNLOAD", speed, "Live device traffic"), 0, 0);
        summary.Controls.Add(DashboardCard("UPLOAD", uploadSpeed, "Live device traffic"), 1, 0);
        summary.Controls.Add(DashboardCard("DAILY USAGE", dailyUsage, "Collector not connected"), 2, 0);
        summary.Controls.Add(DashboardCard("MONTHLY USAGE", monthlyUsage, "Collector not connected"), 3, 0);

        // "Top Applications" / "Daily Usage" panels are hidden until the usage collector exists (see DashboardPlaceholder).

        var active = new Panel { Dock = DockStyle.Fill, BackColor = panel, Margin = new Padding(0, 16, 0, 0), Padding = new Padding(20, 8, 20, 18) };
        var activeHeader = new TableLayoutPanel { Dock = DockStyle.Top, Height = 48, ColumnCount = 2, RowCount = 1, BackColor = panel, Padding = new Padding(0) };
        activeHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        activeHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 640));
        activeHeader.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        toolbar.RowStyles.Clear();
        toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        toolbar.Dock = DockStyle.Fill;
        toolbar.Padding = new Padding(0);
        activeHeader.Controls.Add(new Label { Text = "Active Applications", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI Semibold", 15), ForeColor = ink }, 0, 0);
        activeHeader.Controls.Add(toolbar, 1, 0);
        var limitsNote = new Label
        {
            Text = "Speed limits are saved but not enforced yet   ·   Block uses Windows Firewall   ·   Right-click an app for options",
            Dock = DockStyle.Top, Height = 30, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 9), ForeColor = Color.FromArgb(226, 184, 105)
        };
        active.Controls.Add(limitsNote);   // added before the header so the header docks above it
        active.Controls.Add(activeHeader);
        grid.Dock = DockStyle.Fill;
        active.Controls.Add(grid);
        grid.BringToFront();

        bottom.Visible = false;
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = bg };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.Controls.Add(top, 0, 0);
        content.Controls.Add(summary, 0, 1);
        content.Controls.Add(active, 0, 2);
        dashboard.Controls.Add(content);
        pageHost = new Panel { Dock = DockStyle.Fill, BackColor = bg };
        pageHost.Controls.Add(dashboard);
        shell.Controls.Add(pageHost);
        shell.Controls.Add(sidebar);
        Controls.Add(shell);
        NavigateTo("Dashboard");
        searchDebounce.Tick += (_, _) => { searchDebounce.Stop(); QueueReload(); };
        refresh.Tick += (_, _) => { if (!grid.IsCurrentCellInEditMode && grid.ContextMenuStrip?.Visible != true) QueueReload(); };
        refresh.Start();
        speedTimer.Tick += (_, _) => { try { var rates = network.Sample(); speed.Text = $"↓ {rates.DownloadKBps.ToString("F1", CultureInfo.InvariantCulture)} KB/s"; uploadSpeed.Text = $"↑ {rates.UploadKBps.ToString("F1", CultureInfo.InvariantCulture)} KB/s"; } catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or InvalidOperationException) { speed.Text = "Unavailable"; uploadSpeed.Text = "Unavailable"; } };
        speedTimer.Start();
        Resize += (_, _) => UpdateSamplingState();
        FormClosed += (_, _) => traffic.Dispose();
        _ = traffic.StartAsync();
        SetupContextMenu();
        if (!string.IsNullOrWhiteSpace(SettingsStore.LastLoadWarning))
            settingsSaveDisabled = SettingsStore.LastLoadWarning.Contains("Saving is disabled", StringComparison.OrdinalIgnoreCase);
        Shown += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(SettingsStore.LastLoadWarning))
                MessageBox.Show(SettingsStore.LastLoadWarning, "FluxLimiter settings recovery");
            TrySynchronizeFirewall(showSuccess: false);
        };
        // The first process snapshot is started by NavigateTo("Dashboard") above.
    }

    private void NavigateTo(string pageName)
    {
        if (pageHost is null || dashboardPage is null) return;

        foreach (var pair in navButtons)
        {
            bool selected = string.Equals(pair.Key, pageName, StringComparison.OrdinalIgnoreCase);
            pair.Value.BackColor = selected ? Color.FromArgb(12, 78, 145) : Color.FromArgb(13, 20, 30);
            pair.Value.ForeColor = selected ? Color.White : Color.FromArgb(190, 204, 220);
        }

        pageHost.SuspendLayout();
        try
        {
            foreach (Control old in pageHost.Controls.Cast<Control>().ToList())
            {
                pageHost.Controls.Remove(old);
                if (!ReferenceEquals(old, dashboardPage)) old.Dispose();
            }
            if (pageName == "Dashboard")
            {
                pageHost.Controls.Add(dashboardPage);
                dashboardPage.Dock = DockStyle.Fill;
                QueueReload();
                return;
            }

            var page = BuildNavigationPage(pageName);
            pageHost.Controls.Add(page);
            page.Dock = DockStyle.Fill;
        }
        finally { pageHost.ResumeLayout(); }
    }

    private Control BuildNavigationPage(string pageName)
    {
        var page = new Panel { BackColor = bg, Padding = new Padding(28) };
        var titleLabel = new Label
        {
            Text = pageName,
            Dock = DockStyle.Top,
            Height = 52,
            Font = new Font("Segoe UI Semibold", 24),
            ForeColor = ink
        };
        page.Controls.Add(titleLabel);

        string description = pageName switch
        {
            "Applications" => "Application network controls are available from Active Applications on the Dashboard.",
            "Rules" => $"Saved application rules: {settings.Rules.Count}. Open an application rule from the Dashboard to edit limits or blocking.",
            "Monthly Usage" => "Monthly accounting will appear here when the per-application usage collector is connected.",
            "Statistics" => "Traffic statistics will appear here when per-application accounting is connected.",
            "Settings" => "FluxLimiter settings are persisted locally. Enforcement changes are synchronized with Windows Firewall.",
            "About" => "FluxLimiter\nWindows network control and bandwidth management.",
            _ => ""
        };

        var card = new Panel { Dock = DockStyle.Top, Height = 150, BackColor = panel, Padding = new Padding(22), Margin = new Padding(0, 18, 0, 0) };
        card.Controls.Add(new Label
        {
            Text = description,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 11),
            ForeColor = Color.FromArgb(185, 200, 218),
            TextAlign = ContentAlignment.MiddleLeft
        });
        page.Controls.Add(card);
        card.BringToFront();

        if (pageName == "Applications")
        {
            var openDashboard = Button("Open Active Applications", 0, 0, 220, (_, _) => NavigateTo("Dashboard"));
            openDashboard.Dock = DockStyle.Top;
            openDashboard.Height = 42;
            openDashboard.Margin = new Padding(0, 12, 0, 0);
            page.Controls.Add(openDashboard);
            openDashboard.BringToFront();
        }
        return page;
    }

    private static Image UiIcon(string item)
    {
        var bitmap = new Bitmap(18, 18);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(145, 159, 177), 1.7f);
        using var brush = new SolidBrush(Color.FromArgb(145, 159, 177));
        var r = new RectangleF(2.5f, 2.5f, 13f, 13f);
        switch (item)
        {
            case "Dashboard":
                g.DrawRectangle(pen, 3, 3, 5, 5); g.DrawRectangle(pen, 10, 3, 5, 5);
                g.DrawRectangle(pen, 3, 10, 5, 5); g.DrawRectangle(pen, 10, 10, 5, 5); break;
            case "Applications":
                g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                g.DrawLine(pen, 3, 7, 15, 7); g.DrawLine(pen, 3, 11, 15, 11); break;
            case "Rules":
            case "Settings":
                g.DrawEllipse(pen, 4, 4, 10, 10); g.FillEllipse(brush, 7, 7, 4, 4); break;
            case "Monthly Usage":
            case "Statistics":
                g.DrawLine(pen, 3, 14, 3, 9); g.DrawLine(pen, 8, 14, 8, 5);
                g.DrawLine(pen, 13, 14, 13, 2); break;
            case "About":
                g.DrawEllipse(pen, r); g.DrawLine(pen, 9, 8, 9, 13); g.FillEllipse(brush, 8, 4.5f, 2, 2); break;
            default:
                g.FillEllipse(brush, 6, 6, 6, 6); break;
        }
        return bitmap;
    }

    private Panel DashboardCard(string heading, Label value, string note)
    {
        var card = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 14, 0), BackColor = panel };
        card.Controls.Add(new Label { Text = heading, Left = 18, Top = 14, Width = 180, Height = 20, ForeColor = Color.FromArgb(145, 159, 177), Font = new Font("Segoe UI Semibold", 8) });
        value.Left = 18; value.Top = 38; value.Width = 260; value.Height = 32; value.Font = new Font("Segoe UI Semibold", 17); value.ForeColor = ink;
        card.Controls.Add(value);
        card.Controls.Add(new Label { Text = note, Left = 18, Top = 76, Width = 230, Height = 18, ForeColor = Color.FromArgb(100, 117, 136), Font = new Font("Segoe UI", 8) });
        return card;
    }

    private Panel DashboardPlaceholder(string titleText, string periodText, string bodyText)
    {
        var card = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(25, 33, 45), Padding = new Padding(22), Margin = new Padding(8), MinimumSize = new Size(260, 130) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = card.BackColor };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var title = new Label { Text = titleText, AutoSize = true, Font = new Font("Segoe UI Semibold", 13), ForeColor = Color.FromArgb(224, 232, 240), Margin = new Padding(0, 0, 0, 4) };
        var period = new Label { Text = periodText, AutoSize = true, ForeColor = Color.FromArgb(130, 148, 168), Margin = new Padding(0, 0, 0, 10) };
        var body = new Label { Text = bodyText, Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Color.FromArgb(150, 166, 184), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0) };
        layout.Controls.Add(title, 0, 0); layout.Controls.Add(period, 0, 1); layout.Controls.Add(body, 0, 2);
        card.Controls.Add(layout);
        return card;
    }

    private Button Button(string text, int x, int y, int w, EventHandler click)
    {
        var b = new Button { Text = text, Left = x, Top = y, Width = w, Height = 28, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(45, 74, 91), ForeColor = Color.White };
        b.FlatAppearance.BorderSize = 0;
        b.Click += click;
        return b;
    }

    private sealed record ProcessRow(string Name, int Pid, string Path, bool IsSystem);

    // One grid row = one executable (all of its running instances). Rules are per executable path anyway.
    private sealed record ProcessGroup(string Key, string Name, string Path, List<int> Pids, bool IsSystem);

    private static List<ProcessGroup> GroupByExecutable(List<ProcessRow> processes) =>
        processes
            .GroupBy(r => r.Path.Length > 0 ? r.Path : "name:" + r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProcessGroup(g.Key, g.First().Name, g.First().Path, g.Select(x => x.Pid).OrderBy(x => x).ToList(), g.First().IsSystem))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void QueueReload()
    {
        if (IsDisposed) return;
        if (dashboardPage is not null && dashboardPage.Parent is null) return; // another page is showing
        if (reloading) { refreshQueued = true; return; }
        _ = ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (IsDisposed) return;
        if (reloading) { refreshQueued = true; return; }
        reloading = true;
        try
        {
            string query = search.Text.Trim();
            int filterIndex = filter.SelectedIndex;
            string? selectedKey = (grid.CurrentRow?.Tag as ProcessGroup)?.Key;
            int firstVisible = grid.FirstDisplayedScrollingRowIndex;

            var snapshots = await Task.Run(() => SnapshotProcesses(query, filterIndex));
            await Task.Run(() => { foreach (var ip in snapshots.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase)) ExecutableIcon(ip); });
            if (IsDisposed) return;
            if (grid.ContextMenuStrip?.Visible == true)
            {
                refreshDeferredByContextMenu = true;
                return;
            }
            if (grid.IsCurrentCellInEditMode) return; // the next timer tick retries

            var groups = GroupByExecutable(snapshots);
            var rows = groups.Select(p =>
            {
                var rule = RuleCatalog.Find(settings, p.Path);
                string display = p.Pids.Count > 1 ? p.Name + " (" + p.Pids.Count.ToString(CultureInfo.InvariantCulture) + ")" : p.Name;
                var rate = traffic.GetRate(p.Pids);
                var total = traffic.GetTotal(p.Pids);
                return new object[] { ExecutableIcon(p.Path), display, rule?.Blocked == true ? "Blocked" : "Active",
                    rate.HasData ? rate.DownloadKBps.ToString("F1", CultureInfo.InvariantCulture) + " KB/s" : "—",
                    rate.HasData ? rate.UploadKBps.ToString("F1", CultureInfo.InvariantCulture) + " KB/s" : "—",
                    total.HasData ? FormatBytes(total.TotalBytes) : "—",
                    rule?.DownloadLimitKBps?.ToString(CultureInfo.InvariantCulture) ?? "Unlimited",
                    rule?.UploadLimitKBps?.ToString(CultureInfo.InvariantCulture) ?? "Unlimited",
                    rule is null ? "None" : "Custom", "•••", string.Join(", ", p.Pids), p.Path };
            }).ToList();

            grid.SuspendLayout();
            try
            {
                var existing = grid.Rows.Cast<DataGridViewRow>()
                    .Where(r => r.Tag is ProcessGroup)
                    .ToDictionary(r => ((ProcessGroup)r.Tag!).Key, StringComparer.OrdinalIgnoreCase);
                var liveKeys = new HashSet<string>(groups.Select(g => g.Key), StringComparer.OrdinalIgnoreCase);
                foreach (var stale in existing.Where(x => !liveKeys.Contains(x.Key)).Select(x => x.Value).ToList())
                    grid.Rows.Remove(stale);

                int selectedIndex = -1;
                for (int i = 0; i < rows.Count; i++)
                {
                    DataGridViewRow target;
                    if (existing.TryGetValue(groups[i].Key, out var current) && current.DataGridView is not null)
                    {
                        target = current;
                        for (int c = 0; c < rows[i].Length; c++)
                            if (!Equals(target.Cells[c].Value, rows[i][c])) target.Cells[c].Value = rows[i][c];
                    }
                    else
                    {
                        int added = grid.Rows.Add(rows[i]);
                        target = grid.Rows[added];
                    }
                    target.Tag = groups[i];
                    if (selectedKey is not null && string.Equals(groups[i].Key, selectedKey, StringComparison.OrdinalIgnoreCase))
                        selectedIndex = target.Index;
                }
                grid.ClearSelection();
                if (selectedIndex >= 0)
                {
                    grid.Rows[selectedIndex].Selected = true;
                    grid.CurrentCell = grid.Rows[selectedIndex].Cells["app"];
                }
                if (firstVisible >= 0 && firstVisible < grid.Rows.Count)
                    grid.FirstDisplayedScrollingRowIndex = firstVisible;
            }
            finally { grid.ResumeLayout(); }
            status.Text = "Right-click an application for controls";
            processCount.Text = rows.Count.ToString(CultureInfo.InvariantCulture) + "  Applications";
            ruleCount.Text = settings.Rules.Count.ToString(CultureInfo.InvariantCulture) + "  Saved rules";
        }
        finally
        {
            reloading = false;
            if (refreshQueued && !IsDisposed)
            {
                refreshQueued = false;
                BeginInvoke((Action)QueueReload);
            }
        }
    }

    private readonly Dictionary<string, Image?> iconCache = new(StringComparer.OrdinalIgnoreCase);

    private Image? ExecutableIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        lock (iconCache) { if (iconCache.TryGetValue(path, out var cached)) return cached; }
        Image? image = null;
        try
        {
            if (File.Exists(path))
            {
                using var icon = Icon.ExtractAssociatedIcon(path);
                image = icon?.ToBitmap();
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
        lock (iconCache) { iconCache[path] = image; }
        return image;
    }

    private List<ProcessRow> SnapshotProcesses(string query, int filterIndex)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var rows = new List<ProcessRow>();
        foreach (var p in Process.GetProcesses().OrderBy(p => p.ProcessName))
        {
            try
            {
                string name = p.ProcessName;
                string path;
                long startTicks;
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
                if (!name.Contains(query, StringComparison.OrdinalIgnoreCase) && !path.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                bool isSystem = path.Length == 0 || path.StartsWith(windows, StringComparison.OrdinalIgnoreCase);
                if (filterIndex == 1 && isSystem) continue;
                if (filterIndex == 2 && !isSystem) continue;
                rows.Add(new ProcessRow(name, p.Id, path, isSystem));
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally { p.Dispose(); }
        }
        return rows;
    }

    private void UpdateSamplingState()
    {
        bool active = WindowState != FormWindowState.Minimized;
        refresh.Enabled = active;
        speedTimer.Enabled = active;
        if (active) QueueReload();
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
        if (!TryLimit(grid.Rows[rowIndex].Cells["down"].Value?.ToString() ?? "", out var down) ||
            !TryLimit(grid.Rows[rowIndex].Cells["up"].Value?.ToString() ?? "", out var up))
        {
            MessageBox.Show("Use a positive whole number in KB/s, or Unlimited.", "Invalid limit");
            Reload();
            return;
        }
        if (!TryPersistMutation(() =>
        {
            var rule = RuleCatalog.Find(settings, path) ?? new AppRule { ExecutablePath = path };
            rule.DownloadLimitKBps = down;
            rule.UploadLimitKBps = up;
            if (!settings.Rules.Contains(rule)) settings.Rules.Add(rule);
        }))
        {
            Reload();
            return;
        }
        Reload();
    }

    private void EditRule(int index)
    {
        string path = grid.Rows[index].Cells["path"].Value?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show("Executable path unavailable. Try running FluxLimiter as administrator.", "FluxLimiter");
            return;
        }
        var rule = settings.Rules.FirstOrDefault(r => string.Equals(r.ExecutablePath, path, StringComparison.OrdinalIgnoreCase));
        using var dialog = new Form { Text = "Saved rule — " + Path.GetFileName(path), ClientSize = new Size(460, 250), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = panel, ForeColor = ink, Font = Font, AutoScaleMode = AutoScaleMode.Dpi };
        var formLayout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, RowCount = 5, BackColor = panel };
        formLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        formLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        formLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        formLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        formLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        formLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        formLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        var dl = new TextBox { Dock = DockStyle.Fill, Text = rule?.DownloadLimitKBps?.ToString() ?? "", BackColor = bg, ForeColor = ink, Margin = new Padding(6) };
        var ul = new TextBox { Dock = DockStyle.Fill, Text = rule?.UploadLimitKBps?.ToString() ?? "", BackColor = bg, ForeColor = ink, Margin = new Padding(6) };
        var blocked = new CheckBox { Text = "Block application", Checked = rule?.Blocked ?? false, AutoSize = true, Anchor = AnchorStyles.Left };
        formLayout.Controls.Add(new Label { Text = "Download KB/s", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        formLayout.Controls.Add(dl, 1, 0);
        formLayout.Controls.Add(new Label { Text = "Upload KB/s", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        formLayout.Controls.Add(ul, 1, 1);
        formLayout.Controls.Add(blocked, 1, 2);
        var note = new Label { Text = "Blank = unlimited. Block rules are enforced by Windows Firewall; speed limits await the WFP driver.", Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
        formLayout.SetColumnSpan(note, 2); formLayout.Controls.Add(note, 0, 3);
        var save = new Button { Text = "Save rule", DialogResult = DialogResult.OK, BackColor = Color.FromArgb(45, 74, 91), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Dock = DockStyle.Right, Width = 120, Margin = new Padding(6) };
        formLayout.Controls.Add(save, 1, 4);
        dialog.Controls.Add(formLayout);
        dialog.AcceptButton = save;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!TryLimit(dl.Text, out var down) || !TryLimit(ul.Text, out var up))
        {
            MessageBox.Show("Enter a positive whole number, or leave blank for unlimited.", "Invalid limit");
            return;
        }
        if (!TryPersistMutation(() =>
        {
            var target = RuleCatalog.Find(settings, path) ?? new AppRule { ExecutablePath = path };
            target.DownloadLimitKBps = down;
            target.UploadLimitKBps = up;
            target.Blocked = blocked.Checked;
            if (!settings.Rules.Contains(target)) settings.Rules.Add(target);
        }))
        {
            Reload();
            return;
        }
        Reload();
    }

    private static bool TryLimit(string value, out int? limit)
    {
        limit = null;
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Equals("Unlimited", StringComparison.OrdinalIgnoreCase)) return true;
        if (!int.TryParse(value, out int n) || n <= 0) return false;
        limit = n;
        return true;
    }

    private void SetupContextMenu()
    {
        var menu = new ContextMenuStrip { BackColor = panel, ForeColor = ink, ShowImageMargin = false };
        menu.Items.Add("Open file location", null, (_, _) => WithSelectedPath(path =>
        {
            try
            {
                var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                psi.ArgumentList.Add("/select," + path);
                Process.Start(psi);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { MessageBox.Show(ex.Message); }
        }));
        menu.Items.Add("Open process folder", null, (_, _) => WithSelectedPath(path =>
        {
            try { Process.Start(new ProcessStartInfo(Path.GetDirectoryName(path) ?? path) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { MessageBox.Show(ex.Message); }
        }));
        menu.Items.Add("Copy process path", null, (_, _) => WithSelectedPath(path => Clipboard.SetText(path)));
        menu.Items.Add("Open in Task Manager", null, (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { MessageBox.Show(ex.Message); }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Set speed limit", null, (_, _) => { if (grid.CurrentRow is not null) EditRule(grid.CurrentRow.Index); });
        menu.Items.Add("Toggle block rule", null, (_, _) => ToggleBlockSelected());
        menu.Items.Add("Exclude from list", null, (_, _) => MessageBox.Show("Exclusion persistence is not implemented yet."));

        grid.ContextMenuStrip = menu;
        menu.Closed += (_, _) =>
        {
            if (refreshDeferredByContextMenu && !IsDisposed)
            {
                refreshDeferredByContextMenu = false;
                QueueReload();
            }
        };
        grid.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
            {
                grid.ClearSelection();
                grid.Rows[e.RowIndex].Selected = true;
                grid.CurrentCell = grid.Rows[e.RowIndex].Cells["app"];
            }
        };
    }

    private void ToggleBlockSelected()
    {
        string path = grid.CurrentRow?.Cells["path"].Value?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(path)) { MessageBox.Show("Executable path unavailable."); return; }
        if (!TryPersistMutation(() =>
        {
            var rule = RuleCatalog.Find(settings, path);
            if (rule is null)
                settings.Rules.Add(new AppRule { ExecutablePath = path, Blocked = true });
            else
                rule.Blocked = !rule.Blocked;
        }))
        {
            Reload();
            return;
        }
        Reload();
    }

    private bool TryPersistMutation(Action mutation)
    {
        // Keep the in-memory model identical to disk if persistence is unavailable or fails.
        if (settingsSaveDisabled)
        {
            MessageBox.Show("Settings cannot be saved because the previous settings file could not be safely preserved.", "Saving disabled");
            return false;
        }

        string snapshot = System.Text.Json.JsonSerializer.Serialize(settings);
        mutation();
        if (TrySaveSettings())
        {
            TrySynchronizeFirewall(showSuccess: false);
            return true;
        }

        try
        {
            var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(snapshot);
            if (restored is not null)
            {
                settings.Rules = restored.Rules ?? new();
                settings.Groups = restored.Groups ?? new();
                settings.StartWithWindows = restored.StartWithWindows;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Snapshot is generated from our own in-memory model; this should not occur.
        }
        return false;
    }

    private void TrySynchronizeFirewall(bool showSuccess)
    {
        try
        {
            FirewallEnforcer.Synchronize(settings);
            if (showSuccess) MessageBox.Show("Windows Firewall block rules synchronized.", "FluxLimiter");
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException
            or PlatformNotSupportedException or ArgumentException or InvalidCastException
            or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            MessageBox.Show(
                "The rule was saved, but Windows Firewall enforcement could not be synchronized.\n\n" + ex.Message,
                "FluxLimiter enforcement");
        }
    }

    private bool TrySaveSettings()
    {
        if (settingsSaveDisabled)
        {
            MessageBox.Show("Settings cannot be saved because the previous settings file could not be safely preserved.", "Saving disabled");
            return false;
        }
        try
        {
            SettingsStore.Save(settings);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "Unable to save settings");
            return false;
        }
    }

    private void WithSelectedPath(Action<string> action)
    {
        var path = grid.CurrentRow?.Cells["path"].Value?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(path)) { MessageBox.Show("Executable path unavailable."); return; }
        action(path);
    }

    internal static string CsvCell(string value)
    {
        // Prevent spreadsheet applications from interpreting exported process data as formulas.
        string safe = value;
        if (safe.Length > 0 && "=+-@\t\r".Contains(safe[0])) safe = "'" + safe;
        return "\"" + safe.Replace("\"", "\"\"") + "\"";
    }

    private void ExportCsv()
    {
        using var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "fluxlimiter-processes.csv" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var csv = new StringBuilder();
        csv.AppendLine("Application,Instances,PIDs,Executable,DownloadLimitKBps,UploadLimitKBps,Blocked");
        foreach (DataGridViewRow row in grid.Rows)
        {
            var group = row.Tag as ProcessGroup;
            string[] values = {
                group?.Name ?? row.Cells["app"].Value?.ToString() ?? "",
                (group?.Pids.Count ?? 1).ToString(CultureInfo.InvariantCulture),
                group is null ? row.Cells["pid"].Value?.ToString() ?? "" : string.Join(";", group.Pids),
                row.Cells["path"].Value?.ToString() ?? "",
                row.Cells["down"].Value?.ToString() ?? "",
                row.Cells["up"].Value?.ToString() ?? "",
                string.Equals(row.Cells["status"].Value?.ToString(), "Blocked", StringComparison.OrdinalIgnoreCase) ? "true" : "false"
            };
            csv.AppendLine(string.Join(",", values.Select(CsvCell)));
        }
        try { File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(true)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show(ex.Message, "Export failed"); }
    }
}