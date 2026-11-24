package com.freeremote;

import android.app.Activity;
import android.app.AlertDialog;
import android.app.Dialog;
import android.content.ClipboardManager;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.ServiceConnection;
import android.graphics.Color;
import android.net.VpnService;
import android.os.Bundle;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.Window;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import com.limelight.R;
import com.limelight.binding.PlatformBinding;
import com.limelight.computers.ComputerManagerListener;
import com.limelight.computers.ComputerManagerService;
import com.limelight.nvstream.http.ComputerDetails;
import com.limelight.nvstream.http.NvApp;
import com.limelight.nvstream.http.NvHTTP;
import com.limelight.nvstream.http.PairingManager;
import com.limelight.preferences.StreamSettings;
import com.limelight.utils.ServerHelper;

import java.util.Collections;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;

/**
 * FreeRemote 安卓端首页（HyperOS 风格）：底部三个标签——设备 / 文件 / 我的。
 *  · 首次使用：粘贴电脑端“复制邀请码”得到的 FR1- 邀请码加入组网；
 *  · 设备：组网里的电脑，P2P / 中转、延迟，一键连接（自动添加 → 自动配对 → 打开桌面）；
 *  · 文件：选一台电脑打开双栏文件管理（左手机、右电脑）；
 *  · 我的：本机信息、连接方式、画面设置、退出网络。
 */
public class HomeActivity extends Activity {
    @Override
    protected void attachBaseContext(android.content.Context b) { super.attachBaseContext(FrTheme.wrap(b)); }

