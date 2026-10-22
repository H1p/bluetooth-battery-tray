using System.Text.Json;
using System.Text.Json.Serialization;

namespace MouseBattery;

internal sealed class AppSettings
{
    /// <summary>锁定的设备 MAC（12 位十六进制，不含冒号）。留空表示自动挑选。</summary>
    public string DeviceAddress { get; set; } = string.Empty;

    /// <summary>锁定的设备名称（仅用于显示与二次确认）。</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>低电量提醒阈值，百分比。</summary>
    public int LowThreshold { get; set; } = 20;

    /// <summary>低于此电量显示红色。</summary>
    public int ColorRedBelow { get; set; } = 20;

    /// <summary>达到此电量显示绿色，中间区间为黄色。</summary>
    public int ColorGreenFrom { get; set; } = 50;

    /// <summary>系统属性兜底轮询间隔（秒）。</summary>
    public int PollSeconds { get; set; } = 30;

    /// <summary>是否弹低电量通知。</summary>
    public bool NotifyOnLow { get; set; } = true;

    /// <summary>通知是否带提示音。</summary>
    public bool PlaySound { get; set; } = true;

    /// <summary>开机自动启动。</summary>
    public bool AutoStart { get; set; }

    /// <summary>是否启用 GATT 主动订阅（关掉则只读系统缓存）。</summary>
    public bool UseGatt { get; set; } = true;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static string? _cachedPath;

    public static string SettingsPath
    {
        get
        {
            if (_cachedPath != null) return _cachedPath;

            string beside = Path.Combine(AppContext.BaseDirectory, "settings.json");
            if (CanWrite(AppContext.BaseDirectory))
            {
                _cachedPath = beside;
                return _cachedPath;
            }

            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MouseBattery");
            Directory.CreateDirectory(dir);
            _cachedPath = Path.Combine(dir, "settings.json");
            return _cachedPath;
        }
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".write_probe");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded != null) return loaded.Normalized();
            }
        }
        catch
        {
            // 配置损坏时退回默认值，不让程序起不来
        }

        var fresh = new AppSettings();
        fresh.Save();
        return fresh;
    }

    public AppSettings Normalized()
    {
        LowThreshold = Math.Clamp(LowThreshold, 5, 80);
        PollSeconds = Math.Clamp(PollSeconds, 10, 12 * 60 * 60);

        ColorRedBelow = Math.Clamp(ColorRedBelow, 1, 99);
        ColorGreenFrom = Math.Clamp(ColorGreenFrom, 2, 100);
        // 红绿分界不能重叠，否则黄色区间会变成空集
        if (ColorGreenFrom <= ColorRedBelow) ColorGreenFrom = Math.Min(100, ColorRedBelow + 1);
        DeviceAddress = (DeviceAddress ?? string.Empty).Replace(":", string.Empty).Trim().ToLowerInvariant();
        DeviceName ??= string.Empty;
        return this;
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // 写盘失败不影响运行
        }
    }
}
