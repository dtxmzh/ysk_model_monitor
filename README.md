# 本机模型监控悬浮窗（ysk_model_monitor）v1.0.2

Windows 7 SP1 / 10 / 11 桌面小工具：无边框透明悬浮窗，实时展示本机全部大模型运行时
（Strata / llama-server 系 / Ollama / senko-tts / whisper-asr / ComfyUI）
+ NVIDIA GPU 数据（nvidia-smi 融合）。纯只读观察者，绝不干扰被监控服务。

## 三个版本，按需取用

| 版本 | 文件 | 运行时要求 | 适用 |
|---|---|---|---|
| **轻量版（推荐）** | `ysk_model_monitor_v1.0.2_net48_portable.zip`（1.6MB） | .NET Framework 4.8（Win10 1903+/Win11 系统自带；Win7 SP1 需装一次 [4.8 离线包](https://dotnet.microsoft.com/download/dotnet-framework/net48)）+ WebView2 Runtime | 任何 Win7SP1/10/11 机器，解压即用 |
| 单文件版 A | `ysk_model_monitor.exe`（1.5MB） | .NET 10 桌面运行时 + WebView2 | 已装 .NET 10 的机器 |
| 自包含版 B | `publish\ysk_model_monitor.exe`（157MB） | 仅 WebView2 | 不想装任何 .NET 的 Win10/11 机器 |

> WebView2 Runtime：Win10/11 一般随系统/Edge 自带；没有的话装一次 [Evergreen Bootstrapper](https://developer.microsoft.com/microsoft-edge/webview2/)。

## 运行

解压后双击 `ysk_model_monitor.exe` 即可。绿色免安装：

- **配置 = exe 同目录 `ysk_model_monitor.ini`**：启动时自动查找（exe 目录→当前工作目录），
  找到且含 `[ysk_model_monitor]` 标识段 → 按之启动；找不到 → 默认设置。
  任何设置变更即时整份重写该文件；文件头有识别语句，删掉文件 = 恢复默认。
- 其余可写数据在 exe 同目录 `_data\`（services.json / monitor.log / webview2\）
- 不写注册表（唯一例外：主动勾选「开机自启」时写 HKCU\...\Run 一项，取消即删）
- 整个文件夹拷走即可搬家（ini 跟着走）

## v1.0.2 更新（内存泄漏自检 + 资源优化 + 最轻量运行时）

- **修复 GDI/内核句柄泄漏**：进程快照句柄未关（CreateToolhelp32Snapshot）、托盘图标位图未释放——长跑不再缓涨
- **WebView2 隐藏挂起**：贴边收起/收进托盘时挂起渲染进程（TrySuspend），省 CPU/显存/V8 内存；唤回自动恢复
- **隐藏时零渲染**：不可见期间不再投递快照、不再重建 DOM/SVG；展开立即补推最新帧
- **V8 堆封顶 96MB**：防前端长跑缓涨；序列化配置全局复用
- **新增 .NET Framework 4.8 轻量版**：产物仅 1.6MB（zip），Win10/11 系统自带运行时直接跑，Win7 SP1 兼容
- 同一套源码双目标编译（net10 + net48），差异由 `Compat.cs`/`Polyfill.cs` 条件编译吸收

## v1.0.1 通用化说明

- 内置服务清单改为通用三件套（Ollama / ComfyUI / llama-server:8080）；
  本机专属清单由 `_data\services.json` 首写后自行维护（老机器自动继承旧清单）
- nvidia-smi 自动探测 System32/Sysnative 路径；无 NVIDIA 显卡时 GPU 区显示不可用，不报错
- 旧版 `_data\settings.json` 自动一次性迁移为 ini；开机自启注册表值自动改名 ysk_model_monitor

## 图标

软件图标 = 缘狐小狐脸（橙黄毛+琥珀大眼+左鬓红小花+右下双尾探出，人家自己的样子嘛），
多尺寸（16~256px）打包内嵌在单文件 exe 里（`src\app.ico`，`ApplicationIcon` 嵌入）；
托盘图标同款小狐 + 右下角状态点（绿=正常 / 琥珀=数据陈旧 / 灰=暂停）。
重新生成图标：`py -3.12 src\make_icon.py`（改画法即改这个脚本）。

## 编译

```powershell
# 路线 A（net10 单文件，framework-dependent，约 1.5 MB）
dotnet publish src\ModelMonitor.csproj -c Release -r win-x64 -p:SelfContained=false `
  -p:PublishSingleFile=true -p:EnableWindowsTargeting=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -o publish_a

# 路线 B（net10 单文件，self-contained，约 157 MB，零依赖）
dotnet publish src\ModelMonitor.csproj -c Release -r win-x64 -p:SelfContained=true `
  -p:PublishSingleFile=true -p:EnableWindowsTargeting=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -o publish

# 轻量版（net48，产物在 src\bin48\Release\net48\，exe+依赖dll+wwwroot 打 zip 即可）
dotnet build src\ModelMonitor48.csproj -c Release
```

> 本机 NuGet 若被系统代理（sing-box 10808）卡住：`src\nuget.config` 已内置直连配置；
> 也可临时 `$env:NO_PROXY='*'` 或把包源换成国内镜像。

## 加/改监控服务

编辑 `_data\services.json`（托盘菜单「监控对象 → 打开 services.json」），
保存后托盘菜单「重载 services.json」立即生效，无需重启。

条目字段：`{name, kind, base_url, health_path, props_path, slots_path, metrics_path, pid_match, enabled, note}`
`kind` ∈ `strata | llama | ollama | tts | asr | comfyui | generic`（generic 只探活）。

## 设置清单（托盘右键 与 悬浮窗右键 完全一致）

| 分组 | 项 | 说明 |
|---|---|---|
| 显示 | 显示/隐藏主界面 · 置于顶层 · **鼠标穿透** · **贴边自动隐藏** · **屏幕边缘吸附** · 主题（浅/深） · 缩放 70–150% · 背景不透明度 35–100% | 即时生效 |
| 刷新 | **刷新间隔 1–60s（步进 1s，默认 2s）**：直选 1/2/5/10/15/30/60 + 自定义 ±1s 连点 | 改后下一拍即生效 |
| | 暂停刷新 · 窗口隐藏时（保持/降为10s/暂停，默认降为10s） | |
| 监控对象 | 各服务启用开关（带在线状态） · 自动发现新端口 · 打开/重载 services.json | |
| GPU | 显存归属表 · 服务↔GPU 拓扑图 · nvidia-smi 采集开关 | |
| 其他 | 开机自启 · 恢复默认 · 关于 · 退出 | |

## 快捷键

| 键 | 动作 |
|---|---|
| `Esc` | 隐藏到托盘 |
| `Ctrl+Q` | 退出 |
| `T` / `P` | 切主题 / 切置顶 |
| `+` / `-` | 刷新间隔 ±1s |
| `Space` | 暂停/恢复刷新 |
| `Ctrl+滚轮` | 界面缩放 0.7–1.5× |
| `Alt+滚轮` | 背景不透明度 0.35–1.0 |

## 窗口操作

- 右上角四个按钮：`—` 最小化到托盘 · 👻 鼠标穿透 · 📌侧 贴边自动隐藏 · 图钉 置顶开关
- **鼠标穿透**：点击穿过窗口直达桌面，窗口变半透明提示态；开启后只能从托盘菜单关闭（与贴边隐藏互斥，互斥时另一按钮变暗）
- **贴边自动隐藏**：窗口贴住屏幕边缘（优先级 左>右>上>下）自动收起为 **2px 触发线**；左右贴边时收起高度=工作区高度（自动扣除任务栏区域，不遮挡）；鼠标悬停触发线 0.5s 展开，离开窗口立刻收起；拖离边缘即解除贴边
- **屏幕边缘吸附**：拖动松手时距边缘 **64px** 内自动贴齐（QQ 式，阈值参考 Win 自带贴边吸附的宽松范围）；**任务栏也算吸附边**——拖进任务栏区域会吸回任务栏内沿，不遮挡任务栏
- 托盘图标**左键单击** = 显示/隐藏切换（无双击冲突）；右键 = 设置菜单（子菜单层叠弹出，不会过长）
- 拖动：按住顶部标题条或任意卡片头部行
- 调整大小：四条边 + 四角共 8 方向（含透明边缘），下限 360×420
- 位置/尺寸/主题/缩放/透明度/间隔/穿透/贴边/吸附/展开状态 全部记忆，重启恢复

## 数据口径

- GPU 主数据以 `nvidia-smi` 为准（每拍一次；进程归属表 ×3 拍）；Strata `hardware.*` 仅悬停参考
- 连续失败 3 次才判离线（与 Strata 官方 Monitor 页一致）；恢复自动回在线
- 各数据源节拍 = 基准间隔 × 倍率（metrics ×1、健康 ×2、进程表 ×3、props ×5、tags ×15、扫描 ×30），下限 1s 上限 120s
- 曲线环形缓冲 300 点；1s 档防堆积（上轮未回跳过本轮）；慢档陈旧阈值 = 间隔 × 2.5
- **prefill 口径**：引擎 `history.prefill_tok_s_mean` 恒 0、`live.prefill_tok_s_mean` 仅 reading 瞬间非空——当前值取最近请求 `prompt_tokens/prompt_ms×1000`（回退 live 值），曲线由前端按请求完成时刻自攒（idle 时 decode 显示 `—` 属正常，prefill 保留最近请求值）

## 已知边界（v1.0 本机版）

- 界面为简体中文；模型名/字段名保留英文
- 拓扑图默认折叠（设置里打开）
- 四档尺寸响应式已按 360×420 / 460×780 / 900×600 / 1600×1000 实测
