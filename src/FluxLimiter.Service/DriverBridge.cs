using FluxLimiter.Ipc;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace FluxLimiter.Service;

public sealed class DriverBridge : IDisposable
{
    private const string DevicePath = @"\\.\FluxLimiter";
    private SafeFileHandle? handle;

    public bool IsConnected => handle is { IsInvalid: false, IsClosed: false };

    public ServiceStatus GetStatus() => new(
        typeof(DriverBridge).Assembly.GetName().Version?.ToString() ?? "0.0.0",
        IsConnected,
        IsConnected ? "driver-detected-unverified" : "unavailable",
        false);

    public bool TryConnect()
    {
        if (IsConnected) return true;
        handle?.Dispose();
        handle = CreateFile(
            DevicePath,
            0xC0000000,
            0x00000003,
            IntPtr.Zero,
            3,
            0x40000000,
            IntPtr.Zero);
        return IsConnected;
    }

    public void Dispose() => handle?.Dispose();

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
