using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace FluxLimiter;

public sealed class MainForm : Form
{
    private readonly AppSettings settings = SettingsStore.Load();
    private readonly DataGridView grid = new();
    private readonly TextBox search = new();
    private readonly ComboBox filter = new();
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 2500 };
    private readonly Label status = new();
    private readonly Color bg = Color.FromArgb(16, 22, 34);
    private readonly Color panel = Color.FromArgb(25, 34, 49);
    private readonly Color ink = Color.FromArgb(229, 237, 246);

    public MainForm()
    {
        Text = "FluxLimiter — Network Control";
        Width = 1100;
        Height = 700;
        MinimumSize = new Size(760, 490);
        BackColor = bg;
        ForeColor = ink;
        Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterScreen;

        var top = new Panel { Dock = DockStyle.Top, Height = 125, BackColor = panel };
        var title = new Label { Text = "FLUXLIMITER", Font = new Font("Segoe UI Semibold", 23), ForeColor = Color.FromArgb(91, 225, 184), Left = 24, Top = 12, AutoSize = true };
        var subtitle = new Label { Text = "Per-application bandwidth dashboard • development preview", Left = 26, Top = 60, AutoSize = true, ForeColor = Color.FromArgb(155, 171, 190) };
        search.SetBounds(25, 89, 320, 26);
        search.PlaceholderText = "Search application or executable...";
        search.TextChanged += (_, _) => Reload();
        filter.SetBounds(360, 89, 150, 26);
        filter.DropDownStyle = ComboBoxStyle.DropDownList;
        filter.Items.AddRange(new object[] { "All processes", "User processes", "System processes" });
        filter.SelectedIndex = 0;
        filter.SelectedIndexChanged += (_, _) => Reload();
        var export = Button("Export CSV", 525, 88, 110, (_, _) => ExportCsv());
        var reload = Button("Refresh", 645, 88, 100, (_, _) => Reload());
        top.Controls.AddRange(new Control[] { title, subtitle, search, filter, export, reload });

        grid.Dock = DockStyle.Fill;
        grid.BackgroundColor = bg;
        grid.GridColor = Color.FromArgb(45, 58, 74);
        grid.BorderStyle = BorderStyle.None;
        grid.RowHeadersVisible = false;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.ReadOnly = true;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
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
        grid.Columns.Add("down", "Down limit (KB/s)");
        grid.Columns.Add("up", "Up limit (KB/s)");
        grid.Columns.Add("block", "Blocked");
        grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) EditRule(e.RowIndex); };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 55, BackColor = panel };
        status.AutoSize = true;
        status.Left = 20;
        status.Top = 18;
        status.ForeColor = Color.FromArgb(170, 185, 200);
        bottom.Controls.Add(status);
        Controls.Add(grid);
        Controls.Add(bottom);
        Controls.Add(top);
        refresh.Tick += (_, _) => Reload();
        refresh.Start();
        Reload();
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
        string query = search.Text.Trim();
        string? selectedPath = grid.SelectedRows.Count > 0 ? grid.SelectedRows[0].Cells["path"].Value?.ToString() : null;
        grid.Rows.Clear();
        int count = 0;
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
                var rule = settings.Rules.FirstOrDefault(r => string.Equals(r.ExecutablePath, path, StringComparison.OrdinalIgnoreCase));
                int row = grid.Rows.Add(name, p.Id, path, rule?.DownloadLimitKbps?.ToString() ?? "Unlimited", rule?.UploadLimitKbps?.ToString() ?? "Unlimited", rule?.Blocked == true ? "Yes" : "No");
                if (path == selectedPath) grid.Rows[row].Selected = true;
                count++;
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally { p.Dispose(); }
        }
        status.Text = count.ToString(CultureInfo.InvariantCulture) + " processes  •  Double-click to edit a saved rule  •  Limits are NOT enforced yet";
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
        using var dialog = new Form { Text = "Saved rule — " + Path.GetFileName(path), Width = 450, Height = 260, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var dl = new TextBox { Left = 170, Top = 25, Width = 225, Text = rule?.DownloadLimitKbps?.ToString() ?? "" };
        var ul = new TextBox { Left = 170, Top = 65, Width = 225, Text = rule?.UploadLimitKbps?.ToString() ?? "" };
        var blocked = new CheckBox { Left = 170, Top = 105, Text = "Block application", Checked = rule?.Blocked ?? false, AutoSize = true };
        dialog.Controls.AddRange(new Control[] {
            new Label { Text = "Download KB/s", Left = 18, Top = 28, AutoSize = true },
            new Label { Text = "Upload KB/s", Left = 18, Top = 68, AutoSize = true },
            dl, ul, blocked,
            new Label { Text = "Blank = unlimited. Rules saved but not enforced in this preview.", Left = 18, Top = 143, Width = 410, Height = 34 }
        });
        var save = new Button { Text = "Save rule", Left = 280, Top = 176, Width = 115, DialogResult = DialogResult.OK };
        dialog.Controls.Add(save);
        dialog.AcceptButton = save;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!TryLimit(dl.Text, out var down) || !TryLimit(ul.Text, out var up))
        {
            MessageBox.Show("Enter a positive whole number, or leave blank for unlimited.", "Invalid limit");
            return;
        }
        rule ??= new AppRule { ExecutablePath = path };
        rule.DownloadLimitKbps = down;
        rule.UploadLimitKbps = up;
        rule.Blocked = blocked.Checked;
        if (!settings.Rules.Contains(rule)) settings.Rules.Add(rule);
        try { SettingsStore.Save(settings); }
        catch (IOException ex) { MessageBox.Show(ex.Message, "Unable to save settings"); }
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

    private void ExportCsv()
    {
        using var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "fluxlimiter-processes.csv" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var csv = new StringBuilder();
        csv.AppendLine("Application,PID,Executable,DownloadLimitKBps,UploadLimitKBps,Blocked");
        foreach (DataGridViewRow row in grid.Rows)
            csv.AppendLine(string.Join(",", row.Cells.Cast<DataGridViewCell>().Select(c => "\"" + (c.Value?.ToString() ?? "").Replace("\"", "\"\"") + "\"")));
        File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(true));
    }
}
