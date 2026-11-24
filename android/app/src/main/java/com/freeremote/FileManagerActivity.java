package com.freeremote;

import android.Manifest;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Environment;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.text.TextUtils;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.BaseAdapter;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ListView;
import android.widget.PopupMenu;
import android.widget.ProgressBar;
import android.widget.TextView;
import android.widget.Toast;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.limelight.R;

import java.io.File;
import java.text.SimpleDateFormat;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Date;
import java.util.Deque;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;

/**
 * 双栏文件管理（参考 MT 管理器）：左边手机，右边电脑。
 * 点文件夹进入，点文件 / 长按 = 选中；底部 ‹ › + ⇄ ↑；右上角 ⋮ 更多操作。
 * 复制方向由当前栏决定：在手机栏选中 → 复制到电脑当前文件夹；在电脑栏选中 → 复制到手机当前文件夹。
 */
public class FileManagerActivity extends Activity {
    @Override
    protected void attachBaseContext(android.content.Context b) { super.attachBaseContext(FrTheme.wrap(b)); }

    public static final String EXTRA_IP = "ip", EXTRA_NAME = "name";
    private static final SimpleDateFormat DATE = new SimpleDateFormat("yy-MM-dd HH:mm", Locale.US);

    /** 一个条目（手机或电脑） */
    static final class Entry {
        String name, path; boolean dir; long size, modified;
    }

    /** 一栏 */
    final class Pane {
        final boolean remote;
        String path;                      // 手机：绝对路径；电脑：Windows 路径（"" = 根）
        String parent;                    // 电脑栏由对方告知
        final List<Entry> items = new ArrayList<>();
        final Set<String> selected = new HashSet<>();
        final Deque<String> back = new ArrayDeque<>(), fwd = new ArrayDeque<>();
        ListView list;
        TextView header;
        LinearLayout box;
        final Adapter adapter = new Adapter(this);
        String error;

        Pane(boolean remote, String start) { this.remote = remote; path = start; }
    }

