using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MouseBattery;

/// <summary>
/// 托盘图标：上半部是设备轮廓（鼠标/键盘/耳机/手柄/通用），下半部是电量条。
/// 多设备同屏时，光靠电池形状分不清是哪一台，所以把设备类型直接画进图标里。
/// </summary>
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

    public static Icon Create(DeviceKind kind, int? percent, bool connected)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);

        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            Color color = ColorFor(percent, connected);

            // 设备轮廓：上 21.5px，水平居中。DeviceIcons 内部按 24×24 设计，会等比缩放
            const float glyphSize = 21.5f;
            var glyphRect = new RectangleF((size - glyphSize) / 2f, 0.4f, glyphSize, glyphSize);
            DeviceIcons.Draw(g, kind, glyphRect, color);

            // 底部电量条：外框 + 按比例填充。16px 缩放下只有 3px 高，所以画得比常规粗一些
            var track = new RectangleF(2.6f, 24.6f, 26.8f, 6f);
            using (GraphicsPath trackPath = RoundedRect(track, 3f))
            using (var pen = new Pen(color, 1.35f) { LineJoin = LineJoin.Round })
            {
                g.DrawPath(pen, trackPath);
            }

            if (percent is int p && p > 0)
            {
                float innerW = track.Width - 3f;
                float w = innerW * Math.Clamp(p, 0, 100) / 100f;
                if (w < 2f) w = 2f;

                var fillRect = new RectangleF(track.X + 1.5f, track.Y + 1.5f, w, track.Height - 3f);
                using GraphicsPath fillPath = RoundedRect(fillRect, 1.5f);
                using var fill = new SolidBrush(color);
                g.FillPath(fill, fillPath);
            }

            if (!connected)
            {
                // 断开：一道斜杠压在设备轮廓上，一眼看出没连上
                using var slash = new Pen(Color.FromArgb(216, 58, 44), 2.4f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                };
                g.DrawLine(slash, 8f, 21f, 24f, 5.5f);
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

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2f;
        if (d > rect.Width) d = rect.Width;
        if (d > rect.Height) d = rect.Height;

        path.AddArc(rect.X, rect.Y, d, d, 180f, 90f);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270f, 90f);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0f, 90f);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90f, 90f);
        path.CloseFigure();
        return path;
    }
}
