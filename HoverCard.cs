using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace MouseBattery;

/// <summary>
/// 托盘悬停卡片。
/// 系统的 NotifyIcon.Text 上限 63 个字符且不支持换行，塞不下多设备列表，
/// 所以鼠标停在托盘图标上时改弹这张自绘卡片。
/// 卡片带 WS_EX_NOACTIVATE，弹出不抢焦点；鼠标移上去可以停留查看，不会被右键菜单打断。
/// </summary>
internal sealed class HoverCard : Form
{
    private const int BaseWidth = 300;
    private const int HeaderHeight = 34;
    private const int RowHeight = 48;
    private const int FooterHeight = 26;
    private const int MaxRows = 6;

    private const int IdleTimeoutMs = 3000;   // 鼠标停在图标上不动这么久就淡出
    private const int LeaveGraceMs = 200;     // 离开图标或卡片后的缓冲，避免划一下就消失
    private const int IconZoneRadius = 56;    // 托盘图标附近算"还在悬停"的范围（物理像素）

    private static HoverCard? _current;

    private readonly bool _preview;
    private readonly Point? _previewCursor;
    private readonly System.Windows.Forms.Timer _watch;
    private readonly System.Windows.Forms.Timer _fade;
    private readonly System.Windows.Forms.Timer _show;

    private IReadOnlyList<BatteryReading> _items;
    private DateTime _lastPoke = DateTime.Now;
    private DateTime? _leaveAt;
    private Point _anchor;
    private bool _closing;
    private bool _positioned;
    private float _scale = 1f;

    private HoverCard(IReadOnlyList<BatteryReading> items, bool preview, Point? previewCursor = null)
    {
        _items = items;
        _preview = preview;
        _previewCursor = previewCursor;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.White;
        DoubleBuffered = true;
        Opacity = 1;
        Text = "设备电量";

        _watch = new System.Windows.Forms.Timer { Interval = 70 };
        _watch.Tick += (_, _) => WatchTick();

        // 淡出：16ms 一帧、每帧 -0.15，约 110ms 收干净，移开鼠标后干脆利落
        _fade = new System.Windows.Forms.Timer { Interval = 16 };
        _fade.Tick += (_, _) =>
        {
            Opacity -= 0.15;
            if (Opacity <= 0.02)
            {
                _fade.Stop();
                Close();
            }
        };

        // 淡入：16ms 一帧、每帧 +0.06，约 260ms 显示完整，避免"啪"地一下蹦出来
        _show = new System.Windows.Forms.Timer { Interval = 16 };
        _show.Tick += (_, _) =>
        {
            Opacity = Math.Min(1, Opacity + 0.06);
            if (Opacity >= 1) _show.Stop();
        };
    }

    public static HoverCard? Current => _current;

    public static void ShowOrUpdate(IReadOnlyList<BatteryReading> items)
    {
        if (_current is { IsDisposed: false } card)
        {
            card.UpdateItems(items);
            return;
        }

        var fresh = new HoverCard(items, false);
        _current = fresh;
        fresh.Show();
        fresh.BringToFront();
    }

    /// <summary>鼠标仍在托盘图标上移动，重置自动消失计时。</summary>
    public static void Poke()
    {
        if (_current is { IsDisposed: false } card)
        {
            card._lastPoke = DateTime.Now;
            card._leaveAt = null;
        }
    }

    public static void HideCard() => _current?.StartFade();

    public static HoverCard CreateForPreview(IReadOnlyList<BatteryReading> items, Point? cursorAt = null) =>
        new(items, true, cursorAt);

    /// <summary>卡片依附的光标位置。预览模式可以塞一个假坐标，用来核对托盘场景。</summary>
    private Point AnchorPoint => _previewCursor ?? Cursor.Position;

    // ---------------------------------------------------------------- 生命周期

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _scale = DeviceDpi / 96f;
        ApplyLayout();

        if (!_preview)
        {
            Opacity = 0.02;
            _show.Start();
            _watch.Start();
            _lastPoke = DateTime.Now;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        if (ReferenceEquals(_current, this)) _current = null;
    }

    private void UpdateItems(IReadOnlyList<BatteryReading> items)
    {
        if (_closing) return;

        int oldRows = Math.Min(_items.Count, MaxRows);
        _items = items;
        int newRows = Math.Min(_items.Count, MaxRows);

        if (oldRows != newRows) ApplyLayout();
        Invalidate();
    }

