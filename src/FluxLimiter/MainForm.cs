using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace FluxLimiter;

public sealed class MainForm : Form
{
    private readonly AppSettings settings = SettingsStore.Load();
    private readonly NetworkSampler network = new();
    private readonly Label speed = new();
    private readonly DataGridView grid = new();
    private readonly TextBox search = new();
    private readonly ComboBox filter = new();
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 5000 };
    private bool reloading;
    private readonly System.Windows.Forms.Timer speedTimer = new() { Interval = 1000 };
    private readonly Label status = new();
    private readonly Label processCount = new();
    private readonly Label ruleCount = new();
    private readonly Label dailyUsage = new();
    private readonly Label monthlyUsage = new();
    private readonly Color bg = Color.FromArgb(16, 22, 34);
    private readonly Color panel = Color.FromArgb(25, 34, 49);
    private readonly Color ink = Color.FromArgb(229, 237, 246);

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

        var top = new Panel { Dock = DockStyle.Top, Height = 176, BackColor = panel, Padding = new Padding(28, 18, 28, 14) };
        var title = new Label { Text = "FluxLimiter", Font = new Font("Segoe UI Semibold", 26), ForeColor = Color.FromArgb(91, 225, 184), Left = 28, Top = 16, AutoSize = true };
        var subtitle = new Label { Text = "Network control dashboard", Left = 30, Top = 62, AutoSize = true, ForeColor = Color.FromArgb(155, 171, 190) };
        processCount.SetBounds(780, 24, 170, 54); processCount.Font = new Font("Segoe UI Semibold", 13); processCount.ForeColor = ink; processCount.TextAlign = ContentAlignment.MiddleCenter; processCount.BackColor = bg;
        ruleCount.SetBounds(965, 24, 170, 54); ruleCount.Font = new Font("Segoe UI Semibold", 13); ruleCount.ForeColor = ink; ruleCount.TextAlign = ContentAlignment.MiddleCenter; ruleCount.BackColor = bg;
        search.SetBounds(28, 116, 390, 34);
        search.PlaceholderText = "Search application or executable...";
        search.TextChanged += (_, _) => Reload();
        filter.SetBounds(432, 116, 175, 34);
        filter.DropDownStyle = ComboBoxStyle.DropDownList;
        filter.Items.AddRange(new object[] { "All processes", "User processes", "System processes" });
        filter.SelectedIndex = 0;
        filter.SelectedIndexChanged += (_, _) => Reload();
        var export = Button("Export CSV", 624, 116, 120, (_, _) => ExportCsv()); export.Height = 34;
        var reload = Button("Refresh", 756, 116, 105, (_, _) => Reload()); reload.Height = 34;
        top.Controls.AddRange(new Control[] { title, subtitle, processCount, ruleCount, search, filter, export, reload });

        grid.Dock = DockStyle.Fill;
        grid.BackgroundColor = bg;
        grid.GridColor = Color.FromArgb(45, 58, 74);
        grid.BorderStyle = BorderStyle.None;
        grid.RowHeadersVisible = false;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.ReadOnly = true;
        grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
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
        grid.Columns.Add("app", "Application");
        grid.Columns.Add("pid", "PID");
        grid.Columns.Add("path", "Executable");
        grid.Columns.Add("down", "Download limit");
        grid.Columns.Add("up", "Upload limit");
        grid.Columns.Add("block", "Status");
        grid.Columns["app"].FillWeight = 125;
        grid.Columns["pid"].FillWeight = 55;
        grid.Columns["path"].FillWeight = 230;
        grid.Columns["down"].FillWeight = 90;
        grid.Columns["up"].FillWeight = 90;
        grid.Columns["block"].FillWeight = 70;
        grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) EditRule(e.RowIndex); };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 68, BackColor = panel };
        speed.SetBounds(28, 37, 900, 24);
        speed.ForeColor = Color.FromArgb(91, 225, 184);
        speed.Text = "Device traffic: sampling... (not per-app; virtual adapters may double-count)";
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
            var n = new Button { Text = nav[i], Left = 16, Top = 112 + (i * 50), Width = 186, Height = 40, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16, 0, 0, 0), BackColor = i == 0 ? Color.FromArgb(27, 37, 52) : sidebar.BackColor, ForeColor = i == 0 ? ink : Color.FromArgb(145, 159, 177) };
            n.FlatAppearance.BorderSize = 0;
            sidebar.Controls.Add(n);
        }

        var dashboard = new Panel { Dock = DockStyle.Fill, BackColor = bg, AutoScroll = true };
        top.Dock = DockStyle.None; top.SetBounds(28, 20, 1160, 166); top.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        title.Text = "Dashboard"; title.ForeColor = ink;
        subtitle.Text = "Monitor and control application network activity";
        processCount.Text = "Processes"; ruleCount.Text = "Saved rules";

        var summary = new TableLayoutPanel { Left = 28, Top = 202, Width = 1160, Height = 104, ColumnCount = 4, RowCount = 1, BackColor = bg, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        for (int i = 0; i < 4; i++) summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        summary.Controls.Add(DashboardCard("DOWNLOAD", speed, "Live device traffic"), 0, 0);
        summary.Controls.Add(DashboardCard("UPLOAD", new Label { Text = "Live", ForeColor = ink, Font = new Font("Segoe UI Semibold", 17) }, "Live device traffic"), 1, 0);
        summary.Controls.Add(DashboardCard("DAILY USAGE", dailyUsage, "Collector not connected"), 2, 0);
        summary.Controls.Add(DashboardCard("MONTHLY USAGE", monthlyUsage, "Collector not connected"), 3, 0);

        var topApps = DashboardPlaceholder("Top Applications", "Today", "Per-application traffic attribution is not connected yet.", 28, 324, 565, 160);
        var sevenDays = DashboardPlaceholder("Daily Usage", "Last 7 days", "Usage history will appear here after the collector is implemented.", 608, 324, 580, 160);
        sevenDays.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        var active = new Panel { Left = 28, Top = 502, Width = 1160, Height = 330, BackColor = panel, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
        active.Controls.Add(new Label { Text = "Active Applications", Left = 20, Top = 14, Width = 260, Height = 32, Font = new Font("Segoe UI Semibold", 15), ForeColor = ink });
        grid.Dock = DockStyle.None; grid.SetBounds(20, 54, 1120, 252); grid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        active.Controls.Add(grid);

        bottom.Visible = false;
        dashboard.Controls.Add(active);
        dashboard.Controls.Add(sevenDays);
        dashboard.Controls.Add(topApps);
        dashboard.Controls.Add(summary);
        dashboard.Controls.Add(top);
        shell.Controls.Add(dashboard);
        shell.Controls.Add(sidebar);
        Controls.Add(shell);
        refresh.Tick += (_, _) => { if (!reloading && !grid.ContainsFocus) Reload(); };
        refresh.Start();
        speedTimer.Tick += (_, _) => { try { var rates = network.Sample(); speed.Text = $"Live traffic    ↓ {rates.DownloadKBps:F1} KB/s     ↑ {rates.UploadKBps:F1} KB/s"; } catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or InvalidOperationException) { speed.Text = "Device speed unavailable"; } };
        speedTimer.Start();
        SetupContextMenu();
        Reload();
    }

    private Panel DashboardCard(string heading, Label value, string note)
    {
        var card = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 14, 0), BackColor = panel };
        card.Controls.Add(new Label { Text = heading, Left = 18, Top = 14, Width = 180, Height = 20, ForeColor = Color.FromArgb(145, 159, 177), Font = new Font("Segoe UI Semibold", 8) });
        value.Left = 18; value.Top = 38; value.Width = 220; value.Height = 32; value.Font = new Font("Segoe UI Semibold", 17); value.ForeColor = ink;
        card.Controls.Add(value);
        card.Controls.Add(new Label { Text = note, Left = 18, Top = 76, Width = 230, Height = 18, ForeColor = Color.FromArgb(100, 117, 136), Font = new Font("Segoe UI", 8) });
        return card;
    }

    private Panel DashboardPlaceholder(string heading, string period, string message, int x, int y, int width, int height)
    {
        var card = new Panel { Left = x, Top = y, Width = width, Height = height, BackColor = panel };
        card.Controls.Add(new Label { Text = heading, Left = 20, Top = 14, Width = 260, Height = 30, Font = new Font("Segoe UI Semibold", 14), ForeColor = ink });
        card.Controls.Add(new Label { Text = period, Left = width - 145, Top = 18, Width = 120, Height = 22, TextAlign = ContentAlignment.MiddleRight, ForeColor = Color.FromArgb(145, 159, 177), Anchor = AnchorStyles.Top | AnchorStyles.Right });
        card.Controls.Add(new Label { Text = message, Left = 24, Top = 66, Width = width - 48, Height = 50, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(145, 159, 177), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right });
        return card;
    }

    private Button Button(string text, int x, int y, int w, EventHandler click)
    {
        var b = new Button { Text = text, Left = x, Top = y, Width = w, Height = 28, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(45, 74, 91), ForeColor = Color.White };
        b.FlatAppearance.BorderSize = 0;
        b.Click += click;
        return b;
    }

    private void Reload()
    {
        if (reloading) return;
        reloading = true;
        try
        {
            string query = search.Text.Trim();
            int? selectedPid = grid.CurrentRow?.Cells["pid"].Value is int pid ? pid : null;
            string? selectedPath = grid.CurrentRow?.Cells["path"].Value?.ToString();
            int firstVisible = grid.FirstDisplayedScrollingRowIndex;

            var rows = new List<object[]>();
            foreach (var p in Process.GetProcesses().OrderBy(p => p.ProcessName))
            {
                try
                {
                    string name = p.ProcessName;
                    string path;
                    try { path = p.MainModule?.FileName ?? ""; } catch { path = ""; }
                    if (!name.Contains(query, StringComparison.OrdinalIgnoreCase) && !path.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                    bool isSystem = path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase);
                    if (filter.SelectedIndex == 1 && isSystem) continue;
                    if (filter.SelectedIndex == 2 && !isSystem) continue;
                    var rule = RuleCatalog.Find(settings, path);
                    rows.Add(new object[] { name, p.Id, path, rule?.DownloadLimitKBps is int dl ? dl + " KB/s" : "Unlimited", rule?.UploadLimitKBps is int ul ? ul + " KB/s" : "Unlimited", rule?.Blocked == true ? "Blocked" : "Allowed" });
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                finally { p.Dispose(); }
            }

            grid.SuspendLayout();
            try
            {
                grid.Rows.Clear();
                int selectedIndex = -1;
                for (int i = 0; i < rows.Count; i++)
                {
                    int row = grid.Rows.Add(rows[i]);
                    if (selectedPid.HasValue && Equals(rows[i][1], selectedPid.Value) &&
                        string.Equals(rows[i][2]?.ToString(), selectedPath, StringComparison.OrdinalIgnoreCase))
                        selectedIndex = row;
                }
                grid.ClearSelection();
                if (selectedIndex >= 0)
                {
                    grid.Rows[selectedIndex].Selected = true;
                    grid.CurrentCell = grid.Rows[selectedIndex].Cells[0];
                }
                if (firstVisible >= 0 && firstVisible < grid.Rows.Count)
                    grid.FirstDisplayedScrollingRowIndex = firstVisible;
            }
            finally { grid.ResumeLayout(); }
            status.Text = "Right-click an application for controls";
            processCount.Text = rows.Count.ToString(CultureInfo.InvariantCulture) + "  Processes";
            ruleCount.Text = settings.Rules.Count.ToString(CultureInfo.InvariantCulture) + "  Saved rules";
        }
        finally { reloading = false; }
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
        using var dialog = new Form { Text = "Saved rule — " + Path.GetFileName(path), Width = 450, Height = 260, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = panel, ForeColor = ink, Font = Font };
        var dl = new TextBox { Left = 170, Top = 25, Width = 225, Text = rule?.DownloadLimitKBps?.ToString() ?? "", BackColor = bg, ForeColor = ink };
        var ul = new TextBox { Left = 170, Top = 65, Width = 225, Text = rule?.UploadLimitKBps?.ToString() ?? "", BackColor = bg, ForeColor = ink };
        var blocked = new CheckBox { Left = 170, Top = 105, Text = "Block application", Checked = rule?.Blocked ?? false, AutoSize = true };
        dialog.Controls.AddRange(new Control[] {
            new Label { Text = "Download KB/s", Left = 18, Top = 28, AutoSize = true },
            new Label { Text = "Upload KB/s", Left = 18, Top = 68, AutoSize = true },
            dl, ul, blocked,
            new Label { Text = "Blank = unlimited. Rules saved but not enforced in this preview.", Left = 18, Top = 143, Width = 410, Height = 34 }
        });
        var save = new Button { Text = "Save rule", Left = 280, Top = 176, Width = 115, DialogResult = DialogResult.OK, BackColor = Color.FromArgb(45, 74, 91), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        dialog.Controls.Add(save);
        dialog.AcceptButton = save;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!TryLimit(dl.Text, out var down) || !TryLimit(ul.Text, out var up))
        {
            MessageBox.Show("Enter a positive whole number, or leave blank for unlimited.", "Invalid limit");
            return;
        }
        rule ??= new AppRule { ExecutablePath = path };
        rule.DownloadLimitKBps = down;
        rule.UploadLimitKBps = up;
        rule.Blocked = blocked.Checked;
        if (!settings.Rules.Contains(rule)) settings.Rules.Add(rule);
        try { SettingsStore.Save(settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show(ex.Message, "Unable to save settings"); }
        Reload();
    }

    private static bool TryLimit(string value, out int? limit)
    {
        limit = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!int.TryParse(value, out int n) || n <= 0) return false;
        limit = n;
        return true;
    }

    private void SetupContextMenu()
    {
        var menu = new ContextMenuStrip { BackColor = panel, ForeColor = ink, ShowImageMargin = false };
        menu.Items.Add("Open File Location", null, (_, _) => WithSelectedPath(path =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { MessageBox.Show(ex.Message); }
        }));
        menu.Items.Add("Properties", null, (_, _) => WithSelectedPath(path =>
        {
            try { Process.Start(new ProcessStartInfo("rundll32.exe", "shell32.dll,ShellExec_RunDLL properties \"" + path + "\"") { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { MessageBox.Show(ex.Message); }
        }));
        menu.Items.Add("Set speed limit", null, (_, _) => { if (grid.CurrentRow is not null) EditRule(grid.CurrentRow.Index); });
        menu.Items.Add("Toggle block rule", null, (_, _) => ToggleBlockSelected());
        menu.Items.Add("Usage statistics", null, (_, _) => MessageBox.Show("Per-app traffic attribution is not implemented yet."));
        menu.Items.Add("Copy File Path", null, (_, _) => WithSelectedPath(path => Clipboard.SetText(path)));
        menu.Items.Add("View Process Details", null, (_, _) =>
        {
            if (grid.CurrentRow is null) return;
            MessageBox.Show("PID: " + grid.CurrentRow.Cells["pid"].Value + Environment.NewLine +
                "Executable: " + grid.CurrentRow.Cells["path"].Value, "Process details");
        });
        grid.ContextMenuStrip = menu;
        grid.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
            {
                grid.ClearSelection();
                grid.Rows[e.RowIndex].Selected = true;
                grid.CurrentCell = grid.Rows[e.RowIndex].Cells[0];
            }
        };
    }

    private void ToggleBlockSelected()
    {
        string path = grid.CurrentRow?.Cells["path"].Value?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(path)) { MessageBox.Show("Executable path unavailable."); return; }
        var rule = RuleCatalog.Find(settings, path);
        if (rule is null)
        {
            rule = new AppRule { ExecutablePath = path, Blocked = true };
            settings.Rules.Add(rule);
        }
        else rule.Blocked = !rule.Blocked;
        try { SettingsStore.Save(settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show(ex.Message, "Unable to save settings"); return; }
        Reload();
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
        if (safe.Length > 0 && "=+-@".Contains(safe[0])) safe = "'" + safe;
        return "\"" + safe.Replace("\"", "\"\"") + "\"";
    }

    private void ExportCsv()
    {
        using var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "fluxlimiter-processes.csv" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var csv = new StringBuilder();
        csv.AppendLine("Application,PID,Executable,DownloadLimitKBps,UploadLimitKBps,Blocked");
        foreach (DataGridViewRow row in grid.Rows)
            csv.AppendLine(string.Join(",", row.Cells.Cast<DataGridViewCell>().Select(c => CsvCell(c.Value?.ToString() ?? ""))));
        try { File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(true)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show(ex.Message, "Export failed"); }
    }
}
