using System.Drawing.Drawing2D;
using System.Media;

namespace MouseBattery;

/// <summary>
/// 设置窗口。
/// 布局全部以 96 DPI 为基准手写，再用 Graphics.DpiX 等比放大。
/// 不用 AutoScaleMode：代码构造的窗体和设计器生成的窗体缩放行为不一致，
/// 实测在 150% 缩放下会出现文字被裁切，手动换算更可控。
/// </summary>
internal sealed class SettingsForm : Form
{
    private const int MaxSeconds = 12 * 60 * 60;
    private const int MaxDeviceRows = 4;

    private readonly AppSettings _settings;
    private readonly Action<string, string>? _previewNotify;
    private readonly Func<Task<IReadOnlyList<BatteryReading>>>? _refresh;
    private readonly float _scale;

    private IReadOnlyList<BatteryReading> _readings;
    private readonly GroupBox _deviceBox;
    private readonly Panel _rowHost;
    private readonly Button _btnRefresh;
    private readonly int _rowCapacity;

    private readonly NumericUpDown _threshold;
    private readonly NumericUpDown _redBelow;
    private readonly NumericUpDown _greenFrom;
    private readonly NumericUpDown _poll;
    private readonly ComboBox _pollUnit;
    private readonly CheckBox _notify;
    private readonly CheckBox _sound;
    private readonly CheckBox _autoStart;
    private readonly CheckBox _useGatt;

    private int _unitSeconds = 1;
    private bool _suspendUnitEvent;
    private readonly List<(BatteryReading Reading, Label Label)> _nameLabels = new();

    public SettingsForm(
        AppSettings settings,
        IReadOnlyList<BatteryReading> readings,
        Action<string, string>? previewNotify = null,
        Func<Task<IReadOnlyList<BatteryReading>>>? refresh = null)
    {
        _settings = settings;
        _readings = readings;
        _previewNotify = previewNotify;
        _refresh = refresh;
        _scale = GetDpiScale();

        int rows = Math.Clamp(readings.Count, 1, MaxDeviceRows);
        _rowCapacity = rows;
        int infoH = S(30) + rows * S(26) + S(32);

        int infoY = S(12);
        int alertY = infoY + infoH + S(8);
        int pollY = alertY + S(182) + S(8);
        int btnY = pollY + S(96) + S(12);

        Text = "设备电量 · 设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Microsoft YaHei UI", 9f);
        BackColor = Color.FromArgb(250, 250, 250);
        ClientSize = new Size(S(420), btnY + S(30) + S(8));

        // ---- 设备列表
        _deviceBox = new GroupBox
        {
            Text = readings.Count > 0 ? $"已识别设备（{readings.Count}）" : "当前设备",
            Location = new Point(S(16), infoY),
            Size = new Size(S(388), infoH),
            BackColor = Color.White,
        };

        // 主动刷新：不用等轮询周期，也不用手动重开窗口
        _btnRefresh = new Button
        {
            Text = "刷新",
            Location = new Point(S(296), S(5)),
            Size = new Size(S(76), S(22)),
            FlatStyle = FlatStyle.System,
            Font = new Font("Microsoft YaHei UI", 8.5f),
        };
        _btnRefresh.Click += async (_, _) => await RefreshDeviceRowsAsync();

        _rowHost = new Panel
        {
            Location = new Point(S(1), S(28)),
            Size = new Size(S(386), rows * S(26)),
            BackColor = Color.Transparent,
        };

        int footerY = S(30) + rows * S(26);

        var lblCaveat = new Label
        {
            Text = "刷新速度由设备固件决定",
            Location = new Point(S(15), footerY + S(5)),
            Size = new Size(S(210), S(16)),
            ForeColor = Color.FromArgb(140, 140, 140),
            Font = new Font("Microsoft YaHei UI", 7.5f),
        };

