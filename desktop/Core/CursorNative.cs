using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace FreeRemote.Core;

/// <summary>
/// 光标的读取（被控端）和生成（控制端）。
/// 读取：GetCursorInfo 拿到当前光标，分别画在黑底和白底上，由两张图的差算出透明度，
///       这样彩色光标、单色光标（文本 I 形、反色光标）都能正确还原。
/// 生成：用带 Alpha 的 32 位 DIB 构造 HCURSOR。
/// </summary>
public static class CursorNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }

    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPV5HEADER
    {
        public int bV5Size, bV5Width, bV5Height; public short bV5Planes, bV5BitCount; public int bV5Compression, bV5SizeImage,
            bV5XPelsPerMeter, bV5YPelsPerMeter, bV5ClrUsed, bV5ClrImportant, bV5RedMask, bV5GreenMask, bV5BlueMask, bV5AlphaMask, bV5CSType;
        public int e1, e2, e3, e4, e5, e6, e7, e8, e9; // CIEXYZTRIPLE
        public int bV5GammaRed, bV5GammaGreen, bV5GammaBlue, bV5Intent, bV5ProfileData, bV5ProfileSize, bV5Reserved;
    }

    const int CURSOR_SHOWING = 1;
    const int DI_NORMAL = 3;

    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] static extern IntPtr CopyIcon(IntPtr h);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr h, out ICONINFO ii);
    [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr h, int w, int hgt, int step, IntPtr brush, int flags);
    [DllImport("user32.dll")] static extern IntPtr CreateIconIndirect(ref ICONINFO ii);
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out BITMAP bm);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPV5HEADER bi, int usage, out IntPtr bits, IntPtr section, int offset);
    [DllImport("gdi32.dll")] static extern IntPtr CreateBitmap(int w, int h, int planes, int bpp, IntPtr bits);

    /// <summary>Ok=false：读不到（输入桌面已切到 UAC 等“安全桌面”，本进程无权访问），此时位置无效</summary>
    public readonly record struct State(IntPtr Handle, bool Visible, int X, int Y, bool Ok = true);

    /// <summary>当前光标句柄、是否显示、屏幕位置（很便宜，可以高频调用）</summary>
    public static State Query()
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci)) return new State(IntPtr.Zero, false, 0, 0, false);
        return new State(ci.hCursor, (ci.flags & CURSOR_SHOWING) != 0 && ci.hCursor != IntPtr.Zero, ci.ptScreenPos.X, ci.ptScreenPos.Y);
    }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformation(IntPtr h, int index, System.Text.StringBuilder? info, int len, out int needed);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr h);

    /// <summary>当前接收输入的桌面是不是“安全桌面”（UAC 确认框、Ctrl+Alt+Del、锁屏）</summary>
    public static bool OnSecureDesktop()
    {
        var h = OpenInputDesktop(0, false, 0x0001 /*DESKTOP_READOBJECTS*/);
        if (h == IntPtr.Zero) return true; // 普通程序打不开 Winlogon 桌面
        try
        {
            var sb = new System.Text.StringBuilder(64);
            return GetUserObjectInformation(h, 2 /*UOI_NAME*/, sb, sb.Capacity * 2, out _) && !sb.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(h); }
    }

    public sealed record Shape(ulong Hash, int HotX, int HotY, byte[] Png);

    /// <summary>把光标画成带透明度的 PNG</summary>
    public static Shape? Capture(IntPtr hCursor)
    {
        if (hCursor == IntPtr.Zero) return null;
        IntPtr copy = CopyIcon(hCursor);
        if (copy == IntPtr.Zero) return null;
        try
        {
            if (!GetIconInfo(copy, out var ii)) return null;
            int w, h;
            try
            {
                if (ii.hbmColor == IntPtr.Zero)
                {
                    // 单色光标（文本 I 形、十字等）：直接解码 AND/XOR 掩码
                    var mono = CaptureMonochrome(ii.hbmMask, ii.xHotspot, ii.yHotspot);
                    if (mono != null) return mono;
                }
                if (ii.hbmColor != IntPtr.Zero && GetObject(ii.hbmColor, Marshal.SizeOf<BITMAP>(), out var bc) != 0) { w = bc.bmWidth; h = bc.bmHeight; }
                else if (GetObject(ii.hbmMask, Marshal.SizeOf<BITMAP>(), out var bm) != 0) { w = bm.bmWidth; h = bm.bmHeight / 2; }
                else return null;
            }
            finally
            {
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            }
            if (w <= 0 || h <= 0 || w > 256 || h > 256) return null;

            using var onBlack = Render(copy, w, h, Color.Black);
            using var onWhite = Render(copy, w, h, Color.White);
            using var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var rb = onBlack.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var rw = onWhite.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var ro = result.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            var pb = new byte[w * h * 4]; var pw = new byte[pb.Length]; var po = new byte[pb.Length];
            Marshal.Copy(rb.Scan0, pb, 0, pb.Length);
            Marshal.Copy(rw.Scan0, pw, 0, pw.Length);
            for (int i = 0; i < pb.Length; i += 4)
            {
                // 黑底值 = c·a，白底值 = c·a + (1-a)  →  a = 1 - (白 - 黑)
                int a = 255 - Math.Clamp(((pw[i] - pb[i]) + (pw[i + 1] - pb[i + 1]) + (pw[i + 2] - pb[i + 2])) / 3, 0, 255);
                // 反色像素（白底变黑、黑底变白）会算出负的差值，按不透明黑处理，再加白描边感不现实，这里取黑色
                if (pb[i] > pw[i] + 64) { po[i] = po[i + 1] = po[i + 2] = 0; po[i + 3] = 255; continue; }
                po[i + 3] = (byte)a;
                if (a > 0)
                {
                    po[i] = (byte)Math.Min(255, pb[i] * 255 / a);
                    po[i + 1] = (byte)Math.Min(255, pb[i + 1] * 255 / a);
                    po[i + 2] = (byte)Math.Min(255, pb[i + 2] * 255 / a);
                }
            }
            Marshal.Copy(po, 0, ro.Scan0, po.Length);
            onBlack.UnlockBits(rb); onWhite.UnlockBits(rw); result.UnlockBits(ro);

            using var ms = new MemoryStream();
            result.Save(ms, ImageFormat.Png);
            var png = ms.ToArray();
            GetIconInfo(copy, out var ii2);
            if (ii2.hbmColor != IntPtr.Zero) DeleteObject(ii2.hbmColor);
            if (ii2.hbmMask != IntPtr.Zero) DeleteObject(ii2.hbmMask);
            var hash = BitConverter.ToUInt64(SHA256.HashData(po), 0) ^ (ulong)(ii2.xHotspot * 31 + ii2.yHotspot);
            return new Shape(hash, ii2.xHotspot, ii2.yHotspot, png);
        }
        finally { DestroyIcon(copy); }
    }

    /// <summary>
    /// 单色光标：掩码位图高度是光标的两倍，上半是 AND、下半是 XOR。
    ///   AND=1 XOR=0 透明；AND=0 XOR=0 黑；AND=0 XOR=1 白；AND=1 XOR=1 反色。
    /// 反色在本地没法真正实现（不知道下面是什么颜色），画成黑色并加白色描边，深浅背景都看得清。
    /// </summary>
    static Shape? CaptureMonochrome(IntPtr hbmMask, int hotX, int hotY)
    {
        using var mask = Image.FromHbitmap(hbmMask);
        int w = mask.Width, h = mask.Height / 2;
        if (w <= 0 || h <= 0 || w > 256) return null;
        var px = new byte[w * h * 4];
        var invert = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool and = mask.GetPixel(x, y).R > 127, xor = mask.GetPixel(x, y + h).R > 127;
                int i = (y * w + x) * 4;
                if (and && !xor) continue;                                   // 透明
                if (and && xor) { invert[y * w + x] = true; px[i + 3] = 255; continue; } // 反色 → 黑
                byte c = xor ? (byte)255 : (byte)0;
                px[i] = px[i + 1] = px[i + 2] = c; px[i + 3] = 255;
            }
        // 给反色部分描一圈白边
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (px[i + 3] != 0) continue;
                bool near = false;
                for (int dy = -1; dy <= 1 && !near; dy++)
                    for (int dx = -1; dx <= 1 && !near; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx >= 0 && ny >= 0 && nx < w && ny < h && invert[ny * w + nx]) near = true;
                    }
                if (near) { px[i] = px[i + 1] = px[i + 2] = 255; px[i + 3] = 230; }
            }

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(px, 0, d.Scan0, px.Length);
        bmp.UnlockBits(d);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        var hash = BitConverter.ToUInt64(SHA256.HashData(px), 0) ^ (ulong)(hotX * 31 + hotY);
        return new Shape(hash, hotX, hotY, ms.ToArray());
    }

    static Bitmap Render(IntPtr icon, int w, int h, Color bg)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(bg);
        var hdc = g.GetHdc();
        try { DrawIconEx(hdc, 0, 0, icon, w, h, 0, IntPtr.Zero, DI_NORMAL); }
        finally { g.ReleaseHdc(hdc); }
        return bmp;
    }

    /// <summary>由 PNG + 热点生成 HCURSOR（调用方负责 DestroyIcon）</summary>
    public static IntPtr CreateCursor(byte[] png, int hotX, int hotY)
    {
        using var ms = new MemoryStream(png);
        using var src = new Bitmap(ms);
        return CreateCursor(src, hotX, hotY);
    }

    public static IntPtr CreateCursor(Bitmap src, int hotX, int hotY)
    {
        int w = src.Width, h = src.Height;
        var bi = new BITMAPV5HEADER
        {
            bV5Size = Marshal.SizeOf<BITMAPV5HEADER>(), bV5Width = w, bV5Height = -h, bV5Planes = 1, bV5BitCount = 32,
            bV5Compression = 3 /*BI_BITFIELDS*/, bV5RedMask = 0x00FF0000, bV5GreenMask = 0x0000FF00, bV5BlueMask = 0x000000FF,
            bV5AlphaMask = unchecked((int)0xFF000000),
        };
        var color = CreateDIBSection(IntPtr.Zero, ref bi, 0, out var bits, IntPtr.Zero, 0);
        var data = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var buf = new byte[w * h * 4];
        Marshal.Copy(data.Scan0, buf, 0, buf.Length);
        src.UnlockBits(data);
        Marshal.Copy(buf, 0, bits, buf.Length);
        var mask = CreateBitmap(w, h, 1, 1, IntPtr.Zero);
        var ii = new ICONINFO { fIcon = false, xHotspot = hotX, yHotspot = hotY, hbmMask = mask, hbmColor = color };
        var cur = CreateIconIndirect(ref ii);
        DeleteObject(color);
        DeleteObject(mask);
        return cur;
    }

    [DllImport("user32.dll")] static extern IntPtr CreateCursor(IntPtr inst, int x, int y, int w, int h, byte[] andPlane, byte[] xorPlane);

    /// <summary>
    /// 完全透明的光标。必须用单色光标（AND 全 1、XOR 全 0）：
    /// 带 Alpha 的彩色透明光标在 Sunshine 画面里会被画成黑方块（已实测）。
    /// </summary>
    public static IntPtr CreateBlankCursor()
    {
        var and = new byte[32 * 32 / 8];
        Array.Fill(and, (byte)0xFF);
        return CreateCursor(IntPtr.Zero, 0, 0, 32, 32, and, new byte[and.Length]);
    }

    public static void Destroy(IntPtr h) { if (h != IntPtr.Zero) DestroyIcon(h); }
    public static IntPtr Copy(IntPtr h) => CopyIcon(h);
}
