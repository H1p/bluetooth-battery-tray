# 蓝牙鼠标电量

任务栏托盘小工具，实时显示 **Xiaoxin Mouse 5.0** 的电量，低电量时在右下角弹出提醒卡片。

## 运行

双击 `MouseBattery.exe` 即可。启动后任务栏右下角会出现一个电池图标，不需要安装，不留后台服务。

- 想开机自动启动：右键托盘图标 → 勾选「开机自动启动」
- 退出：右键托盘图标 → 退出

## 怎么看电量

| 操作 | 效果 |
|---|---|
| 看托盘图标 | 电池填充比例就是电量，颜色按电量分档（默认绿 ≥50%、黄 20–49%、红 <20%，两条分界线都能改） |
| 鼠标停在图标上 | 浮出「设备名 · 电量 · 多久前采集」 |
| 右键菜单第一项 | 图标会区分设备类型：鼠标 / 键盘 / 耳机 / 通用蓝牙 |
| 右键图标 | 菜单第一行显示当前设备与百分比 |
| 双击图标 | 立即向鼠标请求一次最新电量 |

图标变灰并带一道斜杠，表示设备当前没连上。

设备类型图标（轮廓风格）出现在右键菜单、提醒卡片和设置窗口里，按设备名自动识别：含 mouse/鼠标 走鼠标图标，含 keyboard/键盘 走键盘图标，耳机类走耳机图标，其余用蓝牙符号。

这几处的图标颜色**全部跟随同一套档位色**，和托盘电池图标一致：托盘电池、右键菜单、提醒卡片、设置窗口，电量跨过红/绿分界线时四处一起变色。

## 设置

右键 → 设置：

- **低电量阈值**：默认 20%，低于它就弹提醒卡片
- **颜色分界**：默认「红色 低于 20%、绿色 ≥50%」，中间区间为黄色。两条线都能改，改完托盘图标立刻变色
- **弹窗/提示音**：可分别关掉
- **轮询间隔**：默认 30 秒兜底刷新一次，单位可选秒 / 分钟 / 小时，**最长 12 小时**（配 GATT 订阅时，间隔再长也不会漏掉推送）
- **启用 GATT 实时订阅**：默认开。这是「实时」的来源，关掉就只读系统缓存值

## 数据来源与刷新速度

程序走两条路，自动择优：

1. **BLE GATT 电池服务（0x180F / 0x2A19）** —— 直接向鼠标读取并订阅推送，鼠标一上报就立刻刷新。置灰日志里会记录 `[GATT] 推送更新 = xx%`。
2. **Windows 设备属性 `DEVPKEY_Bluetooth_Battery`** —— 系统「设置 → 蓝牙和设备」用的同一份缓存，GATT 不可用时兜底。

需要说明一点：**电量能多快更新，上限由鼠标固件决定**，软件无法突破。BLE 鼠标通常只在刚连上、电量变化时上报一次。所以这里的「实时」指的是：

> 鼠标一上报，程序在第一秒收到并刷新；点「立即刷新」可以主动催它应答一次。

如果数字看起来没变，不是没在工作，而是鼠标还没报告新值。

## 兼容性

- **系统**：Windows 10 2004（Build 19041）及以上，或 Windows 11；仅 64 位
- 已适配高 DPI 缩放（卡片和图标都按 DPI 等比绘制）
- 默认自动挑选在线的蓝牙鼠标；若系统里有多只，会在配置文件的 `DeviceAddress` 里锁定
- **不支持经典蓝牙（BT 3.0）鼠标**：这类设备不暴露电量属性，目前这台机器上的 `Xiaoxin Mouse 3.0` 就属于这种情况，系统本身也读不到

## 发给别人使用

### 只需要发一个文件

```
dist-portable\MouseBattery.exe        ← 便携版，约 69 MB
```

**只发这个 exe 就够，不要打包整个文件夹。** `settings.json` 里存着你本机的设备 MAC 和你的偏好设置，`MouseBattery.log` 里有运行记录，都不该带出去。对方首次运行时程序会自己生成属于他的配置。

### 为什么是 69 MB

因为这是**自包含**构建：.NET 运行时和应用本身被压进同一个 exe。对方电脑上什么都不用装，双击就能跑。体积换的是零依赖。

如果确定对方电脑上已经装了 .NET 8 桌面运行时（比如对方是开发机），可以改发 `dist\MouseBattery.exe`，只有 25 MB。

