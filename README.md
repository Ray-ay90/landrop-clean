# 局域投送

离线使用的 Windows 与 Android 局域网互传软件，支持设备配对、文字和文件传输、本地记录、训练任务以及手动双向同步。Windows 还提供屏幕准心和边缘叠加。

当前版本：Windows **V1.0.49**；Android **V1.0.60**（versionCode 68）。

- [Windows 程序](Windows/release/LanDrop.exe)
- [Android 安装包](Android/release/LanDrop-Android-debug.apk)
- [安装与构建说明](docs/INSTALL.md)

本快照包含应用源码、开发测试工具和发布程序。发布目录没有附带笔记、任务、配对配置、附件、个人设置、日志或签名私钥。首次在空白环境运行时，应用使用默认设置，记录和任务列表为空，需要重新配对设备。覆盖安装或复用原有数据目录会保留本机已有内容。

目录按平台整理：`Windows/src`、`Android/src` 为正式源码，`Windows/release`、`Android/release` 为发布程序，`Windows/tools` 为隔离回归与图标工具，`docs` 为通用说明。
