using OpenFsso.Core;
using Windows.Win32.Foundation;

namespace OpenFsso.Tray;
// The standard comctl32 tooltip in tracking mode. The control supplies the
// tooltip theme, font, DPI scaling, outline and shadow; this class only places
// it above the Shell icon rectangle, including inside the overflow flyout.
internal sealed unsafe class StatusPopup : IDisposable
{
    private readonly HWND owner;
    private ClientStatusView? current;
    private RECT anchor;
    internal HWND Handle
    {
        get;
    }
    internal bool Visible
    {
        get; private set;
    }
    internal int ShowCount
    {
        get; private set;
    }
    internal int HideCount
    {
        get; private set;
    }

    internal StatusPopup(HWND owner, Action<string>? trace = null)
    {
        this.owner = owner;
        trace?.Invoke("creating tooltip control");
        Handle = CreateWindowEx(WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW, TOOLTIPS_CLASS, null,
            WINDOW_STYLE.WS_POPUP | (WINDOW_STYLE)(TTS_NOPREFIX | TTS_ALWAYSTIP | TTS_NOFADE | TTS_NOANIMATE), 0, 0, 0, 0, owner, null, GetModuleHandle((string?)null), null);
        if (Handle.IsNull)
        {
            throw new System.ComponentModel.Win32Exception();
        }

        fixed (char* empty = "")
        {
            var tool = Tool(empty);
            SendMessage(Handle, TTM_ADDTOOLW, 0, (nint)(&tool));
        }

        MaximumWidth(GetDpiForWindow(owner));
    }

    // A maximum width also enables the control's multi-line layout.
    private void MaximumWidth(uint dpi) => SendMessage(Handle, TTM_SETMAXTIPWIDTH, 0, (nint)(360 * dpi / 96));

    private TTTOOLINFOW Tool(char* text) => new()
    {
        cbSize = (uint)sizeof(TTTOOLINFOW),
        uFlags = TOOLTIP_FLAGS.TTF_TRACK | TOOLTIP_FLAGS.TTF_ABSOLUTE,
        hwnd = owner,
        uId = 1,
        lpszText = text
    };

    internal void Update(ClientStatusView view)
    {
        if (current?.Title == view.Title && current?.Text == view.Text)
        {
            return;
        }

        static string LayoutKey(string text) => string.Join('\n', text.Split('\n').Where(x => !x.StartsWith("Last sent: ", StringComparison.Ordinal)));
        bool layout = current == null || current.Title != view.Title || LayoutKey(current.Text) != LayoutKey(view.Text);
        if (current?.Title != view.Title)
        {
            fixed (char* title = view.Title)
            {
                SendMessage(Handle, TTM_SETTITLEW, (nuint)EDITBALLOONTIP_ICON.TTI_NONE, (nint)title);
            }
        }

        current = view;
        if (Visible)
        {
            Fit(layout);
        }
        else
        {
            SetText(view.Text);
        }
    }

    // The shown control sizes its window to the title and text, so measure that
    // window: TTM_GETBUBBLESIZE reports a single text line. Elapsed-time ticks
    // only re-center a changed width over the icon.
    private void Fit(bool layout)
    {
        int width = Size().Width;
        SetText(current!.Text);
        if (layout || Size().Width != width)
        {
            Place();
        }
    }

    private void SetText(string text)
    {
        fixed (char* value = text)
        {
            var tool = Tool(value);
            SendMessage(Handle, TTM_UPDATETIPTEXTW, 0, (nint)(&tool));
        }

        SendMessage(Handle, TTM_UPDATE, 0, 0); // Resize now so the window can be measured.
    }

    private (int Width, int Height) Size()
    {
        GetWindowRect(Handle, out var bounds);
        return (bounds.right - bounds.left, bounds.bottom - bounds.top);
    }

    internal void Show(RECT icon, uint dpi)
    {
        if (Visible)
        {
            return;
        }

        anchor = icon;
        MaximumWidth(dpi);
        var tool = Tool(null);
        SendMessage(Handle, TTM_TRACKACTIVATE, 1, (nint)(&tool));
        Visible = true;
        ShowCount++;
        Fit(layout: true);
    }

    private void Place()
    {
        var (width, height) = Size();
        var monitor = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        GetMonitorInfo(MonitorFromRect(anchor, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST), ref monitor);
        var work = monitor.rcWork;
        int gap = (int)(8 * GetDpiForWindow(owner) / 96);
        int x = Math.Clamp((anchor.left + anchor.right - width) / 2, work.left, Math.Max(work.left, work.right - width));
        int y = anchor.top - height - gap;
        if (y < work.top)
        {
            y = anchor.bottom + gap;
        }

        y = Math.Clamp(y, work.top, Math.Max(work.top, work.bottom - height));
        SendMessage(Handle, TTM_TRACKPOSITION, 0, (nint)((ushort)x | (ushort)y << 16));
    }

    internal void Hide()
    {
        if (!Visible)
        {
            return;
        }

        var tool = Tool(null);
        SendMessage(Handle, TTM_TRACKACTIVATE, 0, (nint)(&tool));
        Visible = false;
        HideCount++;
    }

    internal string Text => current?.Text ?? "";
    internal string FontFace
    {
        get
        {
            var font = (HGDIOBJ)(nint)SendMessage(Handle, WM_GETFONT, 0, 0).Value;
            LOGFONTW face;
            return !font.IsNull && GetObject(font, sizeof(LOGFONTW), &face) != 0 ? face.lfFaceName.ToString() : "";
        }
    }

    public void Dispose() => DestroyWindow(Handle);
}
