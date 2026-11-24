package com.freeremote;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.os.Build;
import android.os.IBinder;

/**
 * 后台保持组网：一个低优先级的常驻通知，让系统不要在切到后台后杀掉 Farloop。
 * 组网（EasyTier）一直活着，打通的 P2P 连接和打洞进度就不会因为重新打开 App 而从头开始。
 * “我的 → 后台保持组网”可以关闭。
 */
public class FrNetService extends Service {
    private static final String CHANNEL = "fr_net";
    private static final int ID = 3101;

    public static void start(Context c) {
        if (!c.getSharedPreferences("freeremote", MODE_PRIVATE).getBoolean("keepAlive", true)) return;
        try {
            Intent i = new Intent(c, FrNetService.class);
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) c.startForegroundService(i); else c.startService(i);
        } catch (Exception ignored) { }
    }

    public static void stop(Context c) {
        try { c.stopService(new Intent(c, FrNetService.class)); } catch (Exception ignored) { }
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        NotificationManager m = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && m.getNotificationChannel(CHANNEL) == null) {
            NotificationChannel ch = new NotificationChannel(CHANNEL, "后台保持组网", NotificationManager.IMPORTANCE_MIN);
            ch.setShowBadge(false);
            m.createNotificationChannel(ch);
        }
        Intent open = new Intent(this, HomeActivity.class);
        PendingIntent pi = PendingIntent.getActivity(this, 0, open, PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
        Notification.Builder b = Build.VERSION.SDK_INT >= Build.VERSION_CODES.O ? new Notification.Builder(this, CHANNEL) : new Notification.Builder(this);
        Notification n = b.setSmallIcon(com.limelight.R.drawable.fr_ic_route)
                .setContentTitle("Farloop 组网运行中")
                .setContentText("保持 P2P 连接，随时可以远控（可在“我的”里关闭）")
                .setContentIntent(pi)
                .setOngoing(true)
                .build();
        if (Build.VERSION.SDK_INT >= 34) startForeground(ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE);
        else startForeground(ID, n);
        return START_STICKY;
    }

    @Override
    public IBinder onBind(Intent intent) { return null; }
}
