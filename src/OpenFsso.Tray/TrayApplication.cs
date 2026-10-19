using System.Runtime.InteropServices;
using Microsoft.Win32;
using OpenFsso.Core;
using Windows.Win32.Foundation;

namespace OpenFsso.Tray;

internal sealed class TrayApplication : IDisposable
{
    private const uint Callback = WM_APP + 1, Updated = WM_APP + 2;
    private const string ClassName = "OpenFsso.Tray.Status";
    private readonly WNDPROC procedure;
    private readonly CancellationTokenSource stopping = new();
    private readonly Dictionary<(ClientIndicator, int, bool), HICON> icons = new();
    private readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly HWND window;
    private NOTIFYICONDATAW data;
    private ClientStatusView current = ClientStatusView.Create(null, DateTimeOffset.UtcNow);
    private Task? poll;
    private bool registered;

    public unsafe TrayApplication()
    {
        procedure = HandleMessage;
        var instance = GetModuleHandle((string?)null);
        fixed (char* name = ClassName)
        {
            var cls = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = procedure,
                hInstance = (HINSTANCE)instance.DangerousGetHandle(),
                lpszClassName = name
            };
            if (RegisterClassEx(cls) == 0)
            {
                throw new System.ComponentModel.Win32Exception();
            }
        }

        window = CreateWindowEx(0, ClassName, "OpenFSSO status", 0, 0, 0, 0, 0, HWND.Null, null, instance, null);
        if (window.IsNull)
        {
            throw new System.ComponentModel.Win32Exception();
        }

        // NIF_SHOWTIP lets the Shell draw the standard notification tooltip.
        data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = window,
            uID = 1,
            uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP | NOTIFY_ICON_DATA_FLAGS.NIF_GUID,
            uCallbackMessage = Callback,
            guidItem = new Guid("c47b4ae0-a336-48be-b0d2-74c689e70a71")
        };
        AddIcon();
    }

    public void Run()
    {
        poll = Task.Run(async () =>
        {
            while (!stopping.IsCancellationRequested)
            {
                var status = await StatusReader.Read(stopping.Token);
                Volatile.Write(ref current, ClientStatusView.Create(status, DateTimeOffset.UtcNow));
                PostMessage(window, Updated, 0, 0);
                try
                {
                    await Task.Delay(2000, stopping.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });
        while (GetMessage(out var message, HWND.Null, 0, 0) > 0)
        {
            TranslateMessage(message);
            DispatchMessage(message);
        }
    }

    private void AddIcon()
    {
        data.hIcon = Icon();
        data.szTip = current.Tooltip();
        registered = Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, data);
        if (registered)
        {
            data.Anonymous.uVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_SETVERSION, data);
        }
    }

    private HICON Icon()
    {
        int size = GetSystemMetricsForDpi(SYSTEM_METRICS_INDEX.SM_CXSMICON, GetDpiForWindow(window));
        bool light = (int?)Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) == 1;
        var key = (current.Indicator, Math.Clamp(size, 16, 64), light);
        if (!icons.TryGetValue(key, out var icon))
        {
            icons[key] = icon = StatusIcon.Create(key.Item1, key.Item2, light);
        }

        return icon;
    }

    private void Refresh()
    {
        if (!registered)
        {
            AddIcon();
            return;
        }

        var icon = Icon();
        string tip = current.Tooltip();
        if (icon != data.hIcon || tip != data.szTip.ToString())
        {
            data.hIcon = icon;
            data.szTip = tip;
            registered = Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, data);
        }
    }

    private LRESULT HandleMessage(HWND hwnd, uint message, WPARAM wparam, LPARAM lparam)
    {
        if (message == taskbarCreated)
        {
            registered = false;
            AddIcon();
            return default;
        }

        switch (message)
        {
            // Each status poll also retries registration while Explorer is not ready.
            case Updated:
            case WM_SETTINGCHANGE:
            case WM_THEMECHANGED:
            case WM_DPICHANGED:
                Refresh();
                return default;
            case Callback when (uint)(lparam.Value & 0xffff) == WM_CONTEXTMENU:
                Menu(wparam);
                return default;
            case WM_CLOSE:
                DestroyWindow(window);
                return default;
            case WM_DESTROY:
                PostQuitMessage(0);
                return default;
            case WM_QUERYENDSESSION:
                return (LRESULT)1;
            case WM_ENDSESSION:
                if (wparam.Value != 0)
                {
                    DestroyWindow(window);
                }

                return default;
        }

        return DefWindowProc(hwnd, message, wparam, lparam);
    }

    private unsafe void Menu(WPARAM anchor)
    {
        var menu = CreatePopupMenu();
        int result;
        try
        {
            fixed (char* exit = "Exit status icon")
            {
                AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, 1, exit);
            }

            SetForegroundWindow(window);
            result = TrackPopupMenu(menu, TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON, (short)(anchor.Value & 0xffff), (short)((anchor.Value >> 16) & 0xffff), 0, window, null);
        }
        finally
        {
            DestroyMenu(menu);
        }

        if (result == 1)
        {
            DestroyWindow(window);
        }

        PostMessage(window, WM_NULL, 0, 0);
    }

    public void Dispose()
    {
        stopping.Cancel();
        poll?.GetAwaiter().GetResult();
        Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, data);
        foreach (var icon in icons.Values)
        {
            DestroyIcon(icon);
        }

        stopping.Dispose();
        GC.KeepAlive(procedure);
    }
}
