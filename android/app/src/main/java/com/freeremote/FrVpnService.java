package com.freeremote;

import android.content.Intent;
import android.net.VpnService;
import android.os.ParcelFileDescriptor;
import android.util.Log;

import com.easytier.jni.EasyTierJNI;

/**
 * 虚拟网卡：只把 FreeRemote 的 10.144.144.0/24 网段路由进隧道，手机其他上网流量不受影响，
 * 也不改 DNS。建好的 TUN 交给 EasyTier（setTunFd）。
 */
public class FrVpnService extends VpnService {
    public static final String EXTRA_IP = "ip";
    private static final String TAG = "FrVpnService";

    private ParcelFileDescriptor tun;

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        String ip = intent == null ? null : intent.getStringExtra(EXTRA_IP);
        if (ip == null) ip = new FrConfig(this).virtualIp();
        if (ip.isEmpty()) { stopSelf(); return START_NOT_STICKY; }
        try {
            close();
            Builder b = new Builder()
                    .setSession("Farloop")
                    .addAddress(ip, 24)
                    .addRoute(FrConfig.SUBNET, 24)
                    .setMtu(1280)
                    .setBlocking(false);
            tun = b.establish();
            if (tun == null) {
                Log.e(TAG, "建立虚拟网卡失败（没有 VPN 授权？）");
                stopSelf();
                return START_NOT_STICKY;
            }
            int r = EasyTierJNI.setTunFd(FrNet.INSTANCE, tun.getFd());
            Log.i(TAG, "虚拟网卡 " + ip + "/24 已建立，交给 EasyTier：" + (r == 0 ? "成功" : "失败 " + EasyTierJNI.getLastError()));
        } catch (Exception e) {
            Log.e(TAG, "建立虚拟网卡出错", e);
            stopSelf();
            return START_NOT_STICKY;
        }
        return START_STICKY;
    }

    @Override
    public void onRevoke() {
        // 用户在系统设置里断开了 VPN，或者别的 VPN 应用抢占
        Log.w(TAG, "VPN 授权被撤销");
        close();
        super.onRevoke();
    }

    @Override
    public void onDestroy() {
        close();
        super.onDestroy();
    }

    private void close() {
        if (tun != null) {
            try { tun.close(); } catch (Exception ignored) { }
            tun = null;
        }
    }
}
