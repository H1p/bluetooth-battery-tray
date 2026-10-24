using System.Text.Json;
using System.Text.Json.Serialization;

namespace MouseBattery;

internal sealed class AppSettings
{
    /// <summary>配置格式版本。将来字段结构变化时靠它做迁移。</summary>
    public int SettingsVersion { get; set; } = 1;

    /// <summary>只显示这一台设备的 MAC（12 位十六进制，不含冒号）。留空表示显示全部。</summary>
    public string DeviceAddress { get; set; } = string.Empty;

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

    /// <summary>
    /// 设备自定义名称。键是 12 位十六进制 MAC（小写、不带冒号），值是显示名。
    /// 编辑设备名称时写入这里，留空即恢复设备原始名称。
    /// </summary>
    public Dictionary<string, string> DeviceAliases { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MouseBattery");

    /// <summary>
    /// 配置固定放在 %LOCALAPPDATA%\MouseBattery，不跟 exe 走。
    /// 早先放在程序目录，一换版本（覆盖 exe 或换目录）配置就丢，所以改成用户目录。
    /// </summary>
    public static string SettingsPath { get; } = Path.Combine(ConfigDir, "settings.json");

    /// <summary>旧版放在程序目录的配置，只在首次启动时迁移一次。</summary>
    private static string LegacyPath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    /// <summary>本次启动如果发生过迁移，这里留一句说明，交给日志输出。</summary>
    public static string? MigrationNote { get; private set; }

    /// <summary>把程序目录里遗留的旧配置搬到用户目录，搬完删掉旧文件，避免下次又被误读。</summary>
    private static void MigrateLegacy()
    {
        try
        {
            string legacy = LegacyPath;
            if (!File.Exists(legacy)) return;
            if (string.Equals(Path.GetFullPath(legacy), Path.GetFullPath(SettingsPath), StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(SettingsPath)) return;   // 新位置已有配置，绝不覆盖

            File.Copy(legacy, SettingsPath, false);

            // 能反序列化出来才算迁移成功，确认后再删旧文件
            string json = File.ReadAllText(SettingsPath);
            if (JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) == null) return;

            File.Delete(legacy);
            MigrationNote = $"配置已迁移: {legacy} → {SettingsPath}";
        }
        catch
        {
            // 迁移失败就继续用新位置的默认值，不影响启动
        }
    }

    public static AppSettings Load()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            MigrateLegacy();

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
        DeviceAliases = CleanAliases(DeviceAliases);
        return this;
    }

    /// <summary>键统一成 12 位小写 MAC，空值和非法值丢掉。</summary>
    private static Dictionary<string, string> CleanAliases(Dictionary<string, string>? raw)
    {
        var cleaned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (raw == null) return cleaned;

        foreach (KeyValuePair<string, string> pair in raw)
        {
            string key = (pair.Key ?? string.Empty).Replace(":", string.Empty).Trim().ToLowerInvariant();
            string value = (pair.Value ?? string.Empty).Trim();
            if (key.Length == 12 && value.Length > 0) cleaned[key] = value;
        }
        return cleaned;
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
