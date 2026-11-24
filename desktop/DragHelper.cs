using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using FreeRemote.Core;

namespace FreeRemote;

/// <summary>
/// 拖拽助手：一个以【普通权限】运行的 FreeRemote 进程，专门放拖拽相关的窗口。
///
/// 为什么需要它：FreeRemote 主程序是管理员权限，资源管理器是普通权限。Windows 的权限隔离（UIPI）禁止
/// 低权限程序把文件拖给高权限窗口（微软官方说明，放行消息的绕过办法在新系统上无效，已实测）。
/// 所以“接收层 / 屏幕边缘接收条 / 拖出提示”都放到这个普通权限的助手里，和主程序在本机用 TCP 通信。
///
/// 启动方式：主程序注册一个“最低权限”的计划任务（RunLevel = LeastPrivilege），schtasks /Run 启动它，
/// 这样助手就是普通权限，并且不会弹 UAC。连接信息（端口 + 一次性令牌）写在 helper.json。
/// </summary>
public static class DragHelper
{
    public const string TaskName = Core.Installer.DragHelperTask;
    static string InfoFile => Path.Combine(AppConfig.DataDir, "helper.json");

    // ======================= 主程序（管理员）这一侧 =======================
    public sealed class Host
    {
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        readonly Dispatcher _ui;
        StreamWriter? _out;
        readonly object _gate = new();
        readonly Dictionary<string, JsonObject> _desired = new(); // 当前应当存在的接收层，助手重连后重发
        bool _edgeWanted;
        public event Action<string, string[], float, float>? DropIn;     // 设备 IP, 文件, 落点
        public event Action<string, string?>? DragOutRelease;           // 设备 IP, 本地落点文件夹
        public event Action<string[]>? EdgeDrop;                        // 被控端：拖到屏幕边缘的文件

        public bool Connected => _out != null;

        public Host(Dispatcher ui) { _ui = ui; }

        public void Start()
        {
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            File.WriteAllText(InfoFile, JsonSerializer.Serialize(new { port, token = _token }));
            _ = Task.Run(AcceptLoop);
            _ = Task.Run(async () => { await EnsureTaskAsync(); Launch(); });
            // 兜底：20 秒内没连上就换一种启动方式
            _ = Task.Run(async () => { await Task.Delay(20_000); if (!Connected) { _launchMode = 1 - _launchMode; Launch(); } });
        }

        /// <summary>注册（或更新）最低权限的计划任务，指向当前 exe</summary>
        /// <summary>助手用哪个 exe（默认就是正在运行的这个，版本一定一致；测试时可覆盖）</summary>
        public static string? ExeOverride { get; set; }

        static async Task EnsureTaskAsync()
        {
            var exe = ExeOverride ?? Environment.ProcessPath!;
            var user = WindowsIdentity.GetCurrent().Name;
            var xml = $"""
                <?xml version="1.0" encoding="UTF-16"?>
                <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                  <RegistrationInfo><Description>FreeRemote 拖拽助手（普通权限）</Description></RegistrationInfo>
                  <Principals><Principal id="Author"><UserId>{SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
                  <Settings>
                    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                    <Priority>4</Priority>
                    <Enabled>true</Enabled>
                  </Settings>
                  <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(exe)}</Command><Arguments>--drag-helper</Arguments></Exec></Actions>
                </Task>
                """;
            var f = Path.Combine(Path.GetTempPath(), "fr-helper-" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(f, xml, Encoding.Unicode);
            try
            {
                var (code, o) = await Proc.RunAsync("schtasks", $"/Create /TN \"{TaskName}\" /XML \"{f}\" /F", 20_000);
                if (code != 0) Log.Error("注册拖拽助手任务失败：" + o.Trim());
            }
            finally { File.Delete(f); }
        }

        int _launchMode; // 0 = 通过资源管理器启动；1 = 通过最低权限计划任务启动

        /// <summary>
        /// 以普通权限启动助手。首选“让资源管理器去启动”：资源管理器本身是普通权限，它启动的程序也是普通权限。
        /// （实测：内置 Administrator 账户下，LeastPrivilege 的计划任务仍然给了管理员权限，所以它只做备选。）
        /// </summary>
        void Launch()
        {
            var exe = ExeOverride ?? Environment.ProcessPath!;
            try
            {
                if (_launchMode == 0)
                {
                    var lnk = Path.Combine(AppConfig.DataDir, "Farloop-DragHelper.lnk");
                    dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
                    dynamic l = sh.CreateShortcut(lnk);
                    l.TargetPath = exe;
                    l.Arguments = "--drag-helper";
                    l.WorkingDirectory = Path.GetDirectoryName(exe);
                    l.Save();
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{lnk}\"") { UseShellExecute = false });
                }
                else Process.Start(new ProcessStartInfo("schtasks", $"/Run /TN \"{TaskName}\"") { CreateNoWindow = true, UseShellExecute = false });
            }
            catch (Exception e) { Log.Error("启动拖拽助手失败", e); }
        }

