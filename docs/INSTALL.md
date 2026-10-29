# 安装与构建

以下命令从仓库根目录开始，按各段说明切换工作目录。

## Windows

将 `Windows/release/LanDrop.exe` 与同目录的 `LanDrop.ico` 一起保存到可写文件夹，然后运行 EXE。程序会在自身目录下创建 `data` 文件夹保存本机设置、笔记、任务和配对信息。首次使用空目录运行时，记录和任务列表为空。

如已有 `data` 文件夹，程序会继续读取其中的本机内容。请保留自己的数据备份；本仓库没有附带该目录。

首次连接时，让 Windows 防火墙允许应用访问专用网络，并让手机与电脑连接同一个可信局域网。Android 首次扫描 Windows 显示的配对二维码；以后两端可自动重连。记录和任务由 Android 的 `V` 控制中心手动点击“同步全部”完成交换。

当前传输尚未启用 TLS 或内容加密，适用于可信局域网。

## Android

安装 `Android/release/LanDrop-Android-debug.apk`。首次全新安装时，应用私有存储为空；覆盖安装会保留该设备上已有的笔记、任务、设置和配对信息。请勿通过卸载现有正式应用来验证空白状态，以免丢失本机内容。

发布 APK 保留现有开发证书用于安装校验，包内包含公开证书；签名私钥未随仓库分发。

## 构建 Windows

安装 .NET 8 SDK，在仓库根目录执行：

```powershell
dotnet build Windows/src/LanDrop.Windows.csproj -c Release
```

需要自行生成 Windows x64 单文件程序时：

```powershell
dotnet publish Windows/src/LanDrop.Windows.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o Windows/publish
```

生成目录与现有发布文件分开。请不要把自己的 `data` 文件夹放入提交或分发包。

## Windows 隔离回归

先构建 Windows 主项目，再进入 `Windows` 文件夹执行。测试数据和截图生成在测试工具自身输出目录；不要将工具指向正式程序的数据目录。

```powershell
Set-Location Windows
dotnet run --project tools/shell-regression/ShellHarness.csproj -c Release
dotnet run --project tools/crosshair-regression/CrosshairHarness.csproj -c Release
dotnet run --project tools/edge-overlay-regression/EdgeOverlayHarness.csproj -c Release
dotnet run --project tools/fitness-regression/WinHarness.csproj -c Release
dotnet run --project tools/fitness-regression/WinHarness.csproj -c Release -- --silver
```

图标生成工具位于 `Windows/tools/icon-assets`，用于按原创几何形状生成应用图标，会更新源码内的图标资源。

## 构建 Android

准备 JDK 17 和 Android SDK，用 Android Studio 打开 `Android/src`，按本机 SDK 位置生成不纳入版本管理的 `local.properties`。首次构建需要联网下载 Gradle 和项目依赖。

从仓库根目录进入 Android 项目：

```powershell
Set-Location Android/src
.\gradlew.bat assembleDebug
```

产物位于 `Android/src/app/build/outputs/apk/debug/app-debug.apk`。正式构建不要传入 `fitnessRegression` 或 `silverRegression` 测试参数。

导出的源码使用 Android 标准本机调试签名，不包含原工作区私钥。自行构建的 APK 不能保证与现有发布 APK 同签名，因此不能保证覆盖安装；请使用独立测试设备或隔离测试配置验证，保留现有正式应用及其数据。