    private static final int REQ_VPN = 1;
    /** 设备页“进入桌面 / 观看模式 / 快速启动”：回到首页由这里连接 */
    public static final String EXTRA_CONNECT_IP = "connect_ip", EXTRA_CONNECT_NAME = "connect_name";

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        String cip = intent.getStringExtra(EXTRA_CONNECT_IP);
        if (cip != null) {
            FrNet.Peer p = new FrNet.Peer();
            p.ip = cip;
            String n = intent.getStringExtra(EXTRA_CONNECT_NAME);
            p.name = n == null ? cip : n;
            h.post(() -> connect(p));
        }
    }

    private void openDevice(FrNet.Peer p) {
        Intent i = new Intent(this, DeviceActivity.class);
        i.putExtra(DeviceActivity.EXTRA_IP, p.ip);
        i.putExtra(DeviceActivity.EXTRA_NAME, p.name.isEmpty() ? p.ip : p.name);
        startActivity(i);
    }
    private static final int TAB_DEVICES = 0, TAB_FILES = 1, TAB_ME = 2;

    private final Handler h = new Handler(Looper.getMainLooper());
    private Ui ui;
    private FrConfig cfg;
    private ComputerManagerService.ComputerManagerBinder binder;
    private final Map<String, ComputerDetails> computers = new ConcurrentHashMap<>();
    private boolean busy;

    private static int tab = TAB_DEVICES; // static：切换深浅色重建页面后停在原来的标签
    private FrameLayout content;
    private LinearLayout tabBar;
    private LinearLayout list;   // 设备 / 文件 标签里的列表容器
    private TextView status;
    private List<FrNet.Peer> lastPeers = Collections.emptyList();

    private final ServiceConnection conn = new ServiceConnection() {
        @Override public void onServiceConnected(ComponentName n, IBinder b) {
            binder = (ComputerManagerService.ComputerManagerBinder) b;
            new Thread(() -> {
                binder.waitForReady();
                binder.startPolling(new ComputerManagerListener() {
                    @Override public void notifyComputerUpdated(ComputerDetails d) {
                        for (ComputerDetails.AddressTuple a : new ComputerDetails.AddressTuple[]{d.activeAddress, d.manualAddress, d.localAddress}) {
                            if (a != null && FrHub.isFreeRemoteHost(a.address)) computers.put(FrNet.toVirtual(a.address), d);
                        }
                    }
                });
            }).start();
        }
        @Override public void onServiceDisconnected(ComponentName n) { binder = null; }
    };

    private final Runnable refresher = this::refreshDevices;
    /**
     * 每秒刷新一次设备列表。用后台定时器而不是界面 Handler 链：
     * 部分系统（HyperOS）会只调 onPause 不再调 onResume，以前的刷新链就此断掉，页面不切换就永远不更新。
     */
    private java.util.concurrent.ScheduledExecutorService ticker;
    private int ticks;

    private synchronized void startTicker(String why) {
        if (ticker != null) return;
        android.util.Log.i("FrNet", "开始刷新设备列表（" + why + "）");
        ticker = java.util.concurrent.Executors.newSingleThreadScheduledExecutor();
        ticker.scheduleWithFixedDelay(() -> {
            ticks++;
            h.post(refresher);
        }, 0, 1, java.util.concurrent.TimeUnit.SECONDS);
    }

    private synchronized void stopTicker(String why) {
        if (ticker == null) return;
        android.util.Log.i("FrNet", "停止刷新设备列表（" + why + "）");
        ticker.shutdownNow();
        ticker = null;
    }

    @Override
    protected void onStart() {
        super.onStart();
        if (stoppedAt > 0) FrNet.onForeground(System.currentTimeMillis() - stoppedAt);
        stoppedAt = 0;
        if (cfg != null && cfg.isConfigured()) startTicker("onStart");
    }

    @Override
    protected void onStop() {
        super.onStop();
        stoppedAt = System.currentTimeMillis();
        stopTicker("onStop");
    }

    private long stoppedAt;

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus && cfg != null && cfg.isConfigured()) startTicker("获得焦点");
    }

    private long busySince;

    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        ui = new Ui(this);
        ui.applyWindow(this);
        cfg = new FrConfig(this);
        FrHub.get(this).addListener(netListener);
        bindService(new Intent(this, ComputerManagerService.class), conn, Context.BIND_AUTO_CREATE);
        if (cfg.isConfigured()) showMain(); else showSetup();
        FrUpdate.autoCheck(this); // 每天最多查一次 GitHub 上的新版本
        // 调试版测试入口：adb shell am start ... --es fr_debug_files <ip>
        String dbg = getIntent().getStringExtra("fr_debug_files");
        if (com.limelight.BuildConfig.DEBUG && dbg != null) {
            FrNet.Peer p = new FrNet.Peer(); p.ip = dbg; p.name = "测试电脑"; openFiles(p);
        }
    }

    @Override
    protected void onResume() {
        super.onResume();
        if (!cfg.isConfigured()) return;
        if (!FrNet.isRunning()) requestVpnThenStart();
        if (cfg.isConfigured()) FrNetService.start(this);
        startTicker("onResume");
        // 画面里切换了被控端屏幕：对方 Sunshine 已重启，自动重新连接
        String again = FrNet.toVirtual(FrStreamHooks.takePendingReconnect()); // 免 VPN 模式下画面里记的是 127.144.144.N，换回虚拟 IP
        if (again != null) { autoRetryIp = again; autoRetries = 3; }
        if (again != null) h.postDelayed(() -> {
            for (FrNet.Peer p : lastPeers) if (p.ip.equals(again)) { connect(p); return; }
            FrNet.Peer p = new FrNet.Peer(); p.ip = again; p.name = again; connect(p);
        }, 2500); // 等上一个画面会话在电脑上完全结束
    }

    /** 画面里切换画质 / 屏幕后的自动重连：失败就隔几秒再试，不弹“连接失败” */
    private String autoRetryIp;
    private int autoRetries;

    @Override
    protected void onPause() {
        super.onPause();
    }

    @Override
    protected void onDestroy() {
        super.onDestroy();
        FrHub.get(this).removeListener(netListener);
        if (binder != null) binder.stopPolling();
        unbindService(conn);
    }

    // =============================== 首次设置 ===============================
    private void showSetup() {
        LinearLayout root = ui.col();
        root.setPadding(ui.dp(20), ui.dp(56), ui.dp(20), ui.dp(24));

        android.widget.ImageView logo = new android.widget.ImageView(this);
        logo.setImageResource(R.mipmap.ic_launcher);
        root.addView(logo, new LinearLayout.LayoutParams(ui.dp(72), ui.dp(72)));
        TextView t = ui.title("Farloop");
        t.setPadding(0, ui.dp(20), 0, 0);
        root.addView(t);
        TextView s = ui.text("用手机远程控制你的电脑", 15, ui.sub, false);
        s.setPadding(0, ui.dp(8), 0, ui.dp(28));
        root.addView(s);

        LinearLayout box = ui.card();
        box.setPadding(ui.dp(18), ui.dp(18), ui.dp(18), ui.dp(18));
        box.addView(ui.text("邀请码", 15, ui.text, true));
        TextView hint = ui.text("在电脑上打开 Farloop，设备页右上角点“复制邀请码”", 13, ui.sub, false);
        hint.setPadding(0, ui.dp(6), 0, ui.dp(12));
        box.addView(hint);
        EditText code = input("FR1-…");
        box.addView(code);
        TextView paste = ui.softPill("从剪贴板粘贴");
        paste.setOnClickListener(v -> {
            ClipboardManager cm = (ClipboardManager) getSystemService(CLIPBOARD_SERVICE);
            if (cm.hasPrimaryClip() && cm.getPrimaryClip().getItemCount() > 0) {
                CharSequence x = cm.getPrimaryClip().getItemAt(0).coerceToText(this);
                if (x != null) code.setText(x.toString().trim());
            }
        });
        box.addView(paste, ui.lp(ViewGroup.LayoutParams.MATCH_PARENT, ui.dp(44), 0, 12, 0, 0));
        root.addView(box, ui.match(0, 12));

        LinearLayout box2 = ui.card();
        box2.setPadding(ui.dp(18), ui.dp(18), ui.dp(18), ui.dp(18));
        box2.addView(ui.text("本机名称", 15, ui.text, true));
        EditText name = input("");
        name.setText(cfg.deviceName());
        box2.addView(name, ui.match(12, 0));
        root.addView(box2, ui.match(0, 24));

        TextView join = ui.pill("加入网络");
        join.setOnClickListener(v -> {
            String[] inv = FrConfig.parseInvite(code.getText().toString());
            if (inv == null) { toast("邀请码格式不对，请完整复制（以 FR1- 开头）"); return; }
            cfg.save(inv[0], inv[1], name.getText().toString());
            showMain();
            requestVpnThenStart();
        });
        root.addView(join, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ui.dp(52)));
        setContentView(scroll(root));
    }

    private EditText input(String hint) {
        EditText e = new EditText(this);
        e.setHint(hint);
        e.setHintTextColor(ui.sub);
        e.setSingleLine(true);
        e.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        e.setTextColor(ui.text);
        e.setTextSize(16);
        e.setPadding(ui.dp(16), ui.dp(14), ui.dp(16), ui.dp(14));
        e.setBackground(Ui.round(ui.card2, ui.dp(16)));
        return e;
    }

    // =============================== 主界面 + 底部标签 ===============================
    private void showMain() {
        // 首次设置完成（输入邀请码加入网络）时也要开始刷新：以前只在页面 onStart / onResume 时开始，
        // 而那时还没加入网络，结果要切到后台再回来列表才会动
        if (cfg.isConfigured()) { startTicker("进入首页"); FrNetService.start(this); }
        LinearLayout root = ui.col();
        root.setBackgroundColor(ui.bg);
        content = new FrameLayout(this);
        root.addView(content, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1));

        tabBar = ui.row();
        tabBar.setBackgroundColor(ui.card);
        tabBar.setPadding(0, ui.dp(8), 0, ui.dp(10));
        String[] labels = {"设备", "文件", "我的"};
        int[] icons = {R.drawable.fr_ic_desktop, R.drawable.fr_ic_folder, R.drawable.fr_ic_settings};
        for (int i = 0; i < 3; i++) {
            final int idx = i;
            LinearLayout cell = ui.col();
            cell.setGravity(Gravity.CENTER);
            cell.addView(ui.icon(icons[i], ui.sub, 26));
            TextView l = ui.text(labels[i], 11.5f, ui.sub, false);
            l.setGravity(Gravity.CENTER);
            l.setPadding(0, ui.dp(4), 0, 0);
            cell.addView(l);
            cell.setOnClickListener(v -> selectTab(idx));
            tabBar.addView(cell, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        }
        root.addView(tabBar);
        setContentView(root);
        selectTab(tab);
    }

    private void selectTab(int t) {
        tab = t;
        for (int i = 0; i < tabBar.getChildCount(); i++) {
            LinearLayout cell = (LinearLayout) tabBar.getChildAt(i);
            int c = i == t ? ui.accent : ui.sub;
            ((ImageView) cell.getChildAt(0)).setImageTintList(android.content.res.ColorStateList.valueOf(c));
            ((TextView) cell.getChildAt(1)).setTextColor(c);
        }
        content.removeAllViews();
        list = null;
        status = null;
        if (t == TAB_ME) content.addView(meTab());
        else content.addView(listTab(t == TAB_DEVICES ? "设备" : "文件"));
        render(lastPeers);
    }

    private View listTab(String title) {
        LinearLayout root = ui.col();
        root.setPadding(ui.dp(16), ui.dp(28), ui.dp(16), ui.dp(16));
        TextView t = ui.title(title);
        t.setPadding(ui.dp(4), 0, 0, 0);
        root.addView(t);
        TextView me = ui.text(cfg.deviceName() + " · " + cfg.virtualIp(), 13, ui.sub, false);
        me.setPadding(ui.dp(4), ui.dp(8), 0, ui.dp(16));
        root.addView(me);

        // 组网状态条
        LinearLayout bar = ui.row();
        bar.setBackground(Ui.round(ui.card, ui.dp(18)));
        bar.setPadding(ui.dp(16), ui.dp(14), ui.dp(16), ui.dp(14));
        View dot = new View(this);
        dot.setBackground(Ui.round(ui.green, ui.dp(4)));
        bar.addView(dot, new LinearLayout.LayoutParams(ui.dp(8), ui.dp(8)));
        status = ui.text("正在连接组网…", 14, ui.text, false);
        status.setPadding(ui.dp(10), 0, 0, 0);
        bar.addView(status, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        status.setTag(dot);
        root.addView(bar, ui.match(0, 14));

        if (title.equals("文件")) {
            TextView hint = ui.text("选择一台电脑，左边手机、右边电脑，像 MT 管理器一样直接复制文件，不用打开远程画面。", 13, ui.sub, false);
            hint.setPadding(ui.dp(4), 0, ui.dp(4), ui.dp(12));
            root.addView(hint);
        }
        list = ui.col();
        root.addView(list);
        return scroll(root);
    }

    private View meTab() {
        LinearLayout root = ui.col();
        root.setPadding(ui.dp(16), ui.dp(28), ui.dp(16), ui.dp(24));
        TextView t = ui.title("我的");
        t.setPadding(ui.dp(4), 0, 0, ui.dp(16));
        root.addView(t);

        // 本机卡片
        LinearLayout meCard = ui.card();
        LinearLayout r = ui.row();
        r.setPadding(ui.dp(18), ui.dp(18), ui.dp(18), ui.dp(18));
        r.addView(ui.iconTile(R.drawable.fr_ic_phone, Color.WHITE, ui.accent, 52, 28));
        LinearLayout txt = ui.col();
        txt.setPadding(ui.dp(14), 0, 0, 0);
        txt.addView(ui.text(cfg.deviceName(), 19, ui.text, true));
        TextView ip = ui.text("虚拟 IP " + cfg.virtualIp() + "  ·  Farloop " + FrConfig.VERSION, 12.5f, ui.sub, false);
        ip.setPadding(0, ui.dp(6), 0, 0);
        txt.addView(ip);
        r.addView(txt, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        meCard.addView(r);
        root.addView(meCard);

        root.addView(ui.section("连接"));
        Ui.Switch relay = new Ui.Switch(ui, cfg.forceRelay(), on -> {
            cfg.setForceRelay(on);
            toast(on ? "已切换为仅中转，正在重连组网" : "已切换为 P2P 优先，正在重连组网");
            restartNet();
        });
        LinearLayout rowRelay = ui.settingRow(R.drawable.fr_ic_route, ui.orange, "仅中转", "P2P 不稳定时打开；所有流量经中转节点", relay);
        Ui.Switch vpn = new Ui.Switch(ui, cfg.useVpn(), on -> {
            cfg.setUseVpn(on);
            toast(on ? "已切换为 VPN 模式，正在重连组网" : "已切换为免 VPN 模式，正在重连组网");
            if (on) requestVpnThenStart(); else restartNet();
        });
        LinearLayout rowVpn = ui.settingRow(R.drawable.fr_ic_route, ui.accent, "使用 VPN 模式", "默认关闭：不占用系统 VPN，可和其他 VPN 软件同时用。连接异常时可打开试试", vpn);
        Ui.Switch p2pOnly = new Ui.Switch(ui, cfg.p2pOnly(), on -> {
            cfg.setP2pOnly(on);
            toast(on ? "已切换为仅 P2P，正在重连组网" : "已允许中转，正在重连组网");
            restartNet();
        });
        LinearLayout rowP2p = ui.settingRow(R.drawable.fr_ic_route, ui.green, "仅 P2P", "数据只走直连、不经中转；打洞不成功时会连不上", p2pOnly);
        LinearLayout rowRule = ui.settingRow(R.drawable.fr_ic_edit, ui.accent, "打洞用的公网 IP",
                cfg.ipRule().isEmpty() ? "自动（多条宽带时可指定，例如 203.0）" : "只用 " + cfg.ipRule() + ".*", ui.chevron());
        rowRule.setOnClickListener(v -> editIpRule());
        LinearLayout rowDiag = ui.settingRow(R.drawable.fr_ic_speed, ui.orange, "网络诊断", "NAT 类型、公网 IP、每台电脑是 P2P 还是中转", ui.chevron());
        rowDiag.setOnClickListener(v -> showDiagnose());
        LinearLayout rowLog = ui.settingRow(R.drawable.fr_ic_file, ui.sub, "导出诊断日志", "连接有问题时，把日志发给开发者", ui.chevron());
        rowLog.setOnClickListener(v -> exportLog());
        LinearLayout rowUpdate = ui.settingRow(R.drawable.fr_ic_refresh, ui.accent, "检查更新", "当前 " + FrConfig.VERSION + " · 从 GitHub 下载新版本", ui.chevron());
        rowUpdate.setOnClickListener(v -> FrUpdate.check(this, true));
        LinearLayout rowNet = ui.settingRow(R.drawable.fr_ic_refresh, ui.green, "重新连接组网", null, ui.chevron());
        rowNet.setOnClickListener(v -> { toast("正在重连组网"); restartNet(); });
        android.content.SharedPreferences fsp = getSharedPreferences("freeremote", MODE_PRIVATE);
        Ui.Switch keep = new Ui.Switch(ui, fsp.getBoolean("keepAlive", true), on -> {
            fsp.edit().putBoolean("keepAlive", on).apply();
            if (on) FrNetService.start(this); else FrNetService.stop(this);
        });
        LinearLayout rowKeep = ui.settingRow(R.drawable.fr_ic_power, ui.green, "后台保持组网", "切到后台也保持连接，打通的 P2P 不会丢，打开 App 就能直接连（通知栏会有一条常驻通知）", keep);
        root.addView(ui.group(rowRelay, rowP2p, rowVpn, rowKeep, rowRule, rowDiag, rowLog, rowNet, rowUpdate));

        root.addView(ui.section("外观"));
        LinearLayout themeCard = ui.card();
        themeCard.setPadding(ui.dp(12), ui.dp(12), ui.dp(12), ui.dp(12));
        themeCard.addView(ui.segmented(new String[]{"跟随系统", "浅色", "深色"}, FrTheme.mode(this), i -> {
            if (i == FrTheme.mode(this)) return;
            FrTheme.setMode(this, i);
            h.postDelayed(this::recreate, 180);
        }, ui.card2, ui.accent, ui.text, android.graphics.Color.WHITE));
        root.addView(themeCard);

        root.addView(ui.section("画质"));
        LinearLayout qCard = ui.card();
        qCard.setPadding(ui.dp(12), ui.dp(12), ui.dp(12), ui.dp(12));
        qCard.addView(ui.segmented(FrStreamHooks.QUALITY_NAMES, FrStreamHooks.quality(this), i -> FrStreamHooks.setQuality(this, i),
                ui.card2, ui.accent, ui.text, android.graphics.Color.WHITE));
        TextView qd = ui.text(FrStreamHooks.QUALITY_DESC, 12f, ui.sub, false);
        qd.setPadding(ui.dp(4), ui.dp(10), ui.dp(4), 0);
        qd.setLineSpacing(ui.dp(2), 1f);
        qCard.addView(qd);
        root.addView(qCard);

        root.addView(ui.section("画面与操控"));
        LinearLayout rowStream = ui.settingRow(R.drawable.fr_ic_tune, ui.accent, "画面与操控设置", "分辨率、帧率、码率、编码、触控灵敏度等", ui.chevron());
        rowStream.setOnClickListener(v -> startActivity(new Intent(this, StreamSettings.class)));
        root.addView(ui.group(rowStream));

        root.addView(ui.section("账号"));
        TextView leave = ui.text("退出网络", 16, ui.red, false);
        LinearLayout rowLeave = ui.settingRow(R.drawable.fr_ic_exit, ui.red, "退出网络", "清除邀请码，需要重新输入才能连接", null);
        rowLeave.setOnClickListener(v -> new AlertDialog.Builder(this).setMessage("退出后需要重新输入邀请码才能连接。确定？")
                .setPositiveButton("退出", (dd, w) -> { FrNet.stop(this); FrNetService.stop(this); cfg.clear(); showSetup(); })
                .setNegativeButton("取消", null).show());
        root.addView(ui.group(rowLeave));
        return scroll(root);
    }

    private void editIpRule() {
        EditText e = input("例如 203.0（留空 = 自动）");
        e.setText(cfg.ipRule());
        e.setInputType(InputType.TYPE_CLASS_PHONE);
        LinearLayout box = ui.col();
        box.setPadding(ui.dp(20), ui.dp(8), ui.dp(20), 0);
        TextView tip = ui.text("家里是多条宽带（有多个公网 IP）时，打洞可能一直失败。填公网 IP 的前面几段，只用匹配的那个 IP 打洞。"
                + "运营商的动态 IP 前两段一般不变，填前两段即可。手机这边一般不用填，要在多宽带的那台电脑上设置。", 13, ui.sub, false);
        tip.setLineSpacing(ui.dp(3), 1f);
        box.addView(tip);
        box.addView(e, ui.match(ui.dp(12), 0));
        new AlertDialog.Builder(this).setTitle("打洞用的公网 IP").setView(box)
                .setPositiveButton("保存", (d, w) -> {
                    String r = e.getText().toString().trim();
                    if (!r.isEmpty() && !r.matches("[0-9]{1,3}(\\.[0-9]{1,3}){0,3}(\\.\\*)?")) { toast("格式不对，例如 203.0"); return; }
                    cfg.setIpRule(r.replace(".*", ""));
                    toast("已保存，正在重连组网");
                    restartNet();
                    selectTab(TAB_ME);
                })
                .setNegativeButton("取消", null).show();
    }

    /** 导出本 App 的日志（含网络诊断），通过系统分享发出去 */
    private void exportLog() {
        toast("正在收集日志…");
        new Thread(() -> {
            StringBuilder sb = new StringBuilder();
            sb.append("Farloop ").append(FrConfig.VERSION).append("  ").append(android.os.Build.MANUFACTURER).append(' ').append(android.os.Build.MODEL)
                    .append("  Android ").append(android.os.Build.VERSION.RELEASE).append('\n');
            sb.append("虚拟 IP ").append(cfg.virtualIp()).append("  已知电脑 ").append(cfg.knownHosts()).append("\n\n");
            sb.append(FrNet.diagnose(cfg.virtualIp())).append("\n\n");
            try {
                Process pr = Runtime.getRuntime().exec(new String[]{"logcat", "-d", "-v", "time", "--uid=" + android.os.Process.myUid()});
                java.io.BufferedReader r = new java.io.BufferedReader(new java.io.InputStreamReader(pr.getInputStream()));
                java.util.ArrayDeque<String> last = new java.util.ArrayDeque<>();
                for (String line; (line = r.readLine()) != null; ) {
                    if (line.contains("FrNet") || line.contains("FrHub") || line.contains("FrSession") || line.contains("Farloop")
                            || line.contains("easytier") || line.contains("EasyTier") || line.contains("AndroidRuntime") || line.contains(" E/")) {
                        last.add(line);
                        if (last.size() > 1500) last.poll();
                    }
                }
                for (String l : last) sb.append(l).append('\n');
            } catch (Exception e) { sb.append("读取日志失败：").append(e); }
            String text = sb.toString();
            h.post(() -> {
                Intent i = new Intent(Intent.ACTION_SEND);
                i.setType("text/plain");
                i.putExtra(Intent.EXTRA_SUBJECT, "Farloop 诊断日志");
                i.putExtra(Intent.EXTRA_TEXT, text.length() > 90_000 ? text.substring(text.length() - 90_000) : text);
                try { startActivity(Intent.createChooser(i, "发送诊断日志")); } catch (Exception e) { toast("没有可以分享的应用"); }
            });
        }).start();
    }

    private void showDiagnose() {
        TextView t = ui.text("正在读取…", 13.5f, ui.text, false);
        t.setTextIsSelectable(true);
        t.setLineSpacing(ui.dp(3), 1f);
        t.setPadding(ui.dp(22), ui.dp(8), ui.dp(22), 0);
        android.widget.ScrollView sv = new android.widget.ScrollView(this);
        sv.addView(t);
        new AlertDialog.Builder(this).setTitle("网络诊断").setView(sv).setPositiveButton("好", null).show();
        new Thread(() -> { String s = FrNet.diagnose(cfg.virtualIp()); h.post(() -> t.setText(s)); }).start();
    }

    private void restartNet() {
        new Thread(() -> {
            FrNet.stop(this);
            boolean ok = FrNet.start(this);
            h.post(() -> { if (!ok) toast(FrNet.lastError()); });
        }).start();
    }

    // =============================== 组网 / VPN ===============================
    private void requestVpnThenStart() {
        if (!cfg.useVpn()) { onActivityResult(REQ_VPN, RESULT_OK, null); return; } // 免 VPN 模式：不申请 VPN
        Intent i = VpnService.prepare(this);
        if (i != null) startActivityForResult(i, REQ_VPN);
        else onActivityResult(REQ_VPN, RESULT_OK, null);
    }

    @Override
    protected void onActivityResult(int req, int res, Intent data) {
        super.onActivityResult(req, res, data);
        if (req != REQ_VPN) return;
        if (res != RESULT_OK) {
            if (status != null) status.setText("需要允许“VPN 连接”才能加入组网（只用于 Farloop 的虚拟网段，不影响其他上网）");
            return;
        }
        new Thread(() -> {
            boolean ok = FrNet.start(this);
            h.post(() -> { if (!ok && status != null) status.setText(FrNet.lastError()); else { selectTab(tab); refreshDevices(); } }); // 启动时可能换了虚拟 IP，重建页面让标题里的 IP 也更新
        }).start();
    }

    private final java.util.Map<String, FrNet.Peer> recent = new java.util.concurrent.ConcurrentHashMap<>();
    private final java.util.Map<String, Long> recentAt = new java.util.concurrent.ConcurrentHashMap<>();
    /** 从什么时候开始一直是中转（用于显示打洞进度） */
    private final java.util.Map<String, Long> relaySince = new java.util.concurrent.ConcurrentHashMap<>();
    private static final long PUNCH_EXPECT_MS = 90_000, PUNCH_GIVEUP_MS = 240_000;

    private final java.util.concurrent.atomic.AtomicBoolean refreshing = new java.util.concurrent.atomic.AtomicBoolean();

    private void refreshDevices() {
        if (!refreshing.compareAndSet(false, true)) {
            // 上一次还没读完（组网启动中会慢）：不叠加，但状态行照样更新
            long b = System.currentTimeMillis() - busySince;
            if (b > 10_000 && (b / 1000) % 10 < 2) android.util.Log.w("FrNet", "读取组网状态已卡 " + b / 1000 + " 秒");
            render(lastPeers);
            return;
        }
        busySince = System.currentTimeMillis();
        new Thread(() -> {
          long t0 = System.currentTimeMillis();
          try {
            List<FrNet.Peer> now = FrNet.isRunning() ? FrNet.peers(cfg.virtualIp()) : Collections.emptyList();
            // 虚拟 IP 和别的设备撞了：换一个没人用的号，重连组网
            if (FrNet.takeIpConflict() && !busy) {
                java.util.Set<String> used = new java.util.HashSet<>();
                for (FrNet.Peer p : now) used.add(p.ip);
                final String nip = cfg.freshIp(used);
                cfg.setVirtualIp(nip);
                h.post(() -> toast("虚拟 IP 和别的设备重复了，已自动换成 " + nip + "，正在重连"));
                FrNet.stop(this);
                FrNet.start(this);
                relaySince.clear();
                return;
            }
            // 组网重建 / 网络抖动时路由会短暂消失：最近 60 秒见过的电脑继续显示，不让列表一会儿有一会儿没有
            long t = System.currentTimeMillis();
            java.util.Set<String> seen = new java.util.HashSet<>();
            for (FrNet.Peer p : now) {
                seen.add(p.ip); recent.put(p.ip, p); recentAt.put(p.ip, t);
                if (p.p2p) relaySince.remove(p.ip); else relaySince.putIfAbsent(p.ip, t);
            }
            List<FrNet.Peer> peers = new java.util.ArrayList<>(now);
            if (FrNet.isRunning())
                for (java.util.Map.Entry<String, FrNet.Peer> e : recent.entrySet())
                    if (!seen.contains(e.getKey()) && t - recentAt.get(e.getKey()) < 60_000) peers.add(e.getValue());
            peers.sort((a, b) -> a.name.compareToIgnoreCase(b.name));
            h.post(() -> { lastPeers = peers; render(peers); });
          } catch (Throwable e) {
            android.util.Log.e("FrNet", "刷新设备列表失败", e);
            h.post(() -> render(lastPeers));
          } finally {
            long dt = System.currentTimeMillis() - t0;
            if (dt > 3000) android.util.Log.w("FrNet", "刷新设备列表用了 " + dt + " ms");
            refreshing.set(false);
          }
        }, "FrRefresh").start();
    }

    private void render(List<FrNet.Peer> peers) {
        if (status != null) {
            boolean ok = FrNet.isRunning();
            String s = !ok ? (FrNet.lastError().isEmpty() ? "组网未运行" : FrNet.lastError())
                    : peers.isEmpty() ? (FrNet.relayNodes == 0
                        ? "正在连接中转节点… " + (System.currentTimeMillis() - FrNet.startedAt) / 1000 + " 秒"
                        : "已连接 " + FrNet.relayNodes + " 个中转节点，正在找电脑… " + (System.currentTimeMillis() - FrNet.startedAt) / 1000 + " 秒\n（电脑需开着 Farloop；一般几秒内出现）")
                    : "组网正常 · 在线 " + peers.size() + " 台" + (cfg.forceRelay() ? " · 仅中转" : "");
            status.setText(s);
            Object dot = status.getTag();
            if (dot instanceof View) ((View) dot).setBackground(Ui.round(ok ? (peers.isEmpty() ? ui.orange : ui.green) : ui.red, ui.dp(4)));
        }
        if (list == null) return;
        list.removeAllViews();
        for (FrNet.Peer p : peers) list.addView(tab == TAB_FILES ? fileCard(p) : deviceCard(p), ui.match(0, 12));
        if (tab != TAB_FILES) list.addView(assistCard(), ui.match(0, 12));
    }

    // =============================== 远程协助（设备码 + 临时密码，FrAssist） ===============================
    private View assistCard() {
        LinearLayout card = ui.col();
        card.setBackground(Ui.round(ui.card, ui.dp(24)));
        card.setPadding(ui.dp(20), ui.dp(16), ui.dp(20), ui.dp(16));
        card.addView(ui.text("远程协助陌生电脑", 17, ui.text, true));
        TextView sub = ui.text("对方不在你的组网里也能连：输入对方 Farloop “协助”页上的设备码和临时密码", 13.5f, ui.sub, false);
        sub.setPadding(0, ui.dp(4), 0, 0);
        card.addView(sub);
        card.setOnClickListener(v -> askAssist());
        return card;
    }

    private void askAssist() {
        LinearLayout box = ui.col();
        box.setPadding(ui.dp(22), ui.dp(8), ui.dp(22), 0);
        EditText id = new EditText(this);
        id.setHint("对方的设备码（9 位数字）");
        id.setInputType(android.text.InputType.TYPE_CLASS_NUMBER);
        EditText pw = new EditText(this);
        pw.setHint("临时密码");
        pw.setInputType(android.text.InputType.TYPE_CLASS_TEXT | android.text.InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD);
        box.addView(id);
        box.addView(pw);
        new AlertDialog.Builder(this)
                .setTitle("远程协助")
                .setMessage("对方点“允许”后就能看到并操作他的电脑；关闭画面即结束协助。")
                .setView(box)
                .setPositiveButton("连接", (d, w) -> connectAssist(id.getText().toString(), pw.getText().toString()))
                .setNegativeButton("取消", null)
                .show();
    }

    private void connectAssist(String idText, String pwText) {
        String id = FrAssist.normalizeId(idText);
        String pw = FrAssist.normalizePassword(pwText);
        if (id.length() != 9) { toast("设备码是 9 位数字"); return; }
        if (pw.length() < 4) { toast("请输入对方的临时密码"); return; }
        if (busy) return;
        if (!FrNet.isRunning()) { toast("组网还没启动，请稍等"); return; }
        busy = true;
        Busy pd = new Busy("远程协助");
        new Thread(() -> {
            String err = FrAssist.start(this, id, pw);
            String name = null;
            boolean[] p2p = {false};
            for (int i = 1; err == null && i <= 45 && name == null; i++) {
                pd.step("正在寻找对方电脑… " + i + " 秒");
                try { Thread.sleep(1000); } catch (InterruptedException ignored) { }
                name = FrAssist.findHost(p2p);
            }
            if (err == null && name == null) err = "连不上对方：请确认设备码和密码都对，并且对方电脑开着 Farloop、开启了“允许别人协助我”";
            final String fe = err, fn = name;
            final boolean fp = p2p[0];
            h.post(() -> {
                pd.dismiss();
                busy = false;
                if (fe != null) {
                    FrAssist.stop();
                    new AlertDialog.Builder(this).setTitle("远程协助").setMessage(fe).setPositiveButton("好", null).show();
                    return;
                }
                toast("找到了 " + fn + "（" + (fp ? "P2P 直连" : "中转") + "），等对方点“允许”…");
                FrNet.Peer p = new FrNet.Peer();
                p.ip = FrAssist.HOST_IP;
                p.name = fn;
                p.p2p = fp;
                connect(p);
            });
        }, "FrAssist").start();
    }

    // ---- 电脑多条宽带：首页卡片上直接显示“打洞线路”，一键选推荐 / 更改 ----
    /** ip → 电脑最近一次的 SysInfo（只用其中的网络字段） */
    private final java.util.Map<String, com.google.gson.JsonObject> netInfo = new java.util.concurrent.ConcurrentHashMap<>();
    private final java.util.Set<String> netAsked = java.util.concurrent.ConcurrentHashMap.newKeySet();
    private final FrHub.Listener netListener = (ip, type, payload) -> {
        if (type != FrHub.SYS_INFO) return;
        try {
            com.google.gson.JsonObject j = com.google.gson.JsonParser.parseString(new String(payload, java.nio.charset.StandardCharsets.UTF_8)).getAsJsonObject();
            netInfo.put(FrNet.toVirtual(ip), j);
            h.post(() -> render(lastPeers));
        } catch (Exception e) { android.util.Log.w("FrNet", "读取电脑网络信息失败", e); }
    };

    private void askNetInfo(String ip) {
        if (!netAsked.add(ip)) return;
        new Thread(() -> {
            FrHub hub = FrHub.get(this);
            if (hub.supportsTools(ip)) hub.send(ip, FrHub.SYS_INFO_REQ, new byte[]{0});
            else h.postDelayed(() -> netAsked.remove(ip), 15_000);
        }).start();
    }

    private static String js(com.google.gson.JsonObject j, String k) {
        return j != null && j.has(k) && !j.get(k).isJsonNull() ? j.get(k).getAsString() : "";
    }

    /** 卡片上的“打洞线路”一行：只有电脑有多条宽带时才出现 */
    private View netRow(FrNet.Peer p) {
        com.google.gson.JsonObject j = netInfo.get(p.ip);
        if (j == null || !j.has("PublicIps") || !j.get("PublicIps").isJsonArray() || j.getAsJsonArray("PublicIps").size() < 2) return null;
        com.google.gson.JsonArray ips = j.getAsJsonArray("PublicIps");
        String cur = js(j, "IpRule"), rec = js(j, "SuggestedRule");
        LinearLayout row = ui.row();
        row.setBackground(Ui.round(ui.card2, ui.dp(14)));
        row.setPadding(ui.dp(12), ui.dp(8), ui.dp(8), ui.dp(8));
        row.setGravity(Gravity.CENTER_VERTICAL);
        String label = cur.isEmpty() ? "电脑有 " + ips.size() + " 条宽带 · 打洞线路：自动"
                : "打洞线路：只用 " + cur + ".*" + (cur.equals(rec) ? "（推荐）" : "");
        TextView t = ui.text(label, 13f, ui.sub, false);
        row.addView(t, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        boolean suggest = cur.isEmpty() && !rec.isEmpty();
        TextView b = ui.text(suggest ? "用推荐线路" : "更改", 13f, suggest ? Color.WHITE : ui.accent, true);
        b.setBackground(Ui.round(suggest ? ui.accent : ui.accentSoft, ui.dp(12)));
        b.setPadding(ui.dp(12), ui.dp(6), ui.dp(12), ui.dp(6));
        b.setOnClickListener(v -> { if (suggest) setRule(p.ip, rec); else pickRule(p.ip, ips, cur, rec); });
        row.addView(b);
        row.setOnClickListener(v -> pickRule(p.ip, ips, cur, rec));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.topMargin = ui.dp(12);
        row.setLayoutParams(lp);
        return row;
    }

    private void pickRule(String ip, com.google.gson.JsonArray ips, String cur, String rec) {
        java.util.List<String> labels = new java.util.ArrayList<>(), rules = new java.util.ArrayList<>();
        for (com.google.gson.JsonElement e : ips) {
            String r = DeviceActivity.ruleFor(e.getAsString(), ips);
            if (rules.contains(r)) continue;
            rules.add(r);
            labels.add((r.equals(cur) ? "✓ " : "") + "只用 " + r + ".*" + (r.equals(rec) ? "（推荐，主线路）" : ""));
        }
        rules.add("");
        labels.add((cur.isEmpty() ? "✓ " : "") + "自动（所有线路都用）");
        new AlertDialog.Builder(this).setTitle("打洞用哪条宽带")
                .setItems(labels.toArray(new String[0]), (d, w) -> { if (!rules.get(w).equals(cur)) setRule(ip, rules.get(w)); })
                .show();
    }

    private void setRule(String ip, String rule) {
        new AlertDialog.Builder(this)
                .setTitle(rule.isEmpty() ? "打洞线路改为自动" : "只用 " + rule + ".* 打洞")
                .setMessage("电脑会重启组网，连接断开约 10 秒，然后自动重连并重新打洞。")
                .setPositiveButton("设置", (d, w) -> new Thread(() -> {
                    com.google.gson.JsonObject m = new com.google.gson.JsonObject();
                    m.addProperty("Rule", rule);
                    String err = FrHub.get(this).sendJson(ip, FrHub.SET_IP_RULE, m);
                    com.google.gson.JsonObject j = netInfo.get(ip);
                    if (err == null && j != null) j.addProperty("IpRule", rule);
                    h.post(() -> { toast(err == null ? "已设置，电脑正在重连组网" : "设置失败：" + err); render(lastPeers); });
                    // 电脑重连后再读一次，显示它实际生效的规则
                    h.postDelayed(() -> netAsked.remove(ip), 20_000);
                }).start())
                .setNegativeButton("取消", null).show();
    }

    /** 中转时显示的打洞进度：细进度条 + 一句说明；打不通时说明原因，点一下看网络诊断 */
    private View punchStatus(FrNet.Peer p) {
        long since = relaySince.containsKey(p.ip) ? relaySince.get(p.ip) : System.currentTimeMillis();
        long el = System.currentTimeMillis() - since;
        LinearLayout box = ui.col();
        box.setBackground(Ui.round(ui.card2, ui.dp(14)));
        box.setPadding(ui.dp(12), ui.dp(10), ui.dp(12), ui.dp(10));
        String msg;
        if (cfg.forceRelay()) msg = "已开启“仅中转”，不会尝试 P2P";
        else if (el < PUNCH_GIVEUP_MS) {
            msg = "正在尝试 P2P 直连（打洞）· 已 " + (el / 1000) + " 秒\n现在经中转连接，可以正常使用，打通后自动切换"
                    + (FrNet.vpnBlocked ? "\n⚠ 手机开着 VPN / 代理且不允许绕过，可能打不通：请在代理 App 里把 Farloop 设为不走代理" : "");
        } else {
            boolean sym = FrNet.myNat == 6 || FrNet.myNat == 7;
            boolean peerSym = p.nat == 6 || p.nat == 7;
            String why = FrNet.vpnBlocked ? "手机开着 VPN / 代理，而且它不允许绕过。请在代理 App 里把 Farloop 设为不走代理（分应用代理 / 绕过应用），就能 P2P 直连"
                    : p.publicV4 > 1 ? "对方有 " + p.publicV4 + " 个公网 IP（多条宽带），在对方电脑上设置“打洞用的公网 IP”会更容易打通"
                    : sym && peerSym ? "双方都是对称型 NAT，基本无法直连"
                    : sym ? "本机是对称型 NAT（多见于 4G/5G），对方需要是锥形 NAT 并且公网 IP 固定"
                    : "暂时没打通，会在后台继续尝试";
            msg = "暂时没打通 P2P，正在经中转连接 · 点这里查看原因\n" + why;
        }
        // 进度条：预计 90 秒内打通，超过后停在接近满格
        float frac = cfg.forceRelay() ? 0 : Math.min(0.97f, el / (float) PUNCH_EXPECT_MS);
        LinearLayout bar = ui.row();
        bar.setBackground(Ui.round(ui.line, ui.dp(2)));
        View fill = new View(this);
        fill.setBackground(Ui.round(el < PUNCH_GIVEUP_MS ? ui.accent : ui.orange, ui.dp(2)));
        bar.addView(fill, new LinearLayout.LayoutParams(0, ui.dp(4), Math.max(0.02f, frac)));
        bar.addView(new View(this), new LinearLayout.LayoutParams(0, ui.dp(4), Math.max(0.001f, 1 - frac)));
        if (!cfg.forceRelay()) box.addView(bar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ui.dp(4)));
        TextView t = ui.text(msg, 12.5f, ui.sub, false);
        t.setLineSpacing(ui.dp(2), 1f);
        t.setPadding(0, cfg.forceRelay() ? 0 : ui.dp(8), 0, 0);
        box.addView(t);
        box.setOnClickListener(v -> showDiagnose());
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.topMargin = ui.dp(12);
        box.setLayoutParams(lp);
        return box;
    }

    private View deviceCard(FrNet.Peer p) {
        LinearLayout card = ui.card();
        card.setPadding(ui.dp(16), ui.dp(16), ui.dp(16), ui.dp(16));
        LinearLayout top = ui.row();
        int c = p.p2p ? ui.green : ui.orange;
        top.addView(ui.iconTile(R.drawable.fr_ic_desktop, Color.WHITE, ui.accent, 52, 28));
        LinearLayout txt = ui.col();
        txt.setPadding(ui.dp(14), 0, ui.dp(8), 0);
        TextView name = ui.text(p.name.isEmpty() ? p.ip : p.name, 18, ui.text, true);
        name.setSingleLine(true);
        name.setEllipsize(android.text.TextUtils.TruncateAt.END);
        txt.addView(name);
        TextView st = ui.text((p.p2p ? "P2P 直连" : "中转") + (p.latencyMs != null ? " · " + p.latencyMs + " ms" : "") + "   " + p.ip, 13, c, false);
        st.setPadding(0, ui.dp(6), 0, 0);
        txt.addView(st);
        top.addView(txt, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        TextView go = ui.pill("连接");
        go.setOnClickListener(v -> connect(p));
        top.addView(go, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ui.dp(40)));
        card.addView(top);
        if (!p.p2p) card.addView(punchStatus(p));
        askNetInfo(p.ip);
        View nr = netRow(p);
        if (nr != null) card.addView(nr);

        LinearLayout actions = ui.row();
        actions.setPadding(0, ui.dp(14), 0, 0);
        TextView files = ui.softPill("文件传输");
        files.setTextSize(13.5f);
        files.setOnClickListener(v -> openFiles(p));
        actions.addView(files, new LinearLayout.LayoutParams(0, ui.dp(38), 1));
        card.addView(actions);
        card.setOnClickListener(v -> openDevice(p)); // 点卡片：设备页（预览、终端、快速启动、详情）
        return card;
    }

    private View fileCard(FrNet.Peer p) {
        LinearLayout card = ui.card();
        LinearLayout r = ui.settingRow(R.drawable.fr_ic_folder, ui.accent, p.name.isEmpty() ? p.ip : p.name,
                (p.p2p ? "P2P 直连" : "中转") + (p.latencyMs != null ? " · " + p.latencyMs + " ms" : ""), ui.chevron());
        r.setPadding(ui.dp(16), ui.dp(18), ui.dp(14), ui.dp(18));
        r.setOnClickListener(v -> openFiles(p));
        card.addView(r);
        return card;
    }

    private void openFiles(FrNet.Peer p) {
        Intent i = new Intent(this, FileManagerActivity.class);
        i.putExtra(FileManagerActivity.EXTRA_IP, p.ip);
        i.putExtra(FileManagerActivity.EXTRA_NAME, p.name.isEmpty() ? p.ip : p.name);
        startActivity(i);
    }

    // =============================== 一键连接 ===============================
    private void connect(FrNet.Peer p) {
        if (busy) return;
        if (binder == null) { toast("还在初始化，请稍等"); return; }
        busy = true;
        Busy pd = new Busy(p.name.isEmpty() ? p.ip : p.name);
        new Thread(() -> {
            String err = null;
            try {
                // 1. FreeRemote 会话（顺便验证对方在线、网络密钥一致）
                pd.step("正在建立安全连接…");
                FrHub.get(this).session(p.ip);

                // 2. 在 Artemis 里添加这台电脑（按虚拟 IP）
                ComputerDetails d = computers.get(p.ip);
                String want = FrNet.endpoint(this, p.ip);
                if (d != null && FrAssist.isAssistIp(p.ip)) {
                    // 远程协助：这个地址上次可能是另一台电脑（证书不同），每次都重新添加
                    binder.removeComputer(d);
                    computers.remove(p.ip);
                    d = null;
                }
                if (d != null && d.manualAddress != null && !want.equals(d.manualAddress.address)) {
                    // 切换过 VPN / 免 VPN 模式：旧记录的地址连不上了，删掉重新添加
                    binder.removeComputer(d);
                    computers.remove(p.ip);
                    d = null;
                }
                if (d == null || d.uuid == null) {
                    pd.step("正在添加电脑…");
                    ComputerDetails fake = new ComputerDetails();
                    // 免 VPN 模式下连本机映射地址 127.144.144.N（端口转发到这台电脑）
                    fake.manualAddress = new ComputerDetails.AddressTuple(FrNet.endpoint(this, p.ip), NvHTTP.DEFAULT_HTTP_PORT);
                    if (!binder.addComputerBlocking(fake)) throw new Exception("找不到对方的串流服务（对方需要开启“允许被远程”）");
                    for (int i = 0; i < 50 && ((d = computers.get(p.ip)) == null || d.uuid == null); i++) Thread.sleep(200);
                    if (d == null || d.uuid == null) throw new Exception("添加电脑超时");
                }

                // 3. 配对：Moonlight 生成 PIN 等待对方确认，同时经 FreeRemote 会话把 PIN 交给对方自动确认
                NvHTTP http = new NvHTTP(ServerHelper.getCurrentAddressFromComputer(d), d.httpsPort,
                        binder.getUniqueId(), d.serverCert, PlatformBinding.getCryptoProvider(this));
                if (http.getPairState() != PairingManager.PairState.PAIRED) {
                    pd.step("首次连接，正在自动配对…");
                    String pin = PairingManager.generatePinString();
                    PairingManager pm = http.getPairingManager();
                    final String[] pinErr = {null};
                    Thread sub = new Thread(() -> {
                        try { Thread.sleep(1500); } catch (InterruptedException ignored) { }
                        pinErr[0] = FrHub.get(this).submitPin(p.ip, pin);
                    });
                    sub.start();
                    PairingManager.PairState ps = pm.pair(http.getServerInfo(true), pin, null);
                    sub.join(50000);
                    if (ps != PairingManager.PairState.PAIRED)
                        throw new Exception(pinErr[0] != null ? pinErr[0] : "配对失败（" + ps + "）");
                    ComputerDetails cur = binder.getComputer(d.uuid);
                    if (cur != null) { cur.serverCert = pm.getPairedCert(); d = cur; }
                    else d.serverCert = pm.getPairedCert();
                    binder.invalidateStateForComputer(d.uuid);
                    cfg.setPaired(p.ip, true);
                    http = new NvHTTP(ServerHelper.getCurrentAddressFromComputer(d), d.httpsPort,
                            binder.getUniqueId(), d.serverCert, PlatformBinding.getCryptoProvider(this));
                }

                // 4. 打开“桌面”
                pd.step("正在打开画面…");
                NvApp app = null;
                for (NvApp a : http.getAppList()) {
                    if (a.getAppName().equalsIgnoreCase("Desktop")) { app = a; break; }
                    if (app == null) app = a;
                }
                if (app == null) throw new Exception("对方没有可串流的应用");
                final NvApp fapp = app;
                final ComputerDetails fd = d;
                // 调试版测试开关：存在 files/nostream 时只走到这里，不真正打开画面（避免挤掉对方正在进行的远程）
                if (com.limelight.BuildConfig.DEBUG && new java.io.File(getFilesDir(), "nostream").exists()) {
                    final String msg = "【测试】配对正常，可打开应用：" + fapp.getAppName() + "（id " + fapp.getAppId() + "）";
                    android.util.Log.i("FrTest", msg);
                    h.post(() -> toast(msg));
                } else {
                    h.post(() -> {
                        try { startActivity(ServerHelper.createStartIntent(this, fapp, fd, binder, false)); }
                        catch (Exception e) { toast("打开画面失败：" + e.getMessage()); }
                    });
                }
            } catch (Exception e) {
                err = e.getMessage() == null ? e.toString() : e.getMessage();
            }
            final String fe = err;
            h.post(() -> {
                pd.dismiss();
                busy = false;
                if (fe != null && p.ip.equals(autoRetryIp) && autoRetries-- > 0) {
                    android.util.Log.w("FrHub", "自动重连失败，3 秒后再试：" + fe);
                    toast("画面正在重连…");
                    h.postDelayed(() -> connect(p), 3000);
                    return;
                }
                if (p.ip.equals(autoRetryIp)) autoRetryIp = null;
                if (fe != null && FrAssist.isAssistIp(p.ip)) FrAssist.stop(); // 协助没连上：退出临时组网
                if (fe != null) new AlertDialog.Builder(this).setTitle("连接失败").setMessage(fe).setPositiveButton("好", null).show();
            });
        }).start();
    }

    /** HyperOS 风格的“连接中”卡片 */
    private final class Busy {
        final Dialog d;
        final TextView msg;

        Busy(String title) {
            d = new Dialog(HomeActivity.this);
            d.requestWindowFeature(Window.FEATURE_NO_TITLE);
            LinearLayout box = ui.col();
            box.setGravity(Gravity.CENTER_HORIZONTAL);
            box.setPadding(ui.dp(28), ui.dp(26), ui.dp(28), ui.dp(24));
            box.setBackground(Ui.round(ui.card, ui.dp(28)));
            ProgressBar pb = new ProgressBar(HomeActivity.this);
            pb.setIndeterminateTintList(android.content.res.ColorStateList.valueOf(ui.accent));
            box.addView(pb, new LinearLayout.LayoutParams(ui.dp(40), ui.dp(40)));
            TextView t = ui.text(title, 18, ui.text, true);
            t.setPadding(0, ui.dp(16), 0, 0);
            box.addView(t);
            msg = ui.text("正在连接…", 14, ui.sub, false);
            msg.setPadding(0, ui.dp(8), 0, 0);
            box.addView(msg);
            d.setContentView(box);
            if (d.getWindow() != null) {
                d.getWindow().setBackgroundDrawableResource(android.R.color.transparent);
                d.getWindow().setLayout(ui.dp(280), ViewGroup.LayoutParams.WRAP_CONTENT);
            }
            d.setCancelable(false);
            d.show();
        }

        void step(String s) { h.post(() -> msg.setText(s)); }

        void dismiss() { try { d.dismiss(); } catch (Exception ignored) { } }
    }

    // =============================== 工具 ===============================
    private ScrollView scroll(View v) {
        ScrollView s = new ScrollView(this);
        s.setBackgroundColor(ui.bg);
        s.setFillViewport(true);
        s.addView(v);
        return s;
    }

    private void toast(String s) { Toast.makeText(this, s, Toast.LENGTH_LONG).show(); }
}