        /// <summary>助手报告自己的权限；如果还是管理员，换另一种方式重启它</summary>
        void OnHello(bool elevated, int pid)
        {
            if (!elevated) { Log.Info($"拖拽助手就绪（pid {pid}，普通权限）"); return; }
            Log.Error($"拖拽助手以管理员权限运行（启动方式 {_launchMode}），资源管理器的拖放会被拦截，换一种方式重启");
            _launchMode = 1 - _launchMode;
            try { Process.GetProcessById(pid).Kill(); } catch { }
        }

        async Task AcceptLoop()
        {
            while (true)
            {
                TcpClient c;
                try { c = await _listener.AcceptTcpClientAsync(); } catch { return; }
                _ = Task.Run(() => Serve(c));
            }
        }

        async Task Serve(TcpClient c)
        {
            using var _c = c;
            var stream = c.GetStream();
            var reader = new StreamReader(stream, new UTF8Encoding(false));
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            if (await reader.ReadLineAsync() != _token) return; // 只认带对令牌的助手
            lock (_gate)
            {
                _out = writer;
                foreach (var cmd in _desired.Values) Send(cmd);
                if (_edgeWanted) Send(EdgeCmd(true));
            }
            Log.Info("拖拽助手已连接（普通权限）");
            try
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    var j = JsonNode.Parse(line)!.AsObject();
                    var ev = (string?)j["ev"];
                    string[] Paths() => j["paths"]!.AsArray().Select(x => (string)x!).ToArray();
                    if (ev == "hello") { OnHello((bool)j["elevated"]!, (int)j["pid"]!); continue; }
                    _ui.BeginInvoke(() =>
                    {
                        switch (ev)
                        {
                            case "drop-in": DropIn?.Invoke((string)j["id"]!, Paths(), (float)j["nx"]!, (float)j["ny"]!); break;
                            case "dragout": DragOutRelease?.Invoke((string)j["id"]!, (string?)j["folder"]); break;
                            case "edge-drop": EdgeDrop?.Invoke(Paths()); break;
                        }
                    });
                }
            }
            catch { }
            finally
            {
                lock (_gate) if (_out == writer) _out = null;
                Log.Info("拖拽助手断开，3 秒后重新启动");
                await Task.Delay(3000);
                Launch();
            }
        }

        void Send(JsonObject cmd)
        {
            try { _out?.WriteLine(cmd.ToJsonString()); } catch { _out = null; }
        }

        /// <summary>给某个远程窗口（Moonlight 进程）挂上接收层</summary>
        public void Catcher(string id, int moonlightPid, string resolution, string peer)
        {
            var cmd = new JsonObject { ["cmd"] = "catcher", ["id"] = id, ["pid"] = moonlightPid, ["res"] = resolution, ["peer"] = peer };
            lock (_gate) { _desired[id] = cmd; Send(cmd); }
        }

        public void Arm(string id, bool on)
        {
            if (on && !Connected) Log.Error("拖进远程画面：拖拽助手没有运行，无法接收");
            lock (_gate) Send(new JsonObject { ["cmd"] = "arm", ["id"] = id, ["on"] = on });
        }

        public void StopCatcher(string id)
        {
            lock (_gate) { _desired.Remove(id); Send(new JsonObject { ["cmd"] = "catcher-stop", ["id"] = id }); }
        }

        public void Edge(bool on)
        {
            lock (_gate) { _edgeWanted = on; Send(EdgeCmd(on)); }
        }

        /// <summary>接收条放在正在串流的那块屏幕的四边（多屏时不一定是主屏）</summary>
        static JsonObject EdgeCmd(bool on)
        {
            var cmd = new JsonObject { ["cmd"] = on ? "edge-start" : "edge-stop" };
            if (on)
            {
                var b = HostDisplays.StreamedBounds();
                cmd["x"] = b.X; cmd["y"] = b.Y; cmd["w"] = b.Width; cmd["h"] = b.Height;
            }
            return cmd;
        }
    }

    // ======================= 助手（普通权限）这一侧 =======================
    public static void RunHelper(Application app)
    {
        using var mutex = new Mutex(true, @"Local\FreeRemote.DragHelper", out bool first);
        if (!first) { app.Shutdown(); return; }
        bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        Log.Info($"拖拽助手启动（pid {Environment.ProcessId}，{(elevated ? "管理员权限——拖放会被系统拦截！" : "普通权限")}）");
        var ui = app.Dispatcher;
        var catchers = new Dictionary<string, DropCatcher>();
        EdgeCatcher? edge = null;

        _ = Task.Run(async () =>
        {
            try
            {
                var info = JsonNode.Parse(File.ReadAllText(InfoFile))!;
                using var c = new TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, (int)info["port"]!);
                var stream = c.GetStream();
                var reader = new StreamReader(stream, new UTF8Encoding(false));
                var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                await writer.WriteLineAsync((string)info["token"]!);
                void Emit(JsonObject o) { lock (writer) { try { writer.WriteLine(o.ToJsonString()); } catch { } } }
                Emit(new JsonObject { ["ev"] = "hello", ["elevated"] = elevated, ["pid"] = Environment.ProcessId });
                JsonArray Arr(string[] p) => new(p.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());

                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    var j = JsonNode.Parse(line)!.AsObject();
                    ui.Invoke(() =>
                    {
                        switch ((string?)j["cmd"])
                        {
                            case "catcher":
                            {
                                var id = (string)j["id"]!;
                                uint pid = (uint)(int)j["pid"]!;
                                var res = (string)j["res"]!;
                                if (catchers.Remove(id, out var old)) old.Close();
                                var dc = new DropCatcher((string)j["peer"]!, () => Overlay.FindStreamWindow(pid), () => res,
                                    (p, nx, ny) => Emit(new JsonObject { ["ev"] = "drop-in", ["id"] = id, ["paths"] = Arr(p), ["nx"] = nx, ["ny"] = ny }),
                                    null); // “拖出”由管理员主程序检测（见 DragOutWatcher）
                                dc.Show();
                                catchers[id] = dc;
                                break;
                            }
                            case "arm":
                                if (catchers.TryGetValue((string)j["id"]!, out var ca)) ca.ExternalArm((bool)j["on"]!);
                                else Log.Error("拖进远程画面：助手里没有这个画面的接收层");
                                break;
                            case "catcher-stop":
                                if (catchers.Remove((string)j["id"]!, out var c2)) c2.Close();
                                break;
                            case "edge-start":
                                if (edge == null)
                                {
                                    System.Drawing.Rectangle? area = j["w"] is JsonNode wn
                                        ? new System.Drawing.Rectangle((int)j["x"]!, (int)j["y"]!, (int)wn, (int)j["h"]!) : null;
                                    edge = new EdgeCatcher(p => Emit(new JsonObject { ["ev"] = "edge-drop", ["paths"] = Arr(p) }), area);
                                    edge.Start();
                                }
                                break;
                            case "edge-stop":
                                edge?.Stop(); edge = null;
                                break;
                        }
                    });
                }
            }
            catch (Exception e) { Log.Error("拖拽助手连接失败", e); }
            ui.Invoke(() => app.Shutdown()); // 主程序退出或断开，助手跟着退出（主程序会重新启动它）
        });
    }
}
