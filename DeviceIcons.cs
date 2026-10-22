using System.Drawing;
using System.Drawing.Drawing2D;

namespace MouseBattery;

internal enum DeviceKind
{
    Mouse,
    Keyboard,
    Headset,
    Generic,
}

/// <summary>
/// 设备类型图标，线条风格，24×24 视图框按目标尺寸等比缩放。
/// 不用位图资源，全部矢量绘制，任何 DPI 和尺寸下都清晰。
/// </summary>
internal static class DeviceIcons
{
    public static DeviceKind Detect(string name)
    {
        string n = (name ?? string.Empty).ToLowerInvariant();

        if (n.Contains("mouse") || n.Contains("鼠标")) return DeviceKind.Mouse;
        if (n.Contains("keyboard") || n.Contains("键盘")) return DeviceKind.Keyboard;
        if (n.Contains("headset") || n.Contains("headphone") || n.Contains("earbud")
            || n.Contains("airpods") || n.Contains("tws") || n.Contains("耳机"))
            return DeviceKind.Headset;

        return DeviceKind.Generic;
    }

    public static string Describe(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => "鼠标",
        DeviceKind.Keyboard => "键盘",
        DeviceKind.Headset => "耳机",
        _ => "蓝牙设备",
    };

    public static Bitmap Render(DeviceKind kind, int size, Color color)
    {
        var bmp = new Bitmap(size, size);
        using Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        Draw(g, kind, new RectangleF(0, 0, size, size), color);
        return bmp;
    }

    /// <summary>在 rect 内绘制图标，内部统一按 24×24 坐标设计，再缩放贴合。</summary>
    public static void Draw(Graphics g, DeviceKind kind, RectangleF rect, Color color)
    {
        GraphicsState state = g.Save();
        try
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(rect.X, rect.Y);
            float scale = Math.Min(rect.Width, rect.Height) / 24f;
            g.ScaleTransform(scale, scale);

            float stroke = Math.Max(1.15f, 1.7f);

            using var pen = new Pen(color, stroke)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            using var brush = new SolidBrush(color);

            switch (kind)
            {
                case DeviceKind.Mouse:
                    DrawMouse(g, pen, brush);
                    break;
                case DeviceKind.Keyboard:
                    DrawKeyboard(g, pen, brush);
                    break;
                case DeviceKind.Headset:
                    DrawHeadset(g, pen, brush);
                    break;
                default:
                    DrawGeneric(g, pen, brush);
                    break;
            }
        }
        finally
        {
            g.Restore(state);
        }
    }

    private static void DrawMouse(Graphics g, Pen pen, SolidBrush brush)
    {
        // 机身：竖椭圆，只描边不填充，深浅背景下都能看清
        var body = new RectangleF(7.6f, 2.6f, 8.8f, 18.8f);
        using (var path = new GraphicsPath())
        {
            path.AddArc(body.X, body.Y, body.Width, body.Height * 0.54f, 180f, 180f);
            path.AddArc(body.X, body.Bottom - body.Height * 0.54f, body.Width, body.Height * 0.54f, 0f, 180f);
            path.CloseFigure();
            g.DrawPath(pen, path);
        }

        // 左右键分割线
        g.DrawLine(pen, 12f, 2.8f, 12f, 10.2f);
        g.DrawLine(pen, 7.9f, 10.2f, 16.1f, 10.2f);

        // 滚轮：一小段实心
        g.FillEllipse(brush, new RectangleF(10.9f, 4.6f, 2.2f, 3.2f));
    }

    private static void DrawKeyboard(Graphics g, Pen pen, SolidBrush brush)
    {
        // 连接线
        using (var path = new GraphicsPath())
        {
            path.AddBezier(13.4f, 8.2f, 13.4f, 4.4f, 17f, 5.4f, 17f, 2.6f);
            g.DrawPath(pen, path);
        }

        // 键盘外框，只描边
        var body = new RectangleF(2.8f, 8.2f, 18.4f, 13f);
        using (var path = new GraphicsPath())
        {
            float d = 2.4f;
            path.AddArc(body.X, body.Y, d, d, 180f, 90f);
            path.AddArc(body.Right - d, body.Y, d, d, 270f, 90f);
            path.AddArc(body.Right - d, body.Bottom - d, d, d, 0f, 90f);
            path.AddArc(body.X, body.Bottom - d, d, d, 90f, 90f);
            path.CloseFigure();
            g.DrawPath(pen, path);
        }

        // 键位：两行小方块 + 一行空格长条
        for (int row = 0; row < 2; row++)
        {
            float y = 11.4f + row * 2.9f;
            for (int col = 0; col < 6; col++)
            {
                float x = 5.2f + col * 2.2f;
                g.FillRectangle(brush, new RectangleF(x, y, 1.4f, 1.6f));
            }
        }

        g.FillRectangle(brush, new RectangleF(7.2f, 17.2f, 9.6f, 1.7f));
    }

    private static void DrawHeadset(Graphics g, Pen pen, SolidBrush brush)
    {
        // 头梁
        using (var path = new GraphicsPath())
        {
            path.AddArc(4.2f, 5.2f, 15.6f, 15.2f, 190f, 160f);
            g.DrawPath(pen, path);
        }

        // 左右耳罩
        var left = new RectangleF(2.6f, 13.6f, 4.2f, 7f);
        var right = new RectangleF(17.2f, 13.6f, 4.2f, 7f);
        foreach (RectangleF r in new[] { left, right })
        {
            using var path = new GraphicsPath();
            float d = 2.2f;
            path.AddArc(r.X, r.Y, d, d, 180f, 90f);
            path.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            path.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            path.CloseFigure();
            g.DrawPath(pen, path);
        }
    }

    private static void DrawGeneric(Graphics g, Pen pen, SolidBrush brush)
    {
        // 蓝牙符号
        var points = new[]
        {
            new PointF(7.2f, 8.2f),
            new PointF(16.8f, 15.8f),
            new PointF(12f, 20.2f),
            new PointF(12f, 3.8f),
            new PointF(16.8f, 8.2f),
            new PointF(7.2f, 15.8f),
        };
        g.DrawLines(pen, points);
    }
}
