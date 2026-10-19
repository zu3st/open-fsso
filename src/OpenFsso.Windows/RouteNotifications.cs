using System.ComponentModel;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.IpHelper;

namespace OpenFsso.Windows;

public sealed unsafe class RouteNotifications : IDisposable
{
    private HANDLE handle;
    private readonly PIPFORWARD_CHANGE_CALLBACK callback;
    public RouteNotifications(Action changed)
    {
        callback = (_, _, _) => changed();
        HANDLE registered;
        var result = PInvoke.NotifyRouteChange2(0, callback, null, false, &registered);
        if (result != 0)
        {
            throw new Win32Exception((int)result);
        }

        handle = registered;
    }

    public void Dispose()
    {
        if (handle != default)
        {
            PInvoke.CancelMibChangeNotify2(handle);
            handle = default;
        }

        GC.KeepAlive(callback);
    }
}
