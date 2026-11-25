using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace AorusPanel;

internal sealed class LcdService : IDisposable
{
    private const string Name = "AorusLcdService";
    private const uint Stopped = 1, StartPending = 2, StopPending = 3, RunningState = 4;
    private readonly ServiceHandle manager;
    private readonly ServiceHandle service;

    public LcdService(bool allowChanges)
    {
        manager = OpenSCManager(null, null, 1);
        if (manager.IsInvalid)
            throw Failure("Connect to Windows services");
        service = OpenService(manager, Name, allowChanges ? 0x34u : 4u);
        int error = Marshal.GetLastWin32Error();
        if (service.IsInvalid && error != 1060)
        {
            Dispose();
            throw Failure("Open the AORUS LCD service", error);
        }
    }

    public bool Running => State() == RunningState;
    public bool NeedsPause => State() != Stopped;

    public PanelState WhileStopped(Func<PanelState> action)
    {
        uint state = State();
        if (state is StartPending or StopPending)
            state = WaitForStable();
        if (state == Stopped)
            return action();

        bool stopRequested = false;
        try
        {
            if (!ControlService(service, 1, out _))
                throw Failure("Pause AORUS LCD telemetry");
            stopRequested = true;
            WaitFor(Stopped);
            return action();
        }
        finally
        {
            if (stopRequested)
            {
                if (State() == StopPending)
                    WaitFor(Stopped);
                if (!StartService(service, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() != 1056)
                    throw Failure("Restart AORUS LCD telemetry");
                WaitFor(RunningState);
            }
        }
    }

    private uint State()
    {
        if (service.IsInvalid)
            return Stopped;
        if (!QueryServiceStatus(service, out var status))
            throw Failure("Read the AORUS LCD service state");
        return status.State;
    }

    private uint WaitForStable()
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            uint state = State();
            if (state is not (StartPending or StopPending))
                return state;
            Thread.Sleep(150);
        }

        throw new IOException("The AORUS LCD service is still changing state.");
    }

    private void WaitFor(uint target)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (State() == target)
                return;
            Thread.Sleep(150);
        }

        throw new IOException(target == Stopped
            ? "The AORUS LCD service did not stop."
            : "The AORUS LCD service did not restart.");
    }

    private static IOException Failure(string action, int? code = null) =>
        new($"{action}: {new Win32Exception(code ?? Marshal.GetLastWin32Error()).Message}");

    public void Dispose()
    {
        service.Dispose();
        manager.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint Type, State, Controls, ExitCode, ServiceExitCode, CheckPoint, WaitHint;
    }

    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenService(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(ServiceHandle service, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(ServiceHandle service, uint command, out ServiceStatus status);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool StartService(ServiceHandle service, uint argumentCount, IntPtr arguments);
    [DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
