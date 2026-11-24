package com.freeremote;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.content.SharedPreferences;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Color;
import android.graphics.drawable.GradientDrawable;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.TextUtils;
import android.util.Base64;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.BaseAdapter;
import android.widget.FrameLayout;
import android.widget.GridLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ListView;
import android.widget.ProgressBar;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import com.limelight.R;

import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;

/**
 * 设备页（参考 UU 远程手机端）：顶部是对方桌面的实时缩略图 + “进入桌面”，
 * 下面 文件传输 / 观看模式 / 终端 / 设备详情，再下面是“快速启动”（点一下在电脑上打开这个应用并进入桌面）。
 */
public class DeviceActivity extends Activity implements FrHub.Listener {
    @Override
    protected void attachBaseContext(android.content.Context b) { super.attachBaseContext(FrTheme.wrap(b)); }

    public static final String EXTRA_IP = "ip", EXTRA_NAME = "name";

    private Ui ui;
    private final Handler h = new Handler(Looper.getMainLooper());
    private String ip, name;
    private ImageView preview;
    private TextView previewHint;
    private GridLayout grid;
    private SharedPreferences sp;
    private JsonObject lastInfo;
    private LinearLayout specBody;
    private boolean gotInfo, resumed;
    private int tick;
    /** 页面打开期间每 3 秒刷新一次占用率，每 30 秒刷新一次桌面缩略图 */
    private final Runnable poller = new Runnable() {
        @Override public void run() {
            if (!resumed) return;
            tick++;
            final boolean withPreview = tick % 10 == 0;
            new Thread(() -> { if (FrHub.get(DeviceActivity.this).supportsTools(ip)) FrHub.get(DeviceActivity.this).send(ip, FrHub.SYS_INFO_REQ, new byte[]{(byte) (withPreview ? 1 : 0)}); }).start();
            h.postDelayed(this, 3000);
        }
    };

    @Override
    protected void onResume() {
        super.onResume();
        resumed = true;
        h.removeCallbacks(poller);
        h.postDelayed(poller, 3000);
    }

