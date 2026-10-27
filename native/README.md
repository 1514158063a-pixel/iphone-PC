# 接收核心源码

`source/uxplay-windows/` 是构建本项目接收核心时使用的源码快照，其中包含本项目修改后的 `libuxplay` 源码及其许可证。原始 Git 元数据没有包含在快照中。

- uxplay-windows 基线：[`f76fe48400916449fd601b1c0444021aaf517082`](https://github.com/leapbtw/uxplay-windows/tree/f76fe48400916449fd601b1c0444021aaf517082)
- libuxplay 基线：[`79c620d0e3cd7c503418e1e39bf7b00a1cb16f24`](https://github.com/leapbtw/libuxplay/tree/79c620d0e3cd7c503418e1e39bf7b00a1cb16f24)
- `patches/combined-core.patch` 是相对于上述 libuxplay 基线的完整改动；其中包含上游静音包处理修复及本地时钟、音视频同步和音频生命周期处理。`retained-ec203121.patch` 单独记录其中的上游修复。
- `tests/` 包含接收核心时钟与音频生命周期的回归测试源码。

接收核心的构建依赖 Qt、GStreamer、OpenSSL、libplist、Bonjour SDK 等组件。本仓库不包含编译工具链；请先参考 `source/uxplay-windows/docs/BUILDING.md` 和上游构建说明配置依赖。工作台的 `build.ps1` 只编译 C# 程序，不会编译此接收核心。

原有自动回归曾通过，但 iPhone 实际 AAC 音频、长暂停恢复和口型同步仍需要真机验证。
