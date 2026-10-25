using FluxLimiter.Ipc;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace FluxLimiter.Service;

public sealed class DriverBridge : IDisposable
{
    private const string DevicePath = @"\\.\FluxLimiter";
    private readonly object sync = new();
    private SafeFileHandle? handle;

    public bool IsConnected
    {
        get
        {
            lock (sync)
            {
                return handle is { IsInvalid: false, IsClosed: false };
            }
        }
    }

    public ServiceStatus GetStatus()
    {
        bool connected = TryConnect();
        return new(
            typeof(DriverBridge).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            connected,
            connected ? "driver-detected-unverified" : "unavailable",
            false);
    }

    public bool TryConnect()
    {
        lock (sync)
        {
            if (handle is { IsInvalid: false, IsClosed: false }) return true;
            handle?.Dispose();
            handle = null;

            var candidate = CreateFile(
                DevicePath,
                0xC0000000,
                0x00000003,
                IntPtr.Zero,
                3,
                0x40000000,
                IntPtr.Zero);

            if (candidate.IsInvalid)
            {
                candidate.Dispose();
                return false;
            }

            handle = candidate;
            return true;
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            handle?.Dispose();
            handle = null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);
}
