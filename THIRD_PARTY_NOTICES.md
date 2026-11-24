# 第三方组件与许可证

| 组件 | 用在哪里 | 许可证 | 源码 |
|---|---|---|---|
| Moonlight Android / Artemis | `android/`（本项目在其基础上修改，修改说明见 `android/FREEREMOTE.md`） | GPL-3.0 | https://github.com/ClassicOldSong/moonlight-android |
| moonlight-common-c | `android/app/src/main/jni/moonlight-core/moonlight-common-c`（新增 `FarloopRate.c` 码率自适应控制器） | GPL-3.0 | https://github.com/moonlight-stream/moonlight-common-c |
| Sunshine | 被控端串流；Windows 端内置打过 `patches/sunshine` 补丁的 `sunshine.exe`（基于 v2026.914.233613） | GPL-3.0 | https://github.com/LizardByte/Sunshine |
| EasyTier | 组网；`desktop/EasyTier/` 和 `android/app/src/main/jniLibs/` 里的二进制由 `patches/easytier` 编译（基于提交 8428a89） | LGPL-3.0 | https://github.com/EasyTier/EasyTier |
| Virtual Display Driver (MttVDD) | `desktop/VDD/`，“关闭对方屏幕”用的虚拟显示器 | MIT | https://github.com/VirtualDrivers/Virtual-Display-Driver |
| airhand | 参考了它的码率控制算法（`flow.rs`）和虚拟显示器的启用 / 停用、拓扑处理（`device.rs`、`topology.rs`） | MIT, Copyright (c) 2026 sqlpxc | https://github.com/sqlpxc/airhand |

Sunshine、EasyTier 是作为独立程序随 Windows 端一起分发的；它们的完整源码可从上面的地址获取，本项目的修改全部在 `patches/` 里。
