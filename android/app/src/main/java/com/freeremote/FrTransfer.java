package com.freeremote;

import android.os.Environment;
import android.util.Log;

import com.google.gson.JsonObject;

import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;

/**
 * 文件收发，与电脑端 TransferManager 完全兼容：FileBegin(JSON) → FileChunk([16 字节 Id][数据]) → FileEnd(JSON)。
 * 注意 .NET 的 Guid 字节序：前三段（4+2+2 字节）是小端，和 Java UUID 的字符串顺序不同。
 */
public final class FrTransfer {
    public static final int FILE_BEGIN = 20, FILE_CHUNK = 21, FILE_END = 22, FILE_ABORT = 23;
    private static final int CHUNK = 256 * 1024;
    private static final String TAG = "FrTransfer";

    /** 进度回调（在后台线程里调用，界面自己切回主线程） */
    public interface Progress {
        void update(String name, long done, long total);
        void finished(boolean ok, String message);
    }

    // ---------------- 接收 ----------------
    private static final class RecvBatch {
        File baseDir; long total, done; int files, filesDone; Progress p;
        final Map<String, String> rootMap = new ConcurrentHashMap<>();
    }
    private static final class RecvFile { FileOutputStream out; File part, fin; RecvBatch batch; }

    private final Map<String, RecvBatch> batches = new ConcurrentHashMap<>();
    private final Map<String, RecvFile> files = new ConcurrentHashMap<>();
    /** 令牌 → 我们要求对方发过来时选好的本地文件夹 */
    private final Map<String, Object[]> pulls = new ConcurrentHashMap<>();

