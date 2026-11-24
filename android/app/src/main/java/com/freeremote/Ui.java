package com.freeremote;

import android.app.Activity;
import android.content.Context;
import android.content.res.ColorStateList;
import android.content.res.Configuration;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.Drawable;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;
import android.os.Build;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.Window;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;

/**
 * 小型界面工具：参考小米 HyperOS 的风格——浅灰底、白色大圆角卡片、大号粗标题、
 * 胶囊按钮、分组设置行、分段选择器、圆润开关；跟随系统深色模式。
 */
public final class Ui {
    public final Context c;
    public final boolean dark;
    public final int bg, card, card2, text, sub, line, accent, accentSoft, green, orange, red;

    public Ui(Context c) {
        this.c = c;
        dark = (c.getResources().getConfiguration().uiMode & Configuration.UI_MODE_NIGHT_MASK) == Configuration.UI_MODE_NIGHT_YES;
        bg = dark ? 0xFF000000 : 0xFFF5F5F7;
        card = dark ? 0xFF1C1C1E : 0xFFFFFFFF;
        card2 = dark ? 0xFF2C2C2E : 0xFFF0F1F4;
        text = dark ? 0xFFF2F2F7 : 0xFF111111;
        sub = dark ? 0xFF8E8E93 : 0xFF8A8A8F;
        line = dark ? 0x22FFFFFF : 0x14000000;
        accent = 0xFF277AF7;
        accentSoft = dark ? 0x33277AF7 : 0x1A277AF7;
        green = 0xFF22B35E;
        orange = 0xFFF08A00;
        red = 0xFFF0443A;
    }

