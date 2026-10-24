using Microsoft.Win32;

namespace MouseBattery;

/// <summary>开机自启：写 HKCU 的 Run 键，不需要管理员权限，也不留后台服务。</summary>
internal static class Startup
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MouseBattery";

    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "MouseBattery.exe");

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null) return;

            if (enabled) key.SetValue(ValueName, $"\"{ExecutablePath}\"");
            else if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, false);
        }
        catch
        {
            // 被安全软件拦下时静默失败
        }
    }
}

/// <summary>
/// 托盘常驻只有一个图标，代表"当前最需要注意的那台设备"（电量最低的已连接设备）。
/// 全部设备的电量在悬停卡片和右键菜单里看。
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly BatteryMonitor _monitor;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _hoverDelay;
    private readonly Font _headerFont;

    private readonly ToolStripMenuItem _headerItem;
    private readonly ToolStripMenuItem _detailItem;
    private readonly ToolStripMenuItem _refreshItem;
    private readonly ToolStripMenuItem _autoStartItem;

    /// <summary>已提醒过低电量的设备（键是 MAC），电量回升 5 个点后才允许再提醒。</summary>
    private readonly HashSet<string> _lowNotified = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<BatteryReading> _readings = Array.Empty<BatteryReading>();
    private BatteryReading? _primary;
    private Point _hoverPoint;

    private int? _lastPercent = -2;
    private bool _lastConnected = true;
    private DeviceKind _lastKind = (DeviceKind)(-1);
    private Color _menuColor = Color.Empty;
    private DeviceKind _menuKind = (DeviceKind)(-1);
    private bool _busy;

    public TrayContext()
    {
        _settings = AppSettings.Load();
        BatteryIcon.Configure(_settings.ColorRedBelow, _settings.ColorGreenFrom);
        _headerFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);

        _headerItem = new ToolStripMenuItem("正在读取…") { Enabled = false, Font = _headerFont };
        _detailItem = new ToolStripMenuItem(string.Empty) { Enabled = false };
        var editItem = new ToolStripMenuItem("编辑设备名称…", null, (_, _) => EditDeviceNames());
        _refreshItem = new ToolStripMenuItem("立即刷新全部", null, async (_, _) => await RefreshAsync(true));
        var settingsItem = new ToolStripMenuItem("设置…", null, (_, _) => OpenSettings());
        _autoStartItem = new ToolStripMenuItem("开机自动启动", null, (_, _) => ToggleAutoStart())
        {
            Checked = Startup.IsEnabled(),
            CheckOnClick = false,
        };
        var exitItem = new ToolStripMenuItem("退出", null, (_, _) => ExitApp());

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _headerItem,
            _detailItem,
            new ToolStripSeparator(),
            editItem,
            _refreshItem,
            settingsItem,
            _autoStartItem,
            new ToolStripSeparator(),
            exitItem,
        });
        // 右键菜单弹出时收起悬停卡片，免得两层窗口叠在一起
        _menu.Opening += (_, _) => HoverCard.HideCard();

        _tray = new NotifyIcon
        {
            Icon = BatteryIcon.Create(DeviceKind.Generic, null, false),
            Text = "设备电量 · 正在读取…",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _tray.MouseMove += (_, _) => OnTrayMouseMove();
        _tray.DoubleClick += async (_, _) => await RefreshAsync(true);

        // 悬停 220ms 才弹卡片：鼠标只是划过托盘时不该闪一下
        _hoverDelay = new System.Windows.Forms.Timer { Interval = 220 };
        _hoverDelay.Tick += (_, _) => OnHoverDelayElapsed();

        _monitor = new BatteryMonitor(_settings);
        _monitor.DeviceUpdated += reading => ApplyOne(reading);

        _timer = new System.Windows.Forms.Timer { Interval = _settings.PollSeconds * 1000 };
        _timer.Tick += async (_, _) => await RefreshAsync(false);
        _timer.Start();

        _monitor.Log(
            $"=== 启动 v{Application.ProductVersion} 模式=单图标 轮询={_settings.PollSeconds}s " +
            $"阈值={_settings.LowThreshold}% GATT={_settings.UseGatt} ===");
        _monitor.Log($"配置: {AppSettings.SettingsPath}");
        if (AppSettings.MigrationNote is string migration) _monitor.Log(migration);

        var sync = SynchronizationContext.Current;
        Task.Delay(120).ContinueWith(_ =>
        {
            sync?.Post(async __ =>
            {
                // 先只读系统缓存把图标点亮，再补 GATT 精读
                await RefreshAsync(false, skipGatt: true);
                await RefreshAsync(true);
            }, null);
        });
    }

    // ---------------------------------------------------------------- 刷新

    private async Task RefreshAsync(bool force, bool skipGatt = false)
    {
        if (_busy && !force) return;
        _busy = true;
        try
        {
            SetRefreshing(true);
            List<BatteryReading> list = await _monitor.PollAllAsync(force, allowGatt: !skipGatt);
            Apply(list);
        }
        catch (Exception ex)
        {
            _monitor.Log($"[UI] 刷新异常: {ex}");
        }
        finally
        {
            SetRefreshing(false);
            _busy = false;
        }
    }

    private void SetRefreshing(bool on)
    {
        _refreshItem.Enabled = !on;
        _refreshItem.Text = on ? "刷新中…" : "立即刷新全部";
    }

    private void Apply(IReadOnlyList<BatteryReading> readings)
    {
        _readings = readings;
        _primary = PickPrimary(readings);

        UpdateIcon();
        UpdateMenu();

        if (HoverCard.Current != null) HoverCard.ShowOrUpdate(readings);

        foreach (BatteryReading reading in readings) CheckLowBattery(reading);
    }

    /// <summary>GATT 推送只带一台设备时，替换列表里那一台再整体刷新。</summary>
    private void ApplyOne(BatteryReading reading)
    {
        if (_readings.Count == 0) return;

        var list = _readings.ToList();
        int index = list.FindIndex(r => string.Equals(r.Address, reading.Address, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;

        list[index] = reading;
        Apply(list);
    }

    /// <summary>托盘图标代表谁：优先"已连接且电量最低"的那台；全都断开时取电量最低的。</summary>
    private static BatteryReading? PickPrimary(IReadOnlyList<BatteryReading> readings)
    {
        if (readings.Count == 0) return null;

        return readings
            .OrderBy(r => r.Connected ? 0 : 1)
            .ThenBy(r => r.Percent ?? int.MaxValue)
            .ThenBy(r => (int)r.Kind)
            .First();
    }

    private void UpdateIcon()
    {
        int? percent = _primary?.Percent;
        bool connected = _primary?.Connected ?? false;
        DeviceKind kind = _primary?.Kind ?? DeviceKind.Generic;

        if (percent == _lastPercent && connected == _lastConnected && kind == _lastKind) return;

        Icon? old = _tray.Icon;
        _tray.Icon = BatteryIcon.Create(kind, percent, connected);
        old?.Dispose();

        _lastPercent = percent;
        _lastConnected = connected;
        _lastKind = kind;
    }

    private void UpdateMenu()
    {
        int count = _readings.Count;

        if (count == 0)
        {
            _headerItem.Text = "未找到蓝牙设备";
            _detailItem.Text = "确认设备已配对并处于连接状态";
        }
        else
        {
            int[] values = _readings.Where(r => r.Percent.HasValue).Select(r => r.Percent!.Value).ToArray();
            string lowest = values.Length > 0 ? $"{values.Min()}%" : "未知";
            _headerItem.Text = $"设备电量 · {count} 台，最低 {lowest}";

            string kindText = _primary is null ? "蓝牙设备" : DeviceIcons.Describe(_primary.Kind);
            string name = _primary?.DeviceName ?? "-";
            string percentText = _primary?.Percent is int p ? $"{p}%" : "电量未知";
            _detailItem.Text = $"{kindText} · {name} · {percentText} · {Ago(_primary?.Stamp)}";
        }

        Color color = BatteryIcon.ColorFor(_primary?.Percent, _primary?.Connected ?? false);
        DeviceKind kind = _primary?.Kind ?? DeviceKind.Generic;
        if (kind != _menuKind || color != _menuColor)
        {
            Image? oldImage = _headerItem.Image;
            _headerItem.Image = DeviceIcons.Render(kind, 24, color);
            _headerItem.ImageScaling = ToolStripItemImageScaling.SizeToFit;
            oldImage?.Dispose();
            _menuKind = kind;
            _menuColor = color;
        }

        _tray.Text = Truncate(BuildTooltip(), 63);
    }

    private string BuildTooltip()
    {
        if (_readings.Count == 0) return "设备电量 · 未找到设备";
        if (_primary is null) return $"设备电量 · {_readings.Count} 台";

        string kindText = DeviceIcons.Describe(_primary.Kind);
        string percentText = _primary.Percent is int p ? $"{p}%" : "电量未知";

        return _readings.Count == 1
            ? $"{kindText} · {_primary.DeviceName} · {percentText} · {Ago(_primary.Stamp)}"
            : $"{_readings.Count} 台 · 最低 {percentText} · {_primary.DeviceName}";
    }

    // ---------------------------------------------------------------- 交互

    private void OnTrayMouseMove()
    {
        _hoverPoint = Cursor.Position;

        if (HoverCard.Current != null)
        {
            HoverCard.Poke();
            return;
        }

        if (!_hoverDelay.Enabled) _hoverDelay.Start();
    }

    private void OnHoverDelayElapsed()
    {
        _hoverDelay.Stop();

        if (HoverCard.Current != null || _readings.Count == 0) return;

        // 光标已经离开图标附近就不要再弹，避免划过托盘也弹一下
        Point now = Cursor.Position;
        if (Math.Abs(now.X - _hoverPoint.X) > 48 || Math.Abs(now.Y - _hoverPoint.Y) > 48) return;

        HoverCard.ShowOrUpdate(_readings);
    }

    private void CheckLowBattery(BatteryReading reading)
    {
        if (!_settings.NotifyOnLow || reading.Percent is not int p) return;

        string key = reading.Address;

        if (p > _settings.LowThreshold)
        {
            // 已回升（换电池或充电），允许下次再提醒
            if (p > _settings.LowThreshold + 5) _lowNotified.Remove(key);
            return;
        }

        if (_lowNotified.Contains(key)) return;

        // 陈旧缓存值不打扰
        if (reading.AgeMinutes is double age && age > 360) return;

        _lowNotified.Add(key);

        PopupNotice.Show(
            $"{DeviceIcons.Describe(reading.Kind)}电量仅剩 {p}%",
            $"{reading.DeviceName} 电量偏低，建议尽快充电或更换电池。",
            p,
            _settings.PlaySound,
            reading.Kind);

        _monitor.Log($"[通知] {reading.DeviceName} 低电量 {p}%");
    }

    private void EditDeviceNames()
    {
        HoverCard.HideCard();

        using var form = new DeviceNameForm(_settings, _readings);
        if (form.ShowDialog() != DialogResult.OK) return;

        _settings.Normalized();
        _settings.Save();
        _ = RefreshAsync(true);
    }

    private void OpenSettings()
    {
        HoverCard.HideCard();

        using var form = new SettingsForm(
            _settings,
            _readings,
            (title, body) => PopupNotice.Show(
                title, body, _primary?.Percent, false, _primary?.Kind ?? DeviceKind.Mouse),
            async () =>
            {
                await RefreshAsync(true);
                return _readings;
            });

        form.ShowDialog();

        _settings.Normalized();
        _settings.Save();

        // 阈值可能改了，重新配置配色并强制重绘图标
        BatteryIcon.Configure(_settings.ColorRedBelow, _settings.ColorGreenFrom);
        _lastPercent = -2;
        _menuColor = Color.Empty;
        _menuKind = (DeviceKind)(-1);

        _timer.Interval = _settings.PollSeconds * 1000;
        _autoStartItem.Checked = Startup.IsEnabled();

        _ = RefreshAsync(true);
    }

    private void ToggleAutoStart()
    {
        Startup.Set(!Startup.IsEnabled());
        _autoStartItem.Checked = Startup.IsEnabled();
    }

    private void ExitApp()
    {
        _monitor.Log("=== 退出 ===");
        _timer.Stop();
        _hoverDelay.Stop();
        HoverCard.HideCard();

        _tray.Visible = false;
        _monitor.Dispose();
        _tray.Dispose();
        _menu.Dispose();
        _headerFont.Dispose();
        ExitThread();
    }

    /// <summary>把时间戳说成人话。设置窗口的设备列表也用这个，保持一致。</summary>
    internal static string Ago(DateTime? stamp)
    {
        if (stamp is not DateTime t) return "未知";
        TimeSpan d = DateTime.Now - t;
        if (d.TotalSeconds < 45) return "刚刚";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} 分钟前";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} 小时前";
        return $"{(int)d.TotalDays} 天前";
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _hoverDelay.Dispose();
            _monitor.Dispose();
            _tray.Dispose();
            _menu.Dispose();
            _headerFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
