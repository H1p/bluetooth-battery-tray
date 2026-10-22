using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MouseBattery;

/// <summary>把电量画成 16px 托盘图标里能看清的电池形状。</summary>
internal static class BatteryIcon
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>低于此值显示红色，由设置界面配置。</summary>
    public static int RedBelow { get; private set; } = 20;

    /// <summary>达到此值显示绿色，中间为黄色。</summary>
    public static int GreenFrom { get; private set; } = 50;

    public static void Configure(int redBelow, int greenFrom)
    {
        RedBelow = Math.Clamp(redBelow, 1, 99);
        GreenFrom = Math.Clamp(greenFrom, RedBelow + 1, 100);
    }

    public static Color ColorFor(int? percent, bool connected)
    {
        if (!connected || percent is null) return Color.FromArgb(158, 158, 158);
        if (percent < RedBelow) return Color.FromArgb(216, 58, 44);
        if (percent < GreenFrom) return Color.FromArgb(224, 158, 18);
        return Color.FromArgb(46, 168, 74);
    }

    public static Icon Create(int? percent, bool connected)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);

        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            Color color = ColorFor(percent, connected);

            // 电池：外壳 19×14，右侧一个小正极凸起，整体在 32px 画布里略微内缩
            var bodyRect = new RectangleF(4f, 9f, 19f, 14f);
            var capRect = new RectangleF(23.4f, 12.6f, 3.8f, 6.8f);

            using (var shell = new GraphicsPath())
            {
                AddRoundedRect(shell, bodyRect, 2.6f);
                using var pen = new Pen(color, 1.8f) { LineJoin = LineJoin.Round };
                g.DrawPath(pen, shell);
            }

            using (var capBrush = new SolidBrush(color))
            {
                g.FillRectangle(capBrush, capRect);
            }

            // 内部填充：随电量增减，四周留出空隙不压住边框
            if (percent is int p && p > 0)
            {
                const float innerX = 7.2f;
                const float innerMaxW = 12.6f;
                const float innerY = 11.6f;
                const float innerH = 8.8f;

                float w = innerMaxW * Math.Clamp(p, 0, 100) / 100f;
                if (w < 2f) w = 2f;

                using var fill = new SolidBrush(color);
                var fillRect = new RectangleF(innerX, innerY, w, innerH);
                using var fillPath = new GraphicsPath();
                AddRoundedRect(fillPath, fillRect, 1.1f);
                g.FillPath(fill, fillPath);
            }

            if (!connected)
            {
                // 断开：画一道斜杠，一眼看出没连上
                using var slash = new Pen(Color.FromArgb(216, 58, 44), 2.4f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                };
                g.DrawLine(slash, 7.5f, 22f, 20.5f, 9.5f);
            }
        }

        return ToIcon(bmp);
    }

    private static Icon ToIcon(Bitmap bmp)
    {
        IntPtr handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static void AddRoundedRect(GraphicsPath path, RectangleF rect, float radius)
    {
        float d = radius * 2f;
        if (d > rect.Width) d = rect.Width;
        if (d > rect.Height) d = rect.Height;

        path.AddArc(rect.X, rect.Y, d, d, 180f, 90f);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270f, 90f);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0f, 90f);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90f, 90f);
        path.CloseFigure();
    }
}
