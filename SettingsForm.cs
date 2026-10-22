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

    private readonly AppSettings _settings;
    private readonly BatteryReading _reading;
    private readonly Action<string, string>? _previewNotify;
    private readonly float _scale;

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

    public SettingsForm(AppSettings settings, BatteryReading reading, Action<string, string>? previewNotify = null)
    {
        _settings = settings;
        _reading = reading;
        _previewNotify = previewNotify;
        _scale = GetDpiScale();

        Text = "蓝牙鼠标电量 · 设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Microsoft YaHei UI", 9f);
        BackColor = Color.FromArgb(250, 250, 250);
        ClientSize = new Size(S(420), S(466));

        // ---- 当前设备
        var infoBox = new GroupBox
        {
            Text = "当前设备",
            Location = new Point(S(16), S(12)),
            Size = new Size(S(388), S(110)),
            BackColor = Color.White,
        };

        var iconBox = new PictureBox
        {
            Location = new Point(S(16), S(30)),
            Size = new Size(S(38), S(38)),
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = DeviceIcons.Render(
                reading.Kind,
                S(38),
                BatteryIcon.ColorFor(reading.Percent, reading.Connected)),
            BackColor = Color.Transparent,
        };

        var lblName = new Label
        {
            Text = string.IsNullOrEmpty(_reading.DeviceName) ? "尚未读取到设备" : _reading.DeviceName,
            Location = new Point(S(64), S(24)),
            Size = new Size(S(310), S(22)),
            Font = new Font("Microsoft YaHei UI", 9.8f, FontStyle.Bold),
        };

        string addrText = string.IsNullOrEmpty(_reading.Address) ? "—" : _reading.Address;
        var lblInfo = new Label
        {
            Text = $"{DeviceIcons.Describe(_reading.Kind)}　·　{addrText}",
            Location = new Point(S(64), S(48)),
            Size = new Size(S(310), S(19)),
            ForeColor = Color.FromArgb(92, 92, 92),
            Font = new Font("Microsoft YaHei UI", 8.5f),
        };

        string percentText = _reading.Percent is int p ? $"{p}%" : "未知";
        var lblSub = new Label
        {
            Text = $"电量 {percentText}　·　来源 {_reading.Source}",
            Location = new Point(S(64), S(68)),
            Size = new Size(S(310), S(19)),
            ForeColor = Color.FromArgb(92, 92, 92),
            Font = new Font("Microsoft YaHei UI", 8.5f),
        };

        var lblCaveat = new Label
        {
            Text = "刷新速度由设备固件决定，软件只保证第一时间收到上报。",
            Location = new Point(S(16), S(92)),
            Size = new Size(S(360), S(16)),
            ForeColor = Color.FromArgb(140, 140, 140),
            Font = new Font("Microsoft YaHei UI", 7.5f),
        };

        infoBox.Controls.AddRange(new Control[] { iconBox, lblName, lblInfo, lblSub, lblCaveat });

        // ---- 提醒
        var alertBox = new GroupBox
        {
            Text = "提醒与配色",
            Location = new Point(S(16), S(130)),
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
            Location = new Point(S(16), S(320)),
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
            Location = new Point(S(20), S(432)),
            Size = new Size(S(170), S(24)),
            Checked = Startup.IsEnabled(),
        };

        var btnOk = new Button
        {
            Text = "确定",
            Location = new Point(S(232), S(428)),
            Size = new Size(S(84), S(30)),
            DialogResult = DialogResult.OK,
        };

        var btnCancel = new Button
        {
            Text = "取消",
            Location = new Point(S(322), S(428)),
            Size = new Size(S(84), S(30)),
            DialogResult = DialogResult.Cancel,
        };

        btnOk.Click += (_, _) => SaveAll();

        AcceptButton = btnOk;
        CancelButton = btnCancel;

        Controls.AddRange(new Control[]
        {
            infoBox, alertBox, pollBox, _autoStart, btnOk, btnCancel,
        });
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

    private void ShowTestNotification()
    {
        string kindText = DeviceIcons.Describe(_reading.Kind);
        string title = $"{kindText}电量仅剩（测试）";
        string body = _reading.Percent is int p
            ? $"{_reading.DeviceName} 电量 {p}%，低于 {_threshold.Value}% 时会这样提醒你。"
            : $"设备电量低于 {_threshold.Value}% 时会这样提醒你。";

        if (_previewNotify != null)
        {
            _previewNotify(title, body);
            if (_sound.Checked) SystemSounds.Exclamation.Play();
            return;
        }

        PopupNotice.Show(title, body, _reading.Percent, _sound.Checked, _reading.Kind);
    }
}
