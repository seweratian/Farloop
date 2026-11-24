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
import android.os.SystemClock;

import com.limelight.R;

/**
 * 文件传输的通知栏进度（前台服务）：下拉通知栏能看到“正在复制 xx · 45% · 剩余 12 MB”，
 * 切到后台、锁屏也不会被系统杀掉中断。传完变成“复制完成”，点一下回到文件管理。
 */
public class FrTransferService extends Service {
    private static final String CHANNEL = "fr_transfer";
    private static final int ID_PROGRESS = 4201, ID_DONE = 4202;
    private static long lastPost;
    private static volatile boolean running;

    // ---------------- 给文件管理调用 ----------------
    public static void begin(Context c, String title) {
        Context a = c.getApplicationContext();
        Intent i = new Intent(a, FrTransferService.class).putExtra("title", title);
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) a.startForegroundService(i); else a.startService(i);
            running = true;
        } catch (Exception e) {
            // 没有前台服务条件时（极少数机型）退化为普通通知
            nm(a).notify(ID_PROGRESS, build(a, title, "准备中…", 0, 0, true).build());
        }
        lastPost = 0;
    }

    /** 进度（内部限流，每秒最多 4 次） */
    public static void update(Context c, String name, long done, long total) {
        long now = SystemClock.uptimeMillis();
        if (now - lastPost < 250 && done < total) return;
        lastPost = now;
        Context a = c.getApplicationContext();
        int pct = total > 0 ? (int) (done * 100 / total) : 0;
        String text = pct + "%  ·  " + Ui.size(done) + " / " + Ui.size(total);
        nm(a).notify(ID_PROGRESS, build(a, "正在复制 " + name, text, 100, pct, false).build());
    }

    public static void finish(Context c, boolean ok, String message) {
        Context a = c.getApplicationContext();
        a.stopService(new Intent(a, FrTransferService.class));
        running = false;
        NotificationManager m = nm(a);
        m.cancel(ID_PROGRESS);
        Notification.Builder b = builder(a)
                .setSmallIcon(R.drawable.fr_ic_folder)
                .setContentTitle(ok ? "复制完成" : "复制失败")
                .setContentText(message)
                .setAutoCancel(true)
                .setContentIntent(openApp(a));
        m.notify(ID_DONE, b.build());
    }

    // ---------------- 服务本身 ----------------
    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        String title = intent != null && intent.getStringExtra("title") != null ? intent.getStringExtra("title") : "正在复制文件";
        Notification n = build(this, title, "准备中…", 0, 0, true).build();
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q)
            startForeground(ID_PROGRESS, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC);
        else
            startForeground(ID_PROGRESS, n);
        return START_NOT_STICKY;
    }

    @Override public IBinder onBind(Intent intent) { return null; }

    // ---------------- 工具 ----------------
    private static NotificationManager nm(Context c) {
        NotificationManager m = (NotificationManager) c.getSystemService(Context.NOTIFICATION_SERVICE);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && m.getNotificationChannel(CHANNEL) == null) {
            NotificationChannel ch = new NotificationChannel(CHANNEL, "文件传输", NotificationManager.IMPORTANCE_LOW);
            ch.setDescription("手机和电脑之间复制文件的进度");
            m.createNotificationChannel(ch);
        }
        return m;
    }

    private static Notification.Builder builder(Context c) {
        nm(c);
        return Build.VERSION.SDK_INT >= Build.VERSION_CODES.O ? new Notification.Builder(c, CHANNEL) : new Notification.Builder(c);
    }

    private static Notification.Builder build(Context c, String title, String text, int max, int progress, boolean indeterminate) {
        return builder(c)
                .setSmallIcon(R.drawable.fr_ic_folder)
                .setContentTitle(title)
                .setContentText(text)
                .setOnlyAlertOnce(true)
                .setOngoing(true)
                .setProgress(max, progress, indeterminate)
                .setContentIntent(openApp(c));
    }

    private static PendingIntent openApp(Context c) {
        Intent i = new Intent(c, HomeActivity.class).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP);
        return PendingIntent.getActivity(c, 0, i, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
    }
}
