using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Brain;

/// <summary>
/// Local TCP service for the in-game planner mod (127.0.0.1, one JSON object per line).
///
///   → {"op":"hello"}                                   ← {"ok":true,"version":...}
///   → {"op":"solve","battle":"<live battle .jsonl>","seconds":30}   (0 / missing = no time limit)
///   → {"op":"loadout","file":"<pre-battle .jsonl>","seconds":600}  (time cap; stops by itself when converged)
///   → {"op":"hurry","id":"..."}   the running solve / loadout (with that request id, or any when
///        no id is given) answers now with the best found so far
///        ← {"progress":"...","stage":"sync|resync|search|evaluate|lookahead|screen|full","frac":0..1,"n":..,"of":..}   (zero or more)
///        ← {"done":true, "ok":..., "plan":[tokens], ...}   (see <see cref="SolveReply"/>)
///   → {"op":"quit"}
///
/// Failed replies carry a <c>Code</c> for the client to explain: missing, sync, over, not_turn,
/// unplayable, error. With <c>--parent PID</c> the service quits when that process (the game) ends.
///
/// The worker pool follows the battle file the mod's recorder writes: a new file re-initialises
/// the workers, the same file just applies the newly recorded actions. Every sync is checked
/// against the newest in-game snapshot so a divergence is reported instead of trusted.
/// </summary>
public static class Service
{
    public const string Version = "brain-6";

    /// <summary>0 = no time limit: the full search runs (results reproducible).</summary>
    public const double DefaultSolveSeconds = 0;
    public const double DefaultLoadoutSeconds = 600;

    private static volatile Search activeSearch;
    private static volatile LoadoutSearch activeLoadout;
    private static volatile string activeId;

    private sealed class ClientRequest
    {
        public string Op;
        public string Battle;
        /// <summary>Time budget; 0 = default (<see cref="DefaultSolveSeconds"/> / <see cref="DefaultLoadoutSeconds"/>).</summary>
        public double Seconds;

        /// <summary>Client's id for this request, so a late "hurry" cannot hit the next request.</summary>
        public string Id;
        public string File;
        public int Enemy;
    }

    public sealed class LoadoutReply
    {
        public bool Done = true;
        public bool Ok;
        public string Error;
        public string Code;
        /// <summary>Target layout: x, y, unit index, unit id, lv, source x, source y, bag index (-1 = on ship).</summary>
        public List<int[]> Entries = new();
        public string Value;
        public string Summary;
        public string CurrentValue;
        public string CurrentSummary;
        /// <summary>Structured <see cref="Summary"/> / <see cref="CurrentSummary"/> for the client to word.</summary>
        public Outcome Outcome;
        public Outcome CurrentOutcome;
        public bool IsCurrent;
        public int Evaluated;
        public double Seconds;
    }

    public sealed class SolveReply
    {
        public bool Done = true;
        public bool Ok;
        public string Error;
        public string Code;
        public List<string> Plan = new();
        public string Value;
        public bool Win;
        public int PredictHp;
        /// <summary>Fuel left after the predicted win (it carries over to the next battle).</summary>
        public int PredictFuel;
        public int PredictRound;
        public int Round;
        public int PlayerHp, PlayerShield, EnemyHp, EnemyShield, EnemyBreak;
        /// <summary>Turn result predicted by the real resolution of this plan.</summary>
        public int TurnDealt, TurnTaken;
        /// <summary>Clash preview after the plan (y, player, enemy, result, both-sides, playerLine, enemyLine).</summary>
        public List<int[]> Lines;
        public string NextTurn;
        public List<string> Alternatives = new();
        public List<string> Desync;
        public double Seconds;
        public long Jobs;
    }

    private static WorkerPool pool;

    /// <summary>
    /// Decisions the last loadout study made in its full games, by state hash. A battle started with
    /// the recommended (or current) layout begins in one of those states, and the full search there
    /// is the same search the battle would run: its first turns are answered from here at once.
    /// </summary>
    private static Dictionary<string, (NodeInfo Root, Decision Decision)> loadoutDecisions = new();

    /// <summary>The last loadout study that ended by itself, to answer the same question again at once.</summary>
    private sealed class Study
    {
        /// <summary>The save it answered for (see <see cref="SaveKey"/>) and that answer.</summary>
        public string SaveKey;
        public LoadoutReply Reply;
        /// <summary>The battle (see <see cref="BattleKey"/>) and the dock's units, cells and load limit.</summary>
        public string Battle;
        public string Setup;
        /// <summary>The recommended layout by unit kinds (<see cref="LoadoutSearch.SigKey"/>), and its value.</summary>
        public string BestSig;
        public LoadoutValue Best;
        /// <summary>Layouts with a known start-of-battle state hash, by unit kinds.</summary>
        public Dictionary<string, LoadoutValue> Played = new();
        public int Evaluated;
    }

