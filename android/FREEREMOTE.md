# FreeRemote 安卓端

基于 [Artemis](https://github.com/ClassicOldSong/moonlight-android)（Moonlight 安卓版分支，GPL-3.0）改造，
加入 FreeRemote 的组网和自动配对，用手机控制装了 FreeRemote 的电脑。

## 新增 / 改动

| 文件 | 作用 |
|---|---|
| `app/src/main/java/com/freeremote/HomeActivity.java` | 首页：输入邀请码入网、设备列表、一键连接（自动添加电脑 → 自动配对 → 打开桌面） |
| `app/src/main/java/com/freeremote/FrNet.java` | 在手机上运行 EasyTier（官方安卓 JNI 库），读取组网设备 |
| `app/src/main/java/com/freeremote/FrVpnService.java` | 虚拟网卡，只路由 10.144.144.0/24，不影响手机其他上网 |
| `app/src/main/java/com/freeremote/FrSession.java` | 与电脑端 FreeRemote 的加密会话（协议同电脑端 Core/Protocol.cs） |
| `app/src/main/java/com/freeremote/FrHub.java` | 会话管理：自动配对交 PIN、画面光标、关闭对方屏幕 |
| `app/src/main/java/com/freeremote/FrStreamHooks.java` | 串流画面钩子：视频包 1024 字节、开始/结束时的处理 |
| `app/src/main/java/com/freeremote/FrConfig.java` | 配置：邀请码格式、中转节点、主机名编码（手机用 `frm_` 前缀） |
| `app/src/main/java/com/easytier/jni/EasyTierJNI.java` | EasyTier 原生库接口 |
| `app/src/main/jniLibs/<abi>/libeasytier_android_jni.so` | EasyTier 原生库（由 EasyTier 源码 `easytier-contrib/easytier-android-jni` 编译） |
| `Game.java`（3 处）、`GameMenu.java`（1 处） | 接入上面的钩子；菜单里加“关闭被控端屏幕” |
| `app/build.gradle` | 包名 `com.freeremote.android`、只出 arm64-v8a / x86_64、minSdk 24 |
| `AndroidManifest.xml` | 首页改为 HomeActivity，声明 VPN 服务 |

## 编译

需要 JDK 17、Android SDK（platform 36、build-tools 36）、NDK 27.0.12077973。

```powershell
# Windows 上如果 Gradle 报 “Unable to establish loopback connection”，指定一个短路径的临时目录
$env:JAVA_TOOL_OPTIONS='-Djdk.net.unixdomain.tmpdir=C:\fr\tmp'
.\gradlew.bat assembleNonRoot_gameDebug
```

APK 在 `app/build/outputs/apk/nonRoot_game/debug/`：手机装 `arm64-v8a` 那个。

EasyTier 原生库（需要 Rust 1.95、cargo-ndk、Android NDK；Windows 上需要 VS Build Tools 的 C++ 工具）：

```powershell
$env:ANDROID_NDK_HOME='C:\Android\Sdk\ndk\27.0.12077973'
cd EasyTier\easytier-contrib\easytier-android-jni
cargo ndk -t arm64-v8a -t x86_64 -P 24 -o <本工程>\app\src\main\jniLibs build --release
```

## 许可

本工程基于 GPL-3.0 的 Moonlight / Artemis，修改后的源码同样遵循 GPL-3.0；自己使用不受影响，若分发 APK 需同时提供源码。
