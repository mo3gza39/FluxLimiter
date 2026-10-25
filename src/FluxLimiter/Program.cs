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
            var mainForm = new MainForm();
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
}
