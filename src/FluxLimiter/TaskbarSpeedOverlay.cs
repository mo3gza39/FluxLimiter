using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FluxLimiter;

public sealed class TaskbarSpeedOverlay : Form
{
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private readonly Font speedFont = new("Consolas", 8.5f, FontStyle.Bold);
    private readonly System.Windows.Forms.Timer positionTimer = new() { Interval = 1500 };
    private string downloadText = "↓ 0.0 KB/s";
    private string uploadText = "↑ 0.0 KB/s";
    private Point dragStart;
    private bool dragging;
    private bool manualPosition;

    public bool UseBitsPerSecond { get; set; } = true;
    public bool MonochromeText { get; set; }

    public TaskbarSpeedOverlay(ContextMenuStrip sharedMenu)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        Width = 96;
        Height = 36;
        BackColor = Color.FromArgb(14, 22, 36);
        ForeColor = Color.White;
        ContextMenuStrip = sharedMenu;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        positionTimer.Tick += (_, _) => { if (Visible && !manualPosition) SnapNextToTrayClock(); };
        positionTimer.Start();
        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { dragging = true; dragStart = e.Location; } };
        MouseMove += (_, e) => { if (dragging) { manualPosition = true; Location = new Point(Left + e.X - dragStart.X, Top + e.Y - dragStart.Y); } };
        MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) dragging = false; };
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE; return cp; }
    }

    protected override bool ShowWithoutActivation => true;

    public void UpdateSpeeds(double downloadKBps, double uploadKBps)
    {
        downloadText = "↓ " + FormatRate(downloadKBps, UseBitsPerSecond);
        uploadText = "↑ " + FormatRate(uploadKBps, UseBitsPerSecond);
        if (Visible) { if (!manualPosition) SnapNextToTrayClock(); Invalidate(); }
    }

    private static string FormatRate(double kbpsBytes, bool useBits)
    {
        if (useBits)
        {
            double kbits = Math.Max(0, kbpsBytes * 8.0);
            if (kbits >= 1000.0) return (kbits / 1000.0).ToString("F1", CultureInfo.InvariantCulture) + " Mbps";
            return Math.Round(kbits).ToString(CultureInfo.InvariantCulture) + " Kbps";
        }
        if (kbpsBytes >= 1024.0) return (kbpsBytes / 1024.0).ToString("F2", CultureInfo.InvariantCulture) + " MB/s";
        return kbpsBytes.ToString("F0", CultureInfo.InvariantCulture) + " KB/s";
    }

    public void SnapNextToTrayClock()
    {
        if (!OperatingSystem.IsWindows()) return;
        IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out RECT tbRect)) return;
        IntPtr trayNotify = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        int targetX, targetY;
        if (trayNotify != IntPtr.Zero && GetWindowRect(trayNotify, out RECT trayRect))
        {
            int tbHeight = Math.Max(32, tbRect.Bottom - tbRect.Top);
            Height = Math.Min(40, tbHeight - 6);
            targetX = trayRect.Left - Width - 6;
            targetY = tbRect.Top + (tbHeight - Height) / 2;
        }
        else
        {
            var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1040);
            targetX = screen.Right - Width - 140;
            targetY = screen.Bottom + 4;
        }
        SetWindowPos(Handle, HWND_TOPMOST, targetX, targetY, Width, Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var borderPen = new Pen(Color.FromArgb(35, 53, 84), 1f);
        g.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);
        Color downColor = MonochromeText ? Color.White : Color.FromArgb(0, 230, 118);
        Color upColor = MonochromeText ? Color.FromArgb(226, 232, 240) : Color.FromArgb(29, 155, 240);
        var topRect = new Rectangle(4, 2, Width - 8, (Height / 2) - 1);
        var bottomRect = new Rectangle(4, (Height / 2) - 1, Width - 8, (Height / 2) - 1);
        TextRenderer.DrawText(g, downloadText, speedFont, topRect, downColor, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, uploadText, speedFont, bottomRect, upColor, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { positionTimer.Dispose(); speedFont.Dispose(); }
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
}
