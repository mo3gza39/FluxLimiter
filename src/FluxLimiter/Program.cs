using System.Threading;
using System.Windows.Forms;

namespace FluxLimiter;

internal static class Program
{
    private const string MutexName = @"Local\FluxLimiter.UI";

    [STAThread]
    private static void Main()
    {
        Mutex? singleInstance = null;
        bool ownsMutex = false;
        try
        {
            singleInstance = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            ownsMutex = createdNew;
            if (!createdNew)
            {
                try
                {
                    ownsMutex = singleInstance.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    // The previous process crashed while owning the mutex; ownership transfers here.
                    ownsMutex = true;
                }
            }

            if (!ownsMutex)
            {
                MessageBox.Show("FluxLimiter is already running.", "FluxLimiter");
                return;
            }

            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
        finally
        {
            if (ownsMutex)
            {
                try { singleInstance?.ReleaseMutex(); }
                catch (ApplicationException) { }
            }
            singleInstance?.Dispose();
        }
    }
}
