using System.Threading;
using System.Windows.Forms;

namespace FluxLimiter;

internal static class Program
{
    private const string MutexName = @"Local\FluxLimiter.UI";

    [STAThread]
    private static void Main()
    {
        using var singleInstance = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("FluxLimiter is already running.", "FluxLimiter");
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