    @Override
    protected void onPause() {
        super.onPause();
        resumed = false;
        h.removeCallbacks(poller);
    }
    private AlertDialog appPicker;

    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        ui = new Ui(this);
        ui.applyWindow(this);
        ip = getIntent().getStringExtra(EXTRA_IP);
        name = getIntent().getStringExtra(EXTRA_NAME);
        if (name == null) name = ip;
        sp = getSharedPreferences("freeremote_quick", MODE_PRIVATE);
        build();
        FrHub.get(this).addListener(this);
        requestInfo();
    }

    @Override
    protected void onDestroy() {
        super.onDestroy();
        FrHub.get(this).removeListener(this);
    }

    private void build() {
        LinearLayout root = ui.col();
        root.setPadding(ui.dp(16), ui.dp(10), ui.dp(16), ui.dp(24));

        // 顶栏：返回、在线、名称
        LinearLayout top = ui.row();
        top.setPadding(0, ui.dp(8), 0, ui.dp(14));
        ImageView back = ui.icon(R.drawable.fr_ic_back, ui.text, 26);
        back.setPadding(ui.dp(4), ui.dp(4), ui.dp(4), ui.dp(4));
        back.setLayoutParams(new LinearLayout.LayoutParams(ui.dp(40), ui.dp(40)));
        back.setOnClickListener(v -> finish());
        top.addView(back);
        TextView online = ui.text("●  在线", 13, Color.WHITE, true);
        online.setBackground(Ui.round(0xFF1C1C1E, ui.dp(14)));
        online.setPadding(ui.dp(12), ui.dp(7), ui.dp(12), ui.dp(7));
        LinearLayout.LayoutParams olp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        olp.leftMargin = ui.dp(8);
        top.addView(online, olp);
        TextView title = ui.text(name, 22, ui.text, true);
        title.setSingleLine(true);
        title.setEllipsize(TextUtils.TruncateAt.END);
        title.setPadding(ui.dp(12), 0, 0, 0);
        top.addView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        root.addView(top);

        // 预览卡片
        LinearLayout card = ui.card();
        FrameLayout pv = new FrameLayout(this);
        preview = new ImageView(this);
        preview.setScaleType(ImageView.ScaleType.CENTER_CROP);
        preview.setBackgroundColor(ui.dark ? 0xFF2C2C2E : 0xFFDCE6F5);
        pv.addView(preview, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        View shade = new View(this);
        shade.setBackground(new GradientDrawable(GradientDrawable.Orientation.TOP_BOTTOM, new int[]{0x00000000, 0x00000000, 0x99000000}));
        pv.addView(shade, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        previewHint = ui.text("进入桌面  ›", 18, Color.WHITE, true);
        previewHint.setGravity(Gravity.CENTER);
        previewHint.setLineSpacing(ui.dp(4), 1f);
        FrameLayout.LayoutParams hlp = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL);
        hlp.bottomMargin = ui.dp(18);
        pv.addView(previewHint, hlp);
        pv.setOnClickListener(v -> enterDesktop(false));
        // 圆角裁剪
        pv.setClipToOutline(true);
        pv.setOutlineProvider(new android.view.ViewOutlineProvider() {
            @Override public void getOutline(View view, android.graphics.Outline o) { o.setRoundRect(0, 0, view.getWidth(), view.getHeight() + ui.dp(24), ui.dp(24)); }
        });
        card.addView(pv, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ui.dp(210)));

        LinearLayout acts = ui.row();
        acts.setPadding(0, ui.dp(8), 0, ui.dp(8));
        acts.addView(action(R.drawable.fr_ic_folder, "文件传输", v -> {
            Intent i = new Intent(this, FileManagerActivity.class);
            i.putExtra(FileManagerActivity.EXTRA_IP, ip);
            i.putExtra(FileManagerActivity.EXTRA_NAME, name);
            startActivity(i);
        }));
        acts.addView(action(R.drawable.fr_ic_eye, "观看模式", v -> enterDesktop(true)));
        acts.addView(action(R.drawable.fr_ic_keyboard, "终端", v -> {
            Intent i = new Intent(this, TerminalActivity.class);
            i.putExtra(TerminalActivity.EXTRA_IP, ip);
            i.putExtra(TerminalActivity.EXTRA_NAME, name);
            startActivity(i);
        }));
        acts.addView(action(R.drawable.fr_ic_speed, "设备详情", v -> showDetails()));
        card.addView(acts);
        root.addView(card);

        // 设备参数（直接显示在设备页，和 UU 一样）
        LinearLayout sh = ui.row();
        sh.setPadding(ui.dp(4), ui.dp(26), ui.dp(4), ui.dp(12));
        sh.addView(ui.text("设备参数", 20, ui.text, true), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        TextView all = ui.text("全部参数  ›", 14, ui.accent, false);
        all.setOnClickListener(v -> showDetails());
        sh.addView(all);
        root.addView(sh);
        LinearLayout spec = ui.card();
        specBody = ui.col();
        spec.addView(specBody);
        TextView wait = ui.text("正在读取…", 14, ui.sub, false);
        wait.setPadding(ui.dp(18), ui.dp(16), ui.dp(18), ui.dp(16));
        specBody.addView(wait);
        spec.setOnClickListener(v -> showDetails());
        root.addView(spec);

        TextView ql = ui.text("快速启动", 20, ui.text, true);
        ql.setPadding(ui.dp(4), ui.dp(26), 0, ui.dp(12));
        root.addView(ql);
        grid = new GridLayout(this);
        grid.setColumnCount(4);
        root.addView(grid, ui.match(0, 0));
        renderQuick();

        ScrollView sv = new ScrollView(this);
        sv.setBackgroundColor(ui.bg);
        sv.setFillViewport(true);
        sv.addView(root);
        setContentView(sv);
    }

    private View action(int icon, String label, View.OnClickListener l) {
        LinearLayout c = ui.col();
        c.setGravity(Gravity.CENTER);
        c.setPadding(0, ui.dp(10), 0, ui.dp(8));
        c.setBackground(ui.press(Color.TRANSPARENT, ui.dp(16)));
        c.addView(ui.icon(icon, ui.text, 26));
        TextView t = ui.text(label, 13.5f, ui.text, false);
        t.setGravity(Gravity.CENTER);
        t.setPadding(0, ui.dp(8), 0, 0);
        c.addView(t);
        c.setOnClickListener(l);
        c.setLayoutParams(new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        return c;
    }

    /** 回首页去连接（自动配对、打开桌面）；viewOnly = 观看模式 */
    private void enterDesktop(boolean viewOnly) {
        FrStreamHooks.setStartViewOnly(viewOnly);
        Intent i = new Intent(this, HomeActivity.class);
        i.addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP | Intent.FLAG_ACTIVITY_SINGLE_TOP);
        i.putExtra(HomeActivity.EXTRA_CONNECT_IP, ip);
        i.putExtra(HomeActivity.EXTRA_CONNECT_NAME, name);
        startActivity(i);
    }

    // =============================== 设备信息 / 缩略图 ===============================
    private void requestInfo() {
        new Thread(() -> {
            if (!FrHub.get(this).supportsTools(ip)) {
                h.post(() -> {
                    previewHint.setText("进入桌面  ›\n电脑端升级到 Farloop 0.13 后可显示预览");
                    previewHint.setTextSize(15);
                    specMessage("这台电脑的 Farloop 版本太旧，升级到 0.13 以上后这里会显示处理器、内存、显卡等参数。");
                });
                return;
            }
            h.postDelayed(() -> { if (!gotInfo) specMessage("暂时没有收到电脑的回应（网络慢时会久一点）。如果一直这样，请确认电脑上的 Farloop 正在运行、并开启了“允许被远程”。"); }, 15000);
            String err = FrHub.get(this).send(ip, FrHub.SYS_INFO_REQ, new byte[]{1});
            if (err != null) h.post(() -> toast("连接电脑失败：" + err));
        }).start();
    }

    @Override
    public void onMessage(String from, int type, byte[] payload) {
        if (!from.equals(ip)) return;
        JsonObject j = JsonParser.parseString(new String(payload, StandardCharsets.UTF_8)).getAsJsonObject();
        h.post(() -> {
            if (type == FrHub.SYS_INFO && j.has("Denied")) {
                specMessage("这台电脑关闭了“允许被远程”。请在电脑上打开 Farloop → 设置，开启“允许被远程”。");
                gotInfo = true;
            } else if (type == FrHub.SYS_INFO && j.has("Error")) {
                specMessage("电脑读取设备信息出错：" + j.get("Error").getAsString() + "\n（不影响远程控制）");
                gotInfo = true;
            } else if (type == FrHub.SYS_INFO) {
                gotInfo = true;
                if ((!j.has("PreviewJpg") || j.get("PreviewJpg").isJsonNull()) && lastInfo != null && lastInfo.has("PreviewJpg"))
                    j.add("PreviewJpg", lastInfo.get("PreviewJpg"));
                lastInfo = j;
                renderSpecs(j);
                if (j.has("PreviewJpg") && !j.get("PreviewJpg").isJsonNull()) {
                    byte[] img = Base64.decode(j.get("PreviewJpg").getAsString(), Base64.DEFAULT);
                    Bitmap bm = BitmapFactory.decodeByteArray(img, 0, img.length);
                    if (bm != null) preview.setImageBitmap(bm);
                }
            } else if (type == FrHub.APP_LIST) {
                showAppPicker(j.getAsJsonArray("Apps"));
            } else if (type == FrHub.LAUNCH_RESULT) {
                toast(j.get("Message").getAsString());
            }
        });
    }

    private void specMessage(String m) {
        if (specBody == null || gotInfo) return;
        specBody.removeAllViews();
        TextView t = ui.text(m, 14, ui.sub, false);
        t.setPadding(ui.dp(18), ui.dp(16), ui.dp(18), ui.dp(16));
        t.setLineSpacing(ui.dp(3), 1f);
        specBody.addView(t);
    }

    private static String js(JsonObject j, String k) { return j.has(k) && !j.get(k).isJsonNull() ? j.get(k).getAsString() : ""; }

    private void renderSpecs(JsonObject j) {
        if (specBody == null) return;
        specBody.removeAllViews();
        double cpu = j.has("CpuLoad") ? j.get("CpuLoad").getAsDouble() : 0;
        long total = j.has("RamTotal") ? j.get("RamTotal").getAsLong() : 0, avail = j.has("RamAvail") ? j.get("RamAvail").getAsLong() : 0;
        specBody.addView(meter("处理器占用", cpu, String.format(java.util.Locale.US, "%.0f%%", cpu * 100)));
        specBody.addView(meter("内存占用", total > 0 ? (total - avail) / (double) total : 0, Ui.size(total - avail) + " / " + Ui.size(total)));
        String gpu = j.has("Gpus") && j.get("Gpus").isJsonArray() && j.getAsJsonArray("Gpus").size() > 0 ? j.getAsJsonArray("Gpus").get(0).getAsString() : "";
        specRow("处理器", js(j, "Cpu") + (j.has("Cores") ? "（" + j.get("Cores").getAsInt() + " 线程）" : ""));
        specRow("显卡", gpu);
        specRow("内存", Ui.size(total));
        specRow("系统", js(j, "Os"));
        renderNet(j);
    }

    /** 被控端网络：多条宽带时给出说明，并可以一键设置“打洞用的公网 IP”（电脑 0.13.5+ 才有这些字段） */
    private void renderNet(JsonObject j) {
        if (!j.has("PublicIps") || !j.get("PublicIps").isJsonArray()) return;
        com.google.gson.JsonArray ips = j.getAsJsonArray("PublicIps");
        if (ips.size() == 0) return;
        String advice = js(j, "NetAdvice");
        String cur = js(j, "IpRule");
        LinearLayout box = ui.col();
        box.setPadding(ui.dp(18), ui.dp(12), ui.dp(18), ui.dp(16));
        box.addView(ui.text("网络", 14.5f, ui.sub, false));
        TextView t = ui.text(advice.isEmpty() ? (ips.size() > 1 ? "检测到 " + ips.size() + " 条宽带出口" : "单条宽带") : advice, 14, ui.text, false);
        t.setLineSpacing(ui.dp(3), 1f);
        t.setPadding(0, ui.dp(6), 0, 0);
        box.addView(t);
        if (ips.size() > 1) {
            String suggested = js(j, "SuggestedRule");
            android.widget.LinearLayout chips = ui.row();
            chips.setPadding(0, ui.dp(10), 0, 0);
            HorizontalScrollViewCompat sv = new HorizontalScrollViewCompat(this);
            for (com.google.gson.JsonElement e : ips) {
                String ip = e.getAsString();
                String rule = ruleFor(ip, ips);
                boolean rec = rule.equals(suggested);
                chips.addView(chip((rule.equals(cur) ? "✓ " : "") + "只用 " + rule + ".*" + (rec ? "（推荐）" : ""), rec, () -> setIpRule(rule)));
            }
            chips.addView(chip((cur.isEmpty() ? "✓ " : "") + "自动", false, () -> setIpRule("")));
            sv.addView(chips);
            box.addView(sv);
        }
        specBody.addView(box);
    }

    private static final class HorizontalScrollViewCompat extends android.widget.HorizontalScrollView {
        HorizontalScrollViewCompat(android.content.Context c) { super(c); setHorizontalScrollBarEnabled(false); }
    }

    static String ruleFor(String ip, com.google.gson.JsonArray all) {
        String[] parts = ip.split("\\.");
        for (int n = 2; n <= 4; n++) {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < n && i < parts.length; i++) { if (i > 0) sb.append('.'); sb.append(parts[i]); }
            String prefix = sb.toString();
            boolean unique = true;
            for (com.google.gson.JsonElement e : all) {
                String o = e.getAsString();
                if (!o.equals(ip) && o.startsWith(prefix + ".")) { unique = false; break; }
            }
            if (unique) return prefix;
        }
        return ip;
    }

    private View chip(String label, boolean primary, Runnable r) {
        TextView c = ui.text(label, 13.5f, primary ? android.graphics.Color.WHITE : ui.accent, true);
        c.setBackground(Ui.round(primary ? ui.accent : ui.accentSoft, ui.dp(16)));
        c.setPadding(ui.dp(14), ui.dp(8), ui.dp(14), ui.dp(8));
        c.setOnClickListener(v -> r.run());
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.rightMargin = ui.dp(8);
        c.setLayoutParams(lp);
        return c;
    }

    private void setIpRule(String rule) {
        new android.app.AlertDialog.Builder(this)
                .setTitle(rule.isEmpty() ? "恢复自动" : "只用 " + rule + ".* 打洞")
                .setMessage("电脑会重启组网，连接会断开约 10 秒，然后自动重连。")
                .setPositiveButton("设置", (d, w) -> new Thread(() -> {
                    JsonObject m = new JsonObject();
                    m.addProperty("Rule", rule);
                    String err = FrHub.get(this).sendJson(ip, FrHub.SET_IP_RULE, m);
                    h.post(() -> toast(err == null ? "已设置，电脑正在重连组网" : "设置失败：" + err));
                }).start())
                .setNegativeButton("取消", null).show();
    }

    private void specRow(String k, String v) {
        if (v == null || v.trim().isEmpty()) return;
        LinearLayout r = ui.row();
        r.setPadding(ui.dp(18), ui.dp(10), ui.dp(18), ui.dp(10));
        TextView kt = ui.text(k, 14.5f, ui.sub, false);
        r.addView(kt, new LinearLayout.LayoutParams(ui.dp(72), ViewGroup.LayoutParams.WRAP_CONTENT));
        TextView vt = ui.text(v, 14.5f, ui.text, false);
        r.addView(vt, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        specBody.addView(r);
    }

    private View meter(String k, double frac, String v) {
        LinearLayout r = ui.col();
        r.setPadding(ui.dp(18), ui.dp(14), ui.dp(18), ui.dp(8));
        LinearLayout top = ui.row();
        top.addView(ui.text(k, 14.5f, ui.text, false), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        top.addView(ui.text(v, 14, ui.sub, false));
        r.addView(top);
        LinearLayout bar = ui.row();
        bar.setBackground(Ui.round(ui.card2, ui.dp(4)));
        View fill = new View(this);
        fill.setBackground(Ui.round(frac > 0.85 ? ui.red : ui.accent, ui.dp(4)));
        bar.addView(fill, new LinearLayout.LayoutParams(0, ui.dp(8), (float) Math.max(0.01, Math.min(1, frac))));
        bar.addView(new View(this), new LinearLayout.LayoutParams(0, ui.dp(8), (float) Math.max(0.0001, 1 - Math.min(1, frac))));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ui.dp(8));
        lp.topMargin = ui.dp(8);
        r.addView(bar, lp);
        return r;
    }

    private void showDetails() {
        if (lastInfo == null) { toast("正在读取设备信息…"); requestInfo(); return; }
        Intent i = new Intent(this, DeviceInfoActivity.class);
        i.putExtra(DeviceInfoActivity.EXTRA_JSON, lastInfo.toString());
        startActivity(i);
    }

    // =============================== 快速启动 ===============================
    private JsonArray quick() {
        try { return JsonParser.parseString(sp.getString(ip, "[]")).getAsJsonArray(); } catch (Exception e) { return new JsonArray(); }
    }

    private void saveQuick(JsonArray a) { sp.edit().putString(ip, a.toString()).apply(); }

    private void renderQuick() {
        grid.removeAllViews();
        JsonArray a = quick();
        int w = (getResources().getDisplayMetrics().widthPixels - ui.dp(32)) / 4;
        for (int i = 0; i < a.size(); i++) {
            JsonObject o = a.get(i).getAsJsonObject();
            final int idx = i;
            LinearLayout c = ui.col();
            c.setGravity(Gravity.CENTER_HORIZONTAL);
            c.setPadding(ui.dp(4), ui.dp(10), ui.dp(4), ui.dp(10));
            c.setBackground(ui.press(Color.TRANSPARENT, ui.dp(16)));
            ImageView iv = new ImageView(this);
            Bitmap bm = icon(o);
            if (bm != null) iv.setImageBitmap(bm); else { iv.setImageResource(R.drawable.fr_ic_desktop); iv.setImageTintList(android.content.res.ColorStateList.valueOf(ui.accent)); }
            c.addView(iv, new LinearLayout.LayoutParams(ui.dp(46), ui.dp(46)));
            TextView t = ui.text(o.get("Name").getAsString(), 12.5f, ui.text, false);
            t.setGravity(Gravity.CENTER);
            t.setMaxLines(2);
            t.setEllipsize(TextUtils.TruncateAt.END);
            t.setPadding(0, ui.dp(8), 0, 0);
            c.addView(t);
            c.setOnClickListener(v -> launch(o.get("Path").getAsString(), o.get("Name").getAsString()));
            c.setOnLongClickListener(v -> {
                new AlertDialog.Builder(this).setMessage("从快速启动里移除“" + o.get("Name").getAsString() + "”？")
                        .setPositiveButton("移除", (d, wh) -> { JsonArray q = quick(); q.remove(idx); saveQuick(q); renderQuick(); })
                        .setNegativeButton("取消", null).show();
                return true;
            });
            grid.addView(c, new GridLayout.LayoutParams(new ViewGroup.LayoutParams(w, ViewGroup.LayoutParams.WRAP_CONTENT)));
        }
        // 添加应用（虚线框）
        LinearLayout add = ui.col();
        add.setGravity(Gravity.CENTER);
        GradientDrawable dash = new GradientDrawable();
        dash.setCornerRadius(ui.dp(20));
        dash.setStroke(ui.dp(1.5f), ui.dark ? 0x55FFFFFF : 0x33000000, ui.dp(6), ui.dp(5));
        add.setBackground(dash);
        add.setPadding(0, ui.dp(a.size() == 0 ? 60 : 12), 0, ui.dp(a.size() == 0 ? 60 : 12));
        FrameLayout plus = ui.iconTile(R.drawable.fr_ic_new_folder, ui.sub, ui.card2, 50, 0);
        TextView pt = ui.text("+", 30, ui.sub, false);
        pt.setGravity(Gravity.CENTER);
        plus.addView(pt, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        plus.setBackground(Ui.round(ui.card2, ui.dp(25)));
        add.addView(plus);
        TextView at = ui.text("添加应用", 13.5f, ui.text, false);
        at.setPadding(0, ui.dp(10), 0, 0);
        at.setGravity(Gravity.CENTER);
        add.addView(at);
        add.setOnClickListener(v -> requestApps());
        GridLayout.LayoutParams alp = new GridLayout.LayoutParams(new ViewGroup.LayoutParams(a.size() == 0 ? w * 4 : w, ViewGroup.LayoutParams.WRAP_CONTENT));
        if (a.size() == 0) alp.columnSpec = GridLayout.spec(0, 4);
        grid.addView(add, alp);
    }

    private static Bitmap icon(JsonObject o) {
        try {
            if (!o.has("IconPng") || o.get("IconPng").isJsonNull()) return null;
            byte[] b = Base64.decode(o.get("IconPng").getAsString(), Base64.DEFAULT);
            return BitmapFactory.decodeByteArray(b, 0, b.length);
        } catch (Exception e) { return null; }
    }

    private void requestApps() {
        toast("正在读取电脑上的应用…");
        new Thread(() -> {
            if (!FrHub.get(this).supportsTools(ip)) { h.post(() -> toast("电脑端需要升级到 0.12.1 以上")); return; }
            String err = FrHub.get(this).send(ip, FrHub.APP_LIST_REQ, new byte[0]);
            if (err != null) h.post(() -> toast("连接电脑失败：" + err));
        }).start();
    }

    private void showAppPicker(JsonArray apps) {
        if (appPicker != null && appPicker.isShowing()) appPicker.dismiss();
        List<JsonObject> list = new ArrayList<>();
        for (JsonElement e : apps) list.add(e.getAsJsonObject());
        ListView lv = new ListView(this);
        lv.setDivider(null);
        lv.setAdapter(new BaseAdapter() {
            @Override public int getCount() { return list.size(); }
            @Override public Object getItem(int i) { return list.get(i); }
            @Override public long getItemId(int i) { return i; }
            @Override public View getView(int i, View cv, ViewGroup parent) {
                JsonObject o = list.get(i);
                LinearLayout r = ui.row();
                r.setPadding(ui.dp(20), ui.dp(10), ui.dp(20), ui.dp(10));
                ImageView iv = new ImageView(DeviceActivity.this);
                Bitmap bm = icon(o);
                if (bm != null) iv.setImageBitmap(bm);
                r.addView(iv, new LinearLayout.LayoutParams(ui.dp(36), ui.dp(36)));
                TextView t = ui.text(o.get("Name").getAsString(), 15.5f, ui.text, false);
                t.setPadding(ui.dp(14), 0, 0, 0);
                r.addView(t);
                return r;
            }
        });
        lv.setOnItemClickListener((p, v, pos, id) -> {
            JsonArray q = quick();
            JsonObject o = list.get(pos);
            for (JsonElement e : q) if (e.getAsJsonObject().get("Path").getAsString().equals(o.get("Path").getAsString())) { appPicker.dismiss(); return; }
            q.add(o);
            saveQuick(q);
            renderQuick();
            appPicker.dismiss();
        });
        appPicker = new AlertDialog.Builder(this).setTitle("添加到快速启动（" + list.size() + " 个应用）").setView(lv)
                .setNegativeButton("取消", null).create();
        appPicker.show();
    }

    /** 在电脑上打开应用，然后进入桌面 */
    private void launch(String path, String appName) {
        new Thread(() -> {
            JsonObject m = new JsonObject();
            m.addProperty("Path", path);
            String err = FrHub.get(this).sendJson(ip, FrHub.LAUNCH_APP, m);
            h.post(() -> {
                if (err != null) { toast("打开失败：" + err); return; }
                toast("正在电脑上打开 " + appName);
                enterDesktop(false);
            });
        }).start();
    }

    private void toast(String s) { Toast.makeText(this, s, Toast.LENGTH_SHORT).show(); }
}
