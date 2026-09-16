# MiRemoteControl · ZCode 控制打样

C# / .NET 10 的 Avalonia 生命周期宿主、轻量 IPC Host、命令行客户端，以及小米遥控器和 ZCode 两个单文件插件。

## 构建和运行

Windows 11，安装 .NET 10 SDK。当前打样为依赖框架的构建，运行机器需要 .NET 10 Desktop Runtime。打开 `MiRemoteControl.slnx`，或在本目录运行：

```powershell
.\build.ps1
Set-Location .\artifacts\app
.\mrc.exe start
.\mrc.exe plugins
.\mrc.exe remote buttons
.\mrc.exe remote select mrc.zcode
.\mrc.exe remote press power
.\mrc.exe remote press down
.\mrc.exe voice models
.\mrc.exe voice model select "sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25"
.\mrc.exe voice latest play
.\mrc.exe voice latest pause
.\mrc.exe zcode open
.\mrc.exe zcode input --text "请解释这段代码"
.\mrc.exe zcode send
.\mrc.exe zcode stop
.\mrc.exe zcode close
```

也可以直接运行 `artifacts\app\MiRemoteControl.exe`（Windows）或对应平台的 `MiRemoteControl`。`mrc start` 同样会先启动 Avalonia 桌面框架，再由桌面框架启动 Host 和当前遥控器插件；Host 不再允许脱离 Avalonia 单独启动。界面仍通过 IPC 显示遥控器、语音模型及插件状态。

在“学习语音键”中按一次遥控器语音键，程序保存来源设备及键码。之后按住该键开始本机中文识别，抬起时将识别文字写入 ZCode 当前输入框，回读成功后发送。默认优先选用名称含 `Hands-Free` 的蓝牙麦克风，但不会修改系统默认录音设备；可以在 Avalonia 桌面端选择其他输入设备。每次完成的语音会原子覆盖保存到 `%LocalAppData%\MiRemoteControl\recordings\latest-voice.wav`，只保留最近一条，不把音频内容写入日志；桌面端及 `mrc voice latest play/pause/status` 可以播放或暂停它。

关闭命令行窗口不影响后台。退出 Avalonia（托盘“退出程序”）会停止 Host 和遥控器插件；即使桌面进程异常结束，Host 也会通过父进程监视自动退出。标题栏关闭仍按原界面行为隐藏到托盘，不等于退出程序。停止后台也可使用 `mrc stop`；`mrc zcode stop` 只停止 ZCode 当前页面的运行任务。重新构建前应先退出桌面端，避免覆盖运行中的 DLL。

## 虚拟遥控器与自动化调试

`mrc remote press <button>` 通过后台的标准遥控器事件通道模拟一次按下与释放，Avalonia 桌面端会显示与真实遥控器相同的按键反馈。支持 `power/up/down/left/right/ok/back/home/menu/tv/volume-up/volume-down`，刻意不支持 `voice`，避免自动化误触录音和采集音频。使用 `mrc remote buttons --json` 可让自动化程序读取当前按键清单。

Windows HID 诊断可用 `mrc remote status --json` 查看 Raw Input 与 HID Tap 状态。HID Tap 的本地 IPC 管道为当前用户专用的 `MiRemoteControl.HidTap.<scope>.v2`，帧格式复用四字节长度 + UTF-8 JSON，字段为 `sequence/devicePath/reportHex`。Windows 驱动宿主层将原始报告送入该管道后，`mrc.remote.xiaomi` 插件使用 RC003 的标准 usage 映射（包括 `Back=0xF1`、`VolumeUp=0x80`、`VolumeDown=0x81`）生成按键事件；普通用户态程序不应直接打开系统 HID 集合。

`mrc remote select <plugin-id>` 设置当前工作插件；当前默认是 `mrc.zcode`，并会与 Avalonia 插件卡片的选择状态同步。`power` 是目前已绑定业务动作的按键：后台读取所选插件的 `status.running` 与 `status.focused`；目标未运行或虽已运行但不在系统前台时执行 `open` 并抢占焦点，只有目标已经是前台激活窗口时才执行 `close`。`home` 由 Avalonia 桌面端消费并采用同样的前台语义：主窗口不是系统前台窗口时显示、恢复并给予焦点，只有它已经位于前台时才隐藏。实体 TV 键由桌面进程使用系统热键、低级钩子和全局键态轮询三路冗余捕获并统一去重，虚拟 TV/Home/Power 则由独立后台事件循环消费；这些路径都不依赖 Mi Remote Studio 是否激活或可见。大屏也可由任意鼠标点击或全局 `Esc` 关闭；Home、Power 只把关闭大屏作为附加动作。其他按键当前产生完整的虚拟遥控器事件和界面反馈，后续按键业务映射可以继续复用同一通道。所有命令均支持 `--json`，成功为退出码 0，适合 AI 或脚本判定结果。

