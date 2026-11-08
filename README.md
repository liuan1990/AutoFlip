# AutoFlip（短视频自动滑屏）

AutoFlip 是一个 Android 辅助工具：在用户明确开启 Android 无障碍服务并启动识别后，它会定时识别当前屏幕选定区域中的文字；检测到用户配置的关键词时，自动执行一次滑动手势。主要用途是短视频场景下按提示文字自动翻到下一个视频，也可以用于其他需要按屏幕文字触发翻页的场景。

## 功能

- 自定义触发关键词，支持多个关键词
- 选择上、中、下三分之一区域，或通过浮动控件长按/控制页自定义框选 OCR 识别区域
- 设置识别间隔、滑动冷却时间和滑动方向
- 浮动状态控件用于暂停/恢复识别，并显示滑动计数
- OCR 在设备端通过 Google ML Kit 中文文本识别组件执行

## 使用与权限

本项目当前仅提供 Android 目标框架（Android 11 / API 30 及以上）。首次使用时，请在应用中开启识别，然后按系统提示手动启用 AutoFlip 无障碍服务。用户可随时在系统设置中关闭该服务。

无障碍服务需要屏幕截图和执行手势的能力。启用服务期间，应用按设置频率截取屏幕，并只将所选屏幕区域交给 OCR 识别；识别结果用于关键词匹配，项目代码没有实现向网络上传截图或识别文本的功能。请仅在自己有权操作的设备和内容上使用，并先确认目标应用的服务条款及当地法律允许此类自动化操作。

Android 系统可能会对无障碍服务、通知、后台运行和屏幕截图施加额外限制；不同设备的系统版本和厂商实现可能影响功能。

## 构建

环境需要 .NET 10 SDK、.NET MAUI 工作负载、Android SDK，以及可用于构建 Android 应用的 Java 环境。

```powershell
dotnet workload install maui-android
dotnet restore .\AutoFlip\AutoFlip.csproj
dotnet build .\AutoFlip\AutoFlip.csproj -f net10.0-android
```

构建产物位于 `AutoFlip/bin/`。本仓库不包含签名密钥；发布签名包时请使用你自己的密钥并妥善保管。

## 下载

前往 [GitHub Releases](https://github.com/liuan1990/AutoFlip/releases/latest) 下载最新 APK。

## 最近更新

### v1.0.2

- 新增自定义 OCR 识别区域，可拖动分界线调整识别范围，提高目标文字识别准确率。
- 支持从控制页或长按浮动控件进入区域框选，并可保存或取消。

### v1.0.1

- 增加截图请求超时恢复，避免单次失败导致识别停止。
- 增加手势完成/取消回调与失败重试状态。
- OCR 匹配时忽略空白字符，提升关键词跨空格识别时的命中率。
- 在控制页显示当前识别与滑动处理状态。

## 第三方组件与资源

- [.NET MAUI](https://github.com/dotnet/maui)：应用 UI 与 Android 应用框架，MIT License。
- [Xamarin.Google.MLKit.TextRecognition](https://www.nuget.org/packages/Xamarin.Google.MLKit.TextRecognition) 与 [Xamarin.Google.MLKit.TextRecognition.Chinese](https://www.nuget.org/packages/Xamarin.Google.MLKit.TextRecognition.Chinese)：Google ML Kit 文本识别的 .NET Android 绑定；版本见 `AutoFlip/AutoFlip.csproj`。请遵守各 NuGet 包及其依赖项附带的许可证和通知。
- [Open Sans](https://fonts.google.com/specimen/Open+Sans)：项目内的 Regular 与 Semibold 字体文件，依据 SIL Open Font License 1.1 发布；许可证文本见 [OFL.txt](AutoFlip/Resources/Fonts/OFL.txt)。
- Android 无障碍服务、屏幕截图及手势 API：Android 平台 API；具体实现见 `AutoFlip/Platforms/Android/KeywordFlipService.cs`。

依赖及其传递依赖的完整许可证信息以各自发行包中的许可证和通知为准。分发二进制包时，应一并保留适用的第三方许可证和通知。

## 许可证

除明确标注的第三方组件和资源外，本项目代码以 MIT License 授权，详见 [LICENSE](LICENSE)。

## 贡献者

- CodeX

## 免责声明

本软件按“现状”提供，不作任何明示或暗示保证。使用者应自行评估隐私、安全、设备兼容性和目标平台规则，并对使用行为及其后果负责。作者不对因使用本软件导致的账号限制、内容误操作、设备问题或其他损失承担责任。
