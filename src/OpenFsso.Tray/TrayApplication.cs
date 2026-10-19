using System.Runtime.InteropServices;
using Microsoft.Win32;
using OpenFsso.Core;
using Windows.Win32.Foundation;

namespace OpenFsso.Tray;

internal sealed class TrayApplication : IDisposable
{
    internal const uint Callback = WM_APP + 1, Updated = WM_APP + 2;
    internal const uint NIN_KEYSELECT = NIN_SELECT | NINF_KEY; // An SDK macro of two metadata constants.
    private const string ClassName = "OpenFsso.Tray.Status";
    private readonly WNDPROC procedure;
    private readonly CancellationTokenSource stopping = new();
    private readonly Dictionary<(ClientIndicator, int, bool), HICON> icons = new();
    private readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly HWND window;
    private readonly StatusPopup popup;
    private NOTIFYICONDATAW data;
    private ClientStatusView current = ClientStatusView.Create(null, DateTimeOffset.UtcNow);
    private Task? poll;
    private bool registered;
    internal int ShellUpdateCount
    {
        get; private set;
    }
    internal HWND WindowHandle => window;
    internal HWND TooltipHandle => popup.Handle;
    internal HICON CurrentIconHandle => data.hIcon;
    internal string PopupText => popup.Text;
    internal string PopupFontFace => popup.FontFace;
    internal int PopupShowCount => popup.ShowCount;
    internal int PopupHideCount => popup.HideCount;
    internal bool Registered => registered;
    internal ClientStatusView View => Volatile.Read(ref current);

    public unsafe TrayApplication(Guid? iconId = null, Action<string>? trace = null)
    {
        trace?.Invoke("registering message window");
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

        popup = new StatusPopup(window, trace);
        data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = window,
            uID = 1,
            uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_GUID,
            uCallbackMessage = Callback,
            guidItem = iconId ?? new Guid("c47b4ae0-a336-48be-b0d2-74c689e70a71")
        };
        trace?.Invoke("registering Shell icon");
        AddIcon();
        trace?.Invoke("Shell icon registered");
        SetTimer(window, 1, 2000, null); // Also retries registration when Explorer is not ready.
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
        data.szTip = Tip(current.Title);
        registered = Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, data);
        if (registered)
        {
            data.Anonymous.uVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_SETVERSION, data);
        }
    }

    private static string Tip(string title) => title.Length < 128 ? title : title[..127];

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
        string tip = Tip(current.Title);
        if (icon != data.hIcon || tip != data.szTip.ToString())
        {
            data.hIcon = icon;
            data.szTip = tip;
            registered = Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, data);
            ShellUpdateCount++;
        }

        if (popup.Visible)
        {
            popup.Update(current); // Text only; never reopen or move for elapsed seconds.
        }
    }

    private void Show()
    {
        if (popup.Visible)
        {
            return;
        }

        popup.Update(current);
        popup.Show(IconRect(), GetDpiForWindow(window));
        SetTimer(window, 2, 15000, null); // Keyboard/click invocation also dismisses without a mouse-leave event.
    }

    private unsafe RECT IconRect()
    {
        var identity = new NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)sizeof(NOTIFYICONIDENTIFIER),
            hWnd = window,
            uID = 1,
            guidItem = data.guidItem
        };
        if (Shell_NotifyIconGetRect(identity, out var icon).Failed)
        {
            // The callback coordinates are not reliable in every Shell event.
            // A missing/hidden Shell rectangle falls back to the actual pointer.
            GetCursorPos(out var cursor);
            icon = new RECT { left = cursor.X, right = cursor.X, top = cursor.Y, bottom = cursor.Y };
        }

        return icon;
    }

    private void Hide()
    {
        popup.Hide();
        KillTimer(window, 2);
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
            case Updated:
            case WM_SETTINGCHANGE:
            case WM_THEMECHANGED:
            case WM_DPICHANGED:
                Refresh();
                return default;
            case Callback:
                switch ((uint)(lparam.Value & 0xffff))
                {
                    case NIN_POPUPOPEN:
                    case NIN_SELECT:
                    case NIN_KEYSELECT:
                        Show();
                        break;
                    case NIN_POPUPCLOSE:
                        Hide();
                        break;
                    case WM_CONTEXTMENU:
                        Menu(wparam);
                        break;
                }

                return default;
            case WM_TIMER:
                if (wparam.Value == 2)
                {
                    Hide();
                }
                else
                {
                    Refresh();
                }

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
        Hide();
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
        popup.Dispose();
        foreach (var icon in icons.Values)
        {
            DestroyIcon(icon);
        }

        stopping.Dispose();
        GC.KeepAlive(procedure);
    }
}
