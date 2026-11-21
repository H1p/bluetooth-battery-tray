using System.Text.Json;
using System.Text.Json.Serialization;

namespace MouseBattery;

/// <summary>一个历史采样点。Charging 为 true 表示当时设备在上报"充电中"。</summary>
internal readonly record struct HistoryPoint(DateTime Time, int Percent, bool Charging);

/// <summary>
/// 电量历史。按设备记录「时间 → 电量」采样，落盘到 %LOCALAPPDATA%。
///
/// 存储策略（刻意做得省）：
///   - 采样时间取读数自己的时间戳，超过 1 小时没上报的不入账，避免缓存值被画成假平线；
///   - 只保留 30 天，超期丢弃；
///   - 单设备超过 1500 点时按「隔点抽样」压缩，分辨率随时间自然变粗。
/// 24 小时视图用原始采样，7 天/30 天视图用压缩后的点，画图时不需要再处理。
/// </summary>
internal sealed class HistoryStore : IDisposable
{
    private const int MaxPointsPerDevice = 1500;
    private const int MinSampleIntervalSeconds = 60;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private static readonly TimeSpan RecentWindow = TimeSpan.FromHours(24);
    // 落盘间隔取 60 秒，与常见轮询间隔一致：进程被强杀时最多丢一分钟。
    // 只有出现新采样点才会写，文件约 100KB，开销可以忽略。
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(60);

    private readonly string _path;
    private readonly Dictionary<string, DeviceHistory> _devices = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastSave = DateTime.MinValue;
    private bool _dirty;

