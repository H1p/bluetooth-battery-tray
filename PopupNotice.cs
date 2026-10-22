using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Media;

namespace MouseBattery;

/// <summary>
/// 低电量提示卡片。
/// 之所以不用 NotifyIcon.ShowBalloonTip：Windows 11 要求程序向通知中心注册
/// AppUserModelID，未注册的裸 exe 调用会被静默丢弃（实测通知数据库无写入）。
/// 这里自绘一个右下角卡片，任何环境下都保证可见，也能直接把电量图形呈现出来。
/// 所有尺寸以 96 DPI 为基准，再按 DeviceDpi 等比放大，保证高分屏下不变形。
/// </summary>
internal sealed class PopupNotice : Form
{
    private const int BaseWidth = 372;
    private const int BaseHeight = 118;

    private readonly string _title;
    private readonly string _body;
    private readonly int? _percent;
    private readonly DeviceKind _kind;

    private readonly System.Windows.Forms.Timer _lifeTimer;
    private readonly System.Windows.Forms.Timer _fadeTimer;
    private bool _fadingOut;
    private bool _hovered;

    private PopupNotice(string title, string body, int? percent, DeviceKind kind)
    {
        _title = title;
        _body = body;
        _percent = percent;
        _kind = kind;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.White;
        DoubleBuffered = true;
        Text = title;
        Opacity = 1;

        float scale = DeviceDpi / 96f;
        Size = new Size(Scale(BaseWidth, scale), Scale(BaseHeight, scale));

        Font = new Font("Microsoft YaHei UI", 9f);

        Rectangle work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(
            work.Right - Width - Scale(16, scale),
            work.Bottom - Height - Scale(16, scale));

        using (var path = RoundedPath(new Rectangle(0, 0, Width, Height), Scale(12, scale)))
        {
            Region = new Region(path);
        }

        _lifeTimer = new System.Windows.Forms.Timer { Interval = 5200 };
        _lifeTimer.Tick += (_, _) =>
        {
            _lifeTimer.Stop();
            if (!_hovered) StartFadeOut();
        };

        _fadeTimer = new System.Windows.Forms.Timer { Interval = 18 };
        _fadeTimer.Tick += (_, _) =>
        {
            Opacity -= 0.09;
            if (Opacity <= 0.02)
            {
                _fadeTimer.Stop();
                Close();
            }
        };
    }

    private static int Scale(int baseValue, float scale) => (int)Math.Round(baseValue * scale);

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE：弹出时不抢焦点
            cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
        }
    }

    public static void Show(
        string title,
        string body,
        int? percent = null,
        bool playSound = true,
        DeviceKind kind = DeviceKind.Mouse)
    {
        try
        {
            var notice = new PopupNotice(title, body, percent, kind);
            notice.Show();
            notice.BringToFront();
            notice._lifeTimer.Start();
            Log($"[Popup] shown at {notice.Location} size={notice.Size}");

            if (playSound) SystemSounds.Exclamation.Play();
        }
        catch (Exception ex)
        {
            Log($"[Popup] 失败: {ex.GetType().Name} {ex.Message}");
        }
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "MouseBattery.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch
        {
            // ignore
        }
    }

    private void StartFadeOut()
    {
        _fadingOut = true;
        _fadeTimer.Start();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        if (!_lifeTimer.Enabled && !_fadingOut) StartFadeOut();
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        StartFadeOut();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        float s = DeviceDpi / 96f;
        int w = Width;
        int h = Height;

        using (var bg = new SolidBrush(Color.White))
        {
            g.FillRectangle(bg, ClientRectangle);
        }

        using (var border = new Pen(Color.FromArgb(228, 228, 228), Math.Max(1f, s)))
        using (var path = RoundedPath(new Rectangle(0, 0, w - 1, h - 1), Scale(12, s)))
        {
            g.DrawPath(border, path);
        }

        // 左侧色条：一眼看出严重程度
        Color accent = BatteryIcon.ColorFor(_percent, true);
        using (var bar = new SolidBrush(accent))
        {
            g.FillRectangle(bar, new Rectangle(0, Scale(16, s), Scale(4, s), h - Scale(32, s)));
        }

        // 设备类型图标（鼠标/键盘/耳机…），颜色沿用告警色，垂直居中
        int iconSize = Scale(42, s);
        using (Bitmap icon = DeviceIcons.Render(_kind, iconSize, accent))
        {
            g.DrawImage(icon, new Rectangle(Scale(22, s), (h - iconSize) / 2, iconSize, iconSize));
        }

        float textLeft = Scale(80, s);
        float textWidth = w - textLeft - Scale(34, s);

        using var titleFont = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
        using var bodyFont = new Font("Microsoft YaHei UI", 9f);
        using var titleBrush = new SolidBrush(Color.FromArgb(26, 26, 26));
        using var bodyBrush = new SolidBrush(Color.FromArgb(106, 106, 106));

        var titleRect = new RectangleF(textLeft, Scale(20, s), textWidth, titleFont.GetHeight(g) + Scale(4, s));
        using (var titleFormat = new StringFormat(StringFormatFlags.NoWrap)
        {
            Trimming = StringTrimming.EllipsisCharacter,
        })
        {
            g.DrawString(_title, titleFont, titleBrush, titleRect, titleFormat);
        }

        float bodyTop = titleRect.Bottom + Scale(3, s);
        var bodyRect = new RectangleF(textLeft, bodyTop, textWidth, h - bodyTop - Scale(12, s));
        using (var bodyFormat = new StringFormat(StringFormatFlags.LineLimit)
        {
            Trimming = StringTrimming.EllipsisWord,
        })
        {
            g.DrawString(_body, bodyFont, bodyBrush, bodyRect, bodyFormat);
        }

        // 右上角关闭叉
        float closeSize = Scale(8, s);
        float closeX = w - Scale(26, s);
        float closeY = Scale(16, s);
        using (var pen = new Pen(Color.FromArgb(150, 150, 150), Math.Max(1.4f, 1.4f * s))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        })
        {
            g.DrawLine(pen, closeX, closeY, closeX + closeSize, closeY + closeSize);
            g.DrawLine(pen, closeX + closeSize, closeY, closeX, closeY + closeSize);
        }
    }

    private static GraphicsPath RoundedPath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lifeTimer.Dispose();
            _fadeTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
