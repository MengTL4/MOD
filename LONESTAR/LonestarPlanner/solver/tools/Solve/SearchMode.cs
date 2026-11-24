// Turn-level optimizer: enumerates candidate placements, evaluates each in its own headless process
// (one process per rollout, because the game cannot be reset in-process), ranks by the game's own
// clash preview, then re-simulates the best few through a real turn resolution.
//
// Two entry points:
//   Run(...)        one decision point -> printed report
//   PlayBattle(...) solver-driven full battle: solve turn, apply, repeat (used for validation and
//                   for evaluating pre-battle layouts with a strong policy)

using System.Diagnostics;
using System.Text.Json;

internal sealed class TurnSolution
{
    public string Sequence;
    public bool Resolved;
    public bool Ended;
    public bool Won;
    public int PlayerHp;
    public int EnemyHp;
    public int PlayerHpLost;
    public int EnemyHpLost;
    public int Candidates;
    public double Seconds;
    public string Error;
    public List<string> Report;

    // Projection from the greedy full-battle rollout of the chosen line (evaluation only).
    public bool HasRollout;
    public bool RolloutWon;
    public int RolloutPlayerHp;
    public int RolloutTurns;
}

internal static class SearchMode
{
    public static int Run(string recording, int turn, string prefix, string layout, string forceUnits, string root, int jobs, int maxPlans, int verify, string assemblyPath)
    {
        if (turn <= 0 && prefix == null)
        {
            Console.Error.WriteLine("--search needs --turn N or --prefix <actions>");
            return 2;
        }
        var sw = Stopwatch.StartNew();
        TurnSolution solution = SolveTurn(recording, turn, prefix, layout, forceUnits, jobs, maxPlans, verify, assemblyPath, verbose: true);
        if (solution == null)
        {
            Console.Error.WriteLine("[search] failed");
            return 1;
        }
        Console.WriteLine($"=== LONESTAR battle solver, turn {(turn > 0 ? turn : 0)} ===");
        Console.WriteLine($"candidates={solution.Candidates} time={solution.Seconds:F1}s");
        foreach (string line in solution.Report ?? new List<string>())
        {
            Console.WriteLine(line);
        }
        Console.Error.WriteLine($"[search] total {sw.Elapsed.TotalSeconds:F1}s");
        try
        {
            string outDir = Path.Combine(root, "out", "solve");
            Directory.CreateDirectory(outDir);
            string outFile = Path.Combine(outDir, $"search_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            File.WriteAllText(outFile, JsonSerializer.Serialize(new
            {
                recording,
                turn,
                prefix,
                layout,
                forceUnits,
                solution.Sequence,
                solution.Candidates,
                solution.Seconds,
                solution.Resolved,
                solution.Ended,
                solution.Won,
                solution.PlayerHp,
                solution.EnemyHp,
                solution.PlayerHpLost,
                solution.EnemyHpLost,
                solution.HasRollout,
                solution.RolloutWon,
                solution.RolloutPlayerHp,
                solution.RolloutTurns,
                solution.Report,
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.Error.WriteLine($"[search] wrote {outFile}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[search] could not write report: " + ex.Message);
        }
        return 0;
    }

    /// <summary>Solves the decision at the given prefix and returns the best action sequence.</summary>
    public static TurnSolution SolveTurn(string recording, int turn, string prefix, string layout, string forceUnits,
        int jobs, int maxPlans, int verify, string assemblyPath, bool verbose)
    {
        string dotnet = DotnetPath();
        if (verbose)
        {
            Console.Error.WriteLine($"[search] recording={Path.GetFileName(recording)} turn={turn} prefix={(prefix == null ? "-" : prefix.Length + "ch")} layout={(layout ?? "-")} jobs={jobs} maxPlans={maxPlans} verify={verify}");
        }
        var sw = Stopwatch.StartNew();
        JsonElement query = RunWorker(dotnet, assemblyPath, recording, turn, prefix, layout, forceUnits, null, false, probe: true);
        if (query.ValueKind != JsonValueKind.Object || !query.GetProperty("ok").GetBoolean())
        {
            return null;
        }
        var state = StateView.From(query);
        if (state.Ended)
        {
            return new TurnSolution { Sequence = "", Ended = true, PlayerHp = state.PlayerHp, EnemyHp = state.EnemyHp, Candidates = 0, Seconds = 0 };
        }
        if (verbose)
        {
            Console.Error.WriteLine($"[search] state: round={state.Round} hand={state.Hand.Count} units={state.Units.Count} enemyHp={state.EnemyHp}+{state.EnemyShield}sh moves=[{string.Join(",", state.Moves)}] ({sw.ElapsedMilliseconds}ms)");
            Console.Error.WriteLine($"[search] hand={string.Join(" ", state.Hand.Select(h => $"[{h.Power}/{string.Join(",", h.Colors)}]"))}");
        }

        List<Plan> plans = GeneratePlans(state, maxPlans);
        if (verbose)
        {
            Console.Error.WriteLine($"[search] candidate placements: {plans.Count}");
        }

        // Phase A: rank every place-and-see plan by the game's clash preview (no turn resolution).
        var results = new PlanResult[plans.Count];
        int done = 0;
        Parallel.For(0, plans.Count, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i =>
        {
            JsonElement r = RunWorker(dotnet, assemblyPath, recording, turn, prefix, layout, forceUnits, plans[i].Sequence, false);
            results[i] = Evaluate(plans[i], r);
            int n = Interlocked.Increment(ref done);
            if (verbose && n % 16 == 0)
            {
                Console.Error.Write($"\r[search] preview {n}/{plans.Count}   ");
            }
        });
        if (verbose)
        {
            Console.Error.WriteLine();
        }

        var valid = results.Where(r => r != null && r.Score > double.NegativeInfinity).OrderByDescending(r => r.Score).ToList();
        if (verbose)
        {
            Console.Error.WriteLine($"[search] preview done: {valid.Count}/{plans.Count} valid ({sw.ElapsedMilliseconds}ms)");
            foreach (PlanResult r in valid.Take(5))
            {
                Console.Error.WriteLine($"  preview {r.Score,10:F0}  {r.Plan.Sequence}");
            }
        }

        // Phase B: re-simulate the leading candidates through a real turn resolution, plus order
        // variants (skill triggers are order sensitive) and movement variants.
        var finalists = new List<Plan>();
        foreach (PlanResult r in valid.Take(Math.Max(verify, 1)))
        {
            finalists.Add(r.Plan);
            foreach (string order in new[] { "desc", "asc" })
            {
                Plan variant = r.Plan.WithOrder(order);
                if (variant != null && variant.Sequence != r.Plan.Sequence)
                {
                    finalists.Add(variant);
                }
            }
        }
        if (prefix == null)
        {
            Plan recordedPlan = RecordedPlan(state);
            if (recordedPlan != null)
            {
                finalists.Add(recordedPlan);
            }
        }
        var moveVariants = new List<Plan>();
        foreach (Plan p in finalists.Take(6))
        {
            foreach (int m in state.Moves)
            {
                moveVariants.Add(p.WithMove(m));
            }
        }
        finalists.AddRange(moveVariants);

        // Active-skill variants: click a skill (and pick a target for two-step skills) before the
        // placements. Without a probe the skill list is empty, so this is a no-op for battles whose
        // units have no usable active skill.
        var skillVariants = new List<Plan>();
        if (state.Skills.Count > 0)
        {
            int budget = 12;
            foreach (Plan p in finalists.Take(5))
            {
                foreach (SkillView skill in state.Skills)
                {
                    if (budget-- <= 0)
                    {
                        break;
                    }
                    string click = $"click:{skill.X},{skill.Y}";
                    if (skill.TwoStep)
                    {
                        if (skill.Targets == null || skill.Targets.Count == 0)
                        {
                            continue;
                        }
                        foreach (TargetView target in skill.Targets.Take(4))
                        {
                            skillVariants.Add(p.WithPrefix($"{click};target:{target.Index}"));
                        }
                    }
                    else
                    {
                        skillVariants.Add(p.WithPrefix(click));
                    }
                }
            }
            finalists.AddRange(skillVariants);
            if (verbose)
            {
                Console.Error.WriteLine($"[search] skill variants: {skillVariants.Count} (skills available: {state.Skills.Count})");
            }
        }

        finalists = finalists.GroupBy(p => p.Sequence).Select(g => g.First()).ToList();
        if (verbose)
        {
            Console.Error.WriteLine($"[search] verifying {finalists.Count} finalists with a real turn resolution");
        }

        var finals = new PlanResult[finalists.Count];
        Parallel.For(0, finalists.Count, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i =>
        {
            JsonElement r = RunWorker(dotnet, assemblyPath, recording, turn, prefix, layout, forceUnits, finalists[i].Sequence, true);
            finals[i] = EvaluateResolved(finalists[i], r, state);
        });
        var ranked = finals.Where(f => f != null && !double.IsNegativeInfinity(f.Score)).OrderByDescending(f => f.Score).ToList();
        var rejected = finals.Where(f => f != null && double.IsNegativeInfinity(f.Score)).ToList();
        if (verbose)
        {
            Console.Error.WriteLine($"[search] verify done ({sw.ElapsedMilliseconds}ms): {ranked.Count} ok, {rejected.Count} rejected");
        }

        // Phase C: score the leading plans by playing the rest of the battle out with the greedy
        // policy. This turns the myopic one-turn objective into "what does this line lead to".
        var rolloutResults = new List<RolloutResult>();
        if (ranked.Count > 0)
        {
            int top = Math.Min(ranked.Count, Math.Max(verify / 2, 4));
            var rolloutPlans = ranked.Take(top).Select(r => r.Plan).ToList();
            var rolloutArr = new RolloutResult[rolloutPlans.Count];
            Parallel.For(0, rolloutPlans.Count, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i =>
            {
                JsonElement r = RunWorker(dotnet, assemblyPath, recording, turn, prefix, layout, forceUnits, rolloutPlans[i].Sequence, false, rollout: true);
                rolloutArr[i] = EvaluateRollout(rolloutPlans[i], r, state);
            });
            rolloutResults = rolloutArr.Where(r => r != null && r.Ok).OrderByDescending(r => r.Score).ToList();
            if (verbose)
            {
                Console.Error.WriteLine($"[search] rollout scores: {rolloutResults.Count} ok ({sw.ElapsedMilliseconds}ms)");
                foreach (RolloutResult r in rolloutResults.Take(5))
                {
                    Console.Error.WriteLine($"  rollout {r.Score,10:F0} won={r.Won} hp={r.PlayerHp} turns={r.Turns}  {r.Plan.Sequence}");
                }
            }
        }

        var solution = new TurnSolution
        {
            Candidates = plans.Count,
            Seconds = sw.Elapsed.TotalSeconds,
            Report = new List<string>(),
            Ended = false,
            PlayerHp = state.PlayerHp,
            EnemyHp = state.EnemyHp,
        };
        if (ranked.Count == 0 && rolloutResults.Count == 0)
        {
            solution.Error = "no legal plan verified";
            return solution;
        }
        PlanResult best = ranked.Count > 0 ? ranked[0] : null;
        if (rolloutResults.Count > 0)
        {
            RolloutResult rb = rolloutResults[0];
            solution.Sequence = rb.Plan.Sequence;
            solution.Resolved = true;
            solution.HasRollout = true;
            solution.RolloutWon = rb.Won;
            solution.RolloutPlayerHp = rb.PlayerHp;
            solution.RolloutTurns = rb.Turns;
            // The turn-level verdict comes from the exact one-turn re-simulation, not the rollout:
            // the rollout also plays future turns, so its "ended" says nothing about this turn.
            PlanResult exact = ranked.FirstOrDefault(r => r.Plan.Sequence == rb.Plan.Sequence);
            if (exact != null)
            {
                solution.Ended = exact.BattleEnded;
                solution.Won = exact.Won;
                solution.PlayerHpLost = exact.PlayerHpLost;
                solution.EnemyHpLost = exact.EnemyHpLost;
            }
            solution.PlayerHp = rb.PlayerHp;
            solution.EnemyHp = rb.EnemyHp;
        }
        else
        {
            solution.Sequence = best.Plan.Sequence;
            solution.Resolved = best.Resolved;
            solution.Ended = best.BattleEnded;
            solution.Won = best.Won;
            solution.PlayerHpLost = best.PlayerHpLost;
            solution.EnemyHpLost = best.EnemyHpLost;
        }

        solution.Report.Add($"enemy: hp={state.EnemyHp}+{state.EnemyShield}sh  player: hp={state.PlayerHp}+{state.PlayerShield}sh  hand={string.Join(" ", state.Hand.Select(h => $"[{h.Power}/{string.Join(",", h.Colors)}]"))}");
        if (prefix == null)
        {
            solution.Report.Add($"recorded human line: {string.Join(" ", state.Recorded)}");
        }
        solution.Report.Add("");
        foreach (RolloutResult r in rolloutResults.Take(10))
        {
            solution.Report.Add($"  rollout {r.Score,10:F0} won={r.Won} hp={r.PlayerHp} turns={r.Turns}  {r.Plan.Sequence}");
        }
        if (rolloutResults.Count == 0)
        {
            foreach (PlanResult r in ranked.Take(10))
            {
                string tag = r.Resolved
                    ? $"win={r.Won} enemyHpLost={r.EnemyHpLost} playerHpLost={r.PlayerHpLost}  "
                    : "";
                solution.Report.Add($"  {r.Score,12:F0}  {tag}{r.Plan.Sequence}");
            }
        }
        solution.Report.Add("");
        solution.Report.Add($"RECOMMENDED: {solution.Sequence}");
        if (solution.HasRollout)
        {
            solution.Report.Add($"  projected: {(solution.RolloutWon ? "VICTORY" : "no guaranteed win")} with greedy follow-up, {solution.RolloutTurns} turns, playerHp={solution.RolloutPlayerHp}");
        }
        if (solution.Resolved)
        {
            solution.Report.Add($"  this turn: {(solution.Won ? "VICTORY" : solution.Ended ? "DEFEAT" : "battle continues")}  enemyHpLost={solution.EnemyHpLost} playerHpLost={solution.PlayerHpLost}");
        }
        return solution;
    }

    /// <summary>Plays a whole battle with the turn search as the policy (used for validation).</summary>
    public static int PlayBattle(string recording, string layout, string forceUnits, string root, int jobs, int maxPlans, int verify, string assemblyPath)
    {
        string prefix = "";
        int turns = 0;
        var total = Stopwatch.StartNew();
        var turnLog = new List<string>();
        bool ended = false;
        bool won = false;
        while (turns < 30)
        {
            TurnSolution sol = SolveTurn(recording, -1, prefix, layout, forceUnits, jobs, maxPlans, verify, assemblyPath, verbose: false);
            if (sol == null)
            {
                Console.Error.WriteLine("[play] solver failed");
                return 1;
            }
            Console.Error.WriteLine($"[play] iter turns={turns} ended={sol.Ended} won={sol.Won} seqLen={(sol.Sequence ?? "<null>").Length} cand={sol.Candidates} err={sol.Error}");
            if (string.IsNullOrEmpty(sol.Sequence))
            {
                ended = sol.Ended;
                won = sol.Won;
                break;
            }
            prefix = string.IsNullOrEmpty(prefix) ? sol.Sequence + ";end" : prefix + ";" + sol.Sequence + ";end";
            turns++;
            turnLog.Add($"turn {turns}: {sol.Sequence}  -> enemyHpLost={sol.EnemyHpLost} playerHpLost={sol.PlayerHpLost}{(sol.Ended ? (sol.Won ? " (kills)" : " (lethal self)") : "")}");
            Console.Error.WriteLine($"[play] {turnLog[^1]}");
            if (sol.Ended)
            {
                ended = true;
                won = sol.Won;
                break;
            }
        }
        Console.Error.WriteLine($"[play] final prefix={prefix}"); 
        JsonElement fin = RunWorker(DotnetPath(), assemblyPath, recording, -1, prefix, layout, forceUnits, null, false);
        Console.Error.WriteLine($"[play] fin kind={fin.ValueKind} ended={(fin.ValueKind == JsonValueKind.Object ? fin.GetProperty("ended").GetBoolean() : false)}");
        int playerHp = 0, enemyHp = 0;
        if (fin.ValueKind == JsonValueKind.Object)
        {
            playerHp = fin.GetProperty("player").GetProperty("Hp").GetInt32();
            enemyHp = fin.GetProperty("enemy").GetProperty("Hp").GetInt32();
            ended = ended || fin.GetProperty("ended").GetBoolean();
            if (enemyHp <= 0 && playerHp > 0)
            {
                won = true;
            }
        }
        Console.WriteLine($"=== LONESTAR solver full battle ===");
        Console.WriteLine($"layout: {layout ?? "(recorded)"}");
        Console.WriteLine($"turns played: {turns}  ended={ended} won={won}  playerHp={playerHp} enemyHp={enemyHp}  time={total.Elapsed.TotalSeconds:F1}s");
        foreach (string line in turnLog)
        {
            Console.WriteLine("  " + line);
        }
        string outDir = Path.Combine(root, "out", "solve");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, $"play_{DateTime.Now:yyyyMMdd_HHmmss}.json"), JsonSerializer.Serialize(new
        {
            recording,
            layout,
            turns,
            ended,
            won,
            playerHp,
            enemyHp,
            prefix,
            turnLog,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    // ---- dynamic (stateful) search --------------------------------------------------------------

    /// <summary>
    /// Stateful beam search over real engine states: unlike the static plan enumeration, every step
    /// re-reads the hand, so cards created mid-turn (by skills) become usable actions. One process is
    /// spent per edge because the engine cannot be reset in-process.
    /// </summary>
    public static TurnSolution SolveTurnDynamic(string recording, int turn, string prefix, string layout, string forceUnits,
        int jobs, int beamWidth, int depthLimit, string assemblyPath, bool verbose)
    {
        string dotnet = DotnetPath();
        JsonElement query = RunWorker(dotnet, assemblyPath, recording, turn, prefix, layout, forceUnits, null, false, probe: true);
        if (query.ValueKind != JsonValueKind.Object || !query.GetProperty("ok").GetBoolean())
        {
            return null;
        }
        var start = StateView.From(query);
        if (start.Ended)
        {
            return new TurnSolution { Sequence = "", Ended = true, PlayerHp = start.PlayerHp, EnemyHp = start.EnemyHp, Candidates = 0 };
        }
        if (verbose)
        {
            Console.Error.WriteLine($"[dynamic] turn={turn} hand={start.Hand.Count} beam={beamWidth} depth={depthLimit}");
        }
        var beam = new List<DynNode> { new DynNode { Seq = "", State = start, Score = PreviewScore(start) } };
        var ends = new List<PlanResult>();
        int expansions = 0;
        for (int depth = 0; depth < depthLimit; depth++)
        {
            var edges = new List<(int Node, string Token, bool End)>();
            for (int n = 0; n < beam.Count; n++)
            {
                foreach (string token in LoadTokens(beam[n].State))
                {
                    edges.Add((n, token, false));
                }
                edges.Add((n, "", true));
            }
            if (edges.Count == 0)
            {
                break;
            }
            var childStates = new DynNode[edges.Count];
            var endResults = new PlanResult[edges.Count];
            Parallel.For(0, edges.Count, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i =>
            {
                (int nodeIndex, string token, bool isEnd) = edges[i];
                string seq = Join(beam[nodeIndex].Seq, token);
                if (isEnd)
                {
                    JsonElement r = RunWorker(dotnet, assemblyPath, recording, turn, prefix, layout, forceUnits, seq, true);
                    endResults[i] = EvaluateResolved(new Plan { Sequence = seq }, r, start);
                }
                else
                {
                    JsonElement r = RunWorker(dotnet, assemblyPath, recording, turn, prefix, layout, forceUnits, seq, false);
                    if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("ok", out JsonElement ok) && ok.GetBoolean())
                    {
                        var child = StateView.From(r);
                        childStates[i] = new DynNode { Seq = seq, State = child, Score = PreviewScore(child) };
                    }
                }
            });
            expansions += edges.Count;
            foreach (PlanResult r in endResults)
            {
                if (r != null && !double.IsNegativeInfinity(r.Score))
                {
                    ends.Add(r);
                }
            }
            var children = childStates
                .Where(c => c != null)
                .GroupBy(c => Signature(c.State))
                .Select(g => g.OrderByDescending(c => c.Score).First())
                .OrderByDescending(c => c.Score)
                .Take(beamWidth)
                .ToList();
            if (verbose)
            {
                Console.Error.WriteLine($"[dynamic] depth {depth + 1}: edges={edges.Count} kept={children.Count} ends={ends.Count}" + (children.Count > 0 ? $" bestPreview={children[0].Score:F0}" : ""));
            }
            if (children.Count == 0)
            {
                break;
            }
            beam = children;
        }
        var validEnds = ends.Where(e => !double.IsNegativeInfinity(e.Score)).OrderByDescending(e => e.Score).ToList();
        if (validEnds.Count == 0)
        {
            return null;
        }
        // Long-term refinement: the per-turn score is myopic (it happily trades player HP for speed).
        // Play the leading turn outcomes to the end with the greedy policy and rank by final HP/turns.
        var rollouts = new RolloutResult[Math.Min(validEnds.Count, 12)];
        var leaderEnds = validEnds.Take(rollouts.Length).ToList();
        Parallel.For(0, leaderEnds.Count, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i =>
        {
            JsonElement r = RunWorker(dotnet, assemblyPath, recording, turn, prefix, layout, forceUnits, leaderEnds[i].Plan.Sequence, false, rollout: true);
            rollouts[i] = EvaluateRollout(leaderEnds[i].Plan, r, start);
        });
        var validRollouts = rollouts.Where(r => r != null && r.Ok).OrderByDescending(r => r.Score).ToList();
        PlanResult best;
        if (validRollouts.Count > 0)
        {
            RolloutResult rb = validRollouts[0];
            best = leaderEnds.FirstOrDefault(e => e.Plan.Sequence == rb.Plan.Sequence) ?? validEnds[0];
            return new TurnSolution
            {
                Sequence = rb.Plan.Sequence,
                Resolved = true,
                Ended = best.BattleEnded,
                Won = best.Won,
                PlayerHpLost = best.PlayerHpLost,
                EnemyHpLost = best.EnemyHpLost,
                PlayerHp = best.FinalPlayerHp,
                EnemyHp = best.FinalEnemyHp,
                Candidates = expansions + leaderEnds.Count,
                HasRollout = true,
                RolloutWon = rb.Won,
                RolloutPlayerHp = rb.PlayerHp,
                RolloutTurns = rb.Turns,
                Report = new List<string>
                {
                    $"dynamic beam: {expansions} expansions, beam {beamWidth}, depth {depthLimit}, {leaderEnds.Count} rollouts",
                    $"RECOMMENDED: {rb.Plan.Sequence}",
                    $"  this turn: {(best.Won ? "VICTORY" : best.BattleEnded ? "DEFEAT" : "battle continues")}  enemyHpLost={best.EnemyHpLost} playerHpLost={best.PlayerHpLost}",
                    $"  projected: {(rb.Won ? "VICTORY" : "no guaranteed win")} with greedy follow-up, {rb.Turns} turns, playerHp={rb.PlayerHp}",
                },
            };
        }
        best = validEnds[0];
        return new TurnSolution
        {
            Sequence = best.Plan.Sequence,
            Resolved = true,
            Ended = best.BattleEnded,
            Won = best.Won,
            PlayerHpLost = best.PlayerHpLost,
            EnemyHpLost = best.EnemyHpLost,
            PlayerHp = best.FinalPlayerHp,
            EnemyHp = best.FinalEnemyHp,
            Candidates = expansions,
            Report = new List<string>
            {
                $"dynamic beam: {expansions} expansions, beam {beamWidth}, depth {depthLimit}",
                $"RECOMMENDED: {best.Plan.Sequence}",
                $"  this turn: {(best.Won ? "VICTORY" : best.BattleEnded ? "DEFEAT" : "battle continues")}  enemyHpLost={best.EnemyHpLost} playerHpLost={best.PlayerHpLost}",
            },
        };
    }

    public static int RunDynamic(string recording, int turn, string prefix, string layout, string forceUnits, string root,
        int jobs, int beamWidth, int depthLimit, string assemblyPath)
    {
        var sw = Stopwatch.StartNew();
        TurnSolution sol = SolveTurnDynamic(recording, turn, prefix, layout, forceUnits, jobs, beamWidth, depthLimit, assemblyPath, verbose: true);
        if (sol == null)
        {
            Console.Error.WriteLine("[dynamic] no verified turn found");
            return 1;
        }
        Console.WriteLine($"=== LONESTAR dynamic solver, turn {turn} ===");
        Console.WriteLine($"exact turn resolutions={sol.Candidates} time={sw.Elapsed.TotalSeconds:F1}s");
        foreach (string line in sol.Report ?? new List<string>())
        {
            Console.WriteLine(line);
        }
        return 0;
    }

    public static int PlayBattleDynamic(string recording, string layout, string forceUnits, string root,
        int jobs, int beamWidth, int depthLimit, string assemblyPath)
    {
        string prefix = "";
        int turns = 0;
        var total = Stopwatch.StartNew();
        var turnLog = new List<string>();
        bool ended = false;
        bool won = false;
        while (turns < 30)
        {
            TurnSolution sol = SolveTurnDynamic(recording, -1, prefix, layout, forceUnits, jobs, beamWidth, depthLimit, assemblyPath, verbose: false);
            if (sol == null)
            {
                Console.Error.WriteLine("[dynamic] solver failed");
                return 1;
            }
            if (string.IsNullOrEmpty(sol.Sequence))
            {
                ended = sol.Ended;
                won = sol.Won;
                break;
            }
            prefix = Join(prefix, sol.Sequence + ";end");
            turns++;
            turnLog.Add($"turn {turns}: {sol.Sequence}  -> enemyHpLost={sol.EnemyHpLost} playerHpLost={sol.PlayerHpLost}{(sol.Ended ? (sol.Won ? " (kills)" : " (lethal self)") : "")}");
            Console.Error.WriteLine($"[play-dyn] {turnLog[^1]}");
            if (sol.Ended)
            {
                ended = true;
                won = sol.Won;
                break;
            }
        }
        JsonElement fin = RunWorker(DotnetPath(), assemblyPath, recording, -1, prefix, layout, forceUnits, null, false);
        int playerHp = 0, enemyHp = 0;
        if (fin.ValueKind == JsonValueKind.Object)
        {
            playerHp = fin.GetProperty("player").GetProperty("Hp").GetInt32();
            enemyHp = fin.GetProperty("enemy").GetProperty("Hp").GetInt32();
            ended = ended || fin.GetProperty("ended").GetBoolean();
            if (enemyHp <= 0 && playerHp > 0)
            {
                won = true;
            }
        }
        Console.WriteLine($"=== LONESTAR dynamic full battle ===");
        Console.WriteLine($"layout: {layout ?? "(recorded)"}");
        Console.WriteLine($"turns played: {turns}  ended={ended} won={won}  playerHp={playerHp} enemyHp={enemyHp}  time={total.Elapsed.TotalSeconds:F1}s");
        foreach (string line in turnLog)
        {
            Console.WriteLine("  " + line);
        }
        string outDir = Path.Combine(root, "out", "solve");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, $"play_dynamic_{DateTime.Now:yyyyMMdd_HHmmss}.json"), JsonSerializer.Serialize(new
        {
            recording,
            layout,
            forceUnits,
            turns,
            ended,
            won,
            playerHp,
            enemyHp,
            prefix,
            turnLog,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static string Join(string prefix, string token) =>
        string.IsNullOrEmpty(token) ? prefix : (string.IsNullOrEmpty(prefix) ? token : prefix + ";" + token);

    private static List<string> LoadTokens(StateView state)
    {
        var tokens = new List<string>();
        foreach (HandCard card in state.Hand)
        {
            foreach (UnitView unit in state.Units)
            {
                bool fits = unit.Slots.Any(slot => slot.Empty && !slot.Disturbed && card.Highest >= slot.Rare);
                if (!fits)
                {
                    continue;
                }
                tokens.Add($"loadp:{card.Power}/{string.Join(",", card.Colors)}@{unit.X},{unit.Y}");
            }
        }
        return tokens.Distinct().ToList();
    }

    private static double PreviewScore(StateView state)
    {
        int dealt = 0, taken = 0;
        foreach (var line in state.Lines)
        {
            if (line.Result > 0) dealt += line.Result; else taken += -line.Result;
        }
        int usableDealt = Math.Min(dealt, state.EnemyHp + state.EnemyShield);
        int usableTaken = Math.Min(taken, state.PlayerHp + state.PlayerShield);
        return 1000.0 * usableDealt - 1200.0 * usableTaken;
    }

    private static string Signature(StateView state)
    {
        var sb = new System.Text.StringBuilder();
        foreach (HandCard h in state.Hand.OrderBy(h => h.Power).ThenBy(h => string.Join(",", h.Colors)))
        {
            sb.Append(h.Power).Append('/').Append(string.Join(",", h.Colors)).Append(';');
        }
        sb.Append('|').Append(state.PlayerHp).Append('/').Append(state.PlayerShield);
        sb.Append('|').Append(state.EnemyHp).Append('/').Append(state.EnemyShield);
        sb.Append('|');
        foreach (UnitView u in state.Units.OrderBy(u => u.X).ThenBy(u => u.Y))
        {
            sb.Append(u.X).Append(',').Append(u.Y).Append(':');
            foreach (SlotInfo s in u.Slots)
            {
                sb.Append(s.Rare).Append(s.Empty ? "_" : "x").Append(',');
            }
            sb.Append(';');
        }
        return sb.ToString();
    }

    private sealed class DynNode
    {
        public string Seq;
        public StateView State;
        public double Score;
    }

    // ---- optimality audit -----------------------------------------------------------------------

    /// <summary>
    /// Optimality audit for one decision point: evaluate every enumerated plan (all placements, move
    /// variants and skill variants) plus all card-order permutations of the leaders through a real
    /// turn resolution, then diff the best result against what the normal search chooses.
    /// </summary>
    public static int RunAudit(string recording, int turn, string prefix, string layout, string forceUnits,
        string root, int jobs, int maxPlans, int verify, bool compare, bool dynamicCompare, int beam, int depth, string assemblyPath)
    {
        string dotnet = DotnetPath();
        var sw = Stopwatch.StartNew();
        JsonElement query = RunWorker(dotnet, assemblyPath, recording, turn, prefix, layout, forceUnits, null, false, probe: true);
        if (query.ValueKind != JsonValueKind.Object || !query.GetProperty("ok").GetBoolean())
        {
            Console.Error.WriteLine("[audit] state query failed");
            return 1;
        }
        var state = StateView.From(query);
        if (state.Ended)
        {
            Console.WriteLine($"[audit] turn {turn}: battle already over");
            return 0;
        }
        Console.Error.WriteLine($"[audit] turn={turn} hand={state.Hand.Count} skills={state.Skills.Count} moves=[{string.Join(",", state.Moves)}]");

        List<Plan> assignments = GeneratePlans(state, maxPlans);
        var pool = new List<Plan>();
        foreach (Plan p in assignments)
        {
            pool.Add(p);
            foreach (int m in state.Moves)
            {
                pool.Add(p.WithMove(m));
            }
        }
        int skillBudget = 24;
        if (state.Skills.Count > 0)
        {
            foreach (Plan p in assignments.Take(10))
            {
                foreach (SkillView skill in state.Skills)
                {
                    if (skillBudget-- <= 0)
                    {
                        break;
                    }
                    string click = $"click:{skill.X},{skill.Y}";
                    if (skill.TwoStep)
                    {
                        if (skill.Targets == null)
                        {
                            continue;
                        }
                        foreach (TargetView target in skill.Targets.Take(4))
                        {
                            pool.Add(p.WithPrefix($"{click};target:{target.Index}"));
                        }
                    }
                    else
                    {
                        pool.Add(p.WithPrefix(click));
                    }
                }
            }
        }
        pool = pool.GroupBy(p => p.Sequence).Select(g => g.First()).ToList();
        Console.Error.WriteLine($"[audit] base pool: {pool.Count} sequences ({assignments.Count} assignments)");

        var results = EvaluatePlans(dotnet, assemblyPath, recording, turn, prefix, layout, forceUnits, state, pool, jobs, "pool");
        var ranked = results.OrderByDescending(r => r.Score).ToList();
        int poolSize = pool.Count;

        var leaders = ranked.Take(Math.Max(verify, 4)).Select(r => r.Plan).ToList();
        var orderPlans = new List<Plan>();
        foreach (Plan p in leaders)
        {
            orderPlans.AddRange(Permutations(p));
        }
        var known = new HashSet<string>(pool.Select(p => p.Sequence));
        orderPlans = orderPlans.Where(p => known.Add(p.Sequence)).ToList();
        Console.Error.WriteLine($"[audit] order permutations: {orderPlans.Count}");
        results.AddRange(EvaluatePlans(dotnet, assemblyPath, recording, turn, prefix, layout, forceUnits, state, orderPlans, jobs, "order"));
        ranked = results.OrderByDescending(r => r.Score).ToList();
        int total = poolSize + orderPlans.Count;

        TurnSolution normal = compare
            ? SolveTurn(recording, turn, prefix, layout, forceUnits, jobs, maxPlans, verify, assemblyPath, verbose: false)
            : null;
        PlanResult normalEval = null;
        if (normal != null && !string.IsNullOrEmpty(normal.Sequence))
        {
            JsonElement r = RunWorker(dotnet, assemblyPath, recording, turn, prefix, layout, forceUnits, normal.Sequence, true);
            normalEval = EvaluateResolved(new Plan { Sequence = normal.Sequence }, r, state);
        }

        Console.WriteLine($"=== LONESTAR optimality audit, turn {state.Round} (turn {turn}) ===");
        Console.WriteLine($"hand: {string.Join(" ", state.Hand.Select(h => $"[{h.Power}/{string.Join(",", h.Colors)}]"))}  moves=[{string.Join(",", state.Moves)}]  skills={state.Skills.Count}");
        Console.WriteLine($"exact turn resolutions: {total} ({assignments.Count} assignments, {poolSize} move/skill variants, {orderPlans.Count} order permutations) in {sw.Elapsed.TotalSeconds:F0}s");
        Console.WriteLine();
        Console.WriteLine("top exact turn results:");
        foreach (PlanResult r in ranked.Take(10))
        {
            Console.WriteLine($"  {r.Score,12:F0}  won={r.Won,-5} enemyHpLost={r.EnemyHpLost,-2} playerHpLost={r.PlayerHpLost,-2}  {r.Plan.Sequence}");
        }
        if (normalEval != null)
        {
            Console.WriteLine();
            Console.WriteLine($"solver's normal choice: {normal.Sequence}");
            Console.WriteLine($"  exact: score={normalEval.Score:F0} won={normalEval.Won} enemyHpLost={normalEval.EnemyHpLost} playerHpLost={normalEval.PlayerHpLost}");
            double gap = ranked.Count > 0 ? ranked[0].Score - normalEval.Score : 0;
            Console.WriteLine($"  gap vs exhaustive best: {gap:F0}");
            Console.WriteLine(gap > 1 ? "  => NOT turn-optimal on this decision point" : "  => matches the best exact turn result found");
        }
        if (dynamicCompare)
        {
            TurnSolution dyn = SolveTurnDynamic(recording, turn, prefix, layout, forceUnits, jobs, beam, depth, assemblyPath, verbose: false);
            if (dyn != null && !string.IsNullOrEmpty(dyn.Sequence))
            {
                JsonElement r = RunWorker(dotnet, assemblyPath, recording, turn, prefix, layout, forceUnits, dyn.Sequence, true);
                PlanResult dynEval = EvaluateResolved(new Plan { Sequence = dyn.Sequence }, r, state);
                Console.WriteLine();
                Console.WriteLine($"dynamic (created cards included): {dyn.Sequence}");
                Console.WriteLine($"  exact: score={dynEval.Score:F0} won={dynEval.Won} enemyHpLost={dynEval.EnemyHpLost} playerHpLost={dynEval.PlayerHpLost}");
                double dynGap = ranked.Count > 0 ? dynEval.Score - ranked[0].Score : 0;
                Console.WriteLine($"  gain over static exhaustive best: {dynGap:F0}");
            }
        }

        try
        {
            string outDir = Path.Combine(root, "out", "solve");
            Directory.CreateDirectory(outDir);
            string outFile = Path.Combine(outDir, $"audit_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            File.WriteAllText(outFile, JsonSerializer.Serialize(new
            {
                recording,
                turn,
                prefix,
                layout,
                forceUnits,
                hand = state.Hand.Select(h => new { h.Power, h.Colors }),
                moves = state.Moves,
                assignments = assignments.Count,
                evaluated = total,
                solver = normalEval == null ? null : new
                {
                    sequence = normal.Sequence,
                    score = normalEval.Score,
                    won = normalEval.Won,
                    enemyHpLost = normalEval.EnemyHpLost,
                    playerHpLost = normalEval.PlayerHpLost,
                },
                best = ranked.Take(20).Select(r => new
                {
                    sequence = r.Plan.Sequence,
                    score = r.Score,
                    won = r.Won,
                    enemyHpLost = r.EnemyHpLost,
                    playerHpLost = r.PlayerHpLost,
                }),
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.Error.WriteLine($"[audit] wrote {outFile}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[audit] report write failed: " + ex.Message);
        }
        return 0;
    }

    private static List<PlanResult> EvaluatePlans(string dotnet, string assemblyPath, string recording, int turn,
        string prefix, string layout, string forceUnits, StateView state, List<Plan> plans, int jobs, string label)
    {
        var results = new PlanResult[plans.Count];
        int done = 0;
        Parallel.For(0, plans.Count, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i =>
        {
            JsonElement r = RunWorker(dotnet, assemblyPath, recording, turn, prefix, layout, forceUnits, plans[i].Sequence, true);
            results[i] = EvaluateResolved(plans[i], r, state);
            int n = Interlocked.Increment(ref done);
            if (n % 32 == 0)
            {
                Console.Error.Write($"\r[audit] {label} {n}/{plans.Count}   ");
            }
        });
        Console.Error.WriteLine();
        return results.Where(r => r != null).ToList();
    }

    /// <summary>All card-order permutations of a plan (the placement targets are kept).</summary>
    private static List<Plan> Permutations(Plan plan)
    {
        var tokens = plan.Tokens.ToList();
        if (tokens.Count <= 1)
        {
            return new List<Plan> { plan };
        }
        var result = new List<Plan>();
        void Permute(int k)
        {
            if (result.Count >= 24)
            {
                return;
            }
            if (k == tokens.Count)
            {
                result.Add(new Plan
                {
                    Prefix = plan.Prefix,
                    Sequence = plan.Prefix + string.Join(";", tokens.Select(t => t.Token)),
                    Tokens = tokens.ToList(),
                });
                return;
            }
            for (int i = k; i < tokens.Count; i++)
            {
                (tokens[k], tokens[i]) = (tokens[i], tokens[k]);
                Permute(k + 1);
                (tokens[k], tokens[i]) = (tokens[i], tokens[k]);
            }
        }
        Permute(0);
        return result;
    }

    // ---- plan generation ------------------------------------------------------------------------

    private static List<Plan> GeneratePlans(StateView state, int maxPlans)
    {
        var units = state.Units
            .Select(u => new UnitInfo
            {
                UnitIndex = u.UnitIndex,
                Id = u.Id,
                X = u.X,
                Y = u.Y,
                Rares = u.Slots.Where(s => s.Empty && !s.Disturbed).Select(s => s.Rare).ToList(),
            })
            .ToList();
        var plans = new List<Plan>();
        int[] assigned = new int[state.Hand.Count];
        var used = units.Select(u => new bool[u.Rares.Count]).ToArray();

        void Dfs(int card)
        {
            if (plans.Count >= maxPlans * 8)
            {
                return;
            }
            if (card == state.Hand.Count)
            {
                plans.Add(Plan.FromAssignment(state, units, assigned));
                return;
            }
            int highest = state.Hand[card].Colors.Length == 0 ? 0 : state.Hand[card].Colors.Max();
            bool any = false;
            for (int ui = 0; ui < units.Count; ui++)
            {
                int slot = FirstFittingSlot(units[ui].Rares, used[ui], highest);
                if (slot < 0)
                {
                    continue;
                }
                any = true;
                used[ui][slot] = true;
                assigned[card] = ui;
                Dfs(card + 1);
                assigned[card] = -1;
                used[ui][slot] = false;
            }
            if (!any)
            {
                assigned[card] = -1;
                Dfs(card + 1);
            }
        }
        Dfs(0);
        if (plans.Count > maxPlans)
        {
            int stride = (int)Math.Ceiling(plans.Count / (double)maxPlans);
            plans = plans.Where((p, i) => i % stride == 0).Take(maxPlans).ToList();
        }
        return plans;
    }

    private static int FirstFittingSlot(List<int> rares, bool[] used, int highest)
    {
        for (int i = 0; i < rares.Count; i++)
        {
            if (!used[i] && rares[i] <= highest)
            {
                return i;
            }
        }
        return -1;
    }

    // ---- worker ---------------------------------------------------------------------------------

    private static JsonElement RunWorker(string dotnet, string assemblyPath, string recording, int turn, string prefix, string layout, string forceUnits, string actions, bool endTurn, bool rollout = false, bool probe = false, int timeoutMs = 240000)
    {
        var psi = new ProcessStartInfo(dotnet)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(assemblyPath);
        psi.ArgumentList.Add(recording);
        if (prefix != null)
        {
            psi.ArgumentList.Add("--prefix");
            psi.ArgumentList.Add(prefix);
        }
        else
        {
            psi.ArgumentList.Add("--turn");
            psi.ArgumentList.Add(turn.ToString());
        }
        if (layout != null)
        {
            psi.ArgumentList.Add("--layout");
            psi.ArgumentList.Add(layout);
        }
        if (forceUnits != null)
        {
            psi.ArgumentList.Add("--force-units");
            psi.ArgumentList.Add(forceUnits);
        }
        if (actions != null)
        {
            psi.ArgumentList.Add("--actions");
            psi.ArgumentList.Add(actions);
        }
        if (endTurn)
        {
            psi.ArgumentList.Add("--end-turn");
        }
        if (rollout)
        {
            psi.ArgumentList.Add("--rollout");
        }
        if (probe)
        {
            psi.ArgumentList.Add("--probe-skills");
        }
        psi.ArgumentList.Add("--json");
        psi.ArgumentList.Add("--quiet");
        using var p = Process.Start(psi);
        Task<string> outTask = p.StandardOutput.ReadToEndAsync();
        Task<string> errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return default;
        }
        string stdout = outTask.Result;
        _ = errTask.Result;
        foreach (string line in stdout.Split('\n').Reverse())
        {
            string t = line.Trim();
            if (t.StartsWith("{"))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(t);
                    return doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                }
            }
        }
        return default;
    }

    private static string DotnetPath()
    {
        string p = Environment.ProcessPath;
        if (p != null && Path.GetFileName(p).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return p;
        }
        return "dotnet";
    }

    // ---- scoring --------------------------------------------------------------------------------

    private static PlanResult Evaluate(Plan plan, JsonElement result)
    {
        var pr = new PlanResult { Plan = plan };
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("ok", out JsonElement ok) || !ok.GetBoolean())
        {
            pr.Score = double.NegativeInfinity;
            pr.Error = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("error", out JsonElement e) ? e.GetString() : "worker failed";
            return pr;
        }
        int dealt = 0, taken = 0;
        foreach (JsonElement line in result.GetProperty("lines").EnumerateArray())
        {
            int r = line.GetProperty("Result").GetInt32();
            if (r > 0) dealt += r; else taken += -r;
        }
        int enemyHp = result.GetProperty("enemy").GetProperty("Hp").GetInt32();
        int enemyShield = result.GetProperty("enemy").GetProperty("Shield").GetInt32();
        int playerHp = result.GetProperty("player").GetProperty("Hp").GetInt32();
        int playerShield = result.GetProperty("player").GetProperty("Shield").GetInt32();
        int usableDealt = Math.Min(dealt, enemyHp + enemyShield);
        int usableTaken = Math.Min(taken, playerHp + playerShield);
        pr.Score = 1000.0 * usableDealt - 1200.0 * usableTaken;
        if (dealt >= enemyHp + enemyShield)
        {
            pr.Score += 500000;
        }
        if (taken >= playerHp + playerShield)
        {
            pr.Score -= 500000;
        }
        return pr;
    }

    private static PlanResult EvaluateResolved(Plan plan, JsonElement result, StateView state)
    {
        var pr = new PlanResult { Plan = plan };
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("ok", out JsonElement ok) || !ok.GetBoolean())
        {
            pr.Score = double.NegativeInfinity;
            pr.Error = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("error", out JsonElement e) ? e.GetString() : "worker failed";
            return pr;
        }
        if (!result.TryGetProperty("outcome", out JsonElement outcome) || outcome.ValueKind != JsonValueKind.Object)
        {
            bool ended = result.GetProperty("ended").GetBoolean();
            int eh = result.GetProperty("enemy").GetProperty("Hp").GetInt32();
            int ph = result.GetProperty("player").GetProperty("Hp").GetInt32();
            pr.Resolved = ended;
            pr.BattleEnded = ended;
            pr.EnemyHpLost = Math.Max(0, state.EnemyHp - eh);
            pr.PlayerHpLost = Math.Max(0, state.PlayerHp - ph);
            pr.FinalPlayerHp = ph;
            pr.FinalEnemyHp = eh;
            pr.Score = 1000.0 * pr.EnemyHpLost - 1200.0 * pr.PlayerHpLost;
            return pr;
        }
        pr.Resolved = true;
        pr.Won = outcome.GetProperty("PlayerWon").GetBoolean();
        pr.BattleEnded = outcome.GetProperty("BattleEnded").GetBoolean();
        pr.PlayerHpLost = outcome.GetProperty("PlayerHpLost").GetInt32();
        pr.EnemyHpLost = outcome.GetProperty("EnemyHpLost").GetInt32();
        int enemyHpLeft = outcome.GetProperty("EnemyHp").GetInt32();
        int playerHpLeft = outcome.GetProperty("PlayerHp").GetInt32();
        pr.FinalPlayerHp = playerHpLeft;
        pr.FinalEnemyHp = enemyHpLeft;
        if (pr.BattleEnded)
        {
            pr.Score = pr.Won ? 1e9 + pr.EnemyHpLost * 1000 - pr.PlayerHpLost * 1200 : -1e9;
        }
        else
        {
            pr.Score = pr.EnemyHpLost * 1000 - pr.PlayerHpLost * 1200 - enemyHpLeft * 2 + playerHpLeft;
        }
        return pr;
    }

    private static RolloutResult EvaluateRollout(Plan plan, JsonElement result, StateView state)
    {
        var rr = new RolloutResult { Plan = plan };
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("ok", out JsonElement ok) || !ok.GetBoolean()
            || !result.TryGetProperty("rollout", out JsonElement ro) || ro.ValueKind != JsonValueKind.Object)
        {
            rr.Score = double.NegativeInfinity;
            return rr;
        }
        rr.Ok = true;
        rr.Won = ro.GetProperty("Won").GetBoolean();
        rr.Ended = ro.GetProperty("Ended").GetBoolean();
        rr.PlayerHp = ro.GetProperty("PlayerHp").GetInt32();
        rr.EnemyHp = ro.GetProperty("EnemyHp").GetInt32();
        rr.PlayerHpLost = ro.GetProperty("PlayerHpLost").GetInt32();
        rr.Turns = ro.GetProperty("Turns").GetInt32();
        rr.EnemyHpLost = Math.Max(0, state.EnemyHp - rr.EnemyHp);
        if (rr.Won)
        {
            rr.Score = 1e12 + rr.PlayerHp * 1000.0 - rr.Turns * 10.0;
        }
        else
        {
            rr.Score = rr.EnemyHpLost * 1000.0 + rr.PlayerHp * 100.0 - rr.Turns;
        }
        return rr;
    }

    // ---- data -----------------------------------------------------------------------------------

    private sealed class RolloutResult
    {
        public Plan Plan;
        public bool Ok;
        public bool Won;
        public bool Ended;
        public int PlayerHp;
        public int EnemyHp;
        public int PlayerHpLost;
        public int EnemyHpLost;
        public int Turns;
        public double Score;
    }

    private sealed class PlanResult
    {
        public Plan Plan;
        public double Score;
        public string Error;
        public bool Resolved;
        public bool BattleEnded;
        public bool Won;
        public int EnemyHpLost;
        public int PlayerHpLost;
        public int FinalPlayerHp;
        public int FinalEnemyHp;
    }

    private sealed class UnitInfo
    {
        public int UnitIndex;
        public int Id;
        public int X;
        public int Y;
        public List<int> Rares = new List<int>();
    }

    private sealed class HandCard
    {
        public int Power;
        public int[] Colors = Array.Empty<int>();
        public int Highest => Colors.Length == 0 ? 0 : Colors.Max();
    }

    private sealed class LineView
    {
        public int Y;
        public int PlayerTotal;
        public int EnemyTotal;
        public int Result;
    }

    private sealed class SlotInfo
    {
        public int Rare;
        public bool Empty;
        public bool Disturbed;
    }

    private sealed class UnitView
    {
        public int UnitIndex;
        public int Id;
        public int X;
        public int Y;
        public List<SlotInfo> Slots = new List<SlotInfo>();
    }

    private sealed class TargetView
    {
        public int Index;
        public bool IsCell;
        public int X;
        public int Y;
        public int UnitId;
    }

    private sealed class SkillView
    {
        public int UnitIndex;
        public int X;
        public int Y;
        public int UnitId;
        public string Skill;
        public bool TwoStep;
        public List<TargetView> Targets;
    }

    private sealed class StateView
    {
        public int Round;
        public int PlayerHp;
        public int PlayerShield;
        public int EnemyHp;
        public int EnemyShield;
        public bool Ended;
        public List<HandCard> Hand = new List<HandCard>();
        public List<UnitView> Units = new List<UnitView>();
        public List<int> Moves = new List<int>();
        public List<SkillView> Skills = new List<SkillView>();
        public List<LineView> Lines = new List<LineView>();
        public List<string> Recorded = new List<string>();

        public static StateView From(JsonElement state)
        {
            var view = new StateView
            {
                Round = state.GetProperty("round").GetInt32(),
                PlayerHp = state.GetProperty("player").GetProperty("Hp").GetInt32(),
                PlayerShield = state.GetProperty("player").GetProperty("Shield").GetInt32(),
                EnemyHp = state.GetProperty("enemy").GetProperty("Hp").GetInt32(),
                EnemyShield = state.GetProperty("enemy").GetProperty("Shield").GetInt32(),
                Ended = state.TryGetProperty("ended", out JsonElement endEl) && endEl.ValueKind == JsonValueKind.True,
            };
            foreach (JsonElement h in state.GetProperty("hand").EnumerateArray())
            {
                view.Hand.Add(new HandCard
                {
                    Power = h[0].GetInt32(),
                    Colors = h[1].EnumerateArray().Select(c => c.GetInt32()).ToArray(),
                });
            }
            foreach (JsonElement u in state.GetProperty("units").EnumerateArray())
            {
                if (!u.GetProperty("Active").GetBoolean() || u.GetProperty("Destroyed").GetBoolean())
                {
                    continue;
                }
                var uv = new UnitView
                {
                    UnitIndex = u.GetProperty("UnitIndex").GetInt32(),
                    Id = u.GetProperty("Id").GetInt32(),
                    X = u.GetProperty("X").GetInt32(),
                    Y = u.GetProperty("Y").GetInt32(),
                };
                foreach (JsonElement s in u.GetProperty("slots").EnumerateArray())
                {
                    uv.Slots.Add(new SlotInfo
                    {
                        Rare = s.GetProperty("Rare").GetInt32(),
                        Empty = s.GetProperty("Empty").GetBoolean(),
                        Disturbed = s.GetProperty("Disturbed").GetBoolean(),
                    });
                }
                view.Units.Add(uv);
            }
            if (state.TryGetProperty("lines", out JsonElement linesEl) && linesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement l in linesEl.EnumerateArray())
                {
                    view.Lines.Add(new LineView
                    {
                        Y = l.GetProperty("Y").GetInt32(),
                        PlayerTotal = l.GetProperty("PlayerTotal").GetInt32(),
                        EnemyTotal = l.GetProperty("EnemyTotal").GetInt32(),
                        Result = l.GetProperty("Result").GetInt32(),
                    });
                }
            }
            if (state.TryGetProperty("moves", out JsonElement movesEl) && movesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement m in movesEl.EnumerateArray())
                {
                    view.Moves.Add(m.GetInt32());
                }
            }
            if (state.TryGetProperty("skills", out JsonElement skills) && skills.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement s in skills.EnumerateArray())
                {
                    var sv = new SkillView
                    {
                        UnitIndex = s.GetProperty("UnitIndex").GetInt32(),
                        X = s.GetProperty("X").GetInt32(),
                        Y = s.GetProperty("Y").GetInt32(),
                        UnitId = s.GetProperty("UnitId").GetInt32(),
                        Skill = s.GetProperty("Skill").GetString(),
                        TwoStep = s.GetProperty("TwoStep").GetBoolean(),
                    };
                    if (s.TryGetProperty("targets", out JsonElement targets) && targets.ValueKind == JsonValueKind.Array)
                    {
                        sv.Targets = new List<TargetView>();
                        foreach (JsonElement t in targets.EnumerateArray())
                        {
                            sv.Targets.Add(new TargetView
                            {
                                Index = t.GetProperty("Index").GetInt32(),
                                IsCell = t.GetProperty("IsCell").GetBoolean(),
                                X = t.GetProperty("X").GetInt32(),
                                Y = t.GetProperty("Y").GetInt32(),
                                UnitId = t.GetProperty("UnitId").GetInt32(),
                            });
                        }
                    }
                    view.Skills.Add(sv);
                }
            }
            if (state.TryGetProperty("recorded", out JsonElement rec))
            {
                foreach (JsonElement r in rec.EnumerateArray())
                {
                    string t = r.GetString();
                    if (!string.IsNullOrEmpty(t) && t != "end")
                    {
                        view.Recorded.Add(t);
                    }
                }
            }
            return view;
        }
    }

    private sealed class Plan
    {
        public string Sequence;
        public string Prefix = "";
        public List<CardToken> Tokens = new List<CardToken>();

        public static Plan FromAssignment(StateView state, List<UnitInfo> units, int[] assigned)
        {
            var plan = new Plan();
            for (int i = 0; i < assigned.Length; i++)
            {
                if (assigned[i] < 0)
                {
                    continue;
                }
                UnitInfo u = units[assigned[i]];
                HandCard card = state.Hand[i];
                plan.Tokens.Add(new CardToken
                {
                    CardIndex = i,
                    Power = card.Power,
                    Token = $"loadp:{card.Power}/{string.Join(",", card.Colors)}@{u.X},{u.Y}",
                });
            }
            plan.Sequence = string.Join(";", plan.Tokens.Select(t => t.Token));
            return plan;
        }

        public Plan WithOrder(string order)
        {
            var tokens = Tokens.OrderBy(t => order == "desc" ? -t.Power : t.Power).ThenBy(t => t.CardIndex).ToList();
            return new Plan
            {
                Sequence = string.Join(";", tokens.Select(t => t.Token)),
                Tokens = tokens,
            };
        }

        public Plan WithMove(int y) => new Plan
        {
            Sequence = $"move:{y};" + Sequence,
            Prefix = $"move:{y};" + Prefix,
            Tokens = Tokens,
        };

        public Plan WithPrefix(string prefix) => new Plan
        {
            Sequence = prefix + ";" + Sequence,
            Prefix = prefix + ";" + Prefix,
            Tokens = Tokens,
        };
    }

    private sealed class CardToken
    {
        public int CardIndex;
        public int Power;
        public string Token;
    }

    private static Plan RecordedPlan(StateView state)
    {
        if (state.Recorded.Count == 0)
        {
            return null;
        }
        return new Plan { Sequence = string.Join(";", state.Recorded) };
    }
}