        var btnEditNames = new Button
        {
            Text = "编辑设备名称…",
            Location = new Point(S(236), footerY),
            Size = new Size(S(136), S(24)),
            FlatStyle = FlatStyle.System,
            Font = new Font("Microsoft YaHei UI", 8.5f),
        };
        btnEditNames.Click += (_, _) => EditDeviceNames();

        _deviceBox.Controls.AddRange(new Control[] { _btnRefresh, _rowHost, lblCaveat, btnEditNames });
        BuildDeviceRows(readings);

        // ---- 提醒
        var alertBox = new GroupBox
        {
            Text = "提醒与配色",
            Location = new Point(S(16), alertY),
            Size = new Size(S(388), S(182)),
            BackColor = Color.White,
        };

        var lblThreshold = new Label
        {
            Text = "低于",
            Location = new Point(S(14), S(30)),
            Size = new Size(S(40), S(22)),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _threshold = new NumericUpDown
        {
            Location = new Point(S(58), S(28)),
            Size = new Size(S(66), S(24)),
            Minimum = 5,
            Maximum = 80,
            Value = settings.LowThreshold,
            Increment = 5,
        };

        var lblPercent = new Label
        {
            Text = "% 时弹出提醒",
            Location = new Point(S(130), S(30)),
            Size = new Size(S(150), S(22)),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        var lblRed = new Label
        {
            Text = "红色 低于",
            Location = new Point(S(14), S(64)),
            Size = new Size(S(74), S(22)),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _redBelow = new NumericUpDown
        {
            Location = new Point(S(90), S(62)),
            Size = new Size(S(60), S(24)),
            Minimum = 1,
            Maximum = 99,
            Value = Math.Clamp(settings.ColorRedBelow, 1, 99),
            Increment = 5,
        };

        var lblP1 = new Label
        {
            Text = "%",
            Location = new Point(S(152), S(64)),
            Size = new Size(S(20), S(22)),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        var lblGreen = new Label
        {
            Text = "绿色 ≥",
            Location = new Point(S(182), S(64)),
            Size = new Size(S(56), S(22)),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _greenFrom = new NumericUpDown
        {
            Location = new Point(S(240), S(62)),
            Size = new Size(S(60), S(24)),
            Minimum = 2,
            Maximum = 100,
            Value = Math.Clamp(settings.ColorGreenFrom, 2, 100),
            Increment = 5,
        };

        var lblP2 = new Label
        {
            Text = "%",
            Location = new Point(S(302), S(64)),
            Size = new Size(S(20), S(22)),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _notify = new CheckBox
        {
            Text = "弹出提醒卡片",
            Location = new Point(S(14), S(98)),
            Size = new Size(S(170), S(24)),
            Checked = settings.NotifyOnLow,
        };

        _sound = new CheckBox
        {
            Text = "同时响提示音",
            Location = new Point(S(196), S(98)),
            Size = new Size(S(170), S(24)),
            Checked = settings.PlaySound,
        };

        var btnTest = new Button
        {
            Text = "测试一下",
            Location = new Point(S(14), S(130)),
            Size = new Size(S(100), S(28)),
        };
        btnTest.Click += (_, _) => ShowTestNotification();

        var lblColorHint = new Label
        {
            Text = "红色分界必须小于绿色分界，落在两者之间显示黄色。",
            Location = new Point(S(14), S(162)),
            Size = new Size(S(360), S(16)),
            ForeColor = Color.FromArgb(140, 140, 140),
            Font = new Font("Microsoft YaHei UI", 7.5f),
        };

        alertBox.Controls.AddRange(new Control[]
        {
            lblThreshold, _threshold, lblPercent,
            lblRed, _redBelow, lblP1,
            lblGreen, _greenFrom, lblP2,
            _notify, _sound, btnTest, lblColorHint,
        });

        // ---- 采集
        var pollBox = new GroupBox
        {
            Text = "采集",
            Location = new Point(S(16), pollY),
            Size = new Size(S(388), S(96)),
            BackColor = Color.White,
        };

        var lblPoll = new Label
        {
            Text = "轮询间隔",
            Location = new Point(S(14), S(28)),
            Size = new Size(S(86), S(22)),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _poll = new NumericUpDown
        {
            Location = new Point(S(104), S(26)),
            Size = new Size(S(72), S(24)),
        };

        _pollUnit = new ComboBox
        {
            Location = new Point(S(182), S(25)),
            Size = new Size(S(78), S(26)),
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.System,
        };
        _pollUnit.Items.AddRange(new object[] { "秒", "分钟", "小时" });

        _suspendUnitEvent = true;
        int unitIndex = PickUnitIndex(settings.PollSeconds);
        _unitSeconds = UnitFactor(unitIndex);
        (decimal min, decimal max) = UnitRange(unitIndex);
        _poll.Minimum = min;
        _poll.Maximum = max;
        _poll.Value = ClampDecimal(Math.Round((decimal)settings.PollSeconds / _unitSeconds), min, max);
        _pollUnit.SelectedIndex = unitIndex;
        _suspendUnitEvent = false;
        _pollUnit.SelectedIndexChanged += (_, _) => OnUnitChanged();

        var lblHint = new Label
        {
            Text = "最长 12 小时",
            Location = new Point(S(268), S(28)),
            Size = new Size(S(110), S(22)),
            ForeColor = Color.FromArgb(140, 140, 140),
            Font = new Font("Microsoft YaHei UI", 8f),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _useGatt = new CheckBox
        {
            Text = "启用 GATT 实时订阅（更准，略耗电）",
            Location = new Point(S(14), S(60)),
            Size = new Size(S(358), S(24)),
            Checked = settings.UseGatt,
        };

        pollBox.Controls.AddRange(new Control[]
        {
            lblPoll, _poll, _pollUnit, lblHint, _useGatt,
        });

        // ---- 底部
        _autoStart = new CheckBox
        {
            Text = "开机自动启动",
            Location = new Point(S(20), btnY + S(4)),
            Size = new Size(S(170), S(24)),
            Checked = Startup.IsEnabled(),
        };

        var btnOk = new Button
        {
            Text = "确定",
            Location = new Point(S(232), btnY),
            Size = new Size(S(84), S(30)),
            DialogResult = DialogResult.OK,
        };

        var btnCancel = new Button
        {
            Text = "取消",
            Location = new Point(S(322), btnY),
            Size = new Size(S(84), S(30)),
            DialogResult = DialogResult.Cancel,
        };

        btnOk.Click += (_, _) => SaveAll();

        AcceptButton = btnOk;
        CancelButton = btnCancel;

        Controls.AddRange(new Control[]
        {
            _deviceBox, alertBox, pollBox, _autoStart, btnOk, btnCancel,
        });
    }

    /// <summary>重建设备行。刷新后设备数量可能变化，所以整块重建而不是逐个改文本。</summary>
    private void BuildDeviceRows(IReadOnlyList<BatteryReading> readings)
    {
        _rowHost.SuspendLayout();
        try
        {
            _rowHost.Controls.Clear();
            _nameLabels.Clear();

            if (readings.Count == 0)
            {
                _rowHost.Controls.Add(new Label
                {
                    Text = "尚未读取到设备",
                    Location = new Point(S(15), S(2)),
                    Size = new Size(S(340), S(22)),
                    ForeColor = Color.FromArgb(120, 120, 120),
                });
            }
            else
            {
                for (int i = 0; i < _rowCapacity; i++)
                {
                    int rowY = i * S(26);

                    // 设备比预留行多时，最后一行改成"还有 N 台"
                    if (readings.Count > _rowCapacity && i == _rowCapacity - 1)
                    {
                        _rowHost.Controls.Add(new Label
                        {
                            Text = $"…等共 {readings.Count} 台设备",
                            Location = new Point(S(41), rowY),
                            Size = new Size(S(330), S(22)),
                            ForeColor = Color.FromArgb(120, 120, 120),
                        });
                        break;
                    }

                    BatteryReading r = readings[i];
                    Color color = BatteryIcon.ColorFor(r.Percent, r.Connected);

                    // 图标画在更大的画布上，右上角留出角标的位置
                    const int iconSize = 18;
                    const int padTop = 6;
                    const int padRight = 12;

                    using (Bitmap glyph = DeviceIcons.Render(r.Kind, S(iconSize), color))
                    {
                        var canvas = new Bitmap(S(iconSize + padRight), S(iconSize + padTop));
                        using (Graphics cg = Graphics.FromImage(canvas))
                        {
                            cg.SmoothingMode = SmoothingMode.AntiAlias;
                            cg.Clear(Color.Transparent);
                            cg.DrawImage(glyph, 0, S(padTop));
                            DeviceIcons.DrawTransportBadge(
                                cg,
                                new RectangleF(0, S(padTop), S(iconSize), S(iconSize)),
                                r.Transport,
                                _scale);
                        }

                        var iconBox = new PictureBox
                        {
                            Location = new Point(S(15), rowY + S(2) - S(padTop)),
                            Size = canvas.Size,
                            SizeMode = PictureBoxSizeMode.Normal,
                            Image = canvas,
                            BackColor = Color.Transparent,
                        };
                        _rowHost.Controls.Add(iconBox);
                    }

                    var lblName = new Label
                    {
                        Text = string.IsNullOrEmpty(r.DeviceName) ? "未知设备" : r.DeviceName,
                        Location = new Point(S(41), rowY),
                        Size = new Size(S(168), S(22)),
                        Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
                        AutoEllipsis = true,
                        TextAlign = ContentAlignment.MiddleLeft,
                    };

                    // 最新更新时间：贴在百分比左边，灰色小字，不抢主信息
                    var lblStamp = new Label
                    {
                        Text = TrayContext.Ago(r.Stamp),
                        Location = new Point(S(211), rowY),
                        Size = new Size(S(78), S(22)),
                        ForeColor = Color.FromArgb(150, 150, 150),
                        Font = new Font("Microsoft YaHei UI", 7.8f),
                        TextAlign = ContentAlignment.MiddleRight,
                    };

                    var lblRowPercent = new Label
                    {
                        Text = r.Percent is int p ? $"{p}%" : "未知",
                        Location = new Point(S(291), rowY),
                        Size = new Size(S(80), S(22)),
                        ForeColor = color,
                        Font = new Font("Microsoft YaHei UI", 9.4f, FontStyle.Bold),
                        TextAlign = ContentAlignment.MiddleRight,
                    };

                    _rowHost.Controls.AddRange(new Control[] { lblName, lblStamp, lblRowPercent });
                    _nameLabels.Add((r, lblName));
                }
            }

            _deviceBox.Text = readings.Count > 0 ? $"已识别设备（{readings.Count}）" : "当前设备";
        }
        finally
        {
            _rowHost.ResumeLayout();
        }
    }

    private async Task RefreshDeviceRowsAsync()
    {
        if (_refresh == null) return;

        _btnRefresh.Enabled = false;
        _btnRefresh.Text = "刷新中…";
        try
        {
            IReadOnlyList<BatteryReading> fresh = await _refresh();
            _readings = fresh;
            BuildDeviceRows(fresh);
        }
        catch
        {
            // 刷新失败保留原有列表，不弹窗打断
        }
        finally
        {
            _btnRefresh.Text = "刷新";
            _btnRefresh.Enabled = true;
        }
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

    private static decimal ClampDecimal(decimal value, decimal min, decimal max) =>
        value < min ? min : value > max ? max : value;

    /// <summary>能把秒数整除且不至于太小，就往上用更大的单位显示。</summary>
    private static int PickUnitIndex(int seconds)
    {
        if (seconds >= 3600 && seconds % 3600 == 0) return 2;
        if (seconds >= 60 && seconds % 60 == 0) return 1;
        return 0;
    }

    private static int UnitFactor(int index) => index switch
    {
        1 => 60,
        2 => 3600,
        _ => 1,
    };

    private static (decimal Min, decimal Max) UnitRange(int index) => index switch
    {
        1 => (1m, 720m),      // 1 分钟 ~ 12 小时
        2 => (1m, 12m),       // 1 ~ 12 小时
        _ => (10m, 3600m),    // 10 秒 ~ 1 小时
    };

    private void OnUnitChanged()
    {
        if (_suspendUnitEvent) return;

        decimal totalSeconds = _poll.Value * _unitSeconds;
        int index = _pollUnit.SelectedIndex;
        if (index < 0) return;

        _unitSeconds = UnitFactor(index);
        (decimal min, decimal max) = UnitRange(index);

        _suspendUnitEvent = true;
        try
        {
            _poll.Minimum = min;
            _poll.Maximum = max;
            decimal converted = Math.Round(totalSeconds / _unitSeconds, MidpointRounding.AwayFromZero);
            _poll.Value = ClampDecimal(converted, min, max);
        }
        finally
        {
            _suspendUnitEvent = false;
        }
    }

    private void SaveAll()
    {
        int seconds = (int)Math.Round(_poll.Value * _unitSeconds);
        _settings.PollSeconds = Math.Clamp(seconds, 10, MaxSeconds);
        // 红绿分界一旦重叠，黄色区间会消失，保存时自动纠正
        if (_greenFrom.Value <= _redBelow.Value)
        {
            _greenFrom.Value = Math.Min(100m, _redBelow.Value + 1m);
        }

        _settings.LowThreshold = (int)_threshold.Value;
        _settings.ColorRedBelow = (int)_redBelow.Value;
        _settings.ColorGreenFrom = (int)_greenFrom.Value;
        _settings.NotifyOnLow = _notify.Checked;
        _settings.PlaySound = _sound.Checked;
        _settings.UseGatt = _useGatt.Checked;
        _settings.AutoStart = _autoStart.Checked;
        _settings.Save();

        Startup.Set(_autoStart.Checked);
    }

    private void EditDeviceNames()
    {
        using var form = new DeviceNameForm(_settings, _readings);
        if (form.ShowDialog(this) != DialogResult.OK) return;

        foreach ((BatteryReading reading, Label label) in _nameLabels)
        {
            string name = DisplayNameOf(reading);
            label.Text = string.IsNullOrEmpty(name) ? "未知设备" : name;
        }
    }

    /// <summary>有别名用别名，否则用设备原名。</summary>
    private string DisplayNameOf(BatteryReading reading)
    {
        string mac = (reading.Address ?? string.Empty).Replace(":", string.Empty).Trim().ToLowerInvariant();
        if (_settings.DeviceAliases.TryGetValue(mac, out string? alias) && !string.IsNullOrWhiteSpace(alias))
        {
            return alias.Trim();
        }
        return string.IsNullOrEmpty(reading.OriginalName) ? reading.DeviceName : reading.OriginalName;
    }

    private void ShowTestNotification()
    {        BatteryReading? first = _readings.FirstOrDefault();
        DeviceKind kind = first?.Kind ?? DeviceKind.Mouse;
        string kindText = DeviceIcons.Describe(kind);
        string title = $"{kindText}电量仅剩（测试）";
        string body = first?.Percent is int p
            ? $"{first.DeviceName} 电量 {p}%，低于 {_threshold.Value}% 时会这样提醒你。"
            : $"设备电量低于 {_threshold.Value}% 时会这样提醒你。";

        if (_previewNotify != null)
        {
            _previewNotify(title, body);
            if (_sound.Checked) SystemSounds.Exclamation.Play();
            return;
        }

        PopupNotice.Show(title, body, first?.Percent, _sound.Checked, kind);
    }
}
