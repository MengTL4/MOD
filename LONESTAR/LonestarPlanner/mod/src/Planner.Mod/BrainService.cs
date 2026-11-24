using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using LitJson;
using Debug = UnityEngine.Debug;

namespace LonestarPlanner.Mod
{
    /// <summary>
    /// 本机求解服务（Brain）的进程管理，战斗面板和船坞面板共用一个服务：
    ///
    ///   · 找服务：设置里填了求解器目录（开发用）就用那里的 Brain.dll（dotnet 启动）；
    ///     否则用 mod 自带的 Brain/ 目录（自带运行时，玩家不用装 .NET）。
    ///   · 自带的那份先复制到存档目录下的缓存里再启动：mod 目录本身不会被占用，
    ///     创意工坊更新、开发时重新部署都不会因为文件被锁而失败。
    ///   · 连上以后先 hello 核对版本，旧版本的服务（上次更新前留下的）让它退出再启动新的。
    ///   · 启动时带上游戏进程号：游戏退出（包括崩溃）时服务自己退出。
    /// </summary>
    internal static class BrainService
    {
        /// <summary>和 Brain 的 Service.Version 一致：协议变了两边一起改。</summary>
        public const string Version = "brain-6";

        public const int Port = 47321;

        /// <summary>mod 所在目录（OnLoad 时设置）。</summary>
        public static string ModPath;

        /// <summary>mod 自己的数据目录（日志、缓存、备份），在游戏存档目录下。</summary>
        public static string DataRoot;

        private static readonly object gate = new object();
        private static readonly object logGate = new object();
        private static Process service;
        private static volatile bool versionOk;

        /// <summary>本机内存（MB，OnLoad 时从 SystemInfo 读）。</summary>
        public static int SystemMemoryMB;

        /// <summary>
        /// 工作进程数：设置里填了就用；0 = 自动 —— 每个核一个，但每个进程约占 350MB 内存，
        /// 小内存的机器按内存减（给游戏和系统留 4GB），至少 2 个。
        /// </summary>
        public static int Workers
        {
            get
            {
                int set = PlannerMod.config?.brainJobs ?? 0;
                if (set > 0) return set;
                int cores = Math.Max(2, Environment.ProcessorCount);
                if (SystemMemoryMB <= 0) return cores;
                return Math.Max(2, Math.Min(cores, (SystemMemoryMB - 4096) / 450));
            }
        }

        /// <summary>最近一次启动/连接失败的原因（给界面显示）。</summary>
        public static volatile string LastError;

        private static string DevRoot => PlannerMod.config?.solverRoot?.Trim();

        private static string DevDll =>
            string.IsNullOrEmpty(DevRoot) ? null : Path.Combine(DevRoot, "tools", "Brain", "bin", "Release", "net10.0", "Brain.dll");

        public static string BundleDir => ModPath == null ? null : Path.Combine(ModPath, "Brain");

        private static string BundleExe => BundleDir == null ? null : Path.Combine(BundleDir, "Brain.exe");

        /// <summary>开发用的求解器目录优先，其次是 mod 自带的。</summary>
        public static bool Installed => DevDll != null ? File.Exists(DevDll) : BundleExe != null && File.Exists(BundleExe);

        /// <summary>找不到服务时提示的路径。</summary>
        public static string ExpectedPath => DevDll ?? BundleExe ?? "Brain";

        public static bool UsingDevBuild => DevDll != null;

        public static TcpClient TryConnect()
        {
            var tcp = new TcpClient();
            try
            {
                IAsyncResult ar = tcp.BeginConnect("127.0.0.1", Port, null, null);
                if (ar.AsyncWaitHandle.WaitOne(400) && tcp.Connected)
                {
                    tcp.EndConnect(ar);
                    return tcp;
                }
            }
            catch
            {
                // refused
            }
            try
            {
                tcp.Close();
            }
            catch
            {
                // ignore
            }
            return null;
        }

