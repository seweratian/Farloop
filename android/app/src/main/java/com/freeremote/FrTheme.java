package com.freeremote;

import android.content.Context;
import android.content.res.Configuration;

/**
 * 外观：跟随系统 / 浅色 / 深色（“我的 → 外观”）。
 * Farloop 自己的页面在 attachBaseContext 里用 wrap() 包一层，覆盖系统的深色模式设置；
 * 主题（FarloopTheme = Material3 DayNight）和 Ui 的颜色都跟着这个配置走。
 */
public final class FrTheme {
    public static final int SYSTEM = 0, LIGHT = 1, DARK = 2;

    private FrTheme() {}

    public static int mode(Context c) {
        return c.getSharedPreferences("freeremote", Context.MODE_PRIVATE).getInt("theme", SYSTEM);
    }

    public static void setMode(Context c, int m) {
        c.getSharedPreferences("freeremote", Context.MODE_PRIVATE).edit().putInt("theme", m).apply();
    }

    public static Context wrap(Context base) {
        int m = mode(base);
        if (m == SYSTEM) return base;
        Configuration cfg = new Configuration(base.getResources().getConfiguration());
        cfg.uiMode = (cfg.uiMode & ~Configuration.UI_MODE_NIGHT_MASK)
                | (m == DARK ? Configuration.UI_MODE_NIGHT_YES : Configuration.UI_MODE_NIGHT_NO);
        return base.createConfigurationContext(cfg);
    }
}
