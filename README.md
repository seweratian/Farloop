# Farloop

低延迟远程桌面：组网、打洞、串流、文件、剪贴板一体，电脑和手机都能用，不需要自建服务器。

- **组网**：基于 [EasyTier](https://github.com/EasyTier/EasyTier)，经公共中转节点相遇，优先 P2P 直连（加了对称型 NAT 的端口喷射、双出口宽带的线路规则等打洞改进）
- **串流**：被控端用 [Sunshine](https://github.com/LizardByte/Sunshine)（NVENC / QSV / AMF 硬件编码），控制端用 Moonlight
- **码率自适应**：手机端按每一帧的网络排队时延实时调节码率（思路参考 [airhand](https://github.com/sqlpxc/airhand)），配合打过补丁、能在串流中途改码率的 Sunshine，网络变差时不卡顿、不断线
- **不断线切换**：串流中切换屏幕、切换画质都不用重连
- **远程协助**：陌生电脑凭“设备码 + 临时密码”就能连（像 UU 远程、向日葵），被协助方点“允许”才能控制，协助结束自动换密码
- **关闭对方屏幕**：远程时用虚拟显示器替换实体屏，显示器真正熄灭（省电、OLED 不烧屏），多屏时每块屏一一对应
- **其他**：文件拖拽 / 传输、双向剪贴板、远程开机、终端、设备详情、自动更新

## 下载

到 [Releases](https://github.com/seweratian/Farloop/releases) 下载：

| 文件 | 平台 |
|---|---|
| `Farloop.exe` | Windows 10 / 11（64 位），右键“以管理员身份运行”，首次运行按向导设置 |
| `Farloop-Android-*.apk` | Android 7 及以上（arm64） |

装好后两端都会自动检查更新（电脑：设置 → 组件更新；手机：我的 → 检查更新）。

## 目录

| 目录 | 内容 | 许可证 |
|---|---|---|
| `desktop/` | Windows 端（C# / WPF，.NET 8） | MIT |
| `android/` | 安卓端，基于 [Artemis](https://github.com/ClassicOldSong/moonlight-android)（Moonlight 安卓分支）改造 | **GPL-3.0**（继承自 Moonlight） |
| `patches/sunshine/` | Sunshine 补丁：串流中途改码率（控制消息 0x55F0，NVENC 直接重配置） | GPL-3.0（Sunshine 的许可证） |
| `patches/easytier/` | EasyTier 补丁：打洞加速、线路规则、STUN 改进等 | LGPL-3.0（EasyTier 的许可证） |
| `tools/ratetest/` | 码率自适应控制器的离线仿真测试 | MIT |

## 编译

**Windows 端**（需要 .NET 8 SDK）：

```powershell
dotnet publish desktop/FreeRemote.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o out
```

- `desktop/EasyTier/` 里是用 `patches/easytier` 编译出的 easytier-core / easytier-cli（Windows x64）
- 支持码率自适应的 Sunshine：按 Sunshine 官方文档在 MSYS2 UCRT64 里编译 `v2026.914.233613` 标签，先打上 `patches/sunshine` 的补丁，
  编译时设置 `BUILD_VERSION=v2026.914.233613`，把 strip 后的 `sunshine.exe` 放到 `desktop/SunshineFarloop/` 再编译 Windows 端（不放也能编译，只是没有码率自适应）
- `desktop/VDD/` 是 [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver)（MIT，SignPath 签名）

**安卓端**：见 [`android/FREEREMOTE.md`](android/FREEREMOTE.md)。需要 JDK 17、Android SDK 36、NDK 27；
`android/app/src/main/jniLibs/` 里的 EasyTier 原生库由 `patches/easytier` 编译。

## 贡献者

见 [CONTRIBUTORS.md](CONTRIBUTORS.md)。

## 许可证

本项目自己的代码以 [MIT](LICENSE) 发布；`android/` 目录是 Moonlight / Artemis 的衍生作品，按 [GPL-3.0](android/LICENSE.txt) 发布；
补丁和随附的第三方组件各自遵循原项目的许可证，详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