    private void WatchTick()
    {
        if (_closing) return;

        Point cursor = Cursor.Position;

        // 光标在卡片上，算还在看
        if (Bounds.Contains(cursor))
        {
            _leaveAt = null;
            return;
        }

        // 光标还在托盘图标附近：只有长时间不动才淡出
        if (IsNearAnchor(cursor))
        {
            _leaveAt = null;
            if ((DateTime.Now - _lastPoke).TotalMilliseconds > IdleTimeoutMs) StartFade();
            return;
        }

        // 既不在卡片、也不在图标上：不管是刚从卡片移开还是从图标移开，
        // 都走同一套收尾，不再等满 3 秒空闲
        _leaveAt ??= DateTime.Now;
        if ((DateTime.Now - _leaveAt.Value).TotalMilliseconds > LeaveGraceMs) StartFade();
    }

    /// <summary>光标是否还在托盘图标附近。锚点在弹窗时就固定，不能跟着光标实时变。</summary>
    private bool IsNearAnchor(Point cursor) =>
        Math.Abs(cursor.X - _anchor.X) <= IconZoneRadius &&
        Math.Abs(cursor.Y - _anchor.Y) <= IconZoneRadius;

    private void StartFade()
    {
        if (_closing || _preview) return;
        _closing = true;
        _watch.Stop();
        _show.Stop();
        _fade.Start();
    }

    // ---------------------------------------------------------------- 布局

    private int S(double value) => (int)Math.Round(value * _scale);

    private void ApplyLayout()
    {
        int rows = Math.Min(_items.Count, MaxRows);
        int width = S(BaseWidth);
        int height = S(HeaderHeight) + rows * S(RowHeight) + S(FooterHeight);

        int bottom = Bottom;
        ClientSize = new Size(width, height);

        using (GraphicsPath path = Rounded(new RectangleF(0, 0, width, height), S(10)))
        {
            Region?.Dispose();
            Region = new Region(path);
        }

        if (_positioned)
        {
            // 行数变化时保持底边不动，卡片向上长
            Top = bottom - Height;
            ClampToWorkArea();
        }
        else
        {
            _positioned = true;
            PositionNearCursor();
        }
    }

    private void PositionNearCursor()
    {
        // 锚点在这里固定下来：之后判断"光标是否还停在图标上"要用同一个点
        _anchor = AnchorPoint;
        Point anchor = _anchor;
        Screen screen = Screen.FromPoint(anchor);
        Rectangle work = screen.WorkingArea;
        int gap = S(10);

        // 任务栏自动隐藏时 WorkingArea 等于整屏，得自己让开一条任务栏的高度，
        // 否则卡片底边会压在任务栏上，正好盖住托盘图标
        int bottomLimit = work.Bottom;
        if (screen.Bounds.Bottom - work.Bottom < S(4)) bottomLimit -= S(48);

        // 整张卡片放在光标左上方：托盘图标在右下角，往左上展开就不会压住正在悬停的图标
        bool fitsLeft = anchor.X - Width - gap >= work.Left + gap;
        bool fitsUp = anchor.Y - Height - gap >= work.Top + gap;

        int x = fitsLeft ? anchor.X - Width - gap : anchor.X + gap;
        int y = fitsUp ? anchor.Y - Height - gap : anchor.Y + gap;

        // 再夹一次，保证完整落在工作区内（不会被任务栏或屏幕边缘切掉）
        x = Math.Max(work.Left + gap, Math.Min(x, work.Right - Width - gap));
        y = Math.Max(work.Top + gap, Math.Min(y, bottomLimit - Height - gap));

        Location = new Point(x, y);
    }

    private void ClampToWorkArea()
    {
        Rectangle work = Screen.FromControl(this).WorkingArea;
        int gap = S(8);
        int x = Math.Max(work.Left + gap, Math.Min(Left, work.Right - Width - gap));
        int y = Math.Max(work.Top + gap, Math.Min(Top, work.Bottom - Height - gap));
        Location = new Point(x, y);
    }

    // ---------------------------------------------------------------- 绘制

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        int w = Width;
        int h = Height;
        int padX = S(14);
        int headerH = S(HeaderHeight);
        int rowH = S(RowHeight);
        int rows = Math.Min(_items.Count, MaxRows);

        using (var bg = new SolidBrush(Color.White))
        {
            g.FillRectangle(bg, ClientRectangle);
        }

