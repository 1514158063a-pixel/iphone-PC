# iPhone-PC 工作台

Windows 上的 iPhone 屏幕镜像控制工具。使用 WPF 工作台控制 AirPlay 接收核心，支持在工作台内显示画面、独立视频窗口和全屏。

## 功能

- 请求 720p、1080p、1440p 或 4K 分辨率，以及 24、30 或 60 fps 的帧率上限。
- 设置接收名称和连接密码，保存后重启接收端。
- 在工作台、独立窗口和全屏之间切换；支持 50%、75% 和 100% 的画面缩放。
- 接收核心包含音频静音包、时钟同步及音频生命周期相关修复。

分辨率和帧率是向手机提出的请求，并非画面实测值；实际格式由手机和接收端协商。手机与电脑需要位于同一局域网。

## 使用

如有提供便携包，可在仓库的 Releases 页面下载并完整解压，运行其中的「启动 iPhone 投屏.cmd」。首次运行时，Bonjour 服务或 Windows 防火墙可能需要确认。然后在 iPhone 的控制中心打开「屏幕镜像」，选择工作台显示的接收名称。

默认接收名称为 `iPhone-PC`，默认连接密码为 `6817`，均可在工作台中修改。更改画质或连接设置后需要在手机上重新连接。

## 从源码构建工作台

需要 Windows x64 和 .NET Framework 4.x 编译环境。运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

生成的程序位于 `outputs/iPhone-PC/iPhone-PC.exe`。工作台还需要单独的 `uxplay-windows` 接收核心和运行库：程序优先使用其目录下的 `engine/uxplay-windows.exe`，否则尝试使用 `%LOCALAPPDATA%/Programs/uxplay-windows/uxplay-windows.exe`。

`package-portable.ps1` 可在已有完整接收核心及其运行库时制作便携包；可通过 `-ReceiverSource` 指定核心目录。核心源码、修改补丁和依赖说明见 [native/README.md](native/README.md)。运行库、本机配置、日志和生成的压缩包不随源码一起提交。

## 项目结构

- `src/`：C# 与 WPF 工作台源码。
- `build.ps1`：工作台编译脚本。
- `package-portable.ps1`、`packaging/`：便携包构建脚本和使用说明。
- `native/`：接收核心源码快照、本地修改补丁与回归测试源码。

## 来源与许可

界面参考 [open-claude-ui](https://github.com/Caho1/open-claude-ui) 的视觉组织，使用本地 WPF 实现。接收端基于 [uxplay-windows](https://github.com/leapbtw/uxplay-windows) 与 [UxPlay](https://github.com/FDH2/UxPlay)，相关源码和许可证保存在 `native/source/uxplay-windows/`。发行包中的第三方组件许可信息见其中的 `engine/LICENSE.rtf`。

自动回归不能替代真机测试：iPhone 的实际 AAC 音频、长暂停恢复和口型同步仍待验证。
