<div align="center">

# Cheems遥控器

[![下载](https://img.shields.io/badge/%E4%B8%8B%E8%BD%BD_Cheems%E9%81%A5%E6%8E%A7%E5%99%A8-v0.0.1-16856B)](https://miremote.cheems.cn)
[![QQ群](https://img.shields.io/badge/QQ%E7%BE%A4-1094431427-12B7F5)](https://qm.qq.com/q/gmOiP7PLnW)

![首页](./.github/assets/home.png)

</div>

## 简介

Cheems遥控器（MiRemoteControl）是一个 Windows 桌面应用：把小米蓝牙遥控器变成 AI 编程工具的控制器，按住语音键说话，看着 TV 大屏提交。

- 🎮 小米蓝牙遥控器实时控制（方向、确认、电源、音量、TV 大屏）
- 🎙 中文语音输入（Qwen3-ASR / Whisper / SenseVoice，应用内下载模型）
- 🧩 插件市场——被控端插件（ZCode / ChatGPT / ReasoniX…）与应用本身解耦，云端下载更新
- ⌨️ `mrc` 命令行，全部命令支持 `--json`，适合脚本与 AI 自动化

## 下载

**[⬇ 在线下载页](https://miremote.cheems.cn)** —— 点开即下，始终是最新版

| 渠道 | 地址 |
| --- | --- |
| Gitee Release | https://gitee.com/unbengable/mi-remote-control/releases |
| GitHub Release | https://github.com/firebugbugs/MiRemoteControl/releases |

Windows 10/11 x64，自包含安装包，无需安装 .NET 运行时。

## 构建和运行

Windows 11 + .NET 10 SDK：

```powershell
.\build.ps1 -c Release
Set-Location .\artifacts\app
.\mrc.exe start        # 启动桌面端（托盘常驻）
.\mrc.exe plugins      # 查看已加载插件
```

常用命令速查：

```powershell
.\mrc.exe remote press down                    # 模拟遥控器按键
.\mrc.exe remote select mrc.zcode              # 切换工作插件
.\mrc.exe voice models                         # 列出本地语音模型
.\mrc.exe invoke mrc.zcode input --text "你好"  # 向工作插件输入
.\mrc.exe invoke mrc.zcode send                # 发送
.\mrc.exe invoke mrc.zcode stop                # 停止任务
```

退出软件请用托盘「退出程序」；重新构建前先退出桌面端。

## 工程结构

| 项目 | 职责 |
| --- | --- |
| `src/Contracts` | 插件契约（apiVersion 2）、命令 DTO、消息分帧 |
| `src/Core` | 插件发现与 AssemblyLoadContext 加载 |
| `src/Host` | 桌面端托管的 IPC 宿主（命名管道） |
| `src/Desktop` | Avalonia 桌面端（遥控器 UI、插件市场、语音、TV 大屏） |
| `src/Cli` | `mrc.exe` 命令行 |
| `plugins/*` | 单文件 ZIP 插件（`.mrcplugin`）：`remote` 走蓝牙/语音，`target` 控制被控软件 |

新插件实现 `IHarnessPlugin`（target）或 `IRemotePlugin` + `IHostedRemotePlugin`（remote）即可扩展；插件包放 `plugins/targets|remotes/` 目录或从应用内市场安装。

## 反馈

- Issue：[Gitee](https://gitee.com/unbengable/mi-remote-control/issues) / [GitHub](https://github.com/firebugbugs/MiRemoteControl/issues)