        using (var border = new Pen(Color.FromArgb(224, 224, 224), Math.Max(1f, _scale)))
        using (GraphicsPath outline = Rounded(new RectangleF(0, 0, w - 1, h - 1), S(9)))
        {
            g.DrawPath(border, outline);
        }

        using var titleFont = new Font("Microsoft YaHei UI", 8.8f, FontStyle.Bold);
        using var countFont = new Font("Microsoft YaHei UI", 8f);
        using var nameFont = new Font("Microsoft YaHei UI", 8.5f);
        using var pctFont = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold);
        using var hintFont = new Font("Microsoft YaHei UI", 7.2f);

        using var ink = new SolidBrush(Color.FromArgb(28, 28, 28));
        using var muted = new SolidBrush(Color.FromArgb(120, 120, 120));
        using var faint = new SolidBrush(Color.FromArgb(158, 158, 158));
        using var line = new Pen(Color.FromArgb(234, 234, 234), Math.Max(1f, _scale));

        // 标题行
        g.DrawString("设备电量", titleFont, ink, padX, S(8));

        string countText = $"{_items.Count} 台";
        SizeF countSize = g.MeasureString(countText, countFont);
        g.DrawString(countText, countFont, muted, w - padX - countSize.Width, S(10));

        g.DrawLine(line, padX, headerH, w - padX, headerH);

        // 设备行
        for (int i = 0; i < rows; i++)
        {
            BatteryReading reading = _items[i];
            int top = headerH + i * rowH;
            Color color = BatteryIcon.ColorFor(reading.Percent, reading.Connected);

            using (Bitmap icon = DeviceIcons.Render(reading.Kind, S(20), color))
            {
                g.DrawImage(icon, new Rectangle(padX, top + S(6), S(20), S(20)));
            }

            DeviceIcons.DrawTransportBadge(
                g,
                new RectangleF(padX, top + S(6), S(20), S(20)),
                reading.Transport,
                _scale);

            int textLeft = padX + S(26);
            int pctWidth = S(52);
            var nameRect = new RectangleF(textLeft, top + S(5), w - textLeft - padX - pctWidth, S(17));
            using (var nameFormat = new StringFormat(StringFormatFlags.NoWrap)
            {
                Trimming = StringTrimming.EllipsisCharacter,
            })
            {
                g.DrawString(reading.DeviceName, nameFont, reading.Connected ? ink : muted, nameRect, nameFormat);
            }

            string percentText = reading.Connected
                ? (reading.Percent is int p ? $"{p}%" : "未知")
                : "未连接";
            var pctRect = new RectangleF(w - padX - pctWidth, top + S(5), pctWidth, S(17));
            using (var pctFormat = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Far })
            using (var pctBrush = new SolidBrush(color))
            {
                g.DrawString(percentText, pctFont, pctBrush, pctRect, pctFormat);
            }

            // 电量条
            var track = new RectangleF(textLeft, top + S(28), w - textLeft - padX, S(4));
            using (var trackBrush = new SolidBrush(Color.FromArgb(236, 236, 236)))
            using (GraphicsPath trackPath = Rounded(track, S(2)))
            {
                g.FillPath(trackBrush, trackPath);
            }

            if (reading.Connected && reading.Percent is int value && value > 0)
            {
                float fillWidth = track.Width * Math.Clamp(value, 0, 100) / 100f;
                if (fillWidth < S(2)) fillWidth = S(2);

                var fill = new RectangleF(track.X, track.Y, fillWidth, track.Height);
                using var fillBrush = new SolidBrush(color);
                using GraphicsPath fillPath = Rounded(fill, S(2));
                g.FillPath(fillBrush, fillPath);
            }
        }

        // 底部提示
        int footerTop = headerH + rows * rowH;
        g.DrawLine(line, padX, footerTop, w - padX, footerTop);

        if (_items.Count > MaxRows)
        {
            g.DrawString($"… 还有 {_items.Count - MaxRows} 台未显示", hintFont, faint, padX, footerTop + S(7));
        }
        else
        {
            g.DrawString("双击刷新 · 右键菜单", hintFont, faint, padX, footerTop + S(7));
        }
    }

    private static GraphicsPath Rounded(RectangleF rect, float radius)
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watch.Dispose();
            _fade.Dispose();
            _show.Dispose();
        }
        base.Dispose(disposing);
    }
}