    public HistoryStore(string path)
    {
        _path = path;
        Load();

        // 正常退出由 Dispose 落盘；被系统关闭（注销、关机、任务管理器结束）时
        // 走不到 Dispose，这里补一次，避免丢掉最后一分钟的采样。
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (_dirty) Save();
        };
    }

    internal sealed class Sample
    {
        [JsonPropertyName("t")] public long Ticks { get; set; }
        [JsonPropertyName("p")] public int Percent { get; set; }

        /// <summary>充电中。旧文件没有这个字段，默认 false。</summary>
        [JsonPropertyName("c")] public bool Charging { get; set; }

        [JsonIgnore] public DateTime Time => new(Ticks);
    }

    internal sealed class DeviceHistory
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("points")] public List<Sample> Points { get; set; } = new();
    }

    private sealed class Payload
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("devices")] public List<DeviceHistory> Devices { get; set; } = new();
    }

    /// <summary>有历史数据的设备，按最近一次记录排序。</summary>
    public IReadOnlyList<(string Id, string Name)> Devices =>
        _devices.Values
            .Where(d => d.Points.Count > 0)
            .OrderByDescending(d => d.Points[^1].Time)
            .Select(d => (d.Id, d.Name))
            .ToList();

    /// <summary>记录一轮读数。</summary>
    public void Record(IReadOnlyList<BatteryReading> readings)
    {
        DateTime now = DateTime.Now;
        bool changed = false;

        foreach (BatteryReading reading in readings)
        {
            if (reading.Percent is not int percent || percent is <= 0 or > 100) continue;
            if (string.IsNullOrEmpty(reading.Address)) continue;

            // 采样时间用读数自己的时间戳，而不是"现在"。
            // 设备休眠时程序会拿上次缓存兜底，那条缓存的 Stamp 是旧的；
            // 若按 now 记录，同一个旧值会被写成一条条"新采样"，画出来是一条假平线。
            DateTime stamp = reading.Stamp ?? now;
            if (stamp > now.AddMinutes(1)) stamp = now; // 时钟异常（未来时间）拉回现在

            // 超过 1 小时没上报的读数不再入账。
            // 系统属性只在电量变化时更新时间戳，所以不能要求它每分钟都有新值；
            // 但也不能把一天前、甚至十几天前的缓存当成"当前电量"画进曲线。
            if (now - stamp > TimeSpan.FromMinutes(60)) continue;

            if (!_devices.TryGetValue(reading.Address, out DeviceHistory? device))
            {
                device = new DeviceHistory { Id = reading.Address };
                _devices[reading.Address] = device;
            }

            if (!string.IsNullOrEmpty(reading.DeviceName)) device.Name = reading.DeviceName;

            // 时间戳没有前进 = 这条读数已经记过（缓存兜底），跳过
            if (device.Points.Count > 0)
            {
                Sample last = device.Points[^1];
                if (stamp <= last.Time) continue;

                // 数值和充电状态都没变、且距上一点不到 60 秒，不必再记一条。
                // 2.4G 每次查询都自带"现在"的时间戳，不设这道闸，
                // 30 秒轮询会写进 2880 点/天，很快把上限撑满、逼着压缩去抽稀最近的曲线。
                bool sameState = last.Percent == percent && last.Charging == reading.Charging;
                if (sameState && (stamp - last.Time).TotalSeconds < MinSampleIntervalSeconds) continue;
            }

            device.Points.Add(new Sample
            {
                Ticks = stamp.Ticks,
                Percent = percent,
                Charging = reading.Charging,
            });
            changed = true;
        }

        if (!changed) return;

        Prune(now);
        _dirty = true;

        if (now - _lastSave >= SaveInterval) Save();
    }

    /// <summary>取指定设备在时间窗内的采样点。</summary>
    public List<HistoryPoint> Query(string deviceId, TimeSpan window)
    {
        var result = new List<HistoryPoint>();
        if (!_devices.TryGetValue(deviceId, out DeviceHistory? device)) return result;

        DateTime from = DateTime.Now - window;
        foreach (Sample point in device.Points)
        {
            if (point.Time >= from) result.Add(new HistoryPoint(point.Time, point.Percent, point.Charging));
        }
        return result;
    }

    private void Prune(DateTime now)
    {
        foreach (DeviceHistory device in _devices.Values)
        {
            List<Sample> points = device.Points;

            DateTime cutoff = now - Retention;
            int drop = 0;
            while (drop < points.Count && points[drop].Time < cutoff) drop++;
            if (drop > 0) points.RemoveRange(0, drop);

            // 超量时压缩：隔点抽样，但最近 24 小时保持原始分辨率。
            // 24 小时视图是主要查看范围，把它一起抽稀会让最近的曲线变糊、掉电拐点变圆。
            int keepFrom = points.FindIndex(p => p.Time >= now - RecentWindow);
            if (keepFrom < 0) keepFrom = points.Count;

            while (points.Count > MaxPointsPerDevice && keepFrom > 1)
            {
                var compacted = new List<Sample>(points.Count / 2 + 1);
                for (int i = 0; i < keepFrom; i += 2) compacted.Add(points[i]);

                // 补上老区间的末点，避免与新区间之间出现断口
                if (!ReferenceEquals(compacted[^1], points[keepFrom - 1])) compacted.Add(points[keepFrom - 1]);

                compacted.AddRange(points.Skip(keepFrom));

                keepFrom = compacted.Count - (points.Count - keepFrom);
                points.Clear();
                points.AddRange(compacted);
            }
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            var payload = new Payload { Devices = _devices.Values.Where(d => d.Points.Count > 0).ToList() };
            string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });

            string temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);

            _lastSave = DateTime.Now;
            _dirty = false;
        }
        catch
        {
            // 历史记录失败不该影响主功能
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;

            Payload? payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(_path));
            if (payload?.Devices == null) return;

            foreach (DeviceHistory device in payload.Devices)
            {
                if (string.IsNullOrEmpty(device.Id) || device.Points.Count == 0) continue;
                _devices[device.Id] = device;
            }
        }
        catch
        {
            // 文件损坏就当作没有历史，不影响启动
        }
    }

    public void Dispose()
    {
        if (_dirty) Save();
    }
}