    private Ui ui;
    private final Handler h = new Handler(Looper.getMainLooper());
    private String ip, pcName;
    private Pane phone, pc, active;
    private TextView pathText, statText;
    private LinearLayout progressBox;
    private ProgressBar progressBar;
    private TextView progressText;

    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        ui = new Ui(this);
        ui.applyWindow(this);
        ip = getIntent().getStringExtra(EXTRA_IP);
        pcName = getIntent().getStringExtra(EXTRA_NAME);
        if (pcName == null) pcName = ip;
        phone = new Pane(false, Environment.getExternalStorageDirectory().getAbsolutePath());
        pc = new Pane(true, "");
        active = phone;
        build();
        ensureStoragePermission();
        // 安卓 13+：通知栏显示传输进度需要通知权限
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission("android.permission.POST_NOTIFICATIONS") != PackageManager.PERMISSION_GRANTED)
            requestPermissions(new String[]{"android.permission.POST_NOTIFICATIONS"}, 2);
        load(phone, phone.path, false);
        load(pc, "", false);
    }

    @Override
    protected void onResume() {
        super.onResume();
        if (phone != null && hasStoragePermission()) load(phone, phone.path, false);
    }

    // =============================== 界面 ===============================
    private void build() {
        LinearLayout root = ui.col();
        root.setBackgroundColor(ui.bg);

        // 顶部：返回 + 路径 + 统计 + 更多
        LinearLayout top = ui.row();
        top.setPadding(ui.dp(8), ui.dp(10), ui.dp(8), ui.dp(10));
        ImageView backBtn = ui.icon(R.drawable.fr_ic_back, ui.text, 24);
        backBtn.setPadding(ui.dp(4), ui.dp(4), ui.dp(4), ui.dp(4));
        backBtn.setLayoutParams(new LinearLayout.LayoutParams(ui.dp(40), ui.dp(40)));
        backBtn.setBackground(ui.press(Color.TRANSPARENT, ui.dp(20)));
        backBtn.setOnClickListener(v -> finish());
        top.addView(backBtn);
        LinearLayout titles = ui.col();
        titles.setPadding(ui.dp(6), 0, 0, 0);
        pathText = ui.text("", 17, ui.text, true);
        pathText.setSingleLine(true);
        pathText.setEllipsize(TextUtils.TruncateAt.START);
        statText = ui.text("", 12.5f, ui.sub, false);
        statText.setPadding(0, ui.dp(4), 0, 0);
        titles.addView(pathText);
        titles.addView(statText);
        top.addView(titles, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        ImageView more = ui.icon(R.drawable.fr_ic_tune, ui.text, 24);
        more.setPadding(ui.dp(8), ui.dp(8), ui.dp(8), ui.dp(8));
        more.setLayoutParams(new LinearLayout.LayoutParams(ui.dp(40), ui.dp(40)));
        more.setBackground(ui.press(Color.TRANSPARENT, ui.dp(20)));
        more.setOnClickListener(this::showMore);
        top.addView(more);
        root.addView(top);

        // 两栏
        LinearLayout panes = ui.row();
        panes.setGravity(Gravity.TOP);
        panes.setPadding(ui.dp(8), 0, ui.dp(8), 0);
        panes.addView(buildPane(phone, "手机"), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MATCH_PARENT, 1));
        View gap = new View(this);
        panes.addView(gap, new LinearLayout.LayoutParams(ui.dp(6), 1));
        panes.addView(buildPane(pc, pcName), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MATCH_PARENT, 1));
        root.addView(panes, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1));

        // 传输进度
        progressBox = ui.col();
        progressBox.setPadding(ui.dp(18), ui.dp(10), ui.dp(18), ui.dp(4));
        progressText = ui.text("", 13, ui.sub, false);
        progressBar = new ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal);
        progressBar.setMax(1000);
        progressBar.setProgressTintList(android.content.res.ColorStateList.valueOf(ui.accent));
        progressBox.addView(progressText);
        progressBox.addView(progressBar, ui.match(6, 0));
        progressBox.setVisibility(View.GONE);
        root.addView(progressBox);

        // 底部工具行：‹ › + ⇄ ↑
        LinearLayout bar = ui.row();
        bar.setBackgroundColor(ui.card);
        bar.setPadding(0, ui.dp(6), 0, ui.dp(6));
        int[] icons = {R.drawable.fr_ic_back, R.drawable.fr_ic_forward, R.drawable.fr_ic_new_folder, R.drawable.fr_ic_route, R.drawable.fr_ic_up};
        String[] labels = {"后退", "前进", "新建", "复制到对面", "上一级"};
        View.OnClickListener[] acts = {v -> history(true), v -> history(false), v -> mkdir(), v -> copyAcross(), v -> up()};
        for (int i = 0; i < icons.length; i++) {
            LinearLayout cell = ui.col();
            cell.setGravity(Gravity.CENTER);
            cell.setPadding(0, ui.dp(6), 0, ui.dp(6));
            cell.setBackground(ui.press(Color.TRANSPARENT, ui.dp(16)));
            ImageView ic = ui.icon(icons[i], i == 3 ? ui.accent : ui.text, 26);
            cell.addView(ic);
            TextView t = ui.text(labels[i], 11, ui.sub, false);
            t.setGravity(Gravity.CENTER);
            t.setPadding(0, ui.dp(3), 0, 0);
            cell.addView(t);
            cell.setOnClickListener(acts[i]);
            bar.addView(cell, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        }
        root.addView(bar);
        setContentView(root);
        setActive(phone);
    }

    private View buildPane(Pane p, String label) {
        LinearLayout box = ui.card();
        box.setPadding(0, ui.dp(10), 0, 0);
        TextView head = ui.text(label, 13, ui.sub, true);
        head.setPadding(ui.dp(14), 0, ui.dp(10), ui.dp(6));
        head.setSingleLine(true);
        head.setEllipsize(TextUtils.TruncateAt.END);
        box.addView(head);
        ListView lv = new ListView(this);
        lv.setDivider(null);
        lv.setSelector(android.R.color.transparent);
        lv.setAdapter(p.adapter);
        lv.setOnItemClickListener((parent, view, pos, id) -> { setActive(p); onTap(p, pos); });
        lv.setOnItemLongClickListener((parent, view, pos, id) -> { setActive(p); toggle(p, p.items.get(pos)); return true; });
        lv.setOnTouchListener((v, e) -> { if (active != p) setActive(p); return false; });
        box.addView(lv, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1));
        p.list = lv;
        p.header = head;
        p.box = box;
        box.setOnClickListener(v -> setActive(p));
        return box;
    }

    private void setActive(Pane p) {
        active = p;
        for (Pane x : new Pane[]{phone, pc}) {
            if (x.box == null) continue;
            GradientDrawableHelper.outline(x.box, ui, x == p);
            x.header.setTextColor(x == p ? ui.accent : ui.sub);
        }
        updateHeader();
    }

    private void updateHeader() {
        Pane p = active;
        String shown = p.remote ? (p.path.isEmpty() ? pcName + "：此电脑" : p.path) : p.path;
        pathText.setText(shown);
        int dirs = 0, files = 0;
        for (Entry e : p.items) { if (e.dir) dirs++; else files++; }
        String sel = p.selected.isEmpty() ? "" : "  ·  已选 " + p.selected.size() + " 项";
        statText.setText(p.error != null ? p.error : ("文件夹: " + dirs + "  文件: " + files + sel));
        statText.setTextColor(p.error != null ? ui.red : ui.sub);
    }

    // =============================== 列表 ===============================
    final class Adapter extends BaseAdapter {
        final Pane p;
        Adapter(Pane p) { this.p = p; }
        @Override public int getCount() { return p.items.size(); }
        @Override public Object getItem(int i) { return p.items.get(i); }
        @Override public long getItemId(int i) { return i; }

        @Override
        public View getView(int i, View cv, ViewGroup parent) {
            Entry e = p.items.get(i);
            LinearLayout r = ui.row();
            r.setGravity(Gravity.TOP);
            r.setPadding(ui.dp(10), ui.dp(9), ui.dp(8), ui.dp(9));
            boolean sel = p.selected.contains(e.path);
            r.setBackgroundColor(sel ? ui.accentSoft : Color.TRANSPARENT);
            int tile = e.dir ? (ui.dark ? 0xFF2D3A55 : 0xFFE3ECFF) : (ui.dark ? 0xFF3A3A3C : 0xFFEDEEF1);
            FrameLayout ic = ui.iconTile(e.dir ? R.drawable.fr_ic_folder : R.drawable.fr_ic_file, e.dir ? ui.accent : ui.sub, tile, 38, 20);
            r.addView(ic);
            LinearLayout t = ui.col();
            t.setPadding(ui.dp(10), 0, 0, 0);
            TextView name = ui.text(e.name, 14.5f, ui.text, false);
            name.setMaxLines(3);
            name.setEllipsize(TextUtils.TruncateAt.END);
            t.addView(name);
            String meta = e.modified > 0 ? DATE.format(new Date(e.modified)) : "";
            if (!e.dir) meta = (meta.isEmpty() ? "" : meta + "  ") + Ui.size(e.size);
            else if (e.size > 0 && p.remote && p.path.isEmpty()) meta = "已用 " + Ui.size(e.size);
            if (!meta.isEmpty()) {
                TextView m = ui.text(meta, 11.5f, ui.sub, false);
                m.setPadding(0, ui.dp(4), 0, 0);
                t.addView(m);
            }
            r.addView(t, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
            return r;
        }
    }

    private void onTap(Pane p, int pos) {
        Entry e = p.items.get(pos);
        if (!p.selected.isEmpty()) { toggle(p, e); return; } // 已在选择状态：继续选
        if (e.dir) load(p, e.path, true);
        else toggle(p, e);
    }

    private void toggle(Pane p, Entry e) {
        if (!p.selected.remove(e.path)) p.selected.add(e.path);
        p.adapter.notifyDataSetChanged();
        updateHeader();
    }

    private void load(Pane p, String path, boolean pushHistory) {
        new Thread(() -> {
            List<Entry> out = new ArrayList<>();
            String err = null, parent = null, realPath = path;
            try {
                if (p.remote) {
                    JsonObject r = FrHub.get(this).fsList(ip, path);
                    realPath = r.get("Path").getAsString();
                    parent = r.has("Parent") && !r.get("Parent").isJsonNull() ? r.get("Parent").getAsString() : null;
                    if (r.has("Error") && !r.get("Error").isJsonNull()) err = r.get("Error").getAsString();
                    JsonArray arr = r.getAsJsonArray("Entries");
                    for (JsonElement el : arr) {
                        JsonObject o = el.getAsJsonObject();
                        Entry e = new Entry();
                        e.name = o.get("Name").getAsString();
                        e.dir = o.get("Dir").getAsBoolean();
                        e.size = o.get("Size").getAsLong();
                        e.modified = o.get("Modified").getAsLong();
                        String full = o.has("Path") && !o.get("Path").isJsonNull() ? o.get("Path").getAsString() : null;
                        e.path = full != null ? full : (realPath.endsWith("\\") ? realPath + e.name : realPath + "\\" + e.name);
                        out.add(e);
                    }
                } else {
                    File d = new File(path);
                    File[] kids = d.listFiles();
                    if (kids == null) err = hasStoragePermission() ? "无法读取这个文件夹" : "需要“所有文件访问权限”（点右上角设置授予）";
                    else {
                        Arrays.sort(kids, (a, b) -> a.isDirectory() != b.isDirectory() ? (a.isDirectory() ? -1 : 1) : a.getName().compareToIgnoreCase(b.getName()));
                        for (File f : kids) {
                            Entry e = new Entry();
                            e.name = f.getName(); e.dir = f.isDirectory(); e.size = f.isFile() ? f.length() : 0;
                            e.modified = f.lastModified(); e.path = f.getAbsolutePath();
                            out.add(e);
                        }
                    }
                    File par = d.getParentFile();
                    parent = par != null && d.getAbsolutePath().length() > 1 ? par.getAbsolutePath() : null;
                }
            } catch (Exception ex) {
                err = p.remote ? "连接电脑失败：" + ex.getMessage() : ex.getMessage();
            }
            final String fErr = err, fParent = parent, fPath = realPath;
            h.post(() -> {
                if (pushHistory && !fPath.equals(p.path)) { p.back.push(p.path); p.fwd.clear(); }
                p.path = fPath;
                p.parent = fParent;
                p.error = fErr;
                p.items.clear();
                p.items.addAll(out);
                p.selected.clear();
                p.adapter.notifyDataSetChanged();
                p.list.setSelection(0);
                if (p == active) updateHeader();
            });
        }).start();
    }

    // =============================== 操作 ===============================
    private void up() {
        Pane p = active;
        if (p.remote) { if (p.path.isEmpty()) return; load(p, p.parent == null ? "" : p.parent, true); }
        else if (p.parent != null && !p.path.equals("/storage/emulated")) load(p, p.parent, true);
    }

    private void history(boolean back) {
        Pane p = active;
        Deque<String> from = back ? p.back : p.fwd, to = back ? p.fwd : p.back;
        if (from.isEmpty()) return;
        to.push(p.path);
        load(p, from.pop(), false);
    }

    private void mkdir() {
        Pane p = active;
        if (p.remote && p.path.isEmpty()) { toast("请先打开一个磁盘或文件夹"); return; }
        prompt("新建文件夹", "", name -> new Thread(() -> {
            String err;
            if (p.remote) err = FrHub.get(this).fsOp(ip, "mkdir", p.path, name);
            else err = new File(p.path, name).mkdirs() ? null : "新建失败";
            h.post(() -> { if (err != null) toast(err); load(p, p.path, false); });
        }).start());
    }

    private void rename() {
        Pane p = active;
        if (p.selected.size() != 1) { toast("请只选中一项再重命名"); return; }
        String path = p.selected.iterator().next();
        String old = path.substring(Math.max(path.lastIndexOf('/'), path.lastIndexOf('\\')) + 1);
        prompt("重命名", old, name -> new Thread(() -> {
            String err;
            if (p.remote) err = FrHub.get(this).fsOp(ip, "rename", path, name);
            else { File f = new File(path); err = f.renameTo(new File(f.getParentFile(), name)) ? null : "重命名失败"; }
            h.post(() -> { if (err != null) toast(err); load(p, p.path, false); });
        }).start());
    }

    private void delete() {
        Pane p = active;
        if (p.selected.isEmpty()) { toast("请先选中要删除的项"); return; }
        List<String> sel = new ArrayList<>(p.selected);
        String where = p.remote ? "电脑上的 " + sel.size() + " 项将移到回收站" : "手机上的 " + sel.size() + " 项将被永久删除，无法恢复";
        new AlertDialog.Builder(this).setTitle("删除").setMessage(where + "。确定？")
                .setPositiveButton("删除", (d, w) -> new Thread(() -> {
                    String err = null;
                    for (String s : sel) {
                        String e = p.remote ? FrHub.get(this).fsOp(ip, "delete", s, null) : (deleteRec(new File(s)) ? null : "删除失败：" + s);
                        if (e != null) err = e;
                    }
                    final String fe = err;
                    h.post(() -> { if (fe != null) toast(fe); load(p, p.path, false); });
                }).start())
                .setNegativeButton("取消", null).show();
    }

    private static boolean deleteRec(File f) {
        File[] kids = f.listFiles();
        if (kids != null) for (File k : kids) deleteRec(k);
        return f.delete();
    }

    /** 把当前栏选中的复制到另一栏的当前文件夹 */
    private void copyAcross() {
        Pane src = active, dst = active == phone ? pc : phone;
        if (src.selected.isEmpty()) { toast("先在一栏里点选（或长按）要复制的文件"); return; }
        if (dst.remote && dst.path.isEmpty()) { toast("请先在右边打开电脑上的目标文件夹"); return; }
        List<String> sel = new ArrayList<>(src.selected);
        String dstPath = dst.path;
        final android.content.Context app = getApplicationContext();
        FrTransfer.Progress prog = new FrTransfer.Progress() {
            @Override public void update(String name, long done, long total) {
                FrTransferService.update(app, name, done, total); // 通知栏进度
                h.post(() -> showProgress(name, done, total));
            }
            @Override public void finished(boolean ok, String message) {
                FrTransferService.finish(app, ok, message);
                h.post(() -> { hideProgress(); toast(message); load(dst, dst.path, false); });
            }
        };
        showProgress("准备中…", 0, 1);
        FrTransferService.begin(app, src.remote ? "正在从 " + pcName + " 复制到手机" : "正在从手机复制到 " + pcName);
        new Thread(() -> {
            try {
                if (!src.remote) {
                    List<File> files = new ArrayList<>();
                    for (String s : sel) files.add(new File(s));
                    FrTransfer.send(FrHub.get(this).session(ip), files, dstPath, prog);
                } else {
                    FrHub.get(this).fsPull(ip, sel, new File(dstPath), prog);
                }
            } catch (Exception e) {
                prog.finished(false, "复制失败：" + e.getMessage());
            }
        }).start();
        src.selected.clear();
        src.adapter.notifyDataSetChanged();
        updateHeader();
    }

    private void showMore(View anchor) {
        PopupMenu m = new PopupMenu(this, anchor);
        m.getMenu().add(0, 1, 0, "刷新");
        m.getMenu().add(0, 2, 0, "全选");
        m.getMenu().add(0, 3, 0, "取消选择");
        m.getMenu().add(0, 4, 0, "重命名");
        m.getMenu().add(0, 5, 0, "删除");
        m.getMenu().add(0, 6, 0, "接收文件夹：Download/Farloop");
        m.getMenu().add(0, 7, 0, "授予所有文件访问权限");
        m.setOnMenuItemClickListener(it -> {
            Pane p = active;
            switch (it.getItemId()) {
                case 1: load(phone, phone.path, false); load(pc, pc.path, false); break;
                case 2: for (Entry e : p.items) p.selected.add(e.path); p.adapter.notifyDataSetChanged(); updateHeader(); break;
                case 3: p.selected.clear(); p.adapter.notifyDataSetChanged(); updateHeader(); break;
                case 4: rename(); break;
                case 5: delete(); break;
                case 6: setActive(phone); load(phone, FrTransfer.defaultReceiveDir().getAbsolutePath(), true); break;
                case 7: requestAllFiles(); break;
            }
            return true;
        });
        m.show();
    }

    private void showProgress(String name, long done, long total) {
        progressBox.setVisibility(View.VISIBLE);
        int pct = total > 0 ? (int) (done * 1000 / total) : 0;
        progressBar.setProgress(pct);
        progressText.setText(name + "   " + Ui.size(done) + " / " + Ui.size(total) + "   " + (pct / 10) + "%");
    }

    private void hideProgress() { progressBox.setVisibility(View.GONE); }

    private interface OnText { void ok(String s); }

    private void prompt(String title, String initial, OnText ok) {
        EditText e = new EditText(this);
        e.setText(initial);
        e.setSingleLine(true);
        e.setSelection(initial.length());
        FrameLayout f = new FrameLayout(this);
        f.setPadding(ui.dp(22), ui.dp(8), ui.dp(22), 0);
        f.addView(e);
        new AlertDialog.Builder(this).setTitle(title).setView(f)
                .setPositiveButton("确定", (d, w) -> {
                    String s = e.getText().toString().trim();
                    if (!s.isEmpty()) ok.ok(s);
                })
                .setNegativeButton("取消", null).show();
    }

    // =============================== 存储权限 ===============================
    private boolean hasStoragePermission() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) return Environment.isExternalStorageManager();
        return checkSelfPermission(Manifest.permission.WRITE_EXTERNAL_STORAGE) == PackageManager.PERMISSION_GRANTED;
    }

    private void ensureStoragePermission() {
        if (hasStoragePermission()) return;
        new AlertDialog.Builder(this).setTitle("需要文件访问权限")
                .setMessage("像 MT 管理器一样浏览手机里的文件夹，需要授予“所有文件访问权限”。只用于你在这里选择的文件传输。")
                .setPositiveButton("去授予", (d, w) -> requestAllFiles())
                .setNegativeButton("以后", null).show();
    }

    private void requestAllFiles() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            try {
                startActivity(new Intent(Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION, Uri.parse("package:" + getPackageName())));
            } catch (Exception e) {
                startActivity(new Intent(Settings.ACTION_MANAGE_ALL_FILES_ACCESS_PERMISSION));
            }
        } else {
            requestPermissions(new String[]{Manifest.permission.READ_EXTERNAL_STORAGE, Manifest.permission.WRITE_EXTERNAL_STORAGE}, 1);
        }
    }

    private void toast(String s) { Toast.makeText(this, s, Toast.LENGTH_LONG).show(); }

    /** 当前栏加一圈蓝色描边 */
    static final class GradientDrawableHelper {
        static void outline(View v, Ui ui, boolean on) {
            android.graphics.drawable.GradientDrawable g = Ui.round(ui.card, ui.dp(20));
            if (on) g.setStroke(ui.dp(2), ui.accent);
            v.setBackground(g);
        }
    }
}
