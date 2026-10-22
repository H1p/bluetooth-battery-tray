using System.Media;
using System.Text;

namespace MouseBattery;

internal static class Program
{
    private const string MutexName = @"Local\MouseBatteryTray_SingleInstance_9F2A";

    [STAThread]
    private static int Main(string[] args)
    {
        // DPI 必须在创建任何窗口之前设置，否则截屏与窗口定位都会被系统虚拟化
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        }
        catch
        {
            // 老系统忽略
        }

        // 配色阈值对托盘图标和提醒卡片是全局设置，诊断模式下也要生效
        AppSettings bootSettings = AppSettings.Load();
        BatteryIcon.Configure(bootSettings.ColorRedBelow, bootSettings.ColorGreenFrom);

        // 诊断模式：不弹界面，把读到的数据打到控制台和 dump.txt
        if (args.Any(a => a.Equals("--dump", StringComparison.OrdinalIgnoreCase)))
        {
            return DumpAsync(args).GetAwaiter().GetResult();
        }

        // 诊断模式：把各电量档位的图标放大导出，用来肉眼检查绘制效果
        if (args.Any(a => a.Equals("--iconsheet", StringComparison.OrdinalIgnoreCase)))
        {
            return IconSheet();
        }

        // 诊断模式：单独验证托盘气泡通知能否弹出
        if (args.Any(a => a.Equals("--test-notify", StringComparison.OrdinalIgnoreCase)))
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new NotifyProbeContext());
            return 0;
        }

        // 诊断模式：截取屏幕右下角，用于检查托盘与通知的实际显示
        if (args.Any(a => a.Equals("--shot", StringComparison.OrdinalIgnoreCase)))
        {
            return ScreenShot();
        }

        // 诊断模式：打开设置窗口并自动截图，检查高 DPI 下的排版
        string? settingsShotArg = args.FirstOrDefault(a =>
            a.StartsWith("--settings-shot=", StringComparison.OrdinalIgnoreCase));

        if (settingsShotArg != null || args.Any(a => a.Equals("--settings-shot", StringComparison.OrdinalIgnoreCase)))
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 可通过 --settings-shot=35 指定电量，用来核对不同档位的取色
            int previewPercent = 91;
            if (settingsShotArg != null
                && int.TryParse(settingsShotArg[(settingsShotArg.IndexOf('=') + 1)..], out int parsed))
            {
                previewPercent = Math.Clamp(parsed, 0, 100);
            }

            var settings = AppSettings.Load();
            var reading = new BatteryReading
            {
                Percent = previewPercent,
                DeviceName = "示例蓝牙鼠标",
                Address = "AA:BB:CC:DD:EE:FF",
                Source = "GATT 实时订阅",
                Connected = true,
                Subscribed = true,
                Stamp = DateTime.Now.AddMinutes(-2),
                Kind = DeviceKind.Mouse,
            };

            var form = new SettingsForm(settings, reading, null);
            var timer = new System.Windows.Forms.Timer { Interval = 1400 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                CaptureForm(form, "settings-shot.png");
                form.Close();
            };
            form.Shown += (_, _) => timer.Start();

            Application.Run(form);
            return 0;
        }

        using var mutex = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show(
                "蓝牙鼠标电量已经在运行了，请查看任务栏右下角的托盘图标。",
                "蓝牙鼠标电量",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += (_, e) => WriteCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash(e.ExceptionObject as Exception);

        try
        {
            Application.Run(new TrayContext());
        }
        catch (Exception ex)
        {
            WriteCrash(ex);
            return 1;
        }
        finally
        {
            mutex.ReleaseMutex();
        }

        return 0;
    }

    private static async Task<int> DumpAsync(string[] args)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== MouseBattery dump @ {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");

        var devices = DevProp.EnumBleDevices();
        sb.AppendLine($"在线 BLE 设备数: {devices.Count}");
        foreach (BleDevice d in devices)
        {
            sb.AppendLine($"  - {d.Name} | {d.DisplayAddress} | 类型={DeviceIcons.Describe(d.Kind)} | 电量={d.Battery?.ToString() ?? "无"} | 上报={d.Stamp?.ToString("HH:mm:ss") ?? "-"}");
            sb.AppendLine($"      {d.InstanceId}");
        }

        var settings = AppSettings.Load();
        bool noGatt = args.Any(a => a.Equals("--no-gatt", StringComparison.OrdinalIgnoreCase));
        if (noGatt) settings.UseGatt = false;

        using (var monitor = new BatteryMonitor(settings))
        {
            sb.AppendLine();
            BatteryReading reading = await monitor.PollAsync(true);
            sb.AppendLine("--- 合并结果 ---");
            sb.AppendLine($"  设备: {reading.DeviceName}");
            sb.AppendLine($"  地址: {reading.Address}");
            sb.AppendLine($"  类型: {DeviceIcons.Describe(reading.Kind)}");
            sb.AppendLine($"  电量: {(reading.Percent is int p ? p + "%" : "未知")}");
            sb.AppendLine($"  来源: {reading.Source}");
            sb.AppendLine($"  订阅: {reading.Subscribed}");
            sb.AppendLine($"  连接: {reading.Connected}");
            sb.AppendLine($"  采集: {reading.Stamp:yyyy-MM-dd HH:mm:ss}");
            if (reading.Error != null) sb.AppendLine($"  说明: {reading.Error}");
        }

        string text = sb.ToString();
        string outPath = Path.Combine(AppContext.BaseDirectory, "dump.txt");
        try { File.WriteAllText(outPath, text, Encoding.UTF8); } catch { }

        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "mousebattery_dump.txt"), text, Encoding.UTF8);
        }
        catch
        {
            // ignore
        }

        return 0;
    }

    private static int IconSheet()
    {
        var cases = new (int? Percent, bool Connected, string Label)[]
        {
            (100, true, "100%"),
            (62, true, "62%"),
            (35, true, "35%"),
            (18, true, "18%"),
            (null, false, "断开"),
        };

        const int cell = 152;
        const int pad = 12;
        int width = cases.Length * cell + pad * 2;
        int height = cell + 40 + 200;

        using var sheet = new Bitmap(width, height);
        using (Graphics g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.FromArgb(243, 243, 243));
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;

            using var labelFont = new Font("Segoe UI", 10f, FontStyle.Bold);
            using var smallFont = new Font("Segoe UI", 8f);
            using var labelBrush = new SolidBrush(Color.FromArgb(60, 60, 60));
            using var faintBrush = new SolidBrush(Color.FromArgb(140, 140, 140));

            for (int i = 0; i < cases.Length; i++)
            {
                (int? percent, bool connected, string label) = cases[i];
                int x = pad + i * cell;

                using Icon icon = BatteryIcon.Create(percent, connected);

                // 左：16×16 真实托盘尺寸，放大 5 倍，深色底模拟任务栏
                using (var tiny = new Bitmap(16, 16))
                {
                    using (Graphics tg = Graphics.FromImage(tiny))
                    {
                        tg.Clear(Color.FromArgb(28, 28, 28));
                        tg.DrawIcon(icon, new Rectangle(0, 0, 16, 16));
                    }
                    g.DrawImage(tiny, new Rectangle(x + 8, 16, 80, 80));
                }

                // 右：32×32 原始绘制，放大 1.4 倍看细节
                using (var raw = icon.ToBitmap())
                {
                    g.DrawImage(raw, new Rectangle(x + 96, 32, 45, 45));
                }

                g.DrawString("16px(×5)", smallFont, faintBrush, x + 8, 100);
                g.DrawString("32px", smallFont, faintBrush, x + 96, 100);
                g.DrawString(label, labelFont, labelBrush, x + 8, 114);
            }

            // 第二区：设备类型图标
            int devTop = cell + 50;
            var kinds = new (DeviceKind Kind, string Label)[]
            {
                (DeviceKind.Mouse, "鼠标"),
                (DeviceKind.Keyboard, "键盘"),
                (DeviceKind.Headset, "耳机"),
                (DeviceKind.Generic, "通用"),
            };

            using var ink = new SolidBrush(Color.FromArgb(40, 40, 40));
            for (int i = 0; i < kinds.Length; i++)
            {
                int x = pad + i * cell;

                using (Bitmap small = DeviceIcons.Render(kinds[i].Kind, 16, Color.FromArgb(40, 40, 40)))
                using (var tiny = new Bitmap(16, 16))
                {
                    using (Graphics tg = Graphics.FromImage(tiny))
                    {
                        tg.Clear(Color.FromArgb(246, 246, 246));
                        tg.DrawImage(small, 0, 0);
                    }
                    g.DrawImage(tiny, new Rectangle(x + 8, devTop, 80, 80));
                }

                using (Bitmap big = DeviceIcons.Render(kinds[i].Kind, 60, Color.FromArgb(40, 40, 40)))
                {
                    g.DrawImage(big, new Rectangle(x + 96, devTop + 10, 60, 60));
                }

                g.DrawString("16px(×5)", smallFont, faintBrush, x + 8, devTop + 84);
                g.DrawString(kinds[i].Label, labelFont, ink, x + 8, devTop + 100);
            }
        }

        string path = Path.Combine(AppContext.BaseDirectory, "icon-preview.png");
        sheet.Save(path, System.Drawing.Imaging.ImageFormat.Png);

        try
        {
            sheet.Save(Path.Combine(Path.GetTempPath(), "mousebattery_icons.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        catch
        {
            // ignore
        }

        return 0;
    }

    private static int ScreenShot()
    {
        try
        {
            Rectangle bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            const int w = 760;
            const int h = 440;
            int x = Math.Max(0, bounds.Width - w);
            int y = Math.Max(0, bounds.Height - h);

            using var bmp = new Bitmap(w, h);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(x, y, 0, 0, new Size(w, h));
            }

            bmp.Save(Path.Combine(AppContext.BaseDirectory, "shot.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        catch
        {
            return 1;
        }

        return 0;
    }

    private static void CaptureForm(Form form, string fileName)
    {
        try
        {
            using var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
            bmp.Save(Path.Combine(AppContext.BaseDirectory, fileName), System.Drawing.Imaging.ImageFormat.Png);
        }
        catch
        {
            // 诊断截图失败不影响功能
        }
    }

    private static void WriteCrash(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "MouseBattery.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  [CRASH] {ex}{Environment.NewLine}");
        }
        catch
        {
            // ignore
        }
    }
}

/// <summary>只用来验证气泡通知，弹出后自动退出。</summary>
internal sealed class NotifyProbeContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _timer;

    public NotifyProbeContext()
    {
        _icon = new NotifyIcon
        {
            Icon = BatteryIcon.Create(18, true),
            Text = "鼠标电量仅剩 18%",
            Visible = true,
        };

        PopupNotice.Show(
            "鼠标电量仅剩 18%",
            "示例蓝牙鼠标 电量偏低，建议尽快充电或更换电池。",
            18,
            true);

        _timer = new System.Windows.Forms.Timer { Interval = 8000 };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _icon.Visible = false;
            _icon.Dispose();
            ExitThread();
        };
        _timer.Start();
    }
}
