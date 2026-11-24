package com.freeremote;

import android.annotation.SuppressLint;
import android.app.AlertDialog;
import android.content.Context;
import android.content.SharedPreferences;
import android.content.pm.ActivityInfo;
import android.content.res.Configuration;
import android.graphics.Color;
import android.media.AudioManager;
import android.view.Gravity;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewGroup;
import android.widget.FrameLayout;
import android.widget.HorizontalScrollView;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;
import android.widget.Toast;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.limelight.Game;
import com.limelight.R;
import com.limelight.binding.input.KeyboardTranslator;
import com.limelight.nvstream.NvConnection;
import com.limelight.nvstream.input.KeyboardPacket;
import com.limelight.nvstream.input.MouseButtonPacket;

import java.util.ArrayList;
import java.util.List;

/**
 * 串流画面上的 FreeRemote 浮层（参考 UU 远程手机端）：
 *  · 底部工具栏：展示所有窗口（Win+Tab）、显示桌面（Win+D）、键盘、操作；
 *  · “操作”控制中心：退出远控、旋转屏幕、声音、仅观看、关闭对方屏幕、小窗、性能信息、切换屏幕；
 *    操控方式（屏幕触控 / 鼠标指针）、显示鼠标按键；
 *  · 鼠标指针模式：单指滑动只移动光标，点屏幕不会点击；双指捏合缩放、拖动平移画面；
 *    点击靠实体按键（左键 / 右键，按住左键再滑动 = 拖动），右侧滚轮。
 */
public final class FrStreamOverlay {
    private static final int MODE_TOUCH = 0, MODE_POINTER = 1;

    private final Game game;
    private final Ui ui;
    private final SharedPreferences sp;
    private final String host;
    private final FrameLayout root;
    private View touchLayer, btnL, btnR, wheel, sheet, scrim, handle, editBar;
    private boolean editing;
    private String selected = "L";
    private TextView sizeLabel;
    private LinearLayout bottomBar;
    private int mode;
    private boolean showButtons, viewOnly, muted;

    // 缩放 / 平移（作用在画面容器上）
    private float scale = 1f, tx = 0f, ty = 0f;

    public static FrStreamOverlay attach(Game g, String host) {
        if (!FrHub.isFreeRemoteHost(host)) return null;
        return new FrStreamOverlay(g, host);
    }

    private FrStreamOverlay(Game g, String host) {
        game = g;
        this.host = host;
        ui = new Ui(g);
        sp = g.getSharedPreferences("freeremote_stream", Context.MODE_PRIVATE);
        mode = sp.getInt("mode", MODE_POINTER);
        showButtons = sp.getBoolean("buttons", true);
        viewOnly = FrStreamHooks.takeStartViewOnly(); // 设备页“观看模式”进来的

        // Artemis 自带的悬浮菜单按钮、缩放按钮由“操作”面板替代
        hide(R.id.floatingMenuButton);
        hide(R.id.overlayToggleZoomButton);

        root = new FrameLayout(g);
        root.setMotionEventSplittingEnabled(true); // 一指按住左键、另一指滑动
        ((ViewGroup) g.findViewById(android.R.id.content)).addView(root,
                new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        build();
        applyMode();
    }

    private void hide(int id) {
        View v = game.findViewById(id);
        if (v != null) v.setVisibility(View.GONE);
    }

    private NvConnection conn() { return game.conn; }

    // =============================== 构建 ===============================
    @SuppressLint("ClickableViewAccessibility")
    private void build() {
        // 1. 手势层（鼠标指针模式 / 仅观看时拦截所有触摸）
        touchLayer = new View(game);
        touchLayer.setOnTouchListener(this::onPointerTouch);
        root.addView(touchLayer, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));

        // 2. 鼠标按键：左键、右键（底部居中），滚轮（右侧）
        //    位置和大小可以在“操作 → 按键布局”里调整（横屏、竖屏分别保存）
        btnL = mouseButton(MouseButtonPacket.BUTTON_LEFT, true);
        btnR = mouseButton(MouseButtonPacket.BUTTON_RIGHT, false);
        wheel = buildWheel();
        root.addView(btnL, new FrameLayout.LayoutParams(ui.dp(72), ui.dp(72), Gravity.TOP | Gravity.START));
        root.addView(btnR, new FrameLayout.LayoutParams(ui.dp(72), ui.dp(72), Gravity.TOP | Gravity.START));
        root.addView(wheel, new FrameLayout.LayoutParams(ui.dp(58), ui.dp(170), Gravity.TOP | Gravity.START));
        root.addOnLayoutChangeListener((v, l, t, r, b, ol, ot, or, ob) -> {
            if (r - l != or - ol || b - t != ob - ot) root.post(this::placeControls);
        });

        // 3. 底部工具栏
        bottomBar = ui.row();
        bottomBar.setBackground(Ui.round(0xE6161618, ui.dp(26)));
        bottomBar.setPadding(ui.dp(6), ui.dp(8), ui.dp(6), ui.dp(8));
        bottomBar.addView(barItem(R.drawable.fr_ic_windows, "所有窗口", v -> combo(KeyboardTranslator.VK_LWIN, 9)));     // Win+Tab
        bottomBar.addView(barItem(R.drawable.fr_ic_desktop, "显示桌面", v -> combo(KeyboardTranslator.VK_LWIN, 'D')));  // Win+D
        bottomBar.addView(barItem(R.drawable.fr_ic_keyboard, "键盘", v -> game.toggleKeyboard()));
        bottomBar.addView(barItem(R.drawable.fr_ic_tune, "操作", v -> showSheet()));
        FrameLayout.LayoutParams barLp = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.BOTTOM);
        barLp.setMargins(ui.dp(12), 0, ui.dp(12), ui.dp(14));
        root.addView(bottomBar, barLp);

