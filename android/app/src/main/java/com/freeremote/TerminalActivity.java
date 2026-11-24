package com.freeremote;

import android.app.Activity;
import android.graphics.Color;
import android.graphics.Typeface;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.InputType;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowManager;
import android.view.inputmethod.EditorInfo;
import android.widget.EditText;
import android.widget.HorizontalScrollView;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import com.limelight.R;

import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;

/**
 * 远程终端：在电脑上开一个 PowerShell（以 FreeRemote 的管理员权限运行），一行一行执行命令。
 * 支持 cd、ipconfig、脚本等普通命令；需要全屏交互界面的程序（vim 等）不支持。
 */
public class TerminalActivity extends Activity implements FrHub.Listener {
    public static final String EXTRA_IP = "ip", EXTRA_NAME = "name";
    private static final int MAX_CHARS = 200_000;

    private Ui ui;
    private final Handler h = new Handler(Looper.getMainLooper());
    private String ip, name, termId, cwd = "";
    private TextView out, promptView;
    private ScrollView scroll;
    private EditText input;
    private final StringBuilder buf = new StringBuilder();
    private final List<String> history = new ArrayList<>();
    private int histPos;
    private boolean busy;

    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        ui = new Ui(this);
        getWindow().setStatusBarColor(0xFF0C0C0E);
        getWindow().setNavigationBarColor(0xFF0C0C0E);
        getWindow().getDecorView().setSystemUiVisibility(0);
        getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);
        ip = getIntent().getStringExtra(EXTRA_IP);
        name = getIntent().getStringExtra(EXTRA_NAME);
        build();
        FrHub.get(this).addListener(this);
        append("正在连接 " + name + " 的 PowerShell…\n");
        new Thread(() -> {
            if (!FrHub.get(this).supportsTools(ip)) { h.post(() -> append("电脑端需要升级到 Farloop 0.12.1 以上才能使用终端。\n")); return; }
            String err = FrHub.get(this).send(ip, FrHub.TERM_OPEN, new byte[0]);
            if (err != null) h.post(() -> append("连接失败：" + err + "\n"));
        }).start();
    }

    @Override
    protected void onDestroy() {
        super.onDestroy();
        FrHub.get(this).removeListener(this);
        if (termId != null) {
            final String id = termId;
            new Thread(() -> { JsonObject m = new JsonObject(); m.addProperty("Id", id); FrHub.get(this).sendJson(ip, FrHub.TERM_CLOSE, m); }).start();
        }
    }

    private void build() {
        LinearLayout root = ui.col();
        root.setBackgroundColor(0xFF0C0C0E);

        LinearLayout top = ui.row();
        top.setPadding(ui.dp(8), ui.dp(10), ui.dp(12), ui.dp(10));
        ImageView back = ui.icon(R.drawable.fr_ic_back, Color.WHITE, 24);
        back.setPadding(ui.dp(4), ui.dp(4), ui.dp(4), ui.dp(4));
        back.setLayoutParams(new LinearLayout.LayoutParams(ui.dp(38), ui.dp(38)));
        back.setOnClickListener(v -> finish());
        top.addView(back);
        LinearLayout tt = ui.col();
        tt.setPadding(ui.dp(8), 0, 0, 0);
        tt.addView(ui.text("终端 · " + name, 17, Color.WHITE, true));
        TextView sub = ui.text("Windows PowerShell（管理员权限）", 12, 0xFFF0A040, false);
        sub.setPadding(0, ui.dp(4), 0, 0);
        tt.addView(sub);
        top.addView(tt, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        root.addView(top);

        out = new TextView(this);
        out.setTypeface(Typeface.MONOSPACE);
        out.setTextSize(12.5f);
        out.setTextColor(0xFFE6E6E6);
        out.setTextIsSelectable(true);
        out.setPadding(ui.dp(12), ui.dp(6), ui.dp(12), ui.dp(10));
        scroll = new ScrollView(this);
        HorizontalScrollView hs = new HorizontalScrollView(this);
        hs.addView(out);
        scroll.addView(hs);
        root.addView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1));

        // 快捷键行
        LinearLayout keys = ui.row();
        keys.setPadding(ui.dp(8), ui.dp(4), ui.dp(8), ui.dp(4));
        keys.addView(key("↑", v -> hist(-1)));
        keys.addView(key("↓", v -> hist(1)));
        keys.addView(key("Ctrl+C", v -> interrupt()));
        keys.addView(key("清屏", v -> { buf.setLength(0); out.setText(""); }));
        keys.addView(key("dir", v -> { input.setText("dir"); input.setSelection(3); }));
        keys.addView(key("cd ..", v -> { input.setText("cd .."); input.setSelection(5); }));
        root.addView(keys);

        LinearLayout bar = ui.row();
        bar.setPadding(ui.dp(10), ui.dp(6), ui.dp(10), ui.dp(10));
        promptView = ui.text("PS>", 13, 0xFF5EB3FF, true);
        promptView.setTypeface(Typeface.MONOSPACE, Typeface.BOLD);
        promptView.setMaxWidth(ui.dp(150));
        promptView.setSingleLine(true);
        promptView.setEllipsize(android.text.TextUtils.TruncateAt.START);
        bar.addView(promptView);
        input = new EditText(this);
        input.setTypeface(Typeface.MONOSPACE);
        input.setTextColor(Color.WHITE);
        input.setHintTextColor(0x66FFFFFF);
        input.setHint("输入命令，回车执行");
        input.setSingleLine(true);
        input.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS | InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD);
        input.setImeOptions(EditorInfo.IME_ACTION_SEND);
        input.setBackground(Ui.round(0xFF1E1E22, ui.dp(12)));
        input.setPadding(ui.dp(12), ui.dp(10), ui.dp(12), ui.dp(10));
        input.setOnEditorActionListener((v, actionId, e) -> {
            if (actionId == EditorInfo.IME_ACTION_SEND || (e != null && e.getKeyCode() == KeyEvent.KEYCODE_ENTER && e.getAction() == KeyEvent.ACTION_DOWN)) { run(); return true; }
            return false;
        });
        LinearLayout.LayoutParams ilp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1);
        ilp.leftMargin = ui.dp(8);
        bar.addView(input, ilp);
        TextView send = ui.pill("执行");
        LinearLayout.LayoutParams slp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ui.dp(42));
        slp.leftMargin = ui.dp(8);
        send.setOnClickListener(v -> run());
        bar.addView(send, slp);
        root.addView(bar);
        setContentView(root);
    }

    private View key(String label, View.OnClickListener l) {
        TextView t = ui.text(label, 13, 0xFFDDDDDD, true);
        t.setGravity(Gravity.CENTER);
        t.setBackground(Ui.round(0xFF26262B, ui.dp(10)));
        t.setPadding(ui.dp(12), ui.dp(8), ui.dp(12), ui.dp(8));
        t.setOnClickListener(l);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.rightMargin = ui.dp(8);
        t.setLayoutParams(lp);
        return t;
    }

    private void run() {
        if (termId == null) return;
        String line = input.getText().toString();
        input.setText("");
        if (line.trim().equalsIgnoreCase("cls") || line.trim().equalsIgnoreCase("clear")) { buf.setLength(0); out.setText(""); send(""); return; }
        if (!line.trim().isEmpty()) { history.add(line); histPos = history.size(); }
        append("PS " + cwd + "> " + line + "\n");
        busy = true;
        send(line);
    }

    private void send(String line) {
        final String id = termId;
        new Thread(() -> {
            JsonObject m = new JsonObject();
            m.addProperty("Id", id);
            m.addProperty("Line", line);
            String err = FrHub.get(this).sendJson(ip, FrHub.TERM_INPUT, m);
            if (err != null) h.post(() -> append("[发送失败：" + err + "]\n"));
        }).start();
    }

    private void interrupt() {
        if (termId == null) return;
        append("^C\n[正在结束当前命令…]\n");
        final String id = termId;
        termId = null;
        new Thread(() -> { JsonObject m = new JsonObject(); m.addProperty("Id", id); FrHub.get(this).sendJson(ip, FrHub.TERM_INTERRUPT, m); }).start();
    }

    private void hist(int d) {
        if (history.isEmpty()) return;
        histPos = Math.max(0, Math.min(history.size(), histPos + d));
        String s = histPos < history.size() ? history.get(histPos) : "";
        input.setText(s);
        input.setSelection(s.length());
    }

    @Override
    public void onMessage(String from, int type, byte[] payload) {
        if (!from.equals(ip)) return;
        JsonObject j = JsonParser.parseString(new String(payload, StandardCharsets.UTF_8)).getAsJsonObject();
        h.post(() -> {
            if (type == FrHub.TERM_OPENED) {
                termId = j.get("Id").getAsString();
                cwd = j.get("Cwd").getAsString();
                promptView.setText("PS " + cwd + ">");
                append("已连接。以 Farloop 的管理员权限运行，请谨慎执行删除类命令。\n");
            } else if (type == FrHub.TERM_OUTPUT) {
                if (termId != null && !termId.equals(j.get("Id").getAsString())) return;
                append(j.get("Text").getAsString());
                if (j.has("Prompt") && !j.get("Prompt").isJsonNull()) {
                    cwd = j.get("Prompt").getAsString();
                    promptView.setText("PS " + cwd + ">");
                    busy = false;
                }
            }
        });
    }

    private void append(String s) {
        buf.append(s);
        if (buf.length() > MAX_CHARS) buf.delete(0, buf.length() - MAX_CHARS);
        out.setText(buf);
        scroll.post(() -> scroll.fullScroll(View.FOCUS_DOWN));
    }
}