| 版本 | 路径 | 体积 | 对方需要什么 |
|---|---|---|---|
| 便携版（推荐发给别人） | `dist-portable\MouseBattery.exe` | 约 69 MB | 什么都不用装 |
| 精简版（自己用） | `dist\MouseBattery.exe` | 约 25 MB | 需装 .NET 8 Desktop Runtime |

### 对方怎么用

1. 把 `MouseBattery.exe` 拷到任意位置（桌面、D 盘、U 盘都行，不需要安装）
2. 双击运行，任务栏右下角出现电池图标就是启动成功了
3. 想让它开机自启：右键图标 → 勾「开机自动启动」
4. 想退出：右键图标 → 退出

不需要管理员权限，不写系统目录，不装服务。唯一的系统改动是勾选开机自启时往 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 写一个启动项。

### 前提条件

- **Windows 10 2004（Build 19041）及以上，或 Windows 11**，64 位
- 对方的蓝牙鼠标/键盘**已经在他的系统里完成配对并处于连接状态**

第二条要特别说明：这个工具只**读取**已配对设备的电量，不负责搜索和配对。对方得先在「设置 → 蓝牙和其他设备」里把鼠标连上，系统认了，工具才认得到。

### 对方可能会遇到的问题

**首次启动慢一两秒**
自包含单文件运行时要把内部文件解压到临时目录，属正常现象。之后启动会快一些。

**SmartScreen 拦了一下，提示「Windows 已保护你的电脑」**
因为 exe 没有代码签名，Windows 对来源不明的可执行文件默认会拦。点「更多信息」→「仍要运行」即可。要让这个提示彻底消失只能买代码签名证书，对内部小工具没必要。

**杀毒软件报毒**
单文件自解压类程序常见的误报（它运行时确实会往临时目录释放文件）。加信任即可，或者改发精简版，被误报的概率低一些。

**打开后说「未找到蓝牙鼠标」**
先确认鼠标在系统里是已连接状态。可以让对方在 exe 同目录打开命令行跑一下：

```
MouseBattery.exe --dump
```

看 `dump.txt` 里有没有列出设备。能列出设备但电量显示「无」，说明该设备不通过标准 BLE 电池服务上报——常见于老式经典蓝牙鼠标。

**放在 Program Files 下面**
该目录对普通用户不可写，配置会自动落到 `%LOCALAPPDATA%\MouseBattery\settings.json`，功能不受影响。

**对方是 ARM64 或 32 位系统**
当前只构建了 x64。ARM64 设备（如骁龙本）也能通过 x64 兼容层跑，但需要额外构建原生版本，改 `build.cmd` 里的 `-r win-x64` 为 `-r win-arm64` 即可。

### 自己重新构建

双击 `build.cmd`，会依次产出精简版和便携版。或者手动执行：

```bat
:: 精简版
dotnet publish -c Release -o dist

:: 便携版（零依赖单文件）
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:DebugType=none ^
  -o dist-portable
```

`IncludeNativeLibrariesForSelfExtract` 这个参数不能省。不加的话，WPF 那几个原生 DLL（`D3DCompiler_47_cor3.dll` 等）会散落在 exe 外面，发出去就变成一堆文件而不是单文件了。

## 文件位置

