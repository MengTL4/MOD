using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using LitJson;
using Debug = UnityEngine.Debug;

namespace LonestarPlanner.Mod
{
    /// <summary>Brain 服务给出的一个回合方案。</summary>
    public sealed class BrainPlan
    {
        public List<string> Plan = new List<string>();
        public string Value;
        public bool Win;
        public int PredictHp;
        /// <summary>预计获胜后剩下的燃料（会带到下一场）；-1 = 服务没给。</summary>
        public int PredictFuel = -1;
        public int PredictRound;
        public int Round;
        public int PlayerHp, PlayerShield, EnemyHp, EnemyShield, EnemyBreak;
        public int TurnDealt, TurnTaken;
        /// <summary>每条对撞线：y, 我方, 敌方, 结果, 双方都有部件(1/0), 我方行, 敌方行</summary>
        public List<int[]> Lines = new List<int[]>();
        public string NextTurn;
        public List<string> Alternatives = new List<string>();
        public List<string> Desync = new List<string>();
        public double Seconds;
        public long Jobs;
        public bool Local;

        /// <summary>求解时给的时间（秒，0 = 不限时）。</summary>
        public float Budget;

        public static BrainPlan FromJson(JsonData j)
        {
            var p = new BrainPlan
            {
                Value = Str(j, "Value"),
                Win = Bool(j, "Win"),
                PredictHp = Int(j, "PredictHp"),
                PredictFuel = Has(j, "PredictFuel") ? Int(j, "PredictFuel") : -1,
                PredictRound = Int(j, "PredictRound"),
                Round = Int(j, "Round"),
                PlayerHp = Int(j, "PlayerHp"),
                PlayerShield = Int(j, "PlayerShield"),
                EnemyHp = Int(j, "EnemyHp"),
                EnemyShield = Int(j, "EnemyShield"),
                EnemyBreak = Int(j, "EnemyBreak"),
                TurnDealt = Int(j, "TurnDealt"),
                TurnTaken = Int(j, "TurnTaken"),
                NextTurn = Str(j, "NextTurn"),
                Seconds = Dbl(j, "Seconds"),
                Jobs = Int(j, "Jobs"),
            };
            p.Plan = StrList(j, "Plan");
            p.Alternatives = StrList(j, "Alternatives");
            p.Desync = StrList(j, "Desync");
            if (Has(j, "Lines") && j["Lines"].IsArray)
            {
                foreach (JsonData line in j["Lines"])
                {
                    var arr = new int[line.Count];
                    for (int i = 0; i < line.Count; i++) arr[i] = (int)line[i];
                    p.Lines.Add(arr);
                }
            }
            return p;
        }

        // 游戏自带的 LitJson：JsonData 只实现非泛型 IDictionary，不能强转成 IDictionary<string, JsonData>
        internal static bool Has(JsonData j, string k) => j != null && j.IsObject && j.ContainsKey(k) && j[k] != null;
        internal static string Str(JsonData j, string k) => Has(j, k) && j[k].IsString ? (string)j[k] : null;
        internal static bool Bool(JsonData j, string k) => Has(j, k) && j[k].IsBoolean && (bool)j[k];
        internal static int Int(JsonData j, string k) => Has(j, k) ? (j[k].IsInt ? (int)j[k] : j[k].IsLong ? (int)(long)j[k] : j[k].IsDouble ? (int)(double)j[k] : 0) : 0;
        internal static double Dbl(JsonData j, string k) => Has(j, k) ? (j[k].IsDouble ? (double)j[k] : j[k].IsInt ? (int)j[k] : j[k].IsLong ? (long)j[k] : 0) : 0;

        internal static List<string> StrList(JsonData j, string k)
        {
            var list = new List<string>();
            if (!Has(j, k) || !j[k].IsArray) return list;
            foreach (JsonData s in j[k]) if (s != null && s.IsString) list.Add((string)s);
            return list;
        }
    }

    /// <summary>
    /// 一个求解请求的客户端（战斗面板、船坞面板各一个）。请求在后台线程里跑，主线程只读
    /// <see cref="State"/> / <see cref="ProgressText"/> / <see cref="Result"/>。服务进程由 <see cref="BrainService"/> 统一管理。
    /// </summary>
    public sealed class BrainClient
    {
        public enum ClientState { Idle, Starting, Solving, Ready, Failed }

        private readonly object gate = new object();
        private Thread worker;
        private TcpClient socket;
        private int generation;

        public volatile ClientState State = ClientState.Idle;
        public BrainPlan Result;
        public DateTime StartedAt;
        public string Battle;

        /// <summary>最后一次成功回复的原始 JSON。</summary>
        public JsonData Reply;

