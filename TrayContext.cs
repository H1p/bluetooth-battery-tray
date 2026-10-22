using Microsoft.Win32;
using System.Media;

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

internal sealed class TrayContext : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly BatteryMonitor _monitor;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _timer;

    private readonly ToolStripMenuItem _deviceItem;
    private readonly ToolStripMenuItem _detailItem;
    private readonly ToolStripMenuItem _refreshItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly ToolStripMenuItem _exitItem;

    private BatteryReading _last = new();
    private DeviceKind _lastKind = (DeviceKind)(-1);
    private Color _lastMenuColor = Color.Empty;
    private int? _lastPercent = -2;
    private bool _lastConnected = true;
    private bool _lowNotified;
    private bool _busy;

    public TrayContext()
    {
        _settings = AppSettings.Load();
        BatteryIcon.Configure(_settings.ColorRedBelow, _settings.ColorGreenFrom);

        _deviceItem = new ToolStripMenuItem("正在读取…") { Enabled = false };
        _detailItem = new ToolStripMenuItem(string.Empty) { Enabled = false };
        _refreshItem = new ToolStripMenuItem("立即刷新", null, async (_, _) => await RefreshAsync(true));
        _settingsItem = new ToolStripMenuItem("设置…", null, (_, _) => OpenSettings());
        _autoStartItem = new ToolStripMenuItem("开机自动启动", null, (_, _) => ToggleAutoStart())
        {
            Checked = Startup.IsEnabled(),
            CheckOnClick = false,
        };
        _exitItem = new ToolStripMenuItem("退出", null, (_, _) => ExitApp());

        _deviceItem.Font = new Font(_deviceItem.Font, FontStyle.Bold);

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _deviceItem,
            _detailItem,
            new ToolStripSeparator(),
            _refreshItem,
            _settingsItem,
            _autoStartItem,
            new ToolStripSeparator(),
            _exitItem,
        });

        _tray = new NotifyIcon
        {
            Icon = BatteryIcon.Create(null, false),
            Text = "蓝牙鼠标电量 · 正在读取…",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _tray.DoubleClick += async (_, _) => await RefreshAsync(true);

        _monitor = new BatteryMonitor(_settings);
        _monitor.Updated += reading => Apply(reading);

        _timer = new System.Windows.Forms.Timer { Interval = _settings.PollSeconds * 1000 };
        _timer.Tick += async (_, _) => await RefreshAsync(false);
        _timer.Start();

        _monitor.Log($"=== 启动 v{Application.ProductVersion} 轮询={_settings.PollSeconds}s 阈值={_settings.LowThreshold}% GATT={_settings.UseGatt} ===");

        // 首次进入 UI 循环后再拉数据
        var sync = SynchronizationContext.Current;
        Task.Delay(120).ContinueWith(_ =>
        {
            sync?.Post(async __ => await RefreshAsync(true), null);
        });
    }

    private async Task RefreshAsync(bool force)
    {
        if (_busy && !force) return;
        _busy = true;
        try
        {
            _refreshItem.Enabled = false;
            _refreshItem.Text = "刷新中…";

            BatteryReading reading = await _monitor.PollAsync(force);
            Apply(reading);
        }
        catch (Exception ex)
        {
            _monitor.Log($"[UI] 刷新异常: {ex}");
            Apply(new BatteryReading
            {
                Percent = null,
                DeviceName = "读取失败",
                Connected = false,
                Error = ex.Message,
            });
        }
        finally
        {
            _refreshItem.Text = "立即刷新";
            _refreshItem.Enabled = true;
            _busy = false;
        }
    }

    private void Apply(BatteryReading reading)
    {
        _last = reading;

        bool iconDirty = reading.Percent != _lastPercent || reading.Connected != _lastConnected;
        if (iconDirty)
        {
            _lastPercent = reading.Percent;
            _lastConnected = reading.Connected;
            Icon? old = _tray.Icon;
            _tray.Icon = BatteryIcon.Create(reading.Percent, reading.Connected);
            old?.Dispose();
        }

        // 菜单图标和托盘图标用同一套档位色，跨档或换设备时才重建
        Color menuColor = BatteryIcon.ColorFor(reading.Percent, reading.Connected);
        if (reading.Kind != _lastKind || menuColor != _lastMenuColor)
        {
            _lastKind = reading.Kind;
            _lastMenuColor = menuColor;
            Image? oldIcon = _deviceItem.Image;
            _deviceItem.Image = DeviceIcons.Render(reading.Kind, 24, menuColor);
            _deviceItem.ImageScaling = ToolStripItemImageScaling.SizeToFit;
            oldIcon?.Dispose();
        }

        string percentText = reading.Percent is int p ? $"{p}%" : "未知";

        _deviceItem.Text = reading.Percent is null
            ? reading.DeviceName
            : $"{reading.DeviceName} · {percentText}";

        string detail = reading.Source;
        if (reading.Stamp is DateTime st) detail += $" · 采集于 {Ago(st)}";
        if (reading.Subscribed) detail += " · 推送已开启";
        if (!reading.Connected) detail = "设备未连接";
        _detailItem.Text = detail;

        string tip = reading.Percent is null
            ? $"{reading.DeviceName} · 电量未知"
            : $"{reading.DeviceName} · {percentText} · {Ago(reading.Stamp)}";
        _tray.Text = Truncate(tip, 63);

        CheckLowBattery(reading);
    }

    private void CheckLowBattery(BatteryReading reading)
    {
        if (!_settings.NotifyOnLow || reading.Percent is not int p) return;

        if (p > _settings.LowThreshold)
        {
            // 已回升（换电池或充电），允许下次再提醒
            if (_lowNotified && p > _settings.LowThreshold + 5) _lowNotified = false;
            return;
        }

        if (_lowNotified) return;
        _lowNotified = true;

        PopupNotice.Show(
            $"{DeviceIcons.Describe(reading.Kind)}电量仅剩 {p}%",
            $"{reading.DeviceName} 电量偏低，建议尽快充电或更换电池。",
            p,
            _settings.PlaySound,
            reading.Kind);

        _monitor.Log($"[通知] 低电量提醒 {p}%");
    }

    private void OpenSettings()
    {
        using var form = new SettingsForm(
            _settings,
            _last,
            (title, body) => PopupNotice.Show(title, body, _last.Percent, false));
        form.ShowDialog();

        _settings.Normalized();
        _settings.Save();

        // 阈值可能改了，重新配置配色并强制重绘一次图标
        BatteryIcon.Configure(_settings.ColorRedBelow, _settings.ColorGreenFrom);
        _lastPercent = -2;

        _timer.Interval = _settings.PollSeconds * 1000;
        _autoStartItem.Checked = Startup.IsEnabled();

        _ = RefreshAsync(true);
    }

    private void ToggleAutoStart()
    {
        bool target = !Startup.IsEnabled();
        Startup.Set(target);
        _autoStartItem.Checked = Startup.IsEnabled();
    }

    private void ExitApp()
    {
        _monitor.Log("=== 退出 ===");
        _timer.Stop();
        _tray.Visible = false;
        _monitor.Dispose();
        _tray.Dispose();
        _menu.Dispose();
        ExitThread();
    }

    private static string Ago(DateTime? stamp)
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
            _monitor.Dispose();
            _tray.Dispose();
            _menu.Dispose();
        }
        base.Dispose(disposing);
    }
}