    /** 沉浸式：状态栏 / 导航栏跟背景同色，浅色模式用深色图标 */
    public void applyWindow(Activity a) {
        Window w = a.getWindow();
        w.setStatusBarColor(bg);
        w.setNavigationBarColor(bg);
        int flags = 0;
        if (!dark) {
            flags |= View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) flags |= View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR;
        }
        w.getDecorView().setSystemUiVisibility(flags);
    }

    public int dp(float v) { return (int) TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v, c.getResources().getDisplayMetrics()); }

    public static GradientDrawable round(int color, float radiusPx) {
        GradientDrawable g = new GradientDrawable();
        g.setColor(color);
        g.setCornerRadius(radiusPx);
        return g;
    }

    /** 带按压波纹的圆角背景 */
    public Drawable press(int color, float radiusPx) {
        return new RippleDrawable(ColorStateList.valueOf(dark ? 0x33FFFFFF : 0x1A000000), round(color, radiusPx), round(Color.WHITE, radiusPx));
    }

    public LinearLayout col() { LinearLayout l = new LinearLayout(c); l.setOrientation(LinearLayout.VERTICAL); return l; }

    public LinearLayout row() {
        LinearLayout l = new LinearLayout(c);
        l.setOrientation(LinearLayout.HORIZONTAL);
        l.setGravity(Gravity.CENTER_VERTICAL);
        return l;
    }

    public TextView text(CharSequence s, float sp, int color, boolean bold) {
        TextView t = new TextView(c);
        t.setText(s);
        t.setTextSize(TypedValue.COMPLEX_UNIT_SP, sp);
        t.setTextColor(color);
        t.setIncludeFontPadding(false);
        if (bold) t.setTypeface(Typeface.create("sans-serif-medium", Typeface.BOLD));
        return t;
    }

    public TextView title(String s) {
        TextView t = text(s, 30, text, true);
        t.setLetterSpacing(-0.01f);
        return t;
    }

    public TextView section(String s) {
        TextView t = text(s, 13, sub, false);
        t.setPadding(dp(6), dp(18), 0, dp(8));
        return t;
    }

    public ImageView icon(int res, int tint, int sizeDp) {
        ImageView i = new ImageView(c);
        i.setImageResource(res);
        i.setImageTintList(ColorStateList.valueOf(tint));
        i.setLayoutParams(new LinearLayout.LayoutParams(dp(sizeDp), dp(sizeDp)));
        return i;
    }

    /** 圆角方块里放图标（设备卡片、设置行左侧） */
    public FrameLayout iconTile(int res, int tint, int tileColor, int sizeDp, int iconDp) {
        FrameLayout f = new FrameLayout(c);
        f.setBackground(round(tileColor, dp(sizeDp * 0.3f)));
        ImageView i = icon(res, tint, iconDp);
        f.addView(i, new FrameLayout.LayoutParams(dp(iconDp), dp(iconDp), Gravity.CENTER));
        f.setLayoutParams(new LinearLayout.LayoutParams(dp(sizeDp), dp(sizeDp)));
        return f;
    }

    public LinearLayout card() {
        LinearLayout l = col();
        l.setBackground(round(card, dp(24)));
        return l;
    }

    /** 主按钮：蓝色胶囊 */
    public TextView pill(String s) {
        TextView b = text(s, 15, Color.WHITE, true);
        b.setGravity(Gravity.CENTER);
        b.setBackground(press(accent, dp(100)));
        b.setPadding(dp(20), 0, dp(20), 0);
        return b;
    }

    /** 次按钮：浅蓝胶囊 */
    public TextView softPill(String s) {
        TextView b = text(s, 15, accent, true);
        b.setGravity(Gravity.CENTER);
        b.setBackground(press(accentSoft, dp(100)));
        b.setPadding(dp(18), 0, dp(18), 0);
        return b;
    }

    /** 设置行：左图标 + 标题/说明 + 右侧控件（箭头 / 开关 / 文字） */
    public LinearLayout settingRow(int iconRes, int iconTint, String title, String desc, View right) {
        LinearLayout r = row();
        r.setPadding(dp(16), dp(14), dp(14), dp(14));
        r.setMinimumHeight(dp(60));
        if (iconRes != 0) {
            FrameLayout tile = iconTile(iconRes, Color.WHITE, iconTint, 32, 18);
            r.addView(tile);
            ((LinearLayout.LayoutParams) tile.getLayoutParams()).rightMargin = dp(14);
        }
        LinearLayout texts = col();
        texts.addView(text(title, 16, text, false));
        if (desc != null && !desc.isEmpty()) {
            TextView d = text(desc, 12.5f, sub, false);
            d.setPadding(0, dp(4), 0, 0);
            texts.addView(d);
        }
        r.addView(texts, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        if (right != null) r.addView(right);
        return r;
    }

    public ImageView chevron() { return icon(com.limelight.R.drawable.fr_ic_chevron, sub, 22); }

    /** 分组卡片：多行之间加细分隔线 */
    public LinearLayout group(View... rows) {
        LinearLayout g = card();
        for (int i = 0; i < rows.length; i++) {
            if (i > 0) {
                View sep = new View(c);
                sep.setBackgroundColor(line);
                LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 1);
                lp.leftMargin = dp(62);
                g.addView(sep, lp);
            }
            g.addView(rows[i]);
        }
        return g;
    }

    /** HyperOS 风格开关（圆角轨道 + 白色圆点） */
    public static final class Switch extends FrameLayout {
        private final View knob;
        private final Ui ui;
        private boolean on;
        private OnChange listener;

        public interface OnChange { void changed(boolean on); }

        public Switch(Ui ui, boolean initial, OnChange l) {
            super(ui.c);
            this.ui = ui;
            listener = l;
            knob = new View(ui.c);
            knob.setBackground(round(Color.WHITE, ui.dp(12)));
            knob.setElevation(ui.dp(1));
            addView(knob, new FrameLayout.LayoutParams(ui.dp(24), ui.dp(24), Gravity.CENTER_VERTICAL));
            setLayoutParams(new LinearLayout.LayoutParams(ui.dp(50), ui.dp(30)));
            setPadding(ui.dp(3), 0, ui.dp(3), 0);
            set(initial, false);
            setOnClickListener(v -> { set(!on, true); if (listener != null) listener.changed(on); });
        }

        public boolean isOn() { return on; }

        public void set(boolean v, boolean animate) {
            on = v;
            setBackground(round(on ? ui.accent : (ui.dark ? 0xFF3A3A3C : 0xFFE1E2E6), ui.dp(15)));
            float x = on ? ui.dp(20) : 0;
            if (animate) knob.animate().translationX(x).setDuration(160).start(); else knob.setTranslationX(x);
        }
    }

    /** 分段选择器（如“屏幕触控 | 鼠标指针”） */
    public LinearLayout segmented(String[] labels, int selected, java.util.function.IntConsumer onPick, int bgColor, int selColor, int textColor, int selTextColor) {
        LinearLayout bar = row();
        bar.setBackground(round(bgColor, dp(16)));
        bar.setPadding(dp(4), dp(4), dp(4), dp(4));
        final TextView[] items = new TextView[labels.length];
        for (int i = 0; i < labels.length; i++) {
            final int idx = i;
            TextView t = text(labels[i], 15, textColor, true);
            t.setGravity(Gravity.CENTER);
            t.setMinHeight(dp(44));
            t.setOnClickListener(v -> {
                for (int j = 0; j < items.length; j++) {
                    items[j].setBackground(j == idx ? round(selColor, dp(12)) : null);
                    items[j].setTextColor(j == idx ? selTextColor : textColor);
                }
                onPick.accept(idx);
            });
            t.setBackground(i == selected ? round(selColor, dp(12)) : null);
            t.setTextColor(i == selected ? selTextColor : textColor);
            items[i] = t;
            bar.addView(t, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        }
        return bar;
    }

    public LinearLayout.LayoutParams lp(int w, int h, int l, int t, int r, int b) {
        LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(w, h);
        p.setMargins(dp(l), dp(t), dp(r), dp(b));
        return p;
    }

    public LinearLayout.LayoutParams match(int t, int b) { return lp(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT, 0, t, 0, b); }

    public static String size(long b) {
        if (b >= 1L << 30) return String.format(java.util.Locale.US, "%.2f GB", b / (double) (1L << 30));
        if (b >= 1L << 20) return String.format(java.util.Locale.US, "%.1f MB", b / (double) (1L << 20));
        if (b >= 1L << 10) return String.format(java.util.Locale.US, "%.0f KB", b / 1024.0);
        return b + " B";
    }
}
