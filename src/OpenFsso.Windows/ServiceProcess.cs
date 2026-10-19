using System.ComponentModel;
using Windows.Win32;
using Windows.Win32.System.Services;

namespace OpenFsso.Windows;

public static class ServiceProcess
{
    // The process ID of a running service, or 0 while it is not running.
    public static unsafe uint RunningPid(string name)
    {
        using var manager = PInvoke.OpenSCManager(null, null, PInvoke.SC_MANAGER_CONNECT);
        if (manager.IsInvalid)
        {
            throw new Win32Exception();
        }

        using var service = PInvoke.OpenService(manager, name, PInvoke.SERVICE_QUERY_STATUS);
        if (service.IsInvalid)
        {
            throw new Win32Exception();
        }

        SERVICE_STATUS_PROCESS status;
        uint needed;
        if (!PInvoke.QueryServiceStatusEx((SC_HANDLE)service.DangerousGetHandle(), SC_STATUS_TYPE.SC_STATUS_PROCESS_INFO, (byte*)&status, (uint)sizeof(SERVICE_STATUS_PROCESS), &needed))
        {
            throw new Win32Exception();
        }

        return status.dwCurrentState == SERVICE_STATUS_CURRENT_STATE.SERVICE_RUNNING ? status.dwProcessId : 0;
    }
}