        // 横屏时工具栏收起，右下角小按钮展开
        ImageView hb = ui.icon(R.drawable.fr_ic_up, Color.WHITE, 22);
        hb.setPadding(ui.dp(11), ui.dp(11), ui.dp(11), ui.dp(11));
        hb.setBackground(Ui.round(0xB3161618, ui.dp(22)));
        hb.setOnClickListener(v -> {
            boolean show = bottomBar.getVisibility() != View.VISIBLE;
            bottomBar.setVisibility(show ? View.VISIBLE : View.GONE);
            hb.setImageResource(show ? R.drawable.fr_ic_down : R.drawable.fr_ic_up);
        });
        FrameLayout.LayoutParams hlp = new FrameLayout.LayoutParams(ui.dp(44), ui.dp(44), Gravity.BOTTOM | Gravity.END);
        hlp.setMargins(0, 0, ui.dp(14), ui.dp(14));
        handle = hb;
        root.addView(hb, hlp);
        onConfigurationChanged(game.getResources().getConfiguration());
    }

    /** 横竖屏切换时调整工具栏 */
    public void onConfigurationChanged(Configuration c) {
        boolean land = c.orientation == Configuration.ORIENTATION_LANDSCAPE;
        if (editing) stopEdit();
        bottomBar.setVisibility(land ? View.GONE : View.VISIBLE);
        handle.setVisibility(land ? View.VISIBLE : View.GONE);
        if (handle instanceof ImageView) ((ImageView) handle).setImageResource(R.drawable.fr_ic_up);
        // 横屏时工具栏右边让出小按钮的位置，两者不重叠
        FrameLayout.LayoutParams blp = (FrameLayout.LayoutParams) bottomBar.getLayoutParams();
        blp.rightMargin = land ? ui.dp(70) : ui.dp(12);
        bottomBar.setLayoutParams(blp);
        root.post(this::placeControls);
    }

    private View barItem(int icon, String label, View.OnClickListener l) {
        LinearLayout c = ui.col();
        c.setGravity(Gravity.CENTER);
        c.setPadding(0, ui.dp(4), 0, ui.dp(2));
        c.setBackground(ui.press(Color.TRANSPARENT, ui.dp(18)));
        c.addView(ui.icon(icon, Color.WHITE, 24));
        TextView t = ui.text(label, 11.5f, 0xCCFFFFFF, false);
        t.setGravity(Gravity.CENTER);
        t.setPadding(0, ui.dp(5), 0, 0);
        c.addView(t);
        c.setOnClickListener(l);
        c.setLayoutParams(new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        return c;
    }

    @SuppressLint("ClickableViewAccessibility")
    private View mouseButton(byte button, boolean left) {
        FrameLayout b = new FrameLayout(game);
        b.setBackground(Ui.round(0xCC2A2A2E, ui.dp(36)));
        ImageView ic = ui.icon(R.drawable.fr_ic_mouse, Color.WHITE, 30);
        b.addView(ic, new FrameLayout.LayoutParams(ui.dp(30), ui.dp(30), Gravity.CENTER));
        // 左右键用不同的“高亮半边”区分
        View mark = new View(game);
        mark.setBackground(Ui.round(ui.accent, ui.dp(3)));
        FrameLayout.LayoutParams mlp = new FrameLayout.LayoutParams(ui.dp(8), ui.dp(12), Gravity.TOP | (left ? Gravity.START : Gravity.END));
        mlp.setMargins(ui.dp(left ? 22 : 0), ui.dp(16), ui.dp(left ? 0 : 22), 0);
        b.addView(mark, mlp);
        String key = left ? "L" : "R";
        b.setOnTouchListener((v, e) -> {
            if (editing) return editDrag(v, key, e);
            NvConnection c = conn();
            switch (e.getActionMasked()) {
                case MotionEvent.ACTION_DOWN:
                    v.setBackground(Ui.round(ui.accent, ui.dp(36)));
                    v.performHapticFeedback(android.view.HapticFeedbackConstants.KEYBOARD_TAP);
                    if (c != null) c.sendMouseButtonDown(button);
                    return true;
                case MotionEvent.ACTION_UP:
                case MotionEvent.ACTION_CANCEL:
                    v.setBackground(Ui.round(0xCC2A2A2E, ui.dp(36)));
                    if (c != null) c.sendMouseButtonUp(button);
                    return true;
            }
            return true;
        });
        return b;
    }

    @SuppressLint("ClickableViewAccessibility")
    private View buildWheel() {
        LinearLayout w = ui.col();
        w.setGravity(Gravity.CENTER_HORIZONTAL);
        w.setBackground(Ui.round(0xCC2A2A2E, ui.dp(29)));
        w.setPadding(0, ui.dp(10), 0, ui.dp(10));
        ImageView up = ui.icon(R.drawable.fr_ic_up, Color.WHITE, 30);
        View dot = new View(game);
        dot.setBackground(Ui.round(ui.accent, ui.dp(7)));
        ImageView down = ui.icon(R.drawable.fr_ic_down, Color.WHITE, 30);
        w.addView(up);
        LinearLayout.LayoutParams dlp = new LinearLayout.LayoutParams(ui.dp(14), ui.dp(14));
        dlp.setMargins(0, ui.dp(30), 0, ui.dp(30));
        w.addView(dot, dlp);
        w.addView(down);
        final float[] last = {0}, acc = {0};
        final float[] startY = {0};
        w.setOnTouchListener((v, e) -> {
            if (editing) return editDrag(v, "W", e);
            NvConnection c = conn();
            switch (e.getActionMasked()) {
                case MotionEvent.ACTION_DOWN:
                    last[0] = startY[0] = e.getY(); acc[0] = 0;
                    return true;
                case MotionEvent.ACTION_MOVE: {
                    float dy = e.getY() - last[0];
                    last[0] = e.getY();
                    acc[0] += dy;
                    // 向上拖 = 向上滚
                    int step = ui.dp(6);
                    while (Math.abs(acc[0]) >= step) {
                        short amt = (short) (acc[0] < 0 ? 40 : -40);
                        if (c != null) c.sendMouseHighResScroll(amt);
                        acc[0] += acc[0] < 0 ? step : -step;
                    }
                    return true;
                }
                case MotionEvent.ACTION_UP:
                    // 轻点上半部分 / 下半部分 = 滚一格
                    if (Math.abs(e.getY() - startY[0]) < ui.dp(6) && c != null)
                        c.sendMouseHighResScroll((short) (e.getY() < v.getHeight() / 2f ? 120 : -120));
                    return true;
            }
            return true;
        });
        return w;
    }

    // =============================== 手势（鼠标指针模式） ===============================
    private float lx, ly, lastDist, lastCx, lastCy;
    private int lastPointers;
    private float accX, accY;

    private boolean onPointerTouch(View v, MotionEvent e) {
        if (viewOnly && mode == MODE_TOUCH) return true; // 仅观看：什么都不发
        int n = e.getPointerCount();
        switch (e.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
                lx = e.getX(); ly = e.getY(); lastPointers = 1;
                return true;
            case MotionEvent.ACTION_POINTER_DOWN:
            case MotionEvent.ACTION_POINTER_UP:
                if (n >= 2) { lastDist = dist(e); lastCx = cx(e); lastCy = cy(e); }
                lastPointers = 0; // 下一次 MOVE 重新取起点，避免光标跳
                return true;
            case MotionEvent.ACTION_MOVE:
                if (n >= 2) {
                    // 双指：捏合缩放 + 平移画面（只在本地，不发给电脑）
                    float d = dist(e), x = cx(e), y = cy(e);
                    if (lastPointers >= 2 && lastDist > 0) {
                        float ns = Math.max(1f, Math.min(4f, scale * d / lastDist));
                        // 以双指中心为焦点缩放
                        tx = x - (x - tx) * ns / scale + (x - lastCx);
                        ty = y - (y - ty) * ns / scale + (y - lastCy);
                        scale = ns;
                        applyZoom();
                    }
                    lastDist = d; lastCx = x; lastCy = y; lastPointers = n;
                    return true;
                }
                if (lastPointers != 1) { lx = e.getX(); ly = e.getY(); lastPointers = 1; return true; }
                if (viewOnly) return true;
                // 单指：相对移动光标；放大时移动更精细
                float k = sens() / scale;
                accX += (e.getX() - lx) * k;
                accY += (e.getY() - ly) * k;
                lx = e.getX(); ly = e.getY();
                short dx = (short) accX, dy = (short) accY;
                if ((dx != 0 || dy != 0) && conn() != null) {
                    conn().sendMouseMove(dx, dy);
                    accX -= dx; accY -= dy;
                }
                return true;
            case MotionEvent.ACTION_UP:
            case MotionEvent.ACTION_CANCEL:
                lastPointers = 0;
                return true; // 抬手不点击
        }
        return true;
    }

    private float sens() { return sp.getFloat("sens", 1.6f); }

    private static float dist(MotionEvent e) {
        float dx = e.getX(0) - e.getX(1), dy = e.getY(0) - e.getY(1);
        return (float) Math.sqrt(dx * dx + dy * dy);
    }
    private static float cx(MotionEvent e) { return (e.getX(0) + e.getX(1)) / 2f; }
    private static float cy(MotionEvent e) { return (e.getY(0) + e.getY(1)) / 2f; }

    private void applyZoom() {
        View s = game.frStreamContainer();
        if (s == null) return;
        int w = root.getWidth(), h = root.getHeight();
        // 不让画面拖出屏幕
        tx = Math.min(0, Math.max(w - w * scale, tx));
        ty = Math.min(0, Math.max(h - h * scale, ty));
        if (scale <= 1.001f) { scale = 1f; tx = 0; ty = 0; }
        s.setPivotX(0); s.setPivotY(0);
        s.setScaleX(scale); s.setScaleY(scale);
        s.setTranslationX(tx); s.setTranslationY(ty);
    }

    // =============================== 模式 ===============================
    private void applyMode() {
        boolean pointer = mode == MODE_POINTER;
        // 鼠标指针 / 仅观看：手势层接管全部触摸；屏幕触控：交给 Artemis（点哪点哪）
        touchLayer.setVisibility(pointer || viewOnly ? View.VISIBLE : View.GONE);
        game.frApplyMouseMode(pointer || viewOnly ? 4 : 1);
        boolean btn = editing || (pointer && showButtons && !viewOnly);
        btnL.setVisibility(btn ? View.VISIBLE : View.GONE);
        btnR.setVisibility(btn ? View.VISIBLE : View.GONE);
        wheel.setVisibility(btn ? View.VISIBLE : View.GONE);
        if (!pointer) { scale = 1f; applyZoom(); }
    }

    // =============================== 控制中心 ===============================
    private void showSheet() {
        if (sheet != null) { closeSheet(); return; }
        scrim = new View(game);
        scrim.setBackgroundColor(0x66000000);
        scrim.setOnClickListener(v -> closeSheet());
        root.addView(scrim, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));

        LinearLayout box = ui.col();
        box.setBackground(Ui.round(0xF21C1C1E, ui.dp(30)));
        box.setPadding(ui.dp(16), ui.dp(12), ui.dp(16), ui.dp(20));
        box.setClickable(true);
        View grip = new View(game);
        grip.setBackground(Ui.round(0x55FFFFFF, ui.dp(2)));
        LinearLayout.LayoutParams glp = new LinearLayout.LayoutParams(ui.dp(36), ui.dp(4));
        glp.gravity = Gravity.CENTER_HORIZONTAL;
        glp.bottomMargin = ui.dp(14);
        box.addView(grip, glp);

        // 磁贴（横向可滑动）
        LinearLayout tiles = ui.row();
        tiles.addView(tile(R.drawable.fr_ic_exit, "退出远控", false, v -> { closeSheet(); game.frQuitNow(); }));
        tiles.addView(tile(R.drawable.fr_ic_rotate, "旋转屏幕", false, v -> rotate()));
        List<String[]> displays = displays();
        if (displays.size() > 1) tiles.addView(tile(R.drawable.fr_ic_desktop, "切换屏幕", false, v -> pickDisplay(displays)));
        tiles.addView(tile(R.drawable.fr_ic_volume, muted ? "已静音" : "声音", !muted, v -> { toggleMute(); refreshSheet(); }));
        tiles.addView(tile(R.drawable.fr_ic_eye, "仅观看", viewOnly, v -> { viewOnly = !viewOnly; applyMode(); refreshSheet(); }));
        boolean priv = FrHub.get(game).privacyOn(host);
        tiles.addView(tile(R.drawable.fr_ic_screen_off, "关闭对方屏幕", priv, v -> {
            new Thread(() -> FrHub.get(game).setPrivacy(host, !priv)).start();
            root.postDelayed(this::refreshSheet, 300);
        }));
        tiles.addView(tile(R.drawable.fr_ic_pip, "小窗模式", false, v -> { closeSheet(); game.frEnterPip(); }));
        tiles.addView(tile(R.drawable.fr_ic_speed, "性能信息", false, v -> game.toggleHUD()));
        tiles.addView(tile(R.drawable.fr_ic_mouse, "按键布局", false, v -> { closeSheet(); startEdit(); })); // 不常用，放最后
        HorizontalScrollView hs = new HorizontalScrollView(game);
        hs.setHorizontalScrollBarEnabled(false);
        hs.addView(tiles);
        box.addView(hs);

        TextView qTitle = ui.text("画质", 15, 0x99FFFFFF, false);
        qTitle.setPadding(ui.dp(4), ui.dp(22), 0, ui.dp(10));
        box.addView(qTitle);
        // 码率自适应开着时，标题后面实时显示现在的码率和网络排队
        Runnable adaptiveTick = new Runnable() {
            @Override public void run() {
                if (!qTitle.isAttachedToWindow()) return;
                int kbps = com.limelight.nvstream.jni.MoonBridge.farloopGetBitrateKbps();
                if (kbps > 0) {
                    int q = com.limelight.nvstream.jni.MoonBridge.farloopGetQueueDelayMs();
                    qTitle.setText(String.format(java.util.Locale.ROOT, "画质 · 自适应 %.1fM · 排队 %d ms", kbps / 1000.0, q));
                }
                qTitle.postDelayed(this, 500);
            }
        };
        qTitle.post(adaptiveTick);
        LinearLayout qCard = ui.col();
        qCard.setBackground(Ui.round(0xFF2C2C2E, ui.dp(22)));
        qCard.setPadding(ui.dp(10), ui.dp(10), ui.dp(10), ui.dp(4));
        // 画质：切换后自动重连（几秒）
        LinearLayout qrow = ui.row();
        qrow.setPadding(ui.dp(8), ui.dp(4), ui.dp(4), ui.dp(10));
        int curQ = Math.min(FrStreamHooks.quality(game), 4);
        String[] qn = {"流畅", "标准", "高清", "超清", "自定义"};
        boolean live = com.limelight.nvstream.jni.MoonBridge.farloopGetBitrateKbps() > 0 && FrStreamHooks.quality(game) < 4;
        LinearLayout qseg = ui.segmented(qn, curQ, i -> {
            if (i == 4) { showCustomQuality(); return; } // 自定义：打开参数面板（已选中时再点也打开）
            if (i == FrStreamHooks.quality(game)) return;
            FrStreamHooks.setQuality(game, i);
            if (live) {
                // 无缝切换：分辨率已经跟随电脑屏幕，换档只改码率上限，画面不断
                com.limelight.nvstream.jni.MoonBridge.farloopSetCap(FrStreamHooks.qualityBitrate(i));
                Toast.makeText(game, "已切换为" + qn[i] + "（码率上限 " + FrStreamHooks.qualityBitrate(i) / 1000 + "M）", Toast.LENGTH_SHORT).show();
                refreshSheet();
                return;
            }
            closeSheet();
            Toast.makeText(game, "正在切换为" + qn[i] + "画质，几秒后自动重连", Toast.LENGTH_LONG).show();
            FrStreamHooks.setPendingReconnect(host);
            game.finish();
        }, 0xFF1C1C1E, ui.accent, 0xCCFFFFFF, Color.WHITE);
        qrow.addView(qseg, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        qCard.addView(qrow);
        TextView qd = ui.text(curQ == 4
                ? "自定义：" + FrStreamHooks.customSummary(game) + "（再点“自定义”可修改）"
                : live
                ? "流畅 8M · 标准 20M · 高清 35M · 超清 60M（码率上限）\n分辨率跟随电脑屏幕；切换不断线，网络变差时自动降码率"
                : "720P·8M · 1080P·20M · 2K·35M · 4K·60M\n码率超过电脑宽带的上行速度会大量丢帧；卡了就降一档", 11.5f, 0x80FFFFFF, false);
        qd.setPadding(ui.dp(8), 0, ui.dp(6), ui.dp(8));
        qCard.addView(qd);
        box.addView(qCard);

        TextView opTitle = ui.text("操作", 15, 0x99FFFFFF, false);
        opTitle.setPadding(ui.dp(4), ui.dp(22), 0, ui.dp(10));
        box.addView(opTitle);

        LinearLayout opCard = ui.col();
        opCard.setBackground(Ui.round(0xFF2C2C2E, ui.dp(22)));
        opCard.setPadding(ui.dp(10), ui.dp(10), ui.dp(10), ui.dp(4));
        opCard.addView(ui.segmented(new String[]{"屏幕触控", "鼠标指针"}, mode, i -> {
            mode = i;
            sp.edit().putInt("mode", mode).apply();
            applyMode();
        }, 0xFF1C1C1E, ui.accent, 0xCCFFFFFF, Color.WHITE));
        TextView desc = ui.text("鼠标指针：单指移动光标、点屏幕不会点击；双指缩放 / 平移画面；用下方按键点击。\n屏幕触控：点哪里就点击哪里。", 12, 0x80FFFFFF, false);
        desc.setPadding(ui.dp(6), ui.dp(10), ui.dp(6), ui.dp(6));
        desc.setLineSpacing(ui.dp(3), 1f);
        opCard.addView(desc);
        LinearLayout r = ui.row();
        r.setPadding(ui.dp(8), ui.dp(10), ui.dp(4), ui.dp(10));
        r.addView(ui.text("显示鼠标按键", 16, Color.WHITE, false), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        r.addView(new Ui.Switch(ui, showButtons, on -> { showButtons = on; sp.edit().putBoolean("buttons", on).apply(); applyMode(); }));
        opCard.addView(r);
        // 光标速度
        LinearLayout sr = ui.row();
        sr.setPadding(ui.dp(8), ui.dp(4), ui.dp(4), ui.dp(10));
        sr.addView(ui.text("光标速度", 16, Color.WHITE, false), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        String[] speeds = {"慢", "中", "快"};
        float[] vals = {1.0f, 1.6f, 2.6f};
        int cur = sens() < 1.3f ? 0 : sens() < 2.1f ? 1 : 2;
        LinearLayout seg = ui.segmented(speeds, cur, i -> sp.edit().putFloat("sens", vals[i]).apply(), 0xFF1C1C1E, ui.accent, 0xCCFFFFFF, Color.WHITE);
        sr.addView(seg, new LinearLayout.LayoutParams(ui.dp(180), ViewGroup.LayoutParams.WRAP_CONTENT));
        opCard.addView(sr);
        // 屏幕方向（记住，下次进画面也用）
        LinearLayout orow = ui.row();
        orow.setPadding(ui.dp(8), ui.dp(4), ui.dp(4), ui.dp(10));
        orow.addView(ui.text("屏幕方向", 16, Color.WHITE, false), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        LinearLayout oseg = ui.segmented(new String[]{"横屏", "竖屏", "自动"}, FrStreamHooks.orientation(game), i -> { setOrientation(i); },
                0xFF1C1C1E, ui.accent, 0xCCFFFFFF, Color.WHITE);
        orow.addView(oseg, new LinearLayout.LayoutParams(ui.dp(180), ViewGroup.LayoutParams.WRAP_CONTENT));
        opCard.addView(orow);
        box.addView(opCard);

        FrameLayout.LayoutParams lp = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.BOTTOM);
        lp.setMargins(ui.dp(8), 0, ui.dp(8), ui.dp(8));
        int maxW = ui.dp(520);
        if (root.getWidth() > maxW + ui.dp(16)) { lp.width = maxW; lp.gravity = Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL; }
        lp.topMargin = ui.dp(16);
        android.widget.ScrollView sv = new android.widget.ScrollView(game);
        sv.setVerticalScrollBarEnabled(false);
        sv.setOverScrollMode(View.OVER_SCROLL_NEVER);
        sv.addView(box);
        sheet = sv;
        root.addView(sv, lp);
        sv.setTranslationY(ui.dp(400));
        sv.animate().translationY(0).setDuration(220).start();
    }

    private void refreshSheet() { closeSheet(); showSheet(); }

    /** 返回键：编辑布局时退出编辑；面板开着就关面板；否则弹出退出面板。返回 true = 已处理 */
    public boolean onBack() {
        if (editing) { stopEdit(); return true; }
        if (sheet != null) { closeSheet(); return true; }
        showExitSheet();
        return true;
    }

    /** 退出面板（替代 Artemis 自带的黑色列表菜单） */
    private void showExitSheet() {
        scrim = new View(game);
        scrim.setBackgroundColor(0x66000000);
        scrim.setOnClickListener(v -> closeSheet());
        root.addView(scrim, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));

        LinearLayout box = ui.col();
        box.setBackground(Ui.round(0xF21C1C1E, ui.dp(30)));
        box.setPadding(ui.dp(16), ui.dp(12), ui.dp(16), ui.dp(16));
        box.setClickable(true);
        View grip = new View(game);
        grip.setBackground(Ui.round(0x55FFFFFF, ui.dp(2)));
        LinearLayout.LayoutParams glp = new LinearLayout.LayoutParams(ui.dp(36), ui.dp(4));
        glp.gravity = Gravity.CENTER_HORIZONTAL;
        glp.bottomMargin = ui.dp(14);
        box.addView(grip, glp);
        TextView title = ui.text("退出远控？", 19, Color.WHITE, true);
        title.setPadding(ui.dp(6), 0, 0, ui.dp(4));
        box.addView(title);
        TextView sub = ui.text("断开后电脑保持现在的样子；结束会话会关闭这次远程桌面。", 12.5f, 0x99FFFFFF, false);
        sub.setPadding(ui.dp(6), 0, ui.dp(6), ui.dp(14));
        box.addView(sub);

        TextView quit = ui.pill("断开并退出");
        quit.setOnClickListener(v -> { closeSheet(); game.frQuitNow(); });
        box.addView(quit, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ui.dp(50)));

        LinearLayout list = ui.col();
        list.setBackground(Ui.round(0xFF2C2C2E, ui.dp(20)));
        LinearLayout.LayoutParams llp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        llp.topMargin = ui.dp(12);
        list.addView(exitRow(R.drawable.fr_ic_exit, "断开（保留会话，稍后可继续）", v -> { closeSheet(); game.disconnect(); }));
        list.addView(exitRow(R.drawable.fr_ic_up, "把手机剪贴板发到电脑", v -> { closeSheet(); game.sendClipboard(true); }));
        list.addView(exitRow(R.drawable.fr_ic_down, "获取电脑剪贴板", v -> { closeSheet(); game.getClipboard(0); }));
        list.addView(exitRow(R.drawable.fr_ic_tune, "更多选项", v -> { closeSheet(); game.frShowArtemisMenu(); }));
        box.addView(list, llp);

        TextView cancel = ui.text("继续远控", 16, 0xCCFFFFFF, true);
        cancel.setGravity(Gravity.CENTER);
        cancel.setPadding(0, ui.dp(14), 0, ui.dp(4));
        cancel.setOnClickListener(v -> closeSheet());
        box.addView(cancel, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        FrameLayout.LayoutParams lp = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.BOTTOM);
        lp.setMargins(ui.dp(8), 0, ui.dp(8), ui.dp(8));
        int maxW = ui.dp(480);
        if (root.getWidth() > maxW + ui.dp(16)) { lp.width = maxW; lp.gravity = Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL; }
        sheet = box;
        root.addView(box, lp);
        box.setTranslationY(ui.dp(400));
        box.animate().translationY(0).setDuration(220).start();
    }

    private View exitRow(int icon, String label, View.OnClickListener l) {
        LinearLayout r = ui.row();
        r.setPadding(ui.dp(16), ui.dp(14), ui.dp(16), ui.dp(14));
        r.setBackground(ui.press(Color.TRANSPARENT, ui.dp(20)));
        r.addView(ui.icon(icon, Color.WHITE, 22));
        TextView t = ui.text(label, 15.5f, Color.WHITE, false);
        t.setPadding(ui.dp(14), 0, 0, 0);
        r.addView(t, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        r.setOnClickListener(l);
        return r;
    }

    private void closeSheet() {
        if (sheet != null) { root.removeView(sheet); sheet = null; }
        if (scrim != null) { root.removeView(scrim); scrim = null; }
    }

    private View tile(int icon, String label, boolean on, View.OnClickListener l) {
        LinearLayout c = ui.col();
        c.setGravity(Gravity.CENTER);
        c.setBackground(ui.press(on ? ui.accent : 0xFF2C2C2E, ui.dp(22)));
        c.addView(ui.icon(icon, Color.WHITE, 28));
        TextView t = ui.text(label, 12.5f, Color.WHITE, false);
        t.setGravity(Gravity.CENTER);
        t.setPadding(0, ui.dp(8), 0, 0);
        c.addView(t);
        c.setOnClickListener(l);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ui.dp(92), ui.dp(92));
        lp.rightMargin = ui.dp(10);
        c.setLayoutParams(lp);
        return c;
    }

    private void rotate() {
        boolean land = game.getResources().getConfiguration().orientation == Configuration.ORIENTATION_LANDSCAPE;
        setOrientation(land ? FrStreamHooks.ORIENT_PORTRAIT : FrStreamHooks.ORIENT_LANDSCAPE);
        closeSheet();
    }

    private void setOrientation(int o) {
        FrStreamHooks.setOrientation(game, o);
        game.setRequestedOrientation(FrStreamHooks.activityOrientation(game));
    }

    // =============================== 自定义按键布局 ===============================
    private String orientKey() {
        return root.getWidth() > root.getHeight() ? "land_" : "port_";
    }

    private View ctl(String k) { return k.equals("L") ? btnL : k.equals("R") ? btnR : wheel; }

    private float ctlScale(String k) { return sp.getFloat(orientKey() + "s_" + k, 1f); }

    /** 按保存的位置（中心点占屏幕的比例）摆放左右键和滚轮；没保存过用默认位置 */
    private void placeControls() {
        int w = root.getWidth(), h = root.getHeight();
        if (w == 0 || h == 0) return;
        boolean land = w > h;
        float bottom = h - ui.dp(land ? 40 : 96) - ui.dp(36);
        float[][] def = {
                {w / 2f - ui.dp(56), bottom},
                {w / 2f + ui.dp(56), bottom},
                {w - ui.dp(14) - ui.dp(29), h / 2f},
        };
        String[] keys = {"L", "R", "W"};
        float alpha = sp.getFloat("alpha", 1f);
        for (int i = 0; i < 3; i++) {
            String k = keys[i];
            View v = ctl(k);
            float cx = sp.getFloat(orientKey() + "x_" + k, -1f), cy = sp.getFloat(orientKey() + "y_" + k, -1f);
            float px = cx < 0 ? def[i][0] : cx * w, py = cy < 0 ? def[i][1] : cy * h;
            FrameLayout.LayoutParams lp = (FrameLayout.LayoutParams) v.getLayoutParams();
            lp.leftMargin = Math.round(px - lp.width / 2f);
            lp.topMargin = Math.round(py - lp.height / 2f);
            v.setLayoutParams(lp);
            float sc = ctlScale(k);
            v.setScaleX(sc);
            v.setScaleY(sc);
            v.setAlpha(alpha);
        }
    }

    private float dragDx, dragDy;

    private boolean editDrag(View v, String k, MotionEvent e) {
        FrameLayout.LayoutParams lp = (FrameLayout.LayoutParams) v.getLayoutParams();
        switch (e.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
                dragDx = e.getRawX() - lp.leftMargin;
                dragDy = e.getRawY() - lp.topMargin;
                select(k);
                return true;
            case MotionEvent.ACTION_MOVE: {
                int w = root.getWidth(), h = root.getHeight();
                lp.leftMargin = Math.max(-lp.width / 2, Math.min(w - lp.width / 2, Math.round(e.getRawX() - dragDx)));
                lp.topMargin = Math.max(-lp.height / 2, Math.min(h - lp.height / 2, Math.round(e.getRawY() - dragDy)));
                v.setLayoutParams(lp);
                return true;
            }
            case MotionEvent.ACTION_UP:
            case MotionEvent.ACTION_CANCEL: {
                int w = root.getWidth(), h = root.getHeight();
                sp.edit().putFloat(orientKey() + "x_" + k, (lp.leftMargin + lp.width / 2f) / w)
                        .putFloat(orientKey() + "y_" + k, (lp.topMargin + lp.height / 2f) / h).apply();
                return true;
            }
        }
        return true;
    }

    private void select(String k) {
        selected = k;
        for (String x : new String[]{"L", "R", "W"}) ctl(x).setForeground(editing && x.equals(k) ? outline() : null);
        if (sizeLabel != null) sizeLabel.setText(name(k) + " 大小 " + Math.round(ctlScale(k) * 100) + "%");
        if (editBar != null) {
            android.widget.SeekBar sb = editBar.findViewWithTag("size");
            if (sb != null) sb.setProgress(Math.round((ctlScale(k) - 0.5f) * 100));
        }
    }

    private static String name(String k) { return k.equals("L") ? "左键" : k.equals("R") ? "右键" : "滚轮"; }

    private android.graphics.drawable.Drawable outline() {
        android.graphics.drawable.GradientDrawable d = new android.graphics.drawable.GradientDrawable();
        d.setCornerRadius(ui.dp(36));
        d.setStroke(ui.dp(3), 0xFFFFD60A, ui.dp(6), ui.dp(4));
        return d;
    }

    private void startEdit() {
        if (editing) return;
        editing = true;
        applyMode();
        LinearLayout bar = ui.col();
        bar.setBackground(Ui.round(0xF21C1C1E, ui.dp(22)));
        bar.setPadding(ui.dp(16), ui.dp(12), ui.dp(16), ui.dp(12));
        bar.setClickable(true);
        bar.addView(ui.text("拖动按键调整位置；点选一个按键后用滑块调大小。横屏、竖屏分别保存。", 12.5f, 0xCCFFFFFF, false));
        sizeLabel = ui.text("", 14, Color.WHITE, true);
        sizeLabel.setPadding(0, ui.dp(10), 0, 0);
        bar.addView(sizeLabel);
        android.widget.SeekBar sb = new android.widget.SeekBar(game);
        sb.setTag("size");
        sb.setMax(110); // 50% ~ 160%
        sb.setOnSeekBarChangeListener(new android.widget.SeekBar.OnSeekBarChangeListener() {
            @Override public void onProgressChanged(android.widget.SeekBar s2, int p, boolean user) {
                if (!user) return;
                float sc = 0.5f + p / 100f;
                sp.edit().putFloat(orientKey() + "s_" + selected, sc).apply();
                View v = ctl(selected);
                v.setScaleX(sc);
                v.setScaleY(sc);
                sizeLabel.setText(name(selected) + " 大小 " + Math.round(sc * 100) + "%");
            }
            @Override public void onStartTrackingTouch(android.widget.SeekBar s2) { }
            @Override public void onStopTrackingTouch(android.widget.SeekBar s2) { }
        });
        bar.addView(sb);
        LinearLayout ar = ui.row();
        ar.setPadding(0, ui.dp(6), 0, 0);
        ar.addView(ui.text("透明度", 14, Color.WHITE, false), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        float[] alphas = {1f, 0.7f, 0.4f};
        float ca = sp.getFloat("alpha", 1f);
        int ai = ca > 0.85f ? 0 : ca > 0.55f ? 1 : 2;
        ar.addView(ui.segmented(new String[]{"不透明", "70%", "40%"}, ai, i -> { sp.edit().putFloat("alpha", alphas[i]).apply(); placeControls(); },
                0xFF2C2C2E, ui.accent, 0xCCFFFFFF, Color.WHITE), new LinearLayout.LayoutParams(ui.dp(210), ViewGroup.LayoutParams.WRAP_CONTENT));
        bar.addView(ar);
        LinearLayout row = ui.row();
        row.setPadding(0, ui.dp(12), 0, 0);
        TextView reset = ui.pill("恢复默认");
        reset.setBackground(Ui.round(0xFF3A3A3C, ui.dp(21)));
        reset.setOnClickListener(v -> {
            SharedPreferences.Editor ed = sp.edit();
            for (String k : new String[]{"L", "R", "W"}) for (String a : new String[]{"x_", "y_", "s_"}) ed.remove(orientKey() + a + k);
            ed.remove("alpha").apply();
            placeControls();
            select(selected);
        });
        TextView done = ui.pill("完成");
        done.setOnClickListener(v -> stopEdit());
        LinearLayout.LayoutParams rp = new LinearLayout.LayoutParams(0, ui.dp(42), 1);
        rp.rightMargin = ui.dp(10);
        row.addView(reset, rp);
        row.addView(done, new LinearLayout.LayoutParams(0, ui.dp(42), 1));
        bar.addView(row);
        FrameLayout.LayoutParams lp = new FrameLayout.LayoutParams(Math.min(ui.dp(420), root.getWidth() - ui.dp(24)),
                ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.TOP | Gravity.CENTER_HORIZONTAL);
        lp.topMargin = ui.dp(24);
        editBar = bar;
        root.addView(bar, lp);
        bottomBar.setVisibility(View.GONE);
        handle.setVisibility(View.GONE);
        select(selected);
    }

    private void stopEdit() {
        editing = false;
        if (editBar != null) { root.removeView(editBar); editBar = null; }
        sizeLabel = null;
        for (String x : new String[]{"L", "R", "W"}) ctl(x).setForeground(null);
        boolean land = game.getResources().getConfiguration().orientation == Configuration.ORIENTATION_LANDSCAPE;
        bottomBar.setVisibility(land ? View.GONE : View.VISIBLE);
        handle.setVisibility(land ? View.VISIBLE : View.GONE);
        applyMode();
    }


    private void toggleMute() {
        AudioManager am = (AudioManager) game.getSystemService(Context.AUDIO_SERVICE);
        muted = !muted;
        am.adjustStreamVolume(AudioManager.STREAM_MUSIC, muted ? AudioManager.ADJUST_MUTE : AudioManager.ADJUST_UNMUTE, 0);
    }

    /** 画面结束时：恢复声音、画面变换 */
    public void release() {
        if (muted) toggleMute();
    }

    private List<String[]> displays() {
        List<String[]> out = new ArrayList<>();
        JsonObject info = FrHub.get(game).deviceInfo(host);
        if (info == null || !info.has("Displays") || !info.get("Displays").isJsonArray()) return out;
        JsonArray arr = info.getAsJsonArray("Displays");
        String current = CURRENT.containsKey(host) ? CURRENT.get(host)
                : info.has("CurrentDisplay") && !info.get("CurrentDisplay").isJsonNull() ? info.get("CurrentDisplay").getAsString() : "";
        int i = 1;
        for (JsonElement el : arr) {
            JsonObject d = el.getAsJsonObject();
            String name = d.get("Name").getAsString();
            // 电脑端的虚拟显示器（息屏用，“VDD by MTT”）不是真的屏幕，切过去会黑屏；旧版电脑端没过滤，这里也挡一下
            if ((d.has("Virtual") && d.get("Virtual").getAsBoolean()) || name.contains("VDD")) continue;
            boolean primary = d.has("Primary") && d.get("Primary").getAsBoolean();
            String id = d.get("Id").getAsString();
            boolean isCur = current.isEmpty() ? primary : current.equals(id);
            String label = i++ + (primary ? " 主屏" : " 副屏") + "  " + name
                    + (d.has("Width") && d.get("Width").getAsInt() > 0 ? "  " + d.get("Width").getAsInt() + "×" + d.get("Height").getAsInt() : "")
                    + (isCur ? "  （当前）" : "");
            int idx = d.has("StreamIndex") ? d.get("StreamIndex").getAsInt() : -1;
            out.add(new String[]{id, label, String.valueOf(idx)});
        }
        return out;
    }

    /** 每台电脑上一次切到的屏幕（以手机这边为准：电脑端报的“当前”可能滞后） */
    private static final java.util.Map<String, String> CURRENT = new java.util.concurrent.ConcurrentHashMap<>();

    /** 和控制中心同样风格的居中面板 */
    private LinearLayout showPanel(String title) {
        closeSheet();
        scrim = new View(game);
        scrim.setBackgroundColor(0x66000000);
        scrim.setOnClickListener(v -> closeSheet());
        root.addView(scrim, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        LinearLayout box = ui.col();
        box.setBackground(Ui.round(0xF21C1C1E, ui.dp(30)));
        box.setPadding(ui.dp(18), ui.dp(16), ui.dp(18), ui.dp(18));
        box.setClickable(true);
        TextView t = ui.text(title, 17, Color.WHITE, true);
        t.setPadding(ui.dp(4), 0, 0, ui.dp(12));
        box.addView(t);
        android.widget.ScrollView sv = new android.widget.ScrollView(game);
        sv.setVerticalScrollBarEnabled(false);
        sv.addView(box);
        FrameLayout.LayoutParams lp = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.CENTER);
        lp.setMargins(ui.dp(12), ui.dp(16), ui.dp(12), ui.dp(16));
        int maxW = ui.dp(640);
        if (root.getWidth() > maxW + ui.dp(24)) lp.width = maxW;
        root.addView(sv, lp);
        sheet = sv;
        return box;
    }

    /** 切换屏幕：直接看每块屏的画面选（电脑端 0.20 起）；拿不到画面就退回文字列表 */
    private void pickDisplay(List<String[]> ds) {
        LinearLayout box = showPanel("切换屏幕");
        TextView wait = ui.text("正在读取屏幕画面…", 14, 0x99FFFFFF, false);
        wait.setPadding(ui.dp(4), ui.dp(8), 0, ui.dp(8));
        box.addView(wait);
        new Thread(() -> {
            JsonObject shots = FrHub.get(game).displayShots(host);
            game.runOnUiThread(() -> {
                if (sheet == null) return;
                box.removeView(wait);
                if (shots == null || !shots.has("Displays") || shots.getAsJsonArray("Displays").size() == 0) { pickDisplayList(ds); return; }
                String cur = CURRENT.containsKey(host) ? CURRENT.get(host)
                        : (shots.has("Current") && !shots.get("Current").isJsonNull() ? shots.get("Current").getAsString() : "");
                LinearLayout row = ui.row();
                for (JsonElement el : shots.getAsJsonArray("Displays")) {
                    JsonObject d = el.getAsJsonObject();
                    String id = d.get("Id").getAsString();
                    String name = d.get("Name").getAsString();
                    if (name.contains("VDD")) continue;
                    int idx = d.has("StreamIndex") ? d.get("StreamIndex").getAsInt() : -1;
                    boolean isCur = id.equals(cur);
                    LinearLayout card = ui.col();
                    card.setBackground(Ui.round(isCur ? ui.accent : 0xFF2C2C2E, ui.dp(16)));
                    card.setPadding(ui.dp(6), ui.dp(6), ui.dp(6), ui.dp(8));
                    ImageView iv = new ImageView(game);
                    iv.setScaleType(ImageView.ScaleType.FIT_CENTER);
                    iv.setAdjustViewBounds(true);
                    if (d.has("Jpg") && !d.get("Jpg").isJsonNull()) {
                        byte[] b = android.util.Base64.decode(d.get("Jpg").getAsString(), android.util.Base64.DEFAULT);
                        android.graphics.Bitmap bm = android.graphics.BitmapFactory.decodeByteArray(b, 0, b.length);
                        if (bm != null) iv.setImageBitmap(bm);
                    }
                    card.addView(iv, new LinearLayout.LayoutParams(ui.dp(220), ViewGroup.LayoutParams.WRAP_CONTENT));
                    boolean primary = d.has("Primary") && d.get("Primary").getAsBoolean();
                    TextView l = ui.text((primary ? "主屏 · " : "") + name + (isCur ? "  ✓ 当前" : ""), 13, Color.WHITE, isCur);
                    l.setPadding(ui.dp(4), ui.dp(6), ui.dp(4), 0);
                    card.addView(l);
                    if (d.has("Width") && d.get("Width").getAsInt() > 0) {
                        TextView r = ui.text(d.get("Width").getAsInt() + "×" + d.get("Height").getAsInt(), 11.5f, 0x99FFFFFF, false);
                        r.setPadding(ui.dp(4), ui.dp(2), ui.dp(4), 0);
                        card.addView(r);
                    }
                    card.setOnClickListener(v -> { closeSheet(); if (!isCur) switchTo(id, idx); });
                    LinearLayout.LayoutParams clp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
                    clp.rightMargin = ui.dp(10);
                    row.addView(card, clp);
                }
                HorizontalScrollView hs = new HorizontalScrollView(game);
                hs.setHorizontalScrollBarEnabled(false);
                hs.addView(row);
                box.addView(hs);
                TextView hint = ui.text("点一下画面切换，画面不断开", 12, 0x80FFFFFF, false);
                hint.setPadding(ui.dp(4), ui.dp(10), 0, 0);
                box.addView(hint);
            });
        }, "FrDisplayShots").start();
    }

    /** 切到某块屏：知道序号就用 Sunshine 快捷键当场切（不断线），否则改设置重启后重连 */
    private void switchTo(String id, int idx) {
        NvConnection c = conn();
        CURRENT.put(host, id);
        if (idx >= 0 && idx < 12 && c != null) {
            new Thread(() -> {
                FrStreamHooks.sendCombo(c, com.limelight.binding.input.KeyboardTranslator.VK_F1 + idx);
                FrHub.get(game).rememberDisplay(host, id);
            }, "FrSwitchDisplay").start();
            Toast.makeText(game, "已切换屏幕", Toast.LENGTH_SHORT).show();
            return;
        }
        Toast.makeText(game, "正在切换屏幕，约 5 秒后自动重连", Toast.LENGTH_LONG).show();
        new Thread(() -> {
            String err = FrHub.get(game).switchDisplay(host, id);
            game.runOnUiThread(() -> {
                if (err != null) { Toast.makeText(game, "切换失败：" + err, Toast.LENGTH_LONG).show(); return; }
                FrStreamHooks.setPendingReconnect(host);
                game.finish();
            });
        }).start();
    }

    /** 自定义画质：分辨率 / 帧率 / 码率（和电脑端一样）。只改码率时无缝生效，改了分辨率或帧率要重连几秒 */
    private void showCustomQuality() {
        LinearLayout box = showPanel("自定义画质");
        final int[] res = {FrStreamHooks.customRes(game)};
        final int[] fps = {FrStreamHooks.customFps(game)};
        final int[] kbps = {FrStreamHooks.customKbps(game)};

        LinearLayout rr = ui.row();
        rr.setPadding(ui.dp(4), ui.dp(4), 0, ui.dp(12));
        rr.addView(ui.text("分辨率", 15, Color.WHITE, false), new LinearLayout.LayoutParams(ui.dp(64), ViewGroup.LayoutParams.WRAP_CONTENT));
        rr.addView(ui.segmented(FrStreamHooks.CUSTOM_RES_NAMES, res[0], i -> res[0] = i, 0xFF2C2C2E, ui.accent, 0xCCFFFFFF, Color.WHITE),
                new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        box.addView(rr);

        String[] fpsNames = new String[FrStreamHooks.CUSTOM_FPS.length];
        int fpsIdx = 1;
        for (int i = 0; i < fpsNames.length; i++) { fpsNames[i] = String.valueOf(FrStreamHooks.CUSTOM_FPS[i]); if (FrStreamHooks.CUSTOM_FPS[i] == fps[0]) fpsIdx = i; }
        LinearLayout fr = ui.row();
        fr.setPadding(ui.dp(4), 0, 0, ui.dp(12));
        fr.addView(ui.text("帧率", 15, Color.WHITE, false), new LinearLayout.LayoutParams(ui.dp(64), ViewGroup.LayoutParams.WRAP_CONTENT));
        fr.addView(ui.segmented(fpsNames, fpsIdx, i -> fps[0] = FrStreamHooks.CUSTOM_FPS[i], 0xFF2C2C2E, ui.accent, 0xCCFFFFFF, Color.WHITE),
                new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        box.addView(fr);

        LinearLayout br = ui.row();
        br.setPadding(ui.dp(4), 0, 0, ui.dp(4));
        br.addView(ui.text("码率", 15, Color.WHITE, false), new LinearLayout.LayoutParams(ui.dp(64), ViewGroup.LayoutParams.WRAP_CONTENT));
        TextView bv = ui.text(kbps[0] / 1000 + " Mbps", 15, Color.WHITE, true);
        android.widget.SeekBar sb = new android.widget.SeekBar(game);
        sb.setMax(99); // 1~100 Mbps
        sb.setProgress(Math.max(0, Math.min(99, kbps[0] / 1000 - 1)));
        sb.setOnSeekBarChangeListener(new android.widget.SeekBar.OnSeekBarChangeListener() {
            @Override public void onProgressChanged(android.widget.SeekBar s, int p, boolean u) { kbps[0] = (p + 1) * 1000; bv.setText((p + 1) + " Mbps"); }
            @Override public void onStartTrackingTouch(android.widget.SeekBar s) { }
            @Override public void onStopTrackingTouch(android.widget.SeekBar s) { }
        });
        br.addView(sb, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        br.addView(bv, new LinearLayout.LayoutParams(ui.dp(84), ViewGroup.LayoutParams.WRAP_CONTENT));
        box.addView(br);
        TextView tip = ui.text("码率要低于电脑那边宽带的上行速度；开着码率自适应时它是上限，网络不好会自动往下调。", 12, 0x80FFFFFF, false);
        tip.setPadding(ui.dp(4), ui.dp(4), 0, ui.dp(14));
        box.addView(tip);

        TextView ok = ui.text("应用", 16, Color.WHITE, true);
        ok.setGravity(Gravity.CENTER);
        ok.setBackground(ui.press(ui.accent, ui.dp(14)));
        ok.setPadding(0, ui.dp(12), 0, ui.dp(12));
        ok.setOnClickListener(v -> {
            FrStreamHooks.setCustom(game, res[0], fps[0], kbps[0]);
            closeSheet();
            // 这次串流的分辨率、帧率都没变，又开着码率自适应：只改码率上限，画面不断
            boolean sameRes = game.matchesSelectedStreamSize();
            boolean live = com.limelight.nvstream.jni.MoonBridge.farloopGetBitrateKbps() > 0;
            boolean fpsSame = fps[0] == FrStreamHooks.streamFps;
            boolean fpsLive = fpsSame || FrStreamHooks.liveFramerate(game, host);
            if (live && sameRes && fpsLive) {
                // 分辨率没变：码率、帧率都当场生效，画面不断
                com.limelight.nvstream.jni.MoonBridge.farloopSetCap(kbps[0]);
                if (!fpsSame) {
                    com.limelight.nvstream.jni.MoonBridge.farloopSetFps(fps[0]);
                    FrStreamHooks.streamFps = fps[0];
                }
                Toast.makeText(game, "已应用：" + fps[0] + "fps · 码率上限 " + kbps[0] / 1000 + "M", Toast.LENGTH_SHORT).show();
                return;
            }
            Toast.makeText(game, "已保存：" + FrStreamHooks.customSummary(game) + "，几秒后自动重连", Toast.LENGTH_LONG).show();
            FrStreamHooks.setPendingReconnect(host);
            game.finish();
        });
        box.addView(ok, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
    }

    /** 旧版电脑端（拿不到屏幕画面）：文字列表 */
    private void pickDisplayList(List<String[]> ds) {
        closeSheet();
        String[] labels = new String[ds.size()];
        for (int i = 0; i < ds.size(); i++) labels[i] = ds.get(i)[1];
        new AlertDialog.Builder(game).setTitle("切换被控端屏幕").setItems(labels, (d, which) -> {
            closeSheet();
            String id = ds.get(which)[0];
            int idx = Integer.parseInt(ds.get(which)[2]);
            if (true) { switchTo(id, idx); return; }
            NvConnection c = conn();
            if (idx >= 0 && idx < 12 && c != null) {
                // 画面不断：Sunshine 自带的快捷键 Ctrl+Alt+Shift+F(序号+1) 当场换采集的屏幕，再告诉电脑记下来（下次连接还在这块屏）
                new Thread(() -> {
                    FrStreamHooks.sendCombo(c, com.limelight.binding.input.KeyboardTranslator.VK_F1 + idx);
                    FrHub.get(game).rememberDisplay(host, id);
                }, "FrSwitchDisplay").start();
                Toast.makeText(game, "已切换屏幕", Toast.LENGTH_SHORT).show();
                return;
            }
            // 旧版电脑端（不知道屏幕序号）：改设置并重启 Sunshine，再自动重连
            Toast.makeText(game, "正在切换屏幕，约 5 秒后自动重连（电脑端升级到 0.17.2 后可不断线切换）", Toast.LENGTH_LONG).show();
            new Thread(() -> {
                String err = FrHub.get(game).switchDisplay(host, id);
                game.runOnUiThread(() -> {
                    if (err != null) { Toast.makeText(game, "切换失败：" + err, Toast.LENGTH_LONG).show(); return; }
                    FrStreamHooks.setPendingReconnect(host);
                    game.finish(); // 对方 Sunshine 已重启，回首页自动重连
                });
            }).start();
        }).show();
    }

    // =============================== 组合键 ===============================
    private void combo(int modVk, int keyVk) {
        NvConnection c = conn();
        if (c == null) return;
        new Thread(() -> {
            byte meta = KeyboardPacket.MODIFIER_META;
            c.sendKeyboardInput((short) modVk, KeyboardPacket.KEY_DOWN, meta, (byte) 0);
            c.sendKeyboardInput((short) keyVk, KeyboardPacket.KEY_DOWN, meta, (byte) 0);
            try { Thread.sleep(40); } catch (InterruptedException ignored) { }
            c.sendKeyboardInput((short) keyVk, KeyboardPacket.KEY_UP, meta, (byte) 0);
            c.sendKeyboardInput((short) modVk, KeyboardPacket.KEY_UP, (byte) 0, (byte) 0);
        }).start();
    }
}