## 语音模型管理

语音模型目录为 `%LocalAppData%\MiRemoteControl\models`。当前支持三类本地模型：Whisper GGML 单文件、Qwen3-ASR 的 sherpa-onnx 模型目录、SenseVoice 的 sherpa-onnx 模型目录。桌面端会在约一秒内显示模型名称、引擎、格式和大小；有效模型可直接选择，无效或不完整模型会标记为“不兼容”。当前选择持久化到 `%LocalAppData%\MiRemoteControl\config\voice-model.json`。

```powershell
.\mrc.exe voice models --json
.\mrc.exe voice model status --json
.\mrc.exe voice model select "my-whisper-model.bin" --json
```

Qwen3-ASR ONNX 目录需要 `conv_frontend.onnx`、`encoder.int8.onnx`（或 `encoder.onnx`）、`decoder.int8.onnx`（或 `decoder.onnx`）和 `tokenizer` 子目录。SenseVoice ONNX 目录需要 `model.int8.onnx`（或 `model.onnx`）和 `tokens.txt`。切换时先加载新模型，成功后才释放旧模型；损坏或格式不支持的模型不会破坏当前可用模型。程序不内置也不自动下载默认模型，用户需要自行把模型放进上述目录。

`close` 使用 `WM_SYSCOMMAND / SC_CLOSE`，等价于正常标题栏关闭请求，不杀进程；ZCode 如有托盘驻留或保存确认，遵循其原有行为。`open` 自动查找默认安装路径、启动/恢复窗口并请求前台显示。后台进程默认没有前台激活权：激活被拒时按住一次合成 ALT 键跨越 `SetForegroundWindow` 调用（激活成功后才释放，避免裸 ALT 落到原前台应用的菜单栏），并以短时间重复枚举、重新激活来应对 Electron 启动/单实例移交期间销毁重建窗口句柄的情况。极端情况下仍被拒绝时返回 `FocusDenied`，请手动激活 ZCode 后重试。

## 输入和发送

```powershell
.\mrc.exe zcode input --text "追加内容"
.\mrc.exe zcode input --text "替换整个输入框" --replace
.\mrc.exe zcode input --file .\prompt.txt --replace
.\mrc.exe zcode send
```

输入默认追加到当前输入框末尾，只有 `--replace` 才覆盖。文本通过 Unicode 键盘输入，多行使用 Shift+Enter，输入后回读确认；输入不会自动发送。UTF-8 文件支持中文和 Emoji，上限 20000 字符；换行以外的控制字符不接受。

空输入框在 TV 大屏下接收第一段语音时，ZCode 插件会等待原生窗口与 Electron 编辑器焦点连续稳定后再投递；仅允许在确认文本尚未发送时恢复焦点重试，避免首轮语音丢失或重复。

发送调用当前页面的“发送”按钮，不使用无条件 Enter。成功结果的 `Dispatched` 表示已调用 UI 操作，不等于服务端已完成模型任务。超时或 `InputUnverified` 时先检查页面，不要直接重发。

操作作用于当前页面。一个可见窗口时自动选择；多个窗口时优先当前前台的目标窗口，否则要求 `--window`。不会自动切换到其他任务。

## 确认信息

```powershell
.\mrc.exe zcode confirm status
.\mrc.exe zcode confirm down
.\mrc.exe zcode confirm up
.\mrc.exe zcode confirm select
.\mrc.exe zcode confirm submit
```

- `up/down`：在实际确认卡片内发送上下键，依照 ZCode 自己的导航规则选择。
- `select`：对聚焦选项发送 Enter。权限卡片可能立即提交该选择；普通问答卡片按应用行为选择或进入下一题。
- `submit`：调用卡片内唯一的“确认/提交/继续”按钮。
- `status`：显示可选项、选中/焦点状态以及可提交按钮。

没有确认卡片时返回 `NoConfirmation`，不会向普通页面发送方向键或 Enter。确认卡片存在时阻止输入和发送，仍允许停止任务。不会后台自动批准权限，只有显式执行相应命令才操作卡片。

## 状态、目标与机器接口