        /// <summary>
        /// 连上（必要时启动）一个版本对得上的服务。后台线程调用；<paramref name="wanted"/> 变 false 就放弃等待，
        /// <paramref name="starting"/> 在真的要启动进程时调用一次（界面显示"正在启动"）。
        /// </summary>
        public static TcpClient Connect(Func<bool> wanted, Action starting)
        {
            TcpClient tcp = Usable(TryConnect());
            if (tcp != null) return tcp;
            if (!(PlannerMod.config?.autoStartService ?? true))
            {
                LastError = L.T("求解服务没有运行（设置里关掉了自动启动）", "The solver service is not running (auto-start is off)");
                return null;
            }
            if (!Installed)
            {
                LastError = L.T("没找到求解服务：", "Solver not found: ") + ExpectedPath;
                return null;
            }
            lock (gate)
            {
                // 另一个面板可能刚刚把它启动好
                tcp = Usable(TryConnect());
                if (tcp != null) return tcp;
                starting?.Invoke();
                if (!Start()) return null;
                for (int i = 0; i < 160 && wanted(); i++)
                {
                    Thread.Sleep(250);
                    if (service != null && service.HasExited)
                    {
                        LastError = L.T("求解服务启动后马上退出了（代码 ", "The solver service exited right after starting (code ") + service.ExitCode + L.T("），详见 brain.log", "), see brain.log");
                        return null;
                    }
                    tcp = Usable(TryConnect());
                    if (tcp != null) return tcp;
                }
            }
            if (wanted()) LastError = L.T("等求解服务启动超时", "Timed out waiting for the solver service");
            return null;
        }