| 文件 | 说明 |
|---|---|
| `settings.json` | 配置，和 exe 同目录（目录不可写时落到 `%LOCALAPPDATA%\MouseBattery\`） |
| `MouseBattery.log` | 运行日志，排查问题时看这个 |

## 诊断命令

```
MouseBattery.exe --dump         # 打印设备枚举与合并结果到 dump.txt
MouseBattery.exe --test-notify  # 单独弹一次提醒卡片，检查效果
MouseBattery.exe --iconsheet    # 导出各电量档位图标放大图 icon-preview.png
```

## 实现说明

用 C# / .NET 8 WinForms 编写。提供两个构建产物：便携版零依赖、单文件，精简版依赖机器上的 .NET 8 Desktop Runtime，详见上面的「发给别人使用」。

提醒没用系统的 `NotifyIcon.ShowBalloonTip`：Windows 11 要求程序向通知中心注册 AppUserModelID，未注册的裸 exe 调用会被静默丢弃（实测通知数据库无任何写入）。因此改为自绘的右下角卡片，任何环境下都保证可见，也能顺带把电池图形和告警配色画出来。

## 源码与二次开发

### 项目结构

| 文件 | 职责 |
|---|---|
| `Program.cs` | 入口、单实例互斥、DPI 初始化；`--dump` 等诊断模式的实现 |
| `TrayContext.cs` | 托盘图标、右键菜单、轮询调度、低电量判定、开机自启 |
| `BatteryMonitor.cs` | 采集层：GATT 连接与 Notify 订阅、系统属性兜底、两路数据合并 |
| `DevProp.cs` | SetupAPI P/Invoke，枚举 BLE 设备并读取 `DEVPKEY_Bluetooth_Battery` |
| `BatteryIcon.cs` | 托盘电池图标的矢量绘制；电量分档取色（`ColorFor`） |
| `DeviceIcons.cs` | 设备类型图标（鼠标/键盘/耳机/通用）绘制与类型识别 |
| `PopupNotice.cs` | 右下角低电量提醒卡片（自绘，替代系统气泡） |
| `SettingsForm.cs` | 设置窗口，所有布局按 96 DPI 基准手写缩放 |
| `AppSettings.cs` | `settings.json` 读写、路径回退、参数归一化 |
| `build.cmd` | 一键产出精简版与便携版 |

依赖只有 .NET SDK 自带的 WinForms 与 WinRT（`net8.0-windows10.0.19041.0` 目标框架直接提供），**没有任何 NuGet 第三方包**。

### 换一台电脑继续开发

**第一步：装 .NET 8 SDK**

注意是 **SDK**，不是 Runtime。

```
https://dotnet.microsoft.com/download/dotnet/8.0
```

装完在命令行确认：

```
dotnet --list-sdks
```

应该能看到 `8.0.x`。只要有 SDK，Runtime 会被一并安装。

**第二步：把源码放过去，任选一种**

| 方式 | 做法 |
|---|---|
| 源码压缩包 | 解压 `MouseBattery-src-v1.2.1.zip`，得到上表那 15 个文件 |
| Git 仓库（推荐） | 见下一节，`git clone <你的仓库地址>` |
| U 盘直接拷 | 拷整个 `MouseBattery` 文件夹，但**必须删掉 `bin`、`obj`、`dist`、`dist-portable`**，它们是本机编译产物，换机器没用还可能引起还原失败 |

**第三步：构建并验证**

双击 `build.cmd`，或者：

```
dotnet build -c Release
```

编完跑一下诊断命令确认能读到设备：

```
MouseBattery.exe --dump
```

它会在同目录生成 `dump.txt`，里面有设备名、类型、电量、数据来源。能读到设备就说明环境齐了。

**第四步：开工**

用 Visual Studio 2022 / VS Code / Rider 打开 `MouseBattery.csproj` 都能直接编辑调试。日常改完代码，`build.cmd` 出两个版本。

### 上传到 GitHub

仓库已经在本地初始化好了（`git init`，分支 `main`，首次提交完成），只差一个远端地址。

**方式 A：网页建仓 + 命令行推送**

1. 打开 https://github.com/new
2. 填仓库名（如 `bluetooth-battery-tray`），选 **Private**（如果不想公开），**不要**勾选 "Add a README" 之类的初始化项
3. 创建后复制它给出的仓库地址，然后在 `MouseBattery` 目录执行：

```bash
git remote add origin https://github.com/<你的用户名>/<仓库名>.git
git push -u origin main
```

推送时会要求认证。GitHub 已不支持账号密码，需要用 **Personal Access Token**（Settings → Developer settings → Personal access tokens → 勾 `repo` 权限）当密码；或者装 GitHub CLI 后 `gh auth login` 走浏览器授权。

**方式 B：用 GitHub CLI 一步到位**

装好 `gh` 并 `gh auth login` 之后：

```bash
gh repo create bluetooth-battery-tray --private --source=. --remote=origin --push
```

一条命令建仓、关联、推送全做完。

### 哪些东西不进仓库

`.gitignore` 已把这些排除，都是本机相关的：

- `bin/`、`obj/`、`dist/`、`dist-portable/` —— 编译产物
- `settings.json` —— 存着你自己鼠标的 MAC 和偏好设置
- `*.log` —— 运行日志
- `dump.txt`、`icon-preview.png`、`shot.png`、`settings-shot.png` —— 诊断命令的产物

`.gitattributes` 里固定了换行符：`.cmd`/`.bat` 强制 CRLF，否则批处理在别的机器上被 checkout 成 LF 可能执行异常。

克隆到新机器后，第一次运行会自动生成属于那台机器的 `settings.json`，不需要也不应该从旧机器带过来。