```powershell
.\mrc.exe status --json
.\mrc.exe zcode status --json
.\mrc.exe zcode inspect --json
.\mrc.exe zcode open --exe "C:\Program Files\ZCode\ZCode.exe"
.\mrc.exe zcode status --window 123456
.\mrc.exe invoke mrc.zcode confirm.down --json
```

`--exe` 支持自定义安装位置，以真实进程路径校验窗口；`--window` 使用 status 返回的十进制句柄，也接受 `0x` 十六进制。`inspect` 是按需诊断，输出可能包含当前页面文字，不会自动存入日志或上传。

返回字段为 `success/code/message/data`。退出码：0 成功、2 参数错误、3 客户端错误、4 插件操作失败、6 超时/结果未知。

## 工程职责

| 项目 | 职责 |
| --- | --- |
| Contracts | 插件契约、命令 DTO 和消息分帧 |
| Core | 清单发现、AssemblyLoadContext 插件加载；不包含具体遥控器或语音依赖 |
| Host | Avalonia 所属的无窗口子进程，只负责命名管道、插件生命周期与调用编排 |
| Client | 可供 Avalonia/CLI/其他 .NET 前端直接调用的客户端 SDK |
| Cli | `mrc.exe` 命令入口 |
| Plugin.XiaomiRemote | 小米遥控器 Raw Input/HID Tap、ATVV、语音识别、模型和录音 |
| Plugin.ZCode | 安装发现、窗口操作、UIA 控件选择、标准输入探针、输入和确认交互 |
| Tests | 协议边界及插件失败隔离测试 |

Host/Core/Client 没有对任何具体遥控器或 ZCode 插件项目的编译引用。构建后，遥控器插件分发到 `artifacts/app/plugins/remotes/`，被控端插件分发到 `artifacts/app/plugins/targets/`。兼容用的 `core remote.*` 与 `core voice.*` 命令会代理到当前遥控器插件，所以现有 Avalonia 界面和 CLI 命令无需变化。

新增普通目标插件实现 `IHarnessPlugin`。包含主输入框的 target 插件还必须声明标准 `input.probe` 动作并返回 `InputProbeSnapshot`；TV 大屏只读取当前 target 的探针，不硬编码 ZCode，并以不可激活窗口显示，避免目标输入框丢失焦点。新增遥控器插件将 `PluginDescriptor.Kind` 设为 `remote`，并实现 `IHostedHarnessPlugin`；Host 会在选中时调用 `Start`，切换或退出时调用 `Stop`。异步硬件操作可再实现 `IAsyncHarnessPlugin`，通过 `IPluginHostContext` 调用当前目标插件。DLL、私有依赖、`.deps.json` 和根目录 `plugin.json` 打包为一个 ZIP 容器；`remote` 类型放入 `plugins/remotes/`，`target` 类型放入 `plugins/targets/`，放错目录会拒绝加载。推荐使用 `.mrcplugin` 后缀，但加载器按文件内容识别，不限制扩展名。可用 `mrc remote driver select <plugin-id>` 切换未来新增的遥控器插件，不需要修改界面。

## 打样协议与蓝图区别

本轮优先完成可运行控制闭环：Avalonia 管理 Host 生命周期，Host 管理遥控器插件生命周期，CLI 使用小型参数解析器，未引入完整 Generic Host / System.CommandLine / StreamJsonRpc。

当前 IPC v2 为用户及会话隔离的命名管道，`CurrentUserOnly`，每连接一个请求/响应。v2 增加插件类型、异步执行与托管生命周期契约。分帧仍是 **4 字节 little-endian 长度 + UTF-8 JSON**，最大 1 MiB。

请求示例：

```json
{"id":"unique-request-id","plugin":"mrc.zcode","action":"input","arguments":{"text":"你好"}}
```

响应示例：

```json
{"id":"unique-request-id","result":{"success":true,"code":"Ok","message":"内容已输入并回读核验，尚未发送。","data":{"verified":true}}}
```

核心请求使用 `plugin: "core"` 和 `status/plugins/stop`。Client 的 `StartAsync` 从 CLI 启动 Avalonia，桌面端再通过 `StartForDesktopAsync` 启动带父进程标识的 Host；`InvokeAsync` 可直接调用插件动作，前台操作前可调用 `AllowForegroundAsync` 将前台授权转交后台。

## 构建

```powershell
 .\build.ps1 -Configuration Release
```

构建会生成可运行目录 `artifacts/app`，包含 Desktop、Host、CLI 和分类插件包。

真实应用验证基线：ZCode `3.8.1.5310`，中文界面。已验证真实输入、发送和停止；确认交互的具体实测结果见下方验收记录。ZCode 更新后应重新执行 `status/inspect` 检查控件语义，不假定所有未来版本兼容。

