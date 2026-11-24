package com.easytier.jni;

/**
 * EasyTier 安卓原生库（libeasytier_android_jni.so）的 Java 接口。
 * 方法名和签名必须与 easytier-contrib/easytier-android-jni/src/lib.rs 里导出的 JNI 函数一一对应
 * （官方给的是 Kotlin 的 object + @JvmStatic，等价于这里的静态 native 方法）。
 */
public final class EasyTierJNI {
    static {
        System.loadLibrary("easytier_android_jni");
    }

    private EasyTierJNI() {}

    /** 把安卓 VpnService 建好的 TUN 文件描述符交给某个网络实例。0 = 成功 */
    public static native int setTunFd(String instanceName, int fd);

    /** 只校验 TOML 配置。0 = 合法 */
    public static native int parseConfig(String config);

    /** 按 TOML 配置启动一个网络实例。0 = 成功 */
    public static native int runNetworkInstance(String config);

    /** 只保留给定名字的实例（null = 全部停掉）。0 = 成功 */
    public static native int retainNetworkInstance(String[] instanceNames);

    public static native int deleteNetworkInstance(String instanceName);

    /** 所有实例的运行信息（JSON，NetworkInstanceRunningInfoMap） */
    public static native String collectNetworkInfos(int maxLength);

    public static native String getLastError();

    /** Farloop：运行中追加端口转发，每行 "tcp|udp 绑定地址:端口 目标地址:端口"；成功返回 0 */
    public static native int addPortForwards(String instanceName, String spec);
}
