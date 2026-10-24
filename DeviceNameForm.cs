namespace MouseBattery;

/// <summary>
/// 编辑设备名称。改完写回 settings.json 的 DeviceAliases（键是设备 MAC），
/// 留空或点「恢复」即回到设备在系统里的原始名称。
/// </summary>
internal sealed class DeviceNameForm : Form
{
    private const int BaseWidth = 480;
    private const int RowHeight = 46;

    private readonly AppSettings _settings;
    private readonly float _scale;
    private readonly List<Row> _rows = new();

    private sealed class Row
    {
        public string Mac = string.Empty;
        public string Original = string.Empty;
        public TextBox Input = null!;
    }

    public DeviceNameForm(AppSettings settings, IReadOnlyList<BatteryReading> readings)
    {
        _settings = settings;
        _scale = GetDpiScale();

        int count = readings.Count;
        int btnY = S(40) + count * S(RowHeight) + S(8);

        Text = "编辑设备名称";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Microsoft YaHei UI", 9f);
        BackColor = Color.FromArgb(250, 250, 250);
        ClientSize = new Size(S(BaseWidth), btnY + S(30) + S(12));

        var hint = new Label
        {
            Text = count == 0
                ? "当前没有识别到设备。"
                : "直接改文本框里的名称；点「恢复」回到设备原名，留空同样视为恢复。",
            Location = new Point(S(16), S(12)),
            Size = new Size(S(BaseWidth - 32), S(20)),
            ForeColor = Color.FromArgb(110, 110, 110),
            Font = new Font("Microsoft YaHei UI", 8.5f),
        };
        Controls.Add(hint);

        for (int i = 0; i < count; i++)
        {
            BatteryReading reading = readings[i];
            int top = S(40) + i * S(RowHeight);
            string mac = NormalizeMac(reading.Address);

            var icon = new PictureBox
            {
                Location = new Point(S(16), top + S(6)),
                Size = new Size(S(20), S(20)),
                SizeMode = PictureBoxSizeMode.Zoom,
                Image = DeviceIcons.Render(reading.Kind, S(20), BatteryIcon.ColorFor(reading.Percent, reading.Connected)),
                BackColor = Color.Transparent,
            };

            var input = new TextBox
            {
                Location = new Point(S(44), top + S(4)),
                Size = new Size(S(352), S(24)),
                Text = reading.DeviceName,
                BorderStyle = BorderStyle.FixedSingle,
            };

            var original = new Label
            {
                Text = $"原名 {reading.OriginalName} · MAC {ShortMac(mac)}",
                Location = new Point(S(44), top + S(28)),
                Size = new Size(S(352), S(14)),
                ForeColor = Color.FromArgb(145, 145, 145),
                Font = new Font("Microsoft YaHei UI", 7.5f),
                AutoEllipsis = true,
            };

            var restore = new Button
            {
                Text = "恢复",
                Location = new Point(S(404), top + S(4)),
                Size = new Size(S(60), S(24)),
                FlatStyle = FlatStyle.System,
                Font = new Font("Microsoft YaHei UI", 8.5f),
            };

            var row = new Row { Mac = mac, Original = reading.OriginalName, Input = input };
            restore.Click += (_, _) => input.Text = row.Original;
            _rows.Add(row);

            Controls.AddRange(new Control[] { icon, input, original, restore });
        }

        var btnOk = new Button
        {
            Text = "确定",
            Location = new Point(S(288), btnY),
            Size = new Size(S(84), S(30)),
            DialogResult = DialogResult.OK,
        };
        btnOk.Click += (_, _) => SaveAll();

        var btnCancel = new Button
        {
            Text = "取消",
            Location = new Point(S(380), btnY),
            Size = new Size(S(84), S(30)),
            DialogResult = DialogResult.Cancel,
        };

        AcceptButton = btnOk;
        CancelButton = btnCancel;
        Controls.AddRange(new Control[] { btnOk, btnCancel });
    }

    private int S(int baseValue) => (int)Math.Round(baseValue * _scale);

    private static string NormalizeMac(string address) =>
        (address ?? string.Empty).Replace(":", string.Empty).Trim().ToLowerInvariant();

    private static string ShortMac(string mac) =>
        mac.Length >= 4 ? mac[^4..].ToUpperInvariant() : mac.ToUpperInvariant();

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

    private void SaveAll()
    {
        foreach (Row row in _rows)
        {
            string text = row.Input.Text.Trim();

            if (text.Length == 0 || string.Equals(text, row.Original, StringComparison.Ordinal))
            {
                _settings.DeviceAliases.Remove(row.Mac);
            }
            else
            {
                _settings.DeviceAliases[row.Mac] = text;
            }
        }

        _settings.Save();
    }
}
