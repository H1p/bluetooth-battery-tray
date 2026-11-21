using System.Drawing.Drawing2D;

namespace MouseBattery;

/// <summary>
/// 电量历史窗口：选设备、选时间范围，看一条折线。
/// 图表自己画，不引第三方图表库——这个项目一直保持零 NuGet 依赖。
/// </summary>
internal sealed class HistoryForm : Form
{
    private static readonly Color LineColor = Color.FromArgb(47, 111, 235);
    private static readonly Color GridColor = Color.FromArgb(236, 236, 236);
    private static readonly Color AxisTextColor = Color.FromArgb(130, 130, 130);

    private readonly HistoryStore _store;
    private readonly float _scale;
    private readonly ComboBox _deviceBox;
    private readonly ComboBox _rangeBox;
    private readonly ChartPanel _chart;
    private readonly Label _summary;

    private static readonly (string Text, TimeSpan Window)[] Ranges =
    {
        ("24 小时", TimeSpan.FromHours(24)),
        ("7 天", TimeSpan.FromDays(7)),
        ("30 天", TimeSpan.FromDays(30)),
    };

    public HistoryForm(HistoryStore store)
    {
        _store = store;
        _scale = GetDpiScale();

        Text = "设备电量 · 历史记录";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;
        ClientSize = new Size(S(560), S(430));

        var lblDevice = new Label
        {
            Text = "设备",
            Location = new Point(S(16), S(18)),
            Size = new Size(S(36), S(22)),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(60, 60, 60),
        };

        _deviceBox = new ComboBox
        {
            Location = new Point(S(56), S(16)),
            Size = new Size(S(230), S(24)),
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = new Font("Microsoft YaHei UI", 8.8f),
        };

        var lblRange = new Label
        {
            Text = "范围",
            Location = new Point(S(302), S(18)),
            Size = new Size(S(36), S(22)),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(60, 60, 60),
        };

        _rangeBox = new ComboBox
        {
            Location = new Point(S(342), S(16)),
            Size = new Size(S(120), S(24)),
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = new Font("Microsoft YaHei UI", 8.8f),
        };
        foreach ((string text, _) in Ranges) _rangeBox.Items.Add(text);
        _rangeBox.SelectedIndex = 0;

        _chart = new ChartPanel
        {
            Location = new Point(S(16), S(52)),
            Size = new Size(S(528), S(316)),
            BackColor = Color.White,
        };

        _summary = new Label
        {
            Location = new Point(S(16), S(374)),
            Size = new Size(S(400), S(20)),
            ForeColor = Color.FromArgb(120, 120, 120),
            Font = new Font("Microsoft YaHei UI", 8f),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        var close = new Button
        {
            Text = "关闭",
            Location = new Point(S(462), S(394)),
            Size = new Size(S(82), S(26)),
            FlatStyle = FlatStyle.System,
            Font = new Font("Microsoft YaHei UI", 8.5f),
        };
        close.Click += (_, _) => Close();

        Controls.AddRange(new Control[] { lblDevice, _deviceBox, lblRange, _rangeBox, _chart, _summary, close });

        _deviceBox.SelectedIndexChanged += (_, _) => RefreshChart();
        _rangeBox.SelectedIndexChanged += (_, _) => RefreshChart();

        LoadDevices();
    }

    private void LoadDevices()
    {
        _deviceBox.Items.Clear();

        IReadOnlyList<(string Id, string Name)> devices = _store.Devices;
        if (devices.Count == 0)
        {
            _deviceBox.Items.Add("（暂无历史数据）");
            _deviceBox.SelectedIndex = 0;
            _deviceBox.Enabled = false;
            RefreshChart();
            return;
        }

        foreach ((string id, string name) in devices)
        {
            _deviceBox.Items.Add(new DeviceItem(id, string.IsNullOrEmpty(name) ? id : name));
        }
        _deviceBox.SelectedIndex = 0;
    }

    private void RefreshChart()
    {
        if (_deviceBox.SelectedItem is not DeviceItem item)
        {
            _chart.SetData(new List<HistoryPoint>(), Ranges[0].Window);
            _summary.Text = "还没有历史数据，运行一段时间后再来看。";
            return;
        }

        TimeSpan window = Ranges[Math.Clamp(_rangeBox.SelectedIndex, 0, Ranges.Length - 1)].Window;
        List<HistoryPoint> points = _store.Query(item.Id, window);

        _chart.SetData(points, window);
        _summary.Text = points.Count == 0
            ? "该时间段内没有记录。"
            : $"共 {points.Count} 个采样点　最低 {points.Min(p => p.Percent)}%　最高 {points.Max(p => p.Percent)}%　平均 {points.Average(p => p.Percent):F0}%";
    }

    /// <summary>仅供 --history-preview 截图核对。</summary>
    internal void PreviewHover(int index) => _chart.ForceHover(index);

    /// <summary>仅供 --history-preview：关掉悬停，避免鼠标恰好停在窗口上把提示框画出来。</summary>
    internal void PreviewHoverOff() => _chart.LockHover();

    /// <summary>仅供 --history-preview 截图核对：切到指定时间范围（0=24 小时，1=7 天，2=30 天）。</summary>
    internal void PreviewRange(int index)
    {
        _rangeBox.SelectedIndex = Math.Clamp(index, 0, Ranges.Length - 1);
    }

    private int S(int baseValue) => (int)Math.Round(baseValue * _scale);

    private static float GetDpiScale()
    {
        try
        {
            using Graphics g = Graphics.FromHwnd(IntPtr.Zero);
            return g.DpiX / 96f;
        }
        catch
        {
            return 1f;
        }
    }

    private sealed record DeviceItem(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>折线图面板：左侧百分比刻度，底部时间刻度，缺失区间直接断开不连直线。</summary>
    private sealed class ChartPanel : Control
    {
        private List<HistoryPoint> _points = new();
        private TimeSpan _window = TimeSpan.FromHours(24);

        // 悬停定位：绘图时记下坐标映射，鼠标移动时用同一套换算反查最近的点
        private Rectangle _plot;
        private DateTime _start;
        private DateTime _end = DateTime.Now;
        private float _scale = 1f;
        private int _hover = -1;
        private bool _hoverLocked;

        public ChartPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        /// <summary>仅供截图核对：强制把提示框定在某个点上。</summary>
        /// <summary>仅供截图：忽略真实鼠标移动，保证画面上没有提示框。</summary>
        public void LockHover()
        {
            _hoverLocked = true;
            _hover = -1;
            Invalidate();
        }

        public void ForceHover(int index)
        {
            _hover = index;
            Invalidate();
        }

        public void SetData(List<HistoryPoint> points, TimeSpan window)
        {
            _points = points;
            _window = window;
            _hover = -1;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            float scale = DeviceDpi / 96f;
            int padLeft = (int)Math.Round(50 * scale);
            int padRight = (int)Math.Round(14 * scale);
            int padTop = (int)Math.Round(14 * scale);
            int padBottom = (int)Math.Round(40 * scale);   // 多留一条充电刻度条的高度

            var plot = new Rectangle(
                padLeft,
                padTop,
                Math.Max(10, Width - padLeft - padRight),
                Math.Max(10, Height - padTop - padBottom));

            _plot = plot;
            _scale = scale;
            _end = DateTime.Now;
            _start = _end - _window;

            using var grid = new Pen(GridColor, Math.Max(1f, scale));
            using var axisFont = new Font("Microsoft YaHei UI", 7f * scale);
            using var axisBrush = new SolidBrush(AxisTextColor);

            // 横向刻度 + 百分比
            for (int value = 0; value <= 100; value += 25)
            {
                int y = plot.Bottom - (int)Math.Round(plot.Height * value / 100.0);
                g.DrawLine(grid, plot.Left, y, plot.Right, y);

                string label = $"{value}%";
                SizeF size = g.MeasureString(label, axisFont);
                g.DrawString(label, axisFont, axisBrush, plot.Left - size.Width - 6 * scale, y - size.Height / 2);
            }

            if (_points.Count == 0)
            {
                using var emptyFont = new Font("Microsoft YaHei UI", 9f * scale);
                using var emptyBrush = new SolidBrush(Color.FromArgb(150, 150, 150));
                string text = "暂无数据";
                SizeF size = g.MeasureString(text, emptyFont);
                g.DrawString(text, emptyFont, emptyBrush,
                    plot.Left + (plot.Width - size.Width) / 2,
                    plot.Top + (plot.Height - size.Height) / 2);
                DrawTimeAxis(g, plot, scale, axisFont, axisBrush);
                return;
            }

            DateTime end = _end;
            DateTime start = _start;
            double spanSeconds = Math.Max(1, (end - start).TotalSeconds);

            PointF ToPoint(DateTime time, int percent)
            {
                float x = plot.Left + (float)((time - start).TotalSeconds / spanSeconds * plot.Width);
                float y = plot.Bottom - (float)(percent / 100.0 * plot.Height);
                return new PointF(Math.Clamp(x, plot.Left, plot.Right), Math.Clamp(y, plot.Top, plot.Bottom));
            }

            // 充电时段先铺一层很淡的竖带做背景；下面另有刻度条负责"看得见"，两者分工
            List<(DateTime From, DateTime To)> charging = CollectChargingSegments(start, end);
            DrawChargingWash(g, plot, charging, start, spanSeconds, scale);

            // 分段：相邻两点间隔超过窗口的 8% 视为设备离线，断开不连
            double gapSeconds = Math.Max(600, spanSeconds * 0.08);
            var segments = new List<List<PointF>>();
            List<PointF>? current = null;
            DateTime previous = DateTime.MinValue;

            foreach (HistoryPoint point in _points)
            {
                if (current == null || (point.Time - previous).TotalSeconds > gapSeconds)
                {
                    current = new List<PointF>();
                    segments.Add(current);
                }

                current.Add(ToPoint(point.Time, point.Percent));
                previous = point.Time;
            }

            using var linePen = new Pen(LineColor, Math.Max(1.6f, 1.8f * scale))
            {
                LineJoin = LineJoin.Round,
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            using var fillBrush = new LinearGradientBrush(
                new Rectangle(plot.Left, plot.Top, plot.Width, plot.Height),
                Color.FromArgb(56, LineColor),
                Color.FromArgb(6, LineColor),
                LinearGradientMode.Vertical);
            using var dotBrush = new SolidBrush(LineColor);

            foreach (List<PointF> segment in segments)
            {
                if (segment.Count == 0) continue;

                if (segment.Count > 1)
                {
                    var area = new List<PointF>(segment) { new(segment[^1].X, plot.Bottom), new(segment[0].X, plot.Bottom) };
                    g.FillPolygon(fillBrush, area.ToArray());
                    g.DrawLines(linePen, segment.ToArray());
                }

                // 点少的时候画圆点，便于看清稀疏采样
                if (_points.Count <= 60)
                {
                    float size = Math.Max(2.4f, 2.8f * scale);
                    foreach (PointF point in segment)
                        g.FillEllipse(dotBrush, point.X - size / 2, point.Y - size / 2, size, size);
                }
            }

            // 最后一点强调一下
            PointF last = ToPoint(_points[^1].Time, _points[^1].Percent);
            float lastSize = Math.Max(5f, 6f * scale);
            g.FillEllipse(dotBrush, last.X - lastSize / 2, last.Y - lastSize / 2, lastSize, lastSize);

            // 刻度条画在曲线之后，永远压在数据之上，不会被面积填充盖住
            DrawChargingRail(g, plot, charging, start, spanSeconds, scale);

            // 图例放在曲线之后画，否则会被折线盖住；又放在悬停层之前，让提示框压在最上面
            DrawChargingLegend(g, plot, scale, charging);

            DrawHover(g, plot, scale, start, end);
            DrawTimeAxis(g, plot, scale, axisFont, axisBrush);
        }

        /// <summary>
        /// 收集窗口内的充电时段（已按窗口裁剪、按采样节奏封顶收尾）。
        /// 竖带、底部刻度条、图例三处共用同一份结果，保证三者永远对得上。
        /// </summary>
        private List<(DateTime From, DateTime To)> CollectChargingSegments(DateTime start, DateTime end)
        {
            var result = new List<(DateTime, DateTime)>();

            // 采样节奏的典型值，用来给充电时段留一个合理的收尾。
            // 否则：设备充着电时关掉程序、第二天再开，下一个采样点在十几小时后，
            // 时段会被拉成横跨整张图的一大块，看着像"一直在充电"。
            double maxTailMinutes = Math.Clamp(MedianGapMinutes(_points) * 2, 2, 180);

            int i = 0;
            while (i < _points.Count)
            {
                if (!_points[i].Charging) { i++; continue; }

                int j = i;
                while (j + 1 < _points.Count && _points[j + 1].Charging) j++;

                DateTime from = _points[i].Time;
                DateTime to = j + 1 < _points.Count ? _points[j + 1].Time : end;

                DateTime tailCap = _points[j].Time.AddMinutes(maxTailMinutes);
                if (to > tailCap) to = tailCap;

                if (to > start && from < end)
                {
                    result.Add((from < start ? start : from, to > end ? end : to));
                }

                i = j + 1;
            }

            return result;
        }

        /// <summary>
        /// 充电时段的背景竖带。刻意画得很淡、且垫在曲线下面——
        /// 7 天视图里数据密集，颜色一重就会和面积填充糊在一起，反而看不出边界。
        /// 真正负责"看得见"的是底部那条 <see cref="DrawChargingRail"/>。
        /// </summary>
        private void DrawChargingWash(
            Graphics g, Rectangle plot, List<(DateTime From, DateTime To)> segments,
            DateTime start, double spanSeconds, float scale)
        {
            if (segments.Count == 0) return;

            using var brush = new SolidBrush(Color.FromArgb(24, 232, 150, 40));

            foreach ((DateTime from, DateTime to) in segments)
            {
                float x1 = plot.Left + (float)((from - start).TotalSeconds / spanSeconds * plot.Width);
                float x2 = plot.Left + (float)((to - start).TotalSeconds / spanSeconds * plot.Width);
                if (x2 - x1 < 2f) x2 = x1 + 2f;

                g.FillRectangle(brush, x1, plot.Top, x2 - x1, plot.Height);
            }
        }

        /// <summary>
        /// 绘图区下沿的充电刻度条：一条浅灰轨道，充电时段在上面打琥珀色刻度。
        /// 单独占一条通道，永远不会和曲线、面积填充重叠；
        /// 时间跨度太短（比如 7 天里的 27 分钟）时按最小宽度画，保证还看得见一个刻度。
        /// </summary>
        private void DrawChargingRail(
            Graphics g, Rectangle plot, List<(DateTime From, DateTime To)> segments,
            DateTime start, double spanSeconds, float scale)
        {
            if (segments.Count == 0) return;

            float height = 6 * scale;
            float top = plot.Bottom + 3 * scale;
            var track = new RectangleF(plot.Left, top, plot.Width, height);

            using (GraphicsPath trackPath = RoundedRect(track, height / 2f))
            using (var trackBrush = new SolidBrush(Color.FromArgb(236, 236, 236)))
            {
                g.FillPath(trackBrush, trackPath);
            }

            using var tickBrush = new SolidBrush(Color.FromArgb(232, 150, 40));
            float minWidth = Math.Max(6f, 6f * scale);

            // 刻度往绘图区里探出一截：底部那条留给"0%"刻度线，纯靠轨道上的小点仍然不够显眼
            float rise = 7 * scale;

            foreach ((DateTime from, DateTime to) in segments)
            {
                float x1 = plot.Left + (float)((from - start).TotalSeconds / spanSeconds * plot.Width);
                float x2 = plot.Left + (float)((to - start).TotalSeconds / spanSeconds * plot.Width);
                if (x2 - x1 < minWidth) x2 = x1 + minWidth;

                // 贴边时段不要戳出轨道
                if (x2 > track.Right) { x1 -= x2 - track.Right; x2 = track.Right; }
                if (x1 < track.Left) x1 = track.Left;

                var tick = new RectangleF(x1, top - rise, x2 - x1, height + rise);
                using GraphicsPath tickPath = RoundedRect(tick, Math.Min(height, x2 - x1) / 2f);
                g.FillPath(tickBrush, tickPath);
            }
        }

        /// <summary>
        /// 右上角的充电图例。文字用「充电时段」而不是「充电中」——后者会被读成"此刻正在充电"，
        /// 而这里标的是历史上的一段时间。顺带写出段数与总时长，不用悬停也知道充了多久。
        /// </summary>
        private void DrawChargingLegend(Graphics g, Rectangle plot, float scale, List<(DateTime From, DateTime To)> segments)
        {
            if (segments.Count == 0) return;

            double totalMinutes = segments.Sum(s => (s.To - s.From).TotalMinutes);
            int count = segments.Count;

            string text = count == 1
                ? $"充电时段 · {HumanMinutes(totalMinutes)}"
                : $"充电时段 · {count} 段 {HumanMinutes(totalMinutes)}";

            using var legendFont = new Font("Microsoft YaHei UI", 7.4f * scale);
            using var legendText = new SolidBrush(Color.FromArgb(150, 105, 35));
            using var swatch = new SolidBrush(Color.FromArgb(160, 232, 150, 40));

            float size = 9 * scale;
            float gap = 4 * scale;
            float textWidth = g.MeasureString(text, legendFont).Width;

            // 右对齐，宽度随文字变化，避免长文案顶出绘图区
            float lx = plot.Right - (size + gap + textWidth);
            float ly = plot.Top + 4 * scale;

            // 曲线可能正好从图例这里穿过，垫一块半透明白底保证文字读得清
            float padX = 5 * scale;
            float padY = 3 * scale;
            var plate = new RectangleF(
                lx - padX,
                ly - padY,
                size + gap + textWidth + padX * 2,
                size + padY * 2);

            using (GraphicsPath platePath = RoundedRect(plate, 4 * scale))
            using (var plateBrush = new SolidBrush(Color.FromArgb(214, 255, 255, 255)))
            {
                g.FillPath(plateBrush, platePath);
            }

            g.FillRectangle(swatch, lx, ly, size, size);
            g.DrawString(text, legendFont, legendText, lx + size + gap, ly - 1.5f * scale);
        }

        /// <summary>把分钟数写成「25 分钟」「1.5 小时」这种人话。</summary>
        private static string HumanMinutes(double minutes)
        {
            if (minutes < 1) return "不足 1 分钟";
            if (minutes < 60) return $"{Math.Round(minutes)} 分钟";

            double hours = minutes / 60;
            return hours < 10 ? $"{hours:0.#} 小时" : $"{Math.Round(hours)} 小时";
        }

        /// <summary>相邻采样点间隔的中位数（分钟）。超过 6 小时的间隔视为"没在运行"，不参与统计。</summary>
        private static double MedianGapMinutes(List<HistoryPoint> points)
        {
            if (points.Count < 2) return 2;

            var gaps = new List<double>(points.Count - 1);
            for (int i = 1; i < points.Count; i++)
            {
                double minutes = (points[i].Time - points[i - 1].Time).TotalMinutes;
                if (minutes > 0 && minutes < 360) gaps.Add(minutes);
            }

            if (gaps.Count == 0) return 2;

            gaps.Sort();
            return gaps[gaps.Count / 2];
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_hoverLocked) return;

            int index = -1;

            if (_points.Count > 0 && _plot.Width > 0)
            {
                double span = Math.Max(1, (_end - _start).TotalSeconds);
                float best = float.MaxValue;

                for (int i = 0; i < _points.Count; i++)
                {
                    float px = _plot.Left + (float)((_points[i].Time - _start).TotalSeconds / span * _plot.Width);
                    float dx = Math.Abs(e.X - px);
                    if (dx >= best) continue;
                    best = dx;
                    index = i;
                }

                // 离得太远就不算命中，避免鼠标满屏乱跳提示
                if (best > 14 * _scale) index = -1;
            }

            if (index != _hover)
            {
                _hover = index;
                Invalidate();
            }

            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hover != -1)
            {
                _hover = -1;
                Invalidate();
            }

            base.OnMouseLeave(e);
        }

        /// <summary>悬停处画竖虚线、高亮点和提示框（时间 + 电量）。</summary>
        private void DrawHover(Graphics g, Rectangle plot, float scale, DateTime start, DateTime end)
        {
            if (_hover < 0 || _hover >= _points.Count) return;

            HistoryPoint hovered = _points[_hover];
            DateTime time = hovered.Time;
            int percent = hovered.Percent;
            double span = Math.Max(1, (end - start).TotalSeconds);

            float x = Math.Clamp(plot.Left + (float)((time - start).TotalSeconds / span * plot.Width), plot.Left, plot.Right);
            float y = Math.Clamp(plot.Bottom - (float)(percent / 100.0 * plot.Height), plot.Top, plot.Bottom);

            using (var dash = new Pen(Color.FromArgb(150, 150, 150), Math.Max(1f, scale)) { DashStyle = DashStyle.Dash })
            {
                g.DrawLine(dash, x, plot.Top, x, plot.Bottom);
            }

            using (var ring = new SolidBrush(Color.White))
            using (var core = new SolidBrush(LineColor))
            {
                float radius = Math.Max(4f, 4.6f * scale);
                g.FillEllipse(ring, x - radius - 1.5f, y - radius - 1.5f, (radius + 1.5f) * 2, (radius + 1.5f) * 2);
                g.FillEllipse(core, x - radius, y - radius, radius * 2, radius * 2);
            }

            using var titleFont = new Font("Microsoft YaHei UI", 7.6f * scale);
            using var labelFont = new Font("Microsoft YaHei UI", 8f * scale);
            using var valueFont = new Font("Microsoft YaHei UI", 8.4f * scale, FontStyle.Bold);

            string title = time.ToString("yyyy-MM-dd HH:mm");
            string value = $"{percent}%";
            // 提示框标的是过去某个时刻的状态，用「当时」避免被读成现在正在充电
            string label = hovered.Charging ? "电量 · 当时充电中" : "电量";

            SizeF titleSize = g.MeasureString(title, titleFont);
            SizeF labelSize = g.MeasureString(label, labelFont);
            SizeF valueSize = g.MeasureString(value, valueFont);

            float padX = 9 * scale;
            float padY = 7 * scale;
            float dot = 7 * scale;
            float gap = 6 * scale;
            float lineGap = 14 * scale;

            float rowWidth = dot + gap + labelSize.Width + lineGap + valueSize.Width;
            float boxW = Math.Max(titleSize.Width, rowWidth) + padX * 2;
            float boxH = padY * 2 + titleSize.Height + 4 * scale + Math.Max(valueSize.Height, dot);

            // 默认放右侧，贴边了就翻到左侧
            float bx = x + 14 * scale;
            if (bx + boxW > plot.Right) bx = x - 14 * scale - boxW;
            bx = Math.Clamp(bx, plot.Left, Math.Max(plot.Left, plot.Right - boxW));

            float by = Math.Clamp(y - boxH / 2, plot.Top, Math.Max(plot.Top, plot.Bottom - boxH));

            using (GraphicsPath path = RoundedRect(new RectangleF(bx, by, boxW, boxH), 6 * scale))
            using (var background = new SolidBrush(Color.FromArgb(240, 58, 58, 60)))
            {
                g.FillPath(background, path);
            }

            using var white = new SolidBrush(Color.White);
            using var dim = new SolidBrush(Color.FromArgb(205, 255, 255, 255));

            g.DrawString(title, titleFont, dim, bx + padX, by + padY);

            float rowY = by + padY + titleSize.Height + 4 * scale;
            using (var dotBrush = new SolidBrush(LineColor))
            {
                g.FillEllipse(dotBrush, bx + padX, rowY + (valueSize.Height - dot) / 2, dot, dot);
            }

            g.DrawString(label, labelFont, white, bx + padX + dot + gap, rowY);
            g.DrawString(value, valueFont, white, bx + boxW - padX - valueSize.Width, rowY);
        }

        private static GraphicsPath RoundedRect(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height));

            path.AddArc(rect.X, rect.Y, d, d, 180f, 90f);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270f, 90f);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0f, 90f);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90f, 90f);
            path.CloseFigure();
            return path;
        }

        private void DrawTimeAxis(Graphics g, Rectangle plot, float scale, Font font, Brush brush)
        {
            DateTime end = DateTime.Now;
            DateTime start = end - _window;
            string format = _window <= TimeSpan.FromHours(24) ? "HH:mm" : "MM-dd";

            const int ticks = 4;
            for (int i = 0; i <= ticks; i++)
            {
                double ratio = (double)i / ticks;
                DateTime time = start + TimeSpan.FromTicks((long)((end - start).Ticks * ratio));
                string label = time.ToString(format);

                SizeF size = g.MeasureString(label, font);
                float x = plot.Left + (float)(plot.Width * ratio) - size.Width / 2;
                x = Math.Clamp(x, plot.Left - 2, plot.Right - size.Width + 2);
                g.DrawString(label, font, brush, x, plot.Bottom + 12 * scale);
            }
        }
    }
}