        // 进度（服务每个阶段报一次：阶段名、估计完成比例、计数）
        private volatile string stage;
        private volatile float fraction = -1f;
        private volatile int stageN, stageOf;
        private volatile bool startingService, hurried;

        // 失败原因：错误码（服务的 Code，或本地的 connect / closed）+ 原文
        private volatile string error, errorCode;

        public bool Busy => State == ClientState.Solving || State == ClientState.Starting;

        public double Elapsed => (DateTime.Now - StartedAt).TotalSeconds;

        public bool Installed => BrainService.Installed;

        /// <summary>估计完成比例 0..1，不知道时 -1。</summary>
        public float Fraction => State == ClientState.Solving ? fraction : -1f;

        /// <summary>当前在做什么（给人看的一句话）。</summary>
        public string ProgressText
        {
            get
            {
                if (State == ClientState.Starting)
                {
                    return startingService
                        ? L.T("正在启动求解服务（首次约 10 秒）…", "Starting the solver service (about 10 s the first time)…")
                        : L.T("连接求解服务…", "Connecting to the solver…");
                }
                if (hurried) return L.T("马上出结果…", "Wrapping up…");
                int n = stageN, of = stageOf;
                switch (stage)
                {
                    case "sync": return L.T("启动模拟器…", "Starting the simulator…");
                    case "resync": return L.T("和游戏重新对齐…", "Re-syncing with the game…");
                    case "search": return n > 0 ? L.T("搜索本回合的走法（" + n + " 个局面）", "Searching this turn (" + n + " states)") : L.T("搜索本回合的走法", "Searching this turn");
                    case "evaluate": return L.T("推演 " + of + " 个候选方案", "Playing out " + of + " candidates");
                    case "lookahead": return L.T("前瞻下回合 " + n + "/" + of, "Looking ahead " + n + "/" + of);
                    case "screen": return n > 0 ? L.T("筛选摆法 · 已试 " + n + " 种", "Screening layouts · " + n + " tried") : L.T("准备摆位模拟…", "Preparing layouts…");
                    case "full": return L.T("完整对局验证 " + n + "/" + of, "Full battles " + n + "/" + of);
                    default: return L.T("求解中…", "Solving…");
                }
            }
        }

        /// <summary>失败原因（给人看的一句话）。</summary>
        public string ErrorText
        {
            get
            {
                string e = error;
                switch (errorCode)
                {
                    case "connect": return BrainService.LastError ?? L.T("连不上求解服务", "Cannot reach the solver service");
                    case "closed": return L.T("求解服务断开了连接", "The solver service closed the connection");
                    case "missing": return L.T("找不到战斗记录文件", "Battle record file not found");
                    case "sync": return L.T("模拟器和游戏对不上：", "The simulator does not match the game: ") + e;
                    case "over": return L.T("战斗已经结束", "The battle is over");
                    case "not_turn": return L.T("现在不是我方回合", "It is not your turn");
                    case "unplayable": return L.T("模拟器暂时打不了这场战斗（有还不支持的敌人或事件，详见 brain.log）", "The simulator cannot play this battle yet (unsupported enemy or event, see brain.log)");
                    case "error": return L.T("求解出错：", "Solver error: ") + e;
                    default: return e ?? L.T("求解失败", "Solving failed");
                }
            }
        }

        /// <summary>开始求解当前战斗（之前的请求作废）。</summary>
        public void Solve(string battleFile, float seconds)
        {
            Start("{\"op\":\"solve\",\"battle\":" + Quote(battleFile) + ",\"seconds\":" + Seconds(seconds) + "}", battleFile,
                j =>
                {
                    BrainPlan p = BrainPlan.FromJson(j);
                    p.Budget = seconds;
                    return p;
                });
        }

        /// <summary>船坞：针对下一场敌人求最佳摆位。结果在 <see cref="Reply"/>。</summary>
        public void Loadout(string loadoutFile, int enemyId, float seconds)
        {
            Start("{\"op\":\"loadout\",\"file\":" + Quote(loadoutFile) + ",\"enemy\":" + enemyId + ",\"seconds\":" + Seconds(seconds) + "}", loadoutFile,
                j => null);
        }

        /// <summary>当前请求的 id（Start 里先放弃旧请求，再换新 id）。</summary>
        private volatile string requestId;

        /// <summary>
        /// "立即出结果"：让服务里正在跑的这个请求马上用目前最好的方案回复（回复照常从原连接回来）。
        /// 只作用于本客户端当前的请求 id，晚到的 hurry 不会误伤下一个请求。
        /// </summary>
        public void Hurry()
        {
            string id = requestId;
            if (State != ClientState.Solving || string.IsNullOrEmpty(id)) return;
            hurried = true;
            BrainService.SendBackground("{\"op\":\"hurry\",\"id\":" + Quote(id) + "}");
        }