### 本次验收记录（2026-09-08）

| 操作 | 真实 ZCode 验证结果 |
| --- | --- |
| 打开并前台显示 | 通过；正常关闭后再次打开恢复同一应用窗口 |
| 右上角关闭语义 | 通过；窗口消失，进程按应用自身行为驻留，再次 open 可恢复 |
| 输入当前页面 | 通过；中文/英文文本回读一致；替换模式也已验证 |
| 发送当前内容 | 通过；创建专用测试任务，当前页面出现提交的测试文字 |
| 停止当前任务 | 通过；停止后 `canStop=false`，输入框恢复后续提问状态 |
| 确认上下切换、选择 | 通过；真实问答工具卡片显示甲/乙/丙，down 聚焦乙、up 聚焦甲，select 后卡片消失 |
| 独立提交按钮 | 自建测试窗口通过；真实问答卡片本次使用 Enter 已提交，没有另行重复提交 |
| 命令权限批准卡片 | 控件结构已核实、自建同语义测试通过；未触发真实命令授权 |

真实测试只使用专用测试任务，要求模型不调用工具读写文件；第二轮仅要求显示问答工具卡片。ZCode 内保留该测试任务，未删除用户会话。

### 前台呼出修复验收（2026-09-10）

绕过 `AllowForegroundAsync` 直接走命名管道调用后台（等价于物理遥控按键路径，后台无前台授权），以另一窗口占据前台作为遮挡，逐项验证：

| 场景 | 结果 |
| --- | --- |
| 真实 ZCode 关闭驻留后 `open` 呼出 | 通过；窗口恢复并成为前台窗口 |
| 目标应用完全退出后 `open` 冷启动 | 通过；进程启动、窗口置前 |
| ZCode 已运行时 `open` 抢占前台 | 通过 |
| 主页键呼出 Mi Remote Studio（越过当时前台窗口） | 通过；前台变为桌面端窗口，且未被永久置顶 |
| 桌面端启动后第一次主页键 | 通过；立即切换窗口，不再被基线初始化吞掉 |

### TV 大屏退出与后台按键验收（2026-09-13）

| 场景 | 结果 |
| --- | --- |
| MiRemoteControl 主窗口隐藏时按 TV | 通过；大屏正常打开 |
| Rider 等其他应用位于前台时注入 TV 等价扫描码 | 通过；第一次打开、第二次关闭，大屏不抢走前台焦点 |
| ZCode 输入框位于前台时注入 TV 等价扫描码 | 通过；大屏正常切换，探针确认输入框原文不变 |
| RC003 实体 TV 键 | 必须由用户在真实蓝牙输入链路上复验；自动注入不能替代该项 |
| 全局按 `Esc` | 通过；无需激活大屏或主窗口即可关闭 |
| 鼠标点击大屏任意位置 | 通过；立即关闭 |
| 大屏打开时按 Home | 通过；关闭大屏，并照常切换 MiRemoteControl 主窗口显示/隐藏 |
| 大屏打开时按 Power | 实现为附加关闭大屏；目标插件原有开关逻辑仍由遥控器插件执行，不被替换 |

## 已知边界

- 使用 Windows UI Automation 和 SendInput，需要已登录且未锁定的交互桌面，建议与目标应用保持相同权限级别。
- 不依赖固定坐标、私有服务端协议、令牌或修改 ZCode 安装文件。
- UIA 控件快照使用缓存，真正调用时失效返回 `TargetChanged`，不会自动重复有副作用的操作。
- 进程内插件不是安全沙箱。UIA 提供者永久阻塞时，客户端超时，核心保留执行锁，避免并发注入；需要停止/重启核心恢复。正式版本可将 UIA 执行隔离到工作进程。
- 副作用操作没有跨进程崩溃后的 exactly-once 保证，也不会在超时后自动重试。
- 当前是框架依赖目录产物，不是安装包。Windows 遥控器、自启动和自包含发布仍需单独打包；Avalonia 桌面端已支持跨平台发布。

## 实现参考

- [Microsoft UI Automation Control Patterns](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/ui-automation-control-patterns-overview)
- [Microsoft SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)
- [Microsoft .NET plugin loading](https://learn.microsoft.com/en-us/dotnet/core/tutorials/creating-app-with-plugin-support)
- [Microsoft CreateProcessW：后台启动时禁止继承客户端句柄](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw)

ZCode 控件名依据本机 UIA 观察与本机已安装版本的界面资源核实；没有将其安装包代码复制进项目。
