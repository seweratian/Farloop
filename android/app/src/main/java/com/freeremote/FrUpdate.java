package com.freeremote;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.net.Uri;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.util.Log;
import android.widget.Toast;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;

import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;

/**
 * 安卓端自动更新：查 GitHub 仓库最新的 Release，下载里面的 APK，交给系统安装器（覆盖安装，数据保留）。
 * 打开 App 时每天自动查一次；“我的 → 检查更新”可以手动查。
 */
public final class FrUpdate {
    private FrUpdate() { }

    private static final String TAG = "FrUpdate";
    public static final String REPO = "seweratian/Farloop";
    public static final String RELEASES_PAGE = "https://github.com/" + REPO + "/releases";

    static final class Release { String version, url, notes; long size; }

    private static Release latest() throws Exception {
        HttpURLConnection c = (HttpURLConnection) new URL("https://api.github.com/repos/" + REPO + "/releases/latest").openConnection();
        c.setConnectTimeout(10000);
        c.setReadTimeout(15000);
        c.setRequestProperty("Accept", "application/vnd.github+json");
        c.setRequestProperty("User-Agent", "Farloop-Android/" + FrConfig.VERSION);
        try (InputStream in = c.getInputStream()) {
            byte[] b = readAll(in);
            JsonObject j = JsonParser.parseString(new String(b, StandardCharsets.UTF_8)).getAsJsonObject();
            Release r = new Release();
            r.version = j.get("tag_name").getAsString().replaceFirst("^[vV]", "");
            r.notes = j.has("body") && !j.get("body").isJsonNull() ? j.get("body").getAsString() : "";
            JsonArray assets = j.getAsJsonArray("assets");
            for (JsonElement e : assets) {
                JsonObject a = e.getAsJsonObject();
                String name = a.get("name").getAsString();
                if (name.startsWith("Farloop-Android") && name.endsWith(".apk")) {
                    r.url = a.get("browser_download_url").getAsString();
                    r.size = a.get("size").getAsLong();
                    return r;
                }
            }
            return null;
        } finally {
            c.disconnect();
        }
    }

    private static byte[] readAll(InputStream in) throws Exception {
        java.io.ByteArrayOutputStream bo = new java.io.ByteArrayOutputStream();
        byte[] buf = new byte[16384];
        int n;
        while ((n = in.read(buf)) > 0) bo.write(buf, 0, n);
        return bo.toByteArray();
    }

    /** 按数字段比较版本号：0.19.0 > 0.17.4 */
    static boolean isNewer(String a, String b) {
        String[] x = a.split("[^0-9]+"), y = b.split("[^0-9]+");
        for (int i = 0; i < Math.max(x.length, y.length); i++) {
            int p = i < x.length && !x[i].isEmpty() ? Integer.parseInt(x[i]) : 0;
            int q = i < y.length && !y[i].isEmpty() ? Integer.parseInt(y[i]) : 0;
            if (p != q) return p > q;
        }
        return false;
    }

    /** 打开 App 时调用：一天最多自动查一次，有新版本才弹窗 */
    public static void autoCheck(Activity a) {
        android.content.SharedPreferences sp = a.getSharedPreferences("farloop", Activity.MODE_PRIVATE);
        long last = sp.getLong("update_checked", 0);
        if (System.currentTimeMillis() - last < 24 * 3600_000L) return;
        sp.edit().putLong("update_checked", System.currentTimeMillis()).apply();
        check(a, false);
    }

