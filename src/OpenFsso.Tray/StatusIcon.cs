using System.Runtime.InteropServices;
using OpenFsso.Core;
using Windows.Win32.Foundation;

namespace OpenFsso.Tray;

internal static unsafe class StatusIcon
{
    internal static HICON Create(ClientIndicator indicator, int size, bool light)
    {
        uint foreground = light ? 0x002e3440u : 0x00eff3f8u;
        uint dot = indicator switch
        {
            ClientIndicator.Connected => 0x0034c77bu,
            ClientIndicator.Working => 0x00f2b83fu,
            ClientIndicator.Error => 0x00ee5964u,
            ClientIndicator.OffFabric => 0x004a9effu,
            _ => 0x0095a1b0u
        };
        int[] pixels = new int[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int alpha = 0, red = 0, green = 0, blue = 0;
                for (int sy = 0; sy < 4; sy++)
                {
                    for (int sx = 0; sx < 4; sx++)
                    {
                        double px = (x + (sx + 0.5) / 4) / size, py = (y + (sy + 0.5) / 4) / size;
                        bool Outer(double inset) => Shield(px, py, inset);
                        bool shield = Outer(0) && !Outer(0.065);
                        bool head = Square(px - .41) + Square(py - .35) < Square(.085);
                        bool shoulders = Square((px - .41) / .16) + Square((py - .63) / .16) < 1 && py < .65;
                        double distance = Square(px - .76) + Square(py - .77);
                        uint color;
                        if (distance < Square(.205))
                        {
                            color = dot;
                        }
                        else if (distance < Square(.25))
                        {
                            continue; // Transparent separator around the status dot.
                        }
                        else if (shield || head || shoulders)
                        {
                            color = foreground;
                        }
                        else
                        {
                            continue;
                        }

                        alpha += 255;
                        red += (int)((color >> 16) & 255);
                        green += (int)((color >> 8) & 255);
                        blue += (int)(color & 255);
                    }
                }

                pixels[y * size + x] = unchecked((int)((uint)(alpha / 16) << 24 | (uint)(red / 16) << 16 | (uint)(green / 16) << 8 | (uint)(blue / 16)));
            }
        }

        var header = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = size,
                biHeight = -size,
                biPlanes = 1,
                biBitCount = 32
            }
        };
        void* bits;
        var colorBitmap = CreateDIBSection(default, &header, DIB_USAGE.DIB_RGB_COLORS, &bits, default, 0);
        if (colorBitmap.IsNull)
        {
            throw new System.ComponentModel.Win32Exception();
        }

        HBITMAP mask = default;
        try
        {
            Marshal.Copy(pixels, 0, (nint)bits, pixels.Length);
            fixed (byte* empty = new byte[((size + 15) / 16) * 2 * size])
            {
                mask = CreateBitmap(size, size, 1, 1, empty);
            }

            if (mask.IsNull)
            {
                throw new System.ComponentModel.Win32Exception();
            }

            var info = new ICONINFO
            {
                fIcon = true,
                hbmColor = colorBitmap,
                hbmMask = mask
            };
            var icon = CreateIconIndirect(&info);
            if (icon.IsNull)
            {
                throw new System.ComponentModel.Win32Exception();
            }

            return icon;
        }
        finally
        {
            DeleteObject(colorBitmap);
            if (!mask.IsNull)
            {
                DeleteObject(mask);
            }
        }
    }

    private static double Square(double value) => value * value;
    private static bool Shield(double x, double y, double inset)
    {
        x = Math.Abs(x - .41);
        double top = .10 + inset, bottom = .91 - inset, width = .32 - inset;
        if (y < top || y > bottom || x > width)
        {
            return false;
        }

        if (y < .25)
        {
            return y >= top + x * .28;
        }

        return y <= bottom - x * 1.10;
    }
}
