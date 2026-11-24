package com.freeremote;

import android.app.Activity;
import android.os.Bundle;
import android.view.View;
import android.view.ViewGroup;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import com.limelight.R;

import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

/** 设备详情：系统、处理器、内存、显卡、主板、硬盘、显示器、网络、运行时长、版本（电脑端 RemoteTools.SysInfo） */
public class DeviceInfoActivity extends Activity {
    @Override
    protected void attachBaseContext(android.content.Context b) { super.attachBaseContext(FrTheme.wrap(b)); }

    public static final String EXTRA_JSON = "json";
    private Ui ui;

    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        ui = new Ui(this);
        ui.applyWindow(this);
        JsonObject j = JsonParser.parseString(getIntent().getStringExtra(EXTRA_JSON)).getAsJsonObject();

        LinearLayout root = ui.col();
        root.setPadding(ui.dp(16), ui.dp(10), ui.dp(16), ui.dp(28));
        LinearLayout top = ui.row();
        top.setPadding(0, ui.dp(8), 0, ui.dp(6));
        ImageView back = ui.icon(R.drawable.fr_ic_back, ui.text, 26);
        back.setPadding(ui.dp(4), ui.dp(4), ui.dp(4), ui.dp(4));
        back.setLayoutParams(new LinearLayout.LayoutParams(ui.dp(40), ui.dp(40)));
        back.setOnClickListener(v -> finish());
        top.addView(back);
        root.addView(top);
        TextView t = ui.title("设备详情");
        t.setPadding(ui.dp(4), ui.dp(6), 0, ui.dp(4));
        root.addView(t);
        TextView n = ui.text(s(j, "Name"), 14, ui.sub, false);
        n.setPadding(ui.dp(4), ui.dp(6), 0, ui.dp(6));
        root.addView(n);

        long total = j.get("RamTotal").getAsLong(), avail = j.get("RamAvail").getAsLong();
        double cpu = j.get("CpuLoad").getAsDouble();
        root.addView(ui.section("实时"));
        root.addView(ui.group(
                meter("处理器占用", cpu, String.format(Locale.US, "%.0f%%", cpu * 100)),
                meter("内存占用", total > 0 ? (total - avail) / (double) total : 0, Ui.size(total - avail) + " / " + Ui.size(total))));

        root.addView(ui.section("系统"));
        root.addView(ui.group(row("操作系统", s(j, "Os")), row("已运行", uptime(j.get("UptimeSec").getAsLong())), row("版本", s(j, "Versions"))));

        root.addView(ui.section("硬件"));
        List<View> hw = new ArrayList<>();
        hw.add(row("处理器", s(j, "Cpu") + "（" + j.get("Cores").getAsInt() + " 线程）"));
        hw.add(row("内存", Ui.size(total)));
        for (String g : arr(j, "Gpus")) hw.add(row("显卡", g));
        if (!s(j, "Board").isEmpty()) hw.add(row("主板", s(j, "Board")));
        for (String d : arr(j, "Displays")) hw.add(row("显示器", d));
        root.addView(ui.group(hw.toArray(new View[0])));

        List<View> disks = new ArrayList<>();
        for (String d : arr(j, "Disks")) disks.add(row("磁盘", d));
        if (!disks.isEmpty()) { root.addView(ui.section("存储")); root.addView(ui.group(disks.toArray(new View[0]))); }

        root.addView(ui.section("网络"));
        root.addView(ui.group(row("局域网 IP", s(j, "LanIp")), row("虚拟 IP", s(j, "VirtualIp"))));

        ScrollView sv = new ScrollView(this);
        sv.setBackgroundColor(ui.bg);
        sv.addView(root);
        setContentView(sv);
    }

    private View row(String k, String v) {
        LinearLayout r = ui.col();
        r.setPadding(ui.dp(18), ui.dp(14), ui.dp(18), ui.dp(14));
        r.addView(ui.text(k, 13, ui.sub, false));
        TextView tv = ui.text(v.isEmpty() ? "—" : v, 15.5f, ui.text, false);
        tv.setPadding(0, ui.dp(6), 0, 0);
        tv.setTextIsSelectable(true);
        r.addView(tv);
        return r;
    }

    private View meter(String k, double frac, String v) {
        LinearLayout r = ui.col();
        r.setPadding(ui.dp(18), ui.dp(14), ui.dp(18), ui.dp(16));
        LinearLayout top = ui.row();
        top.addView(ui.text(k, 15.5f, ui.text, false), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        top.addView(ui.text(v, 14, ui.sub, false));
        r.addView(top);
        LinearLayout bar = ui.row();
        bar.setBackground(Ui.round(ui.card2, ui.dp(4)));
        View fill = new View(this);
        fill.setBackground(Ui.round(frac > 0.85 ? ui.red : ui.accent, ui.dp(4)));
        bar.addView(fill, new LinearLayout.LayoutParams(0, ui.dp(8), (float) Math.max(0.01, Math.min(1, frac))));
        View rest = new View(this);
        bar.addView(rest, new LinearLayout.LayoutParams(0, ui.dp(8), (float) Math.max(0.0001, 1 - Math.min(1, frac))));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ui.dp(8));
        lp.topMargin = ui.dp(10);
        r.addView(bar, lp);
        return r;
    }

    private static String s(JsonObject j, String k) { return j.has(k) && !j.get(k).isJsonNull() ? j.get(k).getAsString() : ""; }

    private static List<String> arr(JsonObject j, String k) {
        List<String> out = new ArrayList<>();
        if (j.has(k) && j.get(k).isJsonArray()) for (JsonElement e : (JsonArray) j.get(k)) out.add(e.getAsString());
        return out;
    }

    private static String uptime(long s) {
        long d = s / 86400, hh = s % 86400 / 3600, m = s % 3600 / 60;
        return (d > 0 ? d + " 天 " : "") + hh + " 小时 " + m + " 分";
    }
}
