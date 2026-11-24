using System.Collections.Concurrent;
using System.Diagnostics;

namespace Brain;

/// <summary>
/// N worker processes that all hold the same root decision point. Jobs are independent requests
/// relative to that root; <see cref="RunAll"/> hands them to whichever worker is free.
/// </summary>
public sealed class WorkerPool : IDisposable
{
    private sealed class Proc
    {
        public Process P;
        public StreamWriter In;
        public StreamReader Out;
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int NextId;
    }

    private readonly List<Proc> procs = new();
    private readonly WorkerPool parent;

    public int Count => procs.Count;

    public long Jobs;

    public static bool Trace = Environment.GetEnvironmentVariable("BRAIN_TRACE") == "1";

    public WorkerPool(int count, string brainDll)
    {
        for (int i = 0; i < count; i++)
        {
            ProcessStartInfo psi = WorkerStart(brainDll);
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process p = Process.Start(psi);
            try
            {
                // one worker per core: the game (and everything else) must still win the CPU when it needs it
                p.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch
            {
                // not allowed here: run at normal priority
            }
            p.ErrorDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
            procs.Add(new Proc { P = p, In = p.StandardInput, Out = p.StandardOutput });
        }
    }

    private WorkerPool(WorkerPool parent, IEnumerable<Proc> subset)
    {
        this.parent = parent;
        procs.AddRange(subset);
    }

    /// <summary>
    /// Splits the workers into <paramref name="parts"/> disjoint pools (as even as possible) so that
    /// independent games — e.g. different loadouts — run side by side. The views own nothing: only
    /// the original pool shuts the processes down.
    /// </summary>
    public List<WorkerPool> Split(int parts)
    {
        parts = Math.Clamp(parts, 1, procs.Count);
        return Enumerable.Range(0, parts)
            .Select(k => new WorkerPool(this, procs.Where((_, i) => i % parts == k)))
            .ToList();
    }

    /// <summary>
    /// Workers run the way this process was started: "dotnet Brain.dll" in development, the Brain
    /// executable itself in the planner mod's bundle (self-contained, no .NET install needed).
    /// </summary>
    private static ProcessStartInfo WorkerStart(string brainDll)
    {
        string host = Environment.ProcessPath;
        if (host != null && Path.GetFileNameWithoutExtension(host).Equals("Brain", StringComparison.OrdinalIgnoreCase))
        {
            return new ProcessStartInfo(host, "--worker");
        }
        bool dotnet = host != null && Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        return new ProcessStartInfo(dotnet ? host : "dotnet", $"\"{brainDll}\" --worker");
    }

    private static async Task<Response> Call(Proc w, Request req)
    {
        await w.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            req.Id = ++w.NextId;
            long t0 = Stopwatch.GetTimestamp();
            await w.In.WriteLineAsync(Json.Write(req)).ConfigureAwait(false);
            await w.In.FlushAsync().ConfigureAwait(false);
            string line = await w.Out.ReadLineAsync().ConfigureAwait(false);
            long t1 = Stopwatch.GetTimestamp();
            if (line == null)
            {
                return new Response { Ok = false, Error = "worker exited" };
            }
            Response r = Json.Read<Response>(line);
            if (Trace)
            {
                long t2 = Stopwatch.GetTimestamp();
                Console.Error.WriteLine($"trace {req.Op,-7} rtt {(t1 - t0) * 1000.0 / Stopwatch.Frequency,7:F1}ms worker {r.Ms,7:F1}ms parse {(t2 - t1) * 1000.0 / Stopwatch.Frequency,5:F1}ms bytes {line.Length} {r.Profile}");
            }
            return r;
        }
        finally
        {
            w.Gate.Release();
        }
    }

    /// <summary>Sends the same request to every worker (init / advance / sync).</summary>
    public async Task<List<Response>> Broadcast(Func<Request> make)
    {
        owner.Clear(); // init / advance / sync move every worker's root
        Task<Response>[] tasks = procs.Select(w => Call(w, make())).ToArray();
        return (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList();
    }

    /// <summary>prefix → worker that last reached it (and so has it, or a close ancestor, in its snapshot cache).</summary>
    private readonly ConcurrentDictionary<string, int> owner = new();

    private int Owner(string prefix)
    {
        if (prefix == null)
        {
            return -1;
        }
        string p = prefix;
        while (true)
        {
            if (owner.TryGetValue(p, out int w))
            {
                return w;
            }
            int cut = p.LastIndexOf(';');
            if (cut < 0)
            {
                return -1;
            }
            p = p.Substring(0, cut);
        }
    }

    /// <summary>
    /// Runs independent jobs; results come back in job order. A job goes preferably to the worker
    /// that holds its prefix's nearest ancestor in cache (no replay from the root); idle workers
    /// steal the rest so nobody waits. Once <paramref name="stop"/> says so, jobs not yet handed out
    /// are dropped (their result is null).
    /// </summary>
    public async Task<List<Response>> RunAll(IReadOnlyList<Request> jobs, Func<bool> stop = null)
    {
        var results = new Response[jobs.Count];
        var pending = new List<(int job, int prefer)>();
        for (int i = 0; i < jobs.Count; i++)
        {
            pending.Add((i, Owner(jobs[i].Prefix)));
        }
        bool Take(int w, out int job)
        {
            lock (pending)
            {
                if (stop?.Invoke() == true)
                {
                    pending.Clear();
                }
                int k = pending.FindIndex(p => p.prefer == w);
                if (k < 0)
                {
                    k = pending.FindIndex(p => p.prefer < 0);
                }
                if (k < 0 && pending.Count > 0)
                {
                    k = 0; // steal
                }
                if (k < 0)
                {
                    job = -1;
                    return false;
                }
                job = pending[k].job;
                pending.RemoveAt(k);
                return true;
            }
        }
        async Task Drain(int w)
        {
            while (Take(w, out int i))
            {
                results[i] = await Call(procs[w], jobs[i]).ConfigureAwait(false);
                Interlocked.Increment(ref Jobs);
                if (parent != null)
                {
                    Interlocked.Increment(ref parent.Jobs);
                }
                if (jobs[i].Prefix != null)
                {
                    if (owner.Count > 200_000)
                    {
                        owner.Clear();
                    }
                    owner[jobs[i].Prefix] = w;
                    if (jobs[i].Op == "eval")
                    {
                        owner[jobs[i].Prefix.Length == 0 ? "end" : jobs[i].Prefix + ";end"] = w;
                    }
                }
            }
        }
        await Task.WhenAll(Enumerable.Range(0, procs.Count).Select(Drain)).ConfigureAwait(false);
        return results.ToList();
    }

    /// <summary>Forget cache ownership (after the root moves).</summary>
    public void ResetAffinity() => owner.Clear();

    public Task<Response> One(Request req) => Call(procs[0], req);

    public void Dispose()
    {
        if (parent != null)
        {
            return;
        }
        foreach (Proc w in procs)
        {
            try
            {
                w.In.WriteLine(Json.Write(new Request { Op = "quit" }));
                w.In.Flush();
                if (!w.P.WaitForExit(2000))
                {
                    w.P.Kill();
                }
            }
            catch
            {
                // already gone
            }
        }
    }
}