    private static Study lastStudy;
    private static string poolKey;
    private static string currentBattle;
    private static string brainDll;
    private static string expectedRoot;
    private static string expectedPlan;
    private static int workers;

    /// <summary>One game world per process: a different battle (or a loadout study) gets fresh worker processes.</summary>
    private static void EnsurePool(string key)
    {
        if (pool != null && poolKey == key)
        {
            return;
        }
        pool?.Dispose();
        pool = new WorkerPool(workers, brainDll);
        poolKey = key;
        currentBattle = null;
    }
    private static readonly SemaphoreSlim busy = new(1, 1);

    public static async Task<int> Run(string root, string dll, int jobs, int port, int parent = 0)
    {
        PurgeWorkDirs(root);
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            Console.Error.WriteLine($"port {port} unavailable ({ex.Message}); another Brain service is probably running");
            return 3;
        }
        Console.WriteLine($"Brain service on 127.0.0.1:{port}, {jobs} workers (root {root})");
        brainDll = dll;
        workers = jobs;
        var quit = new CancellationTokenSource();
        if (parent > 0)
        {
            WatchParent(parent, quit);
        }
        try
        {
            while (!quit.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(quit.Token);
                _ = Task.Run(() => Serve(client, quit));
            }
        }
        catch (OperationCanceledException)
        {
        }
        pool?.Dispose();
        return 0;
    }

    private static async Task Serve(TcpClient client, CancellationTokenSource quit)
    {
        using (client)
        {
            NetworkStream stream = client.GetStream();
            var reader = new StreamReader(stream, new UTF8Encoding(false));
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            string line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                ClientRequest req;
                try
                {
                    req = Json.Read<ClientRequest>(line);
                }
                catch
                {
                    await writer.WriteLineAsync("{\"ok\":false,\"error\":\"bad request\"}");
                    continue;
                }
                switch (req.Op)
                {
                    case "hello":
                        await writer.WriteLineAsync(Json.Write(new { ok = true, version = Version, workers }));
                        break;
                    case "solve":
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] solve {req.Battle}");
                        SolveReply reply = await Solve(req, m => Console.WriteLine("  " + m), Stage(writer));
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] -> {(reply.Ok ? string.Join(";", reply.Plan) + "  " + reply.Value : "error: " + reply.Error)} ({reply.Seconds:F1}s)");
                        await writer.WriteLineAsync(Json.Write(reply));
                        break;
                    case "warm":
                    {
                        // battle just started (enemy's first turn): spawn and sync the workers now,
                        // so the first solve does not pay for process start, boot and JIT
                        var sw = Stopwatch.StartNew();
                        string err = await Warm(req);
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] warm {req.Battle}: {err ?? "ok"} ({sw.ElapsedMilliseconds}ms)");
                        await writer.WriteLineAsync(Json.Write(new { Done = true, Ok = err == null, Error = err }));
                        break;
                    }
                    case "loadout":
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] loadout {req.File}");
                        LoadoutReply lr = await Loadout(req, m => Console.WriteLine("  " + m), Stage(writer));
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] -> {(lr.Ok ? lr.Summary + " (current: " + lr.CurrentSummary + ")" : "error: " + lr.Error)} ({lr.Seconds:F0}s)");
                        await writer.WriteLineAsync(Json.Write(lr));
                        break;
                    case "hurry":
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] hurry");
                        if (string.IsNullOrEmpty(req.Id) || req.Id == activeId)
                        {
                            activeSearch?.Hurry();
                            activeLoadout?.Hurry();
                        }
                        await writer.WriteLineAsync("{\"ok\":true}");
                        break;
                    case "quit":
                        await writer.WriteLineAsync("{\"ok\":true}");
                        quit.Cancel();
                        return;
                    default:
                        await writer.WriteLineAsync("{\"ok\":false,\"error\":\"unknown op\"}");
                        break;
                }
            }
        }
    }

    /// <summary>Progress for the client: the stage key, an estimated fraction done and a count (the log lines stay in the console).</summary>
    private static Action<string, double, int, int> Stage(StreamWriter writer) =>
        (stage, frac, n, of) => Send(writer, Json.Write(new { progress = stage, stage, frac = Math.Round(frac, 3), n, of }));

    /// <summary>The service belongs to the game that started it: quit when that process ends (also on a crash).</summary>
    private static void WatchParent(int pid, CancellationTokenSource quit)
    {
        Process game;
        try
        {
            game = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            quit.Cancel();
            return;
        }
        _ = Task.Run(async () =>
        {
            await game.WaitForExitAsync();
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] parent process {pid} exited, quitting");
            quit.Cancel();
        });
    }

    /// <summary>Work folders of workers that are gone (killed before they could clean up).</summary>
    private static void PurgeWorkDirs(string root)
    {
        string dir = Path.Combine(root, "out", "brain");
        if (!Directory.Exists(dir))
        {
            return;
        }
        foreach (string d in Directory.GetDirectories(dir))
        {
            if (int.TryParse(Path.GetFileName(d), out int pid) && Alive(pid))
            {
                continue;
            }
            try
            {
                Directory.Delete(d, true);
            }
            catch
            {
                // in use or already gone
            }
        }

        static bool Alive(int pid)
        {
            try
            {
                using Process p = Process.GetProcessById(pid);
                return !p.HasExited;
            }
            catch
            {
                return false;
            }
        }
    }

    private static void Send(StreamWriter w, string line)
    {
        try
        {
            lock (w)
            {
                w.WriteLine(line);
            }
        }
        catch
        {
            // client went away
        }
    }

    private static async Task<SolveReply> Solve(ClientRequest req, Action<string> log, Action<string, double, int, int> stage)
    {
        var sw = Stopwatch.StartNew();
        await busy.WaitAsync();
        try
        {
            if (string.IsNullOrEmpty(req.Battle) || !File.Exists(req.Battle))
            {
                return new SolveReply { Error = "battle file not found: " + req.Battle, Code = "missing" };
            }
            EnsurePool("battle:" + req.Battle);
            List<string> desync = null;
            NodeInfo root = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                List<Response> sync;
                if (currentBattle != req.Battle || attempt > 0)
                {
                    log(attempt == 0 ? "starting the simulator" : "state differs from the game, syncing again");
                    stage(attempt == 0 ? "sync" : "resync", 0, 0, 0);
                    if (attempt > 0)
                    {
                        poolKey = null; // fresh processes: a world is never re-booted in place
                        EnsurePool("battle:" + req.Battle);
                    }
                    sync = await pool.Broadcast(() => new Request { Op = "init", Recording = req.Battle, SeekTurn = 0 });
                    currentBattle = req.Battle;
                    if (sync.All(r => r.Ok))
                    {
                        sync = await pool.Broadcast(() => new Request { Op = "sync" });
                    }
                }
                else
                {
                    sync = await pool.Broadcast(() => new Request { Op = "sync" });
                }
                Response bad = sync.FirstOrDefault(r => !r.Ok);
                if (bad != null)
                {
                    currentBattle = null;
                    return new SolveReply { Error = "模拟器同步失败: " + bad.Error, Code = "sync" };
                }
                root = sync[0].Node;
                desync = sync[0].Sync;
                if (desync == null || desync.Count == 0)
                {
                    break;
                }
            }
            if (!root.InTurn)
            {
                return new SolveReply { Error = root.Over ? "战斗已结束" : "现在不是玩家回合", Code = root.Over ? "over" : "not_turn", Desync = desync };
            }

            Decision d = null;
            if (desync == null || desync.Count == 0)
            {
                lock (loadoutDecisions)
                {
                    if (root.Hash != null && loadoutDecisions.TryGetValue(root.Hash, out var known))
                    {
                        d = known.Decision;
                    }
                }
                if (d != null)
                {
                    log("same state as in the loadout study: answered from there");
                }
            }
            if (d == null)
            {
                double budget = req.Seconds > 0 ? req.Seconds : DefaultSolveSeconds;
                log(budget > 0 ? $"searching ({budget:0}s)" : "searching (no time limit)");
                var search = new Search(pool, new SearchConfig(), m => log(m.Trim()))
                {
                    Stage = stage,
                    Hint = root.Hash == expectedRoot ? expectedPlan : null,
                    // the budget counts from the request (sync included)
                    Deadline = budget > 0 ? DateTime.UtcNow.AddSeconds(budget - sw.Elapsed.TotalSeconds) : DateTime.MaxValue,
                    UseSpareTime = budget > 0,
                };
                activeId = req.Id;
                activeSearch = search;
                try
                {
                    d = await search.Decide();
                }
                finally
                {
                    activeSearch = null;
                }
            }
            expectedRoot = d.NextHash;
            expectedPlan = d.NextPlan;
            PlanValue best = d.Best;
            var reply = new SolveReply
            {
                Ok = best != null,
                Plan = Lonestar.Engine.SearchWorld.Split(d.Plan).ToList(),
                Value = Search.Fmt(best?.Best ?? double.NegativeInfinity),
                Round = root.Round,
                PlayerHp = root.PHp,
                PlayerShield = root.PSh,
                EnemyHp = root.EHp,
                EnemyShield = root.ESh,
                EnemyBreak = root.EBreak,
                Lines = best?.Node?.Lines,
                NextTurn = best?.LookPlan,
                Desync = desync?.Count > 0 ? desync : null,
                Seconds = sw.Elapsed.TotalSeconds,
                Jobs = d.Jobs,
            };
            if (best?.Eval?.Turn != null)
            {
                reply.TurnDealt = best.Eval.Turn.EnemyHpLost;
                reply.TurnTaken = best.Eval.Turn.PlayerHpLost;
            }
            reply.Win = Search.WinParts(best?.Best ?? double.NegativeInfinity, out reply.PredictHp, out reply.PredictFuel, out reply.PredictRound);
            foreach (PlanValue p in d.Ranked.Skip(1).Take(4))
            {
                reply.Alternatives.Add($"{Search.Fmt(p.Best)}  {p.Prefix}");
            }
            if (reply.Plan.Count == 0)
            {
                reply.Plan = new List<string>();
            }
            reply.Plan.Add("end");
            return reply;
        }
        catch (Exception ex)
        {
            currentBattle = null;
            return new SolveReply { Error = ex.GetType().Name + ": " + ex.Message, Code = "error" };
        }
        finally
        {
            busy.Release();
        }
    }

    private static async Task<string> Warm(ClientRequest req)
    {
        await busy.WaitAsync();
        try
        {
            if (string.IsNullOrEmpty(req.Battle) || !File.Exists(req.Battle))
            {
                return "battle file not found";
            }
            if (poolKey == "battle:" + req.Battle && currentBattle == req.Battle)
            {
                return null;
            }
            EnsurePool("battle:" + req.Battle);
            List<Response> init = await pool.Broadcast(() => new Request { Op = "init", Recording = req.Battle, SeekTurn = 0 });
            Response bad = init.FirstOrDefault(r => !r.Ok);
            if (bad != null)
            {
                poolKey = null;
                return bad.Error;
            }
            currentBattle = req.Battle;
            // touch the search code paths once so the first real solve runs JIT-warm
            await pool.Broadcast(() => new Request { Op = "expand", Prefix = "" });
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            busy.Release();
        }
    }

    private static async Task<LoadoutReply> Loadout(ClientRequest req, Action<string> log, Action<string, double, int, int> stage)
    {
        var sw = Stopwatch.StartNew();
        await busy.WaitAsync();
        try
        {
            if (string.IsNullOrEmpty(req.File) || !File.Exists(req.File))
            {
                return new LoadoutReply { Error = "loadout file not found: " + req.File, Code = "missing" };
            }
            double budget = req.Seconds > 0 ? req.Seconds : DefaultLoadoutSeconds;
            string battle = BattleKey(req.File, req.Enemy), saveKey = SaveKey(req.File, battle);
            Study last = lastStudy;
            if (last != null && last.SaveKey == saveKey)
            {
                log("same save and battle as the last study: answered from there");
                return Answered(last.Reply, sw);
            }
            EnsurePool("loadout:" + req.File + ":" + File.GetLastWriteTimeUtc(req.File).Ticks);
            log("starting the simulator");
            stage("sync", 0, 0, 0);
            var search = new LoadoutSearch(pool, new SearchConfig(), m => log(m.Trim())) { Seconds = budget, Stage = stage };
            if (last != null && last.Battle == battle && await Again(last, search, req, log) is { } again)
            {
                last.SaveKey = saveKey;
                last.Reply = again;
                return Answered(again, sw);
            }
            activeId = req.Id;
            activeLoadout = search;
            List<LoadoutValue> ranked;
            try
            {
                ranked = await search.Run(req.File, req.Enemy);
            }
            finally
            {
                activeLoadout = null;
            }
            lock (loadoutDecisions)
            {
                lock (search.Decisions)
                {
                    loadoutDecisions = new Dictionary<string, (NodeInfo, Decision)>(search.Decisions);
                }
            }
            log($"{loadoutDecisions.Count} decisions kept for the battle");
            LoadoutValue best = ranked.FirstOrDefault();
            LoadoutInfo info = search.Info;
            LoadoutValue current = ranked.FirstOrDefault(v => v.Layout == info.Current);
            bool isCurrent = best != null && search.Canon(best.Layout) == search.Canon(info.Current);
            LoadoutReply reply = Reply(info, best, best == null ? null : isCurrent ? info.Current : search.Tidy(best.Layout), current, isCurrent, ranked.Count);
            reply.Seconds = sw.Elapsed.TotalSeconds;
            lastStudy = null;
            if (search.Complete && best != null)
            {
                var study = new Study
                {
                    SaveKey = saveKey,
                    Reply = reply,
                    Battle = battle,
                    Setup = search.SetupKey(),
                    BestSig = search.SigKey(best.Layout),
                    Best = best,
                    Evaluated = ranked.Count,
                };
                foreach (LoadoutValue v in ranked.Where(v => v.StartHash != null))
                {
                    study.Played.TryAdd(search.SigKey(v.Layout), v);
                }
                lastStudy = study;
            }
            return reply;
        }
        catch (Exception ex)
        {
            return new LoadoutReply
            {
                Error = ex.GetType().Name + ": " + ex.Message,
                // the simulator cannot play this battle at all (an enemy it does not handle yet)
                Code = ex.Message.Contains("模拟器打不了") ? "unplayable" : "error",
            };
        }
        finally
        {
            busy.Release();
        }
    }

    private static LoadoutReply Reply(LoadoutInfo info, LoadoutValue best, string bestLayout, LoadoutValue current, bool isCurrent, int evaluated)
    {
        var reply = new LoadoutReply
        {
            Ok = best != null,
            Value = Search.Fmt(best?.Best ?? double.NegativeInfinity),
            Summary = Summary(best),
            Outcome = OutcomeOf(best),
            CurrentOutcome = OutcomeOf(current),
            CurrentValue = Search.Fmt(current?.Best ?? double.NegativeInfinity),
            CurrentSummary = Summary(current),
            IsCurrent = isCurrent,
            Evaluated = evaluated,
        };
        foreach (var (x, y, unit) in Worker.ParseLayout(bestLayout))
        {
            LoadoutUnit u = info.Units[unit];
            reply.Entries.Add(new[] { x, y, unit, u.Id, u.Lv, u.X, u.Y, u.Bag });
        }
        return reply;
    }

    /// <summary>A stored answer, sent again (with this request's time).</summary>
    private static LoadoutReply Answered(LoadoutReply r, Stopwatch sw)
    {
        LoadoutReply copy = Json.Read<LoadoutReply>(Json.Write(r));
        copy.Seconds = sw.Elapsed.TotalSeconds;
        return copy;
    }

    /// <summary>
    /// The same dock and battle as <paramref name="last"/> with the units moved around (typically:
    /// the player applied the recommendation and asks again). The study still holds when the units,
    /// cells and load limit are the same and the battle with the layout now on the ship starts in
    /// the very state that study played it from (then the rest of the save is the same too).
    /// Null when that cannot be shown.
    /// </summary>
    private static async Task<LoadoutReply> Again(Study last, LoadoutSearch search, ClientRequest req, Action<string> log)
    {
        LoadoutInfo info = await search.Prepare(req.File, req.Enemy);
        string curSig = search.SigKey(info.Current);
        if (search.SetupKey() != last.Setup)
        {
            log("not the dock of the last study (other units, cells or load limit)");
            return null;
        }
        if (!last.Played.TryGetValue(curSig, out LoadoutValue cur))
        {
            log("the layout on the ship was not played in the last study");
            return null;
        }
        string start = await search.StartHash(info.Current);
        if (start != cur.StartHash)
        {
            log($"the battle starts in another state than in the last study ({start} vs {cur.StartHash})");
            return null;
        }
        // a layout as good as the recommended one needs no change
        bool isCurrent = curSig == last.BestSig || cur.Best >= last.Best.Best;
        string bestLayout = isCurrent ? info.Current : search.FromSigKey(last.BestSig);
        if (bestLayout == null)
        {
            return null;
        }
        log("same dock and battle as the last study, units moved: answered from there");
        return Reply(info, isCurrent ? cur : last.Best, bestLayout, cur, isCurrent, last.Evaluated);
    }

    /// <summary>The battle a loadout file asks about: everything but the save (enemy, battle event).</summary>
    private static string BattleKey(string file, int enemy) =>
        enemy + "\n" + string.Join("\n", Lonestar.Engine.Recording.ReadRecords(file)
            .Where(r => Lonestar.Engine.Recording.Type(r) != "battle_begin").Select(r => r.GetRawText()));

    /// <summary>
    /// The save and battle of a loadout file, minus the run's play timer: two clicks a minute apart
    /// write saves that differ only in that (it does not take part in battles).
    /// </summary>
    private static string SaveKey(string file, string battle)
    {
        var sb = new StringBuilder(battle).Append('\n');
        foreach (JsonElement r in Lonestar.Engine.Recording.ReadRecords(file))
        {
            if (Lonestar.Engine.Recording.Type(r) != "battle_begin" || !r.TryGetProperty("save_files", out JsonElement files))
            {
                continue;
            }
            foreach (JsonProperty f in files.EnumerateObject().OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                string text = f.Value.GetString() ?? "";
                sb.Append(f.Name).Append('\n').Append(f.Name.EndsWith("wantedProcess.json") ? WithoutPlayTime(text) : text).Append('\n');
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string WithoutPlayTime(string encrypted)
    {
        try
        {
            JsonNode n = JsonNode.Parse(DecryptSave(encrypted).TrimStart('\uFEFF'));
            if (n?["processStatistics"] is JsonObject st)
            {
                st.Remove("gameTime");
                st.Remove("gamteTimeString"); // sic
            }
            return n?.ToJsonString() ?? encrypted;
        }
        catch
        {
            return encrypted;
        }
    }

    /// <summary>The game's save encryption (EncryptionTool: TripleDES-ECB, PKCS7, key MD5("777")).</summary>
    private static string DecryptSave(string text)
    {
        using TripleDES des = TripleDES.Create();
        des.Key = MD5.HashData(Encoding.UTF8.GetBytes("777"));
        des.Mode = CipherMode.ECB;
        des.Padding = PaddingMode.PKCS7;
        byte[] b = Convert.FromBase64String(text);
        return Encoding.UTF8.GetString(des.CreateDecryptor().TransformFinalBlock(b, 0, b.Length));
    }

    /// <summary>What the layout was shown to achieve, for the dock panel.</summary>
    private static string Summary(LoadoutValue v)
    {
        if (v == null)
        {
            return null;
        }
        if (v.FullSummary != null && v.Full >= v.Greedy)
        {
            return v.FullSummary;
        }
        if (Search.WinParts(v.Best, out int hp, out int fuel, out int round))
        {
            return $"胜利 第{round}回合 剩{hp}HP {fuel}燃料";
        }
        if (double.IsNegativeInfinity(v.Best))
        {
            return "无法模拟";
        }
        if (v.Best <= -1e9 + 1e7)
        {
            // Lost(damage, round) = -1e9 + damage * 1e3 + round
            double r = v.Best + 1e9;
            return $"失败 第{(int)Math.Round(r % 1e3)}回合 共造成{(int)(r / 1e3)}伤害";
        }
        return "未分胜负";
    }

    private static Outcome OutcomeOf(LoadoutValue v)
    {
        if (v == null)
        {
            return null;
        }
        if (v.FullOutcome != null && v.Full >= v.Greedy)
        {
            return v.FullOutcome;
        }
        if (Search.WinParts(v.Best, out int hp, out int fuel, out int round))
        {
            return new Outcome { Kind = "win", Hp = hp, Fuel = fuel, Round = round };
        }
        if (double.IsNegativeInfinity(v.Best))
        {
            return new Outcome { Kind = "none" };
        }
        if (v.Best <= -1e9 + 1e7)
        {
            double r = v.Best + 1e9;
            return new Outcome { Kind = "loss", Round = (int)Math.Round(r % 1e3), Damage = (int)(r / 1e3) };
        }
        return new Outcome { Kind = "open" };
    }

}

/// <summary>How a battle ends: Kind = win (Round, Hp and Fuel left), loss (Round, Damage dealt), open (not decided), none (could not be played).</summary>
public sealed class Outcome
{
    public string Kind;
    public int Round;
    public int Hp;
    public int Fuel;
    public int Damage;
    /// <summary>From a game played partly with cut-short decisions: the layout does at least this well.</summary>
    public bool AtLeast;
}
