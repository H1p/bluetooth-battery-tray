using System.Drawing;
using System.Drawing.Drawing2D;

namespace MouseBattery;

internal enum DeviceKind
{
    Mouse,
    Keyboard,
    Headset,
    Gamepad,
    Generic,
}

/// <summary>
/// 设备类型图标，线条风格，24×24 视图框按目标尺寸等比缩放。
/// 不用位图资源，全部矢量绘制，任何 DPI 和尺寸下都清晰。
/// </summary>
internal static class DeviceIcons
{
    // 名称匹配用关键字。BLE 设备的 FriendlyName 五花八门，没有标准字段可查
    // （BluetoothLEAppearance 要设备在线才读得到），所以这里按关键字猜类型。
    // 新增设备认错了，把型号关键字追加到对应的数组即可。
    private static readonly string[] KeyboardWords =
    {
        "keyboard", "键盘", "kbd", "keychron", "hhkb", "nuphy", "iqunix", "melgeek",
        "akko", "royal kludge", "vgn", "狼蛛", "达尔优", "珂芝", "黑爵", "客制化",
    };

    private static readonly string[] MouseWords =
    {
        "mouse", "鼠标", "mice", "mx master", "mx anywhere", "g304", "g502", "g102",
        "viper", "deathadder", "炼狱蝰蛇", "巴塞利斯", "rival",
    };

    private static readonly string[] HeadsetWords =
    {
        "headset", "headphone", "earbud", "airpods", "tws", "buds", "耳机", "耳麦",
        "wh-1000", "qc35", "qc45", "soundcore", "漫步者",
    };

    private static readonly string[] GamepadWords =
    {
        "gamepad", "game pad", "controller", "joystick", "手柄", "xbox", "dualsense",
        "dualshock", "joycon", "joy-con", "8bitdo", "八位堂", "飞智", "盖世小鸡",
    };

    public static DeviceKind Detect(string name)
    {
        string n = (name ?? string.Empty).ToLowerInvariant();
        if (n.Length == 0) return DeviceKind.Generic;

        if (ContainsAny(n, KeyboardWords)) return DeviceKind.Keyboard;
        if (ContainsAny(n, MouseWords)) return DeviceKind.Mouse;
        if (ContainsAny(n, HeadsetWords)) return DeviceKind.Headset;
        if (ContainsAny(n, GamepadWords)) return DeviceKind.Gamepad;
        return DeviceKind.Generic;
    }

    private static bool ContainsAny(string text, string[] words)
    {
        foreach (string w in words)
        {
            if (text.Contains(w, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    public static string Describe(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => "鼠标",
        DeviceKind.Keyboard => "键盘",
        DeviceKind.Headset => "耳机",
        DeviceKind.Gamepad => "手柄",
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

    /// <summary>蓝牙角标的蓝色。</summary>
    public static readonly Color BluetoothBadgeColor = Color.FromArgb(30, 136, 229);

    /// <summary>2.4G 角标的紫色。</summary>
    public static readonly Color WirelessBadgeColor = Color.FromArgb(124, 77, 255);

    /// <summary>
    /// 在设备图标右上角画连接方式角标：蓝牙画蓝色符号，2.4G 画紫色「2.4G」字样。
    /// 都先描一圈白边，保证叠在图标上也看得清。
    /// </summary>
    public static void DrawTransportBadge(Graphics g, RectangleF icon, Transport transport, float scale)
    {
        if (transport == Transport.Bluetooth)
        {
            float w = 7f * scale;
            float h = 9f * scale;
            var box = new RectangleF(icon.Right - w * 0.35f, icon.Top - h * 0.35f, w, h);

            using var pen = new Pen(BluetoothBadgeColor, Math.Max(1.1f, 1.15f * scale))
            {
                LineJoin = LineJoin.Round,
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            using var halo = new Pen(Color.White, Math.Max(2.4f, 2.8f * scale))
            {
                LineJoin = LineJoin.Round,
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };

            PointF[] pts =
            {
                new(box.Left + w * 0.5f, box.Top),
                new(box.Left + w * 0.5f, box.Bottom),
            };
            PointF[][] zig =
            {
                new[] { new PointF(box.Left + w * 0.5f, box.Top), new PointF(box.Right, box.Top + h * 0.28f), new PointF(box.Left, box.Top + h * 0.72f) },
                new[] { new PointF(box.Left + w * 0.5f, box.Bottom), new PointF(box.Right, box.Bottom - h * 0.28f), new PointF(box.Left, box.Bottom - h * 0.72f) },
            };

            g.DrawLines(halo, pts);
            foreach (PointF[] seg in zig) g.DrawLines(halo, seg);
            g.DrawLines(pen, pts);
            foreach (PointF[] seg in zig) g.DrawLines(pen, seg);
            return;
        }

        // 2.4G：紫底白字的小胶囊
        using var font = new Font("Microsoft YaHei UI", 5.2f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        const string text = "2.4G";
        SizeF textSize = g.MeasureString(text, font);

        float padX = 2.2f * scale;
        float padY = 1.1f * scale;
        float bw = textSize.Width + padX * 2;
        float bh = textSize.Height + padY * 2;
        var pill = new RectangleF(icon.Right - bw * 0.62f, icon.Top - bh * 0.42f, bw, bh);

        using (var path = RoundedRect(pill, bh / 2f))
        using (var fill = new SolidBrush(WirelessBadgeColor))
        using (var edge = new Pen(Color.White, Math.Max(1f, 1.2f * scale)))
        {
            g.FillPath(fill, path);
            g.DrawPath(edge, path);
        }

        using (var textBrush = new SolidBrush(Color.White))
        {
            g.DrawString(text, font, textBrush, pill.Left + padX, pill.Top + padY - 0.6f * scale);
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

            float stroke = Math.Max(1.15f, 1.95f);

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
                case DeviceKind.Gamepad:
                    DrawGamepad(g, pen, brush);
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

    private static void DrawGamepad(Graphics g, Pen pen, SolidBrush brush)
    {
        // 机身：两端大圆角的横条，看着像手柄的两个握把
        var body = new RectangleF(2.4f, 8.4f, 19.2f, 9f);
        using (var path = new GraphicsPath())
        {
            float d = 4.6f;
            path.AddArc(body.X, body.Y, d, d, 180f, 90f);
            path.AddArc(body.Right - d, body.Y, d, d, 270f, 90f);
            path.AddArc(body.Right - d, body.Bottom - d, d, d, 0f, 90f);
            path.AddArc(body.X, body.Bottom - d, d, d, 90f, 90f);
            path.CloseFigure();
            g.DrawPath(pen, path);
        }

        // 顶部肩键
        g.DrawLine(pen, 6.2f, 7.4f, 9.4f, 7.4f);
        g.DrawLine(pen, 14.6f, 7.4f, 17.8f, 7.4f);

        // 左侧十字键
        g.DrawLine(pen, 8.2f, 11.2f, 8.2f, 14.8f);
        g.DrawLine(pen, 6.4f, 13f, 10f, 13f);

        // 右侧两颗功能键
        g.FillEllipse(brush, new RectangleF(14.2f, 11.3f, 1.9f, 1.9f));
        g.FillEllipse(brush, new RectangleF(16.7f, 12.7f, 1.9f, 1.9f));
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