        private static string Seconds(float s) => s.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// 战斗刚开始（敌方先手回合）时调用：让服务提前拉起工作进程、启动游戏、预热 JIT，
        /// 第一次求解就不用再等这些。后台进行，不影响 <see cref="State"/>。
        /// </summary>
        public void Warm(string battleFile)
        {
            if (!Installed || string.IsNullOrEmpty(battleFile)) return;
            string json = "{\"op\":\"warm\",\"battle\":" + Quote(battleFile) + "}";
            new Thread(() =>
            {
                try
                {
                    TcpClient tcp = BrainService.Connect(() => true, null);
                    if (tcp == null) return;
                    using (tcp)
                    {
                        var w = new StreamWriter(tcp.GetStream(), new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
                        var r = new StreamReader(tcp.GetStream(), new UTF8Encoding(false));
                        w.WriteLine(json);
                        r.ReadLine();
                    }
                }
                catch (Exception ex)
                {
                    Debug.Log("[Planner] brain warm-up failed: " + ex.Message);
                }
            }) { IsBackground = true, Name = "BrainWarm" }.Start();
        }

        private Func<JsonData, BrainPlan> parse;

        private void Start(string requestJson, string battleFile, Func<JsonData, BrainPlan> parser)
        {
            Cancel();
            requestId = Guid.NewGuid().ToString("N");
            requestJson = requestJson.Substring(0, requestJson.Length - 1) + ",\"id\":" + Quote(requestId) + "}";
            parse = parser;
            int gen;
            lock (gate)
            {
                gen = ++generation;
                State = ClientState.Starting;
                stage = null;
                fraction = -1f;
                stageN = stageOf = 0;
                startingService = false;
                hurried = false;
                error = errorCode = null;
                Result = null;
                Reply = null;
                StartedAt = DateTime.Now;
                Battle = battleFile;
            }
            worker = new Thread(() => Run(gen, requestJson)) { IsBackground = true, Name = "BrainClient" };
            worker.Start();
        }

        public void Cancel()
        {
            // 服务一次只跑一个求解：被放弃的请求先让它尽快收尾，下一个请求才不用干等
            string id = requestId;
            if (Busy && !string.IsNullOrEmpty(id)) BrainService.SendBackground("{\"op\":\"hurry\",\"id\":" + Quote(id) + "}");
            lock (gate)
            {
                generation++;
                try
                {
                    socket?.Close();
                }
                catch
                {
                    // ignore
                }
                socket = null;
                if (Busy) State = ClientState.Idle;
            }
        }

        public void Clear()
        {
            Cancel();
            lock (gate)
            {
                Result = null;
                error = errorCode = null;
                State = ClientState.Idle;
            }
        }

        private bool Current(int gen) => gen == generation;

        private void Run(int gen, string requestJson)
        {
            try
            {
                TcpClient tcp = BrainService.Connect(() => Current(gen), () => startingService = true);
                if (tcp == null)
                {
                    Fail(gen, "connect", null);
                    return;
                }
                lock (gate)
                {
                    if (!Current(gen))
                    {
                        tcp.Close();
                        return;
                    }
                    socket = tcp;
                    State = ClientState.Solving;
                }
                NetworkStream stream = tcp.GetStream();
                var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
                var reader = new StreamReader(stream, new UTF8Encoding(false));
                writer.WriteLine(requestJson);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!Current(gen)) return;
                    JsonData j = JsonMapper.ToObject(line);
                    if (BrainPlan.Has(j, "progress"))
                    {
                        stage = BrainPlan.Str(j, "stage");
                        fraction = BrainPlan.Has(j, "frac") ? (float)BrainPlan.Dbl(j, "frac") : -1f;
                        stageN = BrainPlan.Int(j, "n");
                        stageOf = BrainPlan.Int(j, "of");
                        continue;
                    }
                    if (!BrainPlan.Bool(j, "Ok"))
                    {
                        Fail(gen, BrainPlan.Str(j, "Code"), BrainPlan.Str(j, "Error"));
                        return;
                    }
                    BrainPlan plan = parse?.Invoke(j);
                    lock (gate)
                    {
                        if (!Current(gen)) return;
                        Result = plan;
                        Reply = j;
                        State = ClientState.Ready;
                    }
                    tcp.Close();
                    return;
                }
                Fail(gen, "closed", null);
            }
            catch (Exception ex)
            {
                if (Current(gen)) Fail(gen, "error", ex.Message);
            }
        }

        private void Fail(int gen, string code, string message)
        {
            lock (gate)
            {
                if (!Current(gen)) return;
                errorCode = code;
                error = message;
                State = ClientState.Failed;
            }
            Debug.LogWarning("[Planner] brain: " + code + " " + message);
        }

        private static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