    /** 查更新；manual = 用户点了“检查更新”（没有新版本也要告诉他） */
    public static void check(Activity a, boolean manual) {
        Handler h = new Handler(Looper.getMainLooper());
        if (manual) Toast.makeText(a, "正在检查更新…", Toast.LENGTH_SHORT).show();
        new Thread(() -> {
            Release r = null;
            String err = null;
            try { r = latest(); } catch (Exception e) { err = e.getMessage(); Log.w(TAG, "检查更新失败", e); }
            final Release fr = r;
            final String fe = err;
            h.post(() -> {
                if (a.isFinishing()) return;
                if (fr == null) {
                    if (manual) Toast.makeText(a, fe != null ? "检查更新失败（网络连不上 GitHub？）" : "还没有发布安卓版", Toast.LENGTH_LONG).show();
                    return;
                }
                if (!isNewer(fr.version, FrConfig.VERSION)) {
                    if (manual) Toast.makeText(a, "已是最新版本 " + FrConfig.VERSION, Toast.LENGTH_SHORT).show();
                    return;
                }
                String notes = fr.notes.length() > 600 ? fr.notes.substring(0, 600) + "…" : fr.notes;
                new AlertDialog.Builder(a)
                        .setTitle("发现新版本 " + fr.version)
                        .setMessage("当前 " + FrConfig.VERSION + (notes.isEmpty() ? "" : "\n\n" + notes))
                        .setPositiveButton("更新", (d, w) -> download(a, fr))
                        .setNegativeButton("以后", null)
                        .show();
            });
        }, "FrUpdateCheck").start();
    }

    private static void download(Activity a, Release r) {
        // 安卓 8 起要允许本 App“安装未知应用”
        if (Build.VERSION.SDK_INT >= 26 && !a.getPackageManager().canRequestPackageInstalls()) {
            new AlertDialog.Builder(a)
                    .setTitle("需要允许安装")
                    .setMessage("请在接下来的页面里允许 Farloop“安装未知应用”，然后回来再点一次更新。")
                    .setPositiveButton("去设置", (d, w) -> a.startActivity(new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:" + a.getPackageName()))))
                    .setNegativeButton("取消", null)
                    .show();
            return;
        }
        AlertDialog progress = new AlertDialog.Builder(a).setTitle("正在下载 " + r.version).setMessage("0%").setCancelable(false).show();
        Handler h = new Handler(Looper.getMainLooper());
        new Thread(() -> {
            File dir = new File(a.getCacheDir(), "update");
            //noinspection ResultOfMethodCallIgnored
            dir.mkdirs();
            File[] old = dir.listFiles();
            if (old != null) for (File f : old) //noinspection ResultOfMethodCallIgnored
                f.delete();
            File apk = new File(dir, "Farloop-" + r.version + ".apk");
            String err = null;
            try {
                HttpURLConnection c = (HttpURLConnection) new URL(r.url).openConnection();
                c.setConnectTimeout(15000);
                c.setReadTimeout(30000);
                c.setInstanceFollowRedirects(true);
                c.setRequestProperty("User-Agent", "Farloop-Android/" + FrConfig.VERSION);
                try (InputStream in = c.getInputStream(); OutputStream out = new FileOutputStream(apk)) {
                    byte[] buf = new byte[65536];
                    long done = 0, lastUi = 0;
                    int n;
                    while ((n = in.read(buf)) > 0) {
                        out.write(buf, 0, n);
                        done += n;
                        long now = System.currentTimeMillis();
                        if (now - lastUi > 400) {
                            lastUi = now;
                            final long d = done;
                            h.post(() -> progress.setMessage(r.size > 0 ? (d * 100 / r.size) + "%" : (d / 1048576) + " MB"));
                        }
                    }
                } finally {
                    c.disconnect();
                }
                if (r.size > 0 && apk.length() != r.size) err = "下载不完整，请重试";
            } catch (Exception e) {
                err = "下载失败：" + e.getMessage();
                Log.w(TAG, "下载更新失败", e);
            }
            final String fe = err;
            h.post(() -> {
                progress.dismiss();
                if (fe != null) {
                    new AlertDialog.Builder(a).setTitle("更新失败").setMessage(fe + "\n\n也可以到 " + RELEASES_PAGE + " 手动下载。")
                            .setPositiveButton("好", null).show();
                    return;
                }
                Uri uri = androidx.core.content.FileProvider.getUriForFile(a, a.getPackageName() + ".fileprovider", apk);
                Intent i = new Intent(Intent.ACTION_VIEW);
                i.setDataAndType(uri, "application/vnd.android.package-archive");
                i.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_ACTIVITY_NEW_TASK);
                a.startActivity(i);
            });
        }, "FrUpdateDownload").start();
    }
}