    public static File defaultReceiveDir() {
        File d = new File(Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS), "Farloop");
        //noinspection ResultOfMethodCallIgnored
        d.mkdirs();
        return d;
    }

    /** 请电脑把这些文件发到手机的 dir：返回令牌，调用方用 FsPull 发给对方 */
    public String expectPull(File dir, Progress p) {
        String token = UUID.randomUUID().toString().replace("-", "");
        pulls.put(token, new Object[]{dir, p});
        return token;
    }

    public void handle(int type, byte[] p) {
        try {
            switch (type) {
                case FILE_BEGIN: onBegin(FrSession.json(p)); break;
                case FILE_CHUNK: onChunk(p); break;
                case FILE_END: onEnd(FrSession.json(p).get("Id").getAsString()); break;
                case FILE_ABORT: {
                    JsonObject j = FrSession.json(p);
                    abort(j.get("Batch").getAsString(), j.has("Reason") ? j.get("Reason").getAsString() : "对方取消");
                    break;
                }
            }
        } catch (Exception e) {
            Log.w(TAG, "处理文件消息出错", e);
        }
    }

    private void onBegin(JsonObject m) throws IOException {
        String batchId = m.get("Batch").getAsString();
        RecvBatch b = batches.get(batchId);
        if (b == null) {
            b = new RecvBatch();
            String token = m.has("Token") && !m.get("Token").isJsonNull() ? m.get("Token").getAsString() : null;
            Object[] pull = token == null ? null : pulls.remove(token);
            b.baseDir = pull != null ? (File) pull[0] : defaultReceiveDir();
            b.p = pull != null ? (Progress) pull[1] : null;
            b.total = m.get("BatchBytes").getAsLong();
            b.files = m.get("Total").getAsInt();
            //noinspection ResultOfMethodCallIgnored
            b.baseDir.mkdirs();
            batches.put(batchId, b);
        }
        String root = m.get("Root").getAsString();
        String[] segs = safeSegments(m.get("RelPath").getAsString());
        String[] rootSegs = safeSegments(root);
        if (segs.length == 0 || rootSegs.length == 0 || !segs[0].equalsIgnoreCase(rootSegs[0])) throw new IOException("非法路径");
        String mapped = b.rootMap.get(segs[0]);
        if (mapped == null) { mapped = uniqueName(b.baseDir, segs[0]); b.rootMap.put(segs[0], mapped); }
        segs[0] = mapped;
        File fin = b.baseDir;
        for (String s : segs) fin = new File(fin, s);
        if (!fin.getCanonicalPath().startsWith(b.baseDir.getCanonicalPath() + File.separator)) throw new IOException("非法路径");
        //noinspection ResultOfMethodCallIgnored
        fin.getParentFile().mkdirs();
        RecvFile f = new RecvFile();
        f.fin = fin;
        f.part = new File(fin.getPath() + ".frpart");
        f.out = new FileOutputStream(f.part);
        f.batch = b;
        files.put(key(guidBytes(m.get("Id").getAsString())), f);
    }

    private void onChunk(byte[] p) throws IOException {
        if (p.length < 16) return;
        byte[] id = new byte[16];
        System.arraycopy(p, 0, id, 0, 16);
        RecvFile f = files.get(key(id));
        if (f == null) return;
        f.out.write(p, 16, p.length - 16);
        f.batch.done += p.length - 16;
        if (f.batch.p != null) f.batch.p.update(f.fin.getName(), f.batch.done, f.batch.total);
    }

    private void onEnd(String id) throws IOException {
        RecvFile f = files.remove(key(guidBytes(id)));
        if (f == null) return;
        f.out.close();
        if (f.fin.exists()) //noinspection ResultOfMethodCallIgnored
            f.fin.delete();
        if (!f.part.renameTo(f.fin)) throw new IOException("保存文件失败：" + f.fin);
        RecvBatch b = f.batch;
        if (++b.filesDone < b.files) return;
        batches.values().remove(b);
        if (b.p != null) b.p.finished(true, "已保存到 " + b.baseDir.getPath());
    }

    private void abort(String batchId, String reason) {
        RecvBatch b = batches.remove(batchId);
        if (b == null) return;
        for (Map.Entry<String, RecvFile> e : files.entrySet()) {
            if (e.getValue().batch != b) continue;
            files.remove(e.getKey());
            try { e.getValue().out.close(); } catch (IOException ignored) { }
            //noinspection ResultOfMethodCallIgnored
            e.getValue().part.delete();
        }
        if (b.p != null) b.p.finished(false, reason);
    }

    // ---------------- 发送 ----------------
    /** 把手机上的文件 / 文件夹发到电脑的 destDir（阻塞，后台线程调用） */
    public static void send(FrSession s, List<File> roots, String destDir, Progress p) {
        List<File[]> list = new ArrayList<>(); // {文件, 顶层}
        for (File r : roots) expand(r, r, list);
        long total = 0;
        for (File[] f : list) total += f[0].length();
        String batch = UUID.randomUUID().toString();
        long done = 0;
        byte[] buf = new byte[16 + CHUNK];
        try {
            for (int i = 0; i < list.size(); i++) {
                File f = list.get(i)[0], root = list.get(i)[1];
                String rel = root.getName();
                if (!f.equals(root)) rel = rel + "/" + root.toURI().relativize(f.toURI()).getPath();
                String id = UUID.randomUUID().toString();
                JsonObject m = new JsonObject();
                m.addProperty("Id", id);
                m.addProperty("Batch", batch);
                m.addProperty("Root", root.getName());
                m.addProperty("RelPath", rel);
                m.addProperty("Size", f.length());
                m.addProperty("Index", i);
                m.addProperty("Total", list.size());
                m.addProperty("BatchBytes", total);
                m.addProperty("ForClipboard", false);
                m.addProperty("DestDir", destDir);
                s.sendJson(FILE_BEGIN, m);
                System.arraycopy(guidBytes(id), 0, buf, 0, 16);
                try (FileInputStream in = new FileInputStream(f)) {
                    int n;
                    while ((n = in.read(buf, 16, CHUNK)) > 0) {
                        byte[] frame = new byte[16 + n];
                        System.arraycopy(buf, 0, frame, 0, 16 + n);
                        s.send(FILE_CHUNK, frame);
                        done += n;
                        if (p != null) p.update(f.getName(), done, total);
                    }
                }
                JsonObject e = new JsonObject();
                e.addProperty("Id", id);
                s.sendJson(FILE_END, e);
            }
            if (p != null) p.finished(true, "已发送 " + list.size() + " 个文件");
        } catch (Exception ex) {
            Log.w(TAG, "发送失败", ex);
            try {
                JsonObject a = new JsonObject();
                a.addProperty("Batch", batch);
                a.addProperty("Reason", String.valueOf(ex.getMessage()));
                s.sendJson(FILE_ABORT, a);
            } catch (Exception ignored) { }
            if (p != null) p.finished(false, "发送失败：" + ex.getMessage());
        }
    }

    private static void expand(File f, File root, List<File[]> out) {
        if (f.isDirectory()) {
            File[] kids = f.listFiles();
            if (kids != null) for (File k : kids) expand(k, root, out);
        } else if (f.isFile()) out.add(new File[]{f, root});
    }

    // ---------------- 工具 ----------------
    /** "aabbccdd-eeff-gghh-iijj-kkllmmnnoopp" → .NET Guid 的 16 字节（前三段小端） */
    static byte[] guidBytes(String s) {
        String h = s.replace("-", "");
        byte[] raw = new byte[16];
        for (int i = 0; i < 16; i++) raw[i] = (byte) Integer.parseInt(h.substring(i * 2, i * 2 + 2), 16);
        byte[] b = raw.clone();
        b[0] = raw[3]; b[1] = raw[2]; b[2] = raw[1]; b[3] = raw[0];
        b[4] = raw[5]; b[5] = raw[4];
        b[6] = raw[7]; b[7] = raw[6];
        return b;
    }

    private static String key(byte[] b) {
        StringBuilder sb = new StringBuilder();
        for (byte x : b) sb.append(String.format(Locale.US, "%02x", x));
        return sb.toString();
    }

    static String[] safeSegments(String rel) throws IOException {
        List<String> out = new ArrayList<>();
        for (String s : rel.replace('\\', '/').split("/")) {
            if (s.isEmpty()) continue;
            if (s.equals(".") || s.equals("..") || s.contains(":")) throw new IOException("非法路径：" + rel);
            out.add(s);
        }
        return out.toArray(new String[0]);
    }

    private static String uniqueName(File dir, String name) {
        if (!new File(dir, name).exists()) return name;
        int dot = name.lastIndexOf('.');
        String stem = dot > 0 ? name.substring(0, dot) : name, ext = dot > 0 ? name.substring(dot) : "";
        for (int i = 1; ; i++) {
            String n = stem + " (" + i + ")" + ext;
            if (!new File(dir, n).exists()) return n;
        }
    }
}