        /// <summary>第一次连上时核对版本；版本不对就让旧服务退出，返回 null（调用方接着启动新的）。</summary>
        private static TcpClient Usable(TcpClient tcp)
        {
            if (tcp == null || versionOk) return tcp;
            string version = null;
            try
            {
                using (tcp)
                {
                    var w = new StreamWriter(tcp.GetStream(), new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
                    var r = new StreamReader(tcp.GetStream(), new UTF8Encoding(false));
                    w.WriteLine("{\"op\":\"hello\"}");
                    tcp.ReceiveTimeout = 3000;
                    JsonData j = JsonMapper.ToObject(r.ReadLine() ?? "{}");
                    version = BrainPlan.Str(j, "version");
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[Planner] brain hello failed: " + ex.Message);
            }
            if (version == Version)
            {
                versionOk = true;
                return TryConnect();
            }
            Debug.Log("[Planner] brain service version " + (version ?? "?") + " != " + Version + ", restarting it");
            Send("{\"op\":\"quit\"}");
            for (int i = 0; i < 24; i++)
            {
                Thread.Sleep(250);
                TcpClient still = TryConnect();
                if (still == null) break;
                still.Close();
            }
            return null;
        }

        private static bool Start()
        {
            try
            {
                string exe, args, dir;
                if (DevDll != null)
                {
                    exe = "dotnet";
                    args = "\"" + DevDll + "\" ";
                    dir = DevRoot;
                }
                else
                {
                    exe = PrepareBundle();
                    args = "";
                    dir = Path.GetDirectoryName(exe);
                }
                args += "--serve --port " + Port + " --parent " + Process.GetCurrentProcess().Id + " --jobs " + Workers;
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = dir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                service = Process.Start(psi);
                StartLog(service);
                versionOk = true; // 我们自己启动的就是当前版本
                Debug.Log("[Planner] started brain service: " + exe + " " + args);
                return true;
            }
            catch (Exception ex)
            {
                LastError = L.T("启动求解服务失败：", "Could not start the solver service: ") + ex.Message;
                Debug.LogWarning("[Planner] cannot start brain service: " + ex);
                return false;
            }
        }

        /// <summary>服务的控制台输出写到 logs/brain.log（每次启动覆盖），出问题时可以发给作者看。</summary>
        private static void StartLog(Process p)
        {
            StreamWriter log = null;
            try
            {
                string dir = Path.Combine(DataRoot ?? Path.GetTempPath(), "logs");
                Directory.CreateDirectory(dir);
                log = new StreamWriter(Path.Combine(dir, "brain.log"), false, new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch
            {
                // 没日志也能跑
            }
            long written = 0;
            DataReceivedEventHandler sink = (_, e) =>
            {
                if (e.Data == null || log == null) return;
                lock (logGate)
                {
                    if (written > 8 << 20) return; // 最多 8MB
                    written += e.Data.Length + 1;
                    try
                    {
                        log.WriteLine(e.Data);
                    }
                    catch
                    {
                        // ignore
                    }
                }
            };
            p.OutputDataReceived += sink;
            p.ErrorDataReceived += sink;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }

        /// <summary>
        /// 把 mod 自带的 Brain/ 复制到 数据目录/brain/&lt;版本戳&gt;/（已经有就直接用），删掉旧版本的缓存。
        /// </summary>
        private static string PrepareBundle()
        {
            string src = BundleDir;
            string cache = Path.Combine(DataRoot, "brain");
            string dst = Path.Combine(cache, Stamp(src));
            if (!File.Exists(Path.Combine(dst, ".complete")))
            {
                string tmp = dst + ".tmp";
                if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                CopyDirectory(src, tmp);
                File.WriteAllText(Path.Combine(tmp, ".complete"), DateTime.Now.ToString("o"));
                if (Directory.Exists(dst)) Directory.Delete(dst, true);
                Directory.Move(tmp, dst);
            }
            foreach (string old in Directory.GetDirectories(cache))
            {
                if (string.Equals(old, dst, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    Directory.Delete(old, true);
                }
                catch
                {
                    // 还在用（旧服务没退出）：下次再删
                }
            }
            return Path.Combine(dst, "Brain.exe");
        }

        /// <summary>版本戳：brain.json 的内容 + Brain.dll 的大小和时间。</summary>
        private static string Stamp(string dir)
        {
            var sb = new StringBuilder();
            string info = Path.Combine(dir, "brain.json");
            if (File.Exists(info)) sb.Append(File.ReadAllText(info));
            var dll = new FileInfo(Path.Combine(dir, "Brain.dll"));
            if (dll.Exists) sb.Append('|').Append(dll.Length).Append('|').Append(dll.LastWriteTimeUtc.Ticks);
            using (var sha = SHA1.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return BitConverter.ToString(h, 0, 6).Replace("-", "").ToLowerInvariant();
            }
        }

        private static void CopyDirectory(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string f in Directory.GetFiles(from))
            {
                File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            }
            foreach (string d in Directory.GetDirectories(from))
            {
                CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
            }
        }

        /// <summary>
        /// 游戏更新后，自带的求解器（按旧版游戏代码做的）可能和游戏对不上。brain.json 里记着做它时
        /// 游戏 Assembly-CSharp.dll 的 SHA256，和当前游戏比一下。开发用的求解器目录不检查。
        /// </summary>
        public static volatile bool GameChanged;

        public static void CheckGameVersion(string managedDir)
        {
            if (UsingDevBuild || BundleDir == null) return;
            new Thread(() =>
            {
                try
                {
                    string info = Path.Combine(BundleDir, "brain.json");
                    if (!File.Exists(info)) return;
                    string expected = BrainPlan.Str(JsonMapper.ToObject(File.ReadAllText(info)), "game");
                    if (string.IsNullOrEmpty(expected)) return;
                    using (var sha = SHA256.Create())
                    using (FileStream f = File.OpenRead(Path.Combine(managedDir, "Assembly-CSharp.dll")))
                    {
                        string actual = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "");
                        GameChanged = !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
                    }
                    if (GameChanged) Debug.LogWarning("[Planner] the game was updated after this solver was built");
                }
                catch (Exception ex)
                {
                    Debug.Log("[Planner] game version check skipped: " + ex.Message);
                }
            }) { IsBackground = true, Name = "PlannerGameCheck" }.Start();
        }

        /// <summary>发一条不需要回复内容的命令（hurry / quit），后台线程里发。</summary>
        public static void SendBackground(string json)
        {
            new Thread(() => Send(json)) { IsBackground = true, Name = "BrainSend" }.Start();
        }

        private static void Send(string json)
        {
            try
            {
                TcpClient tcp = TryConnect();
                if (tcp == null) return;
                using (tcp)
                {
                    var w = new StreamWriter(tcp.GetStream(), new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
                    var r = new StreamReader(tcp.GetStream(), new UTF8Encoding(false));
                    tcp.ReceiveTimeout = 3000;
                    w.WriteLine(json);
                    r.ReadLine();
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[Planner] brain send failed: " + ex.Message);
            }
        }

        /// <summary>游戏退出时让我们启动的服务退出（崩溃时服务也会因为 --parent 自己退出）。</summary>
        public static void Shutdown()
        {
            if (service == null || service.HasExited) return;
            Send("{\"op\":\"quit\"}");
        }
    }
}
