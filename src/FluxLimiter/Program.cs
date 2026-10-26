using System.Threading;
using System.Windows.Forms;

namespace FluxLimiter;

internal static class Program
{
    private const string MutexName = @"Local\FluxLimiter.UI";
    internal const string ShowEventName = @"Local\FluxLimiter.Show";

    [STAThread]
    private static void Main()
    {
        Mutex? singleInstance = null;
        bool ownsMutex = false;
        try
        {
            singleInstance = new Mutex(true, MutexName, out bool createdNew);
            ownsMutex = createdNew;
            if (!createdNew)
            {
                try { ownsMutex = singleInstance.WaitOne(0); }
                catch (AbandonedMutexException) { ownsMutex = true; }
            }

            if (!ownsMutex)
            {
                try { using var existing = EventWaitHandle.OpenExisting(ShowEventName); existing.Set(); }
                catch (WaitHandleCannotBeOpenedException) { MessageBox.Show("FluxLimiter is already running.", "FluxLimiter"); }
                return;
            }

            ApplicationConfiguration.Initialize();
            using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            using var listenerStop = new CancellationTokenSource();
            MainForm mainForm;
            try
            {
                mainForm = new MainForm();
            }
            catch (Exception ex)
            {
                ReportStartupFailure(ex);
                return;
            }
            var listener = new Thread(() =>
            {
                while (!listenerStop.IsCancellationRequested)
                {
                    if (!showSignal.WaitOne(500) || listenerStop.IsCancellationRequested) continue;
                    try
                    {
                        if (mainForm.IsHandleCreated && !mainForm.IsDisposed)
                            mainForm.BeginInvoke((Action)(() =>
                            {
                                mainForm.Show();
                                if (mainForm.WindowState == FormWindowState.Minimized) mainForm.WindowState = FormWindowState.Normal;
                                mainForm.Activate();
                            }));
                    }
                    catch (InvalidOperationException) { }
                }
            }) { IsBackground = true, Name = "FluxLimiter second-launch listener" };
            listener.Start();
            try { Application.Run(mainForm); }
            finally { listenerStop.Cancel(); showSignal.Set(); listener.Join(1000); }
        }
        finally
        {
            if (ownsMutex) { try { singleInstance?.ReleaseMutex(); } catch (ApplicationException) { } }
            singleInstance?.Dispose();
        }
    }
    private static void ReportStartupFailure(Exception ex)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluxLimiter");
            Directory.CreateDirectory(dir);
            string logPath = Path.Combine(dir, "startup-crash.log");
            File.AppendAllText(logPath, $"[{DateTimeOffset.Now:O}] {ex}\\n\\n");
            MessageBox.Show($"FluxLimiter could not start.\\n\\nA crash log was written to:\\n{logPath}\\n\\n{ex.GetType().Name}: {ex.Message}", "FluxLimiter startup error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            MessageBox.Show($"FluxLimiter could not start.\\n\\n{ex.GetType().Name}: {ex.Message}", "FluxLimiter startup error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

}
