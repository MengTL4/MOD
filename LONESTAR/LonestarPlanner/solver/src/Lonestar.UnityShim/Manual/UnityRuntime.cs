using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DG.Tweening;

namespace UnityEngine
{
    internal enum WaitKind
    {
        None,
        NextFrame,
        Seconds,
        EndOfFrame,
        Coroutine,
        Custom,
        Tween,
    }

    internal sealed class CoroutineState
    {
        public readonly Stack<IEnumerator> Stack = new Stack<IEnumerator>();
        public MonoBehaviour Owner;
        public IEnumerator Root;
        public Coroutine Handle;
        public bool Done;
        public WaitKind Wait;
        public int ResumeFrame;
        public float ResumeTime;
        public Coroutine WaitCoroutine;
        public CustomYieldInstruction WaitCustom;
        public Tween WaitTween;
    }

    /// <summary>
    /// Headless replacement for the Unity player loop. Time is virtual: each <see cref="Step"/> is one
    /// frame of <see cref="DeltaTime"/> seconds. Within a frame the order follows Unity:
    /// Start → Update → coroutines (null / WaitForSeconds / nested) → tweens → LateUpdate →
    /// end-of-frame coroutines → deferred Destroy.
    /// </summary>
    public static class UnityRuntime
    {
        public static float DeltaTime = 1f / 60f;

        /// <summary>Length of the frame being run: <see cref="DeltaTime"/>, or a multiple when frames are merged.</summary>
        public static float FrameDelta = 1f / 60f;

        /// <summary>
        /// Merge frames in which nothing can happen: when every running coroutine is in a timed wait
        /// the runtime understands (Unity WaitForSeconds, the game's StaticTool.WaitForSeconds loop, a
        /// tween, another coroutine) and nothing else is due, one frame stands in for the n frames up
        /// to the next timed event (a wait or logic tween ending, an Invoke, an async delay, a
        /// deferred Destroy), with deltaTime = n × DeltaTime. That event fires in the same frame it
        /// would have with fixed steps, so the order of everything that happens is unchanged; only
        /// idle frames are skipped (like a slow frame in the real game). Anything else steps normally.
        /// </summary>
        [NotWorldState]
        public static bool MergeIdleFrames;

        public const int MaxMergedFrames = 600;

        /// <summary>Frames saved by merging (diagnostics).</summary>
        [NotWorldState]
        public static long MergedFrames;

        /// <summary>Diagnostics: when set, counts per frame why it was or was not merged.</summary>
        [NotWorldState]
        public static Dictionary<string, int> MergeStats;

        private static int NoMerge(string why)
        {
            if (MergeStats != null)
            {
                MergeStats.TryGetValue(why, out int c);
                MergeStats[why] = c + 1;
            }
            return 1;
        }
        public static float TimeScale = 1f;
        public static float Time;
        public static float UnscaledTime;
        public static int FrameCount;

        /// <summary>When set, Step accumulates Stopwatch ticks per phase: starts, Update, coroutines, tweens, LateUpdate, end of frame.</summary>
        public static long[] Profile;

        /// <summary>Behaviour types whose Update/LateUpdate run each frame; everything else is skipped.</summary>
        public static readonly HashSet<string> UpdateTypes = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Exceptions swallowed the way Unity would (coroutines, callbacks), plus Debug.LogError.</summary>
        public static readonly List<string> Errors = new List<string>();

        /// <summary>
        /// At most this many errors are kept (the rest only counted): a visual effect failing every
        /// frame must not grow the world — every snapshot copies this list — or pay for stack-trace text.
        /// </summary>
        public const int MaxErrors = 200;

        public static int ErrorsDropped;

        public static void AddError(string message)
        {
            if (Errors.Count < MaxErrors)
            {
                Errors.Add(message);
            }
            else
            {
                ErrorsDropped++;
            }
        }

        internal static readonly List<GameObject> AllGameObjects = new List<GameObject>();
        private static readonly List<MonoBehaviour> behaviours = new List<MonoBehaviour>();
        // behaviours whose type is in UpdateTypes, in registration order; rebuilt when behaviours changes
        private static readonly List<MonoBehaviour> updatables = new List<MonoBehaviour>();
        private static int behavioursVersion;
        private static int updatablesVersion = -1;
        private static int updateTypesCount = -1;
        private static readonly HashSet<MonoBehaviour> awakened = new HashSet<MonoBehaviour>();
        private static readonly List<MonoBehaviour> pendingStart = new List<MonoBehaviour>();
        private static readonly List<CoroutineState> coroutines = new List<CoroutineState>();
        private static readonly List<Tween> tweens = new List<Tween>();
        private static readonly List<(float at, MonoBehaviour target, string method)> invokes = new List<(float, MonoBehaviour, string)>();
        private static readonly List<(float at, Object obj)> destroyQueue = new List<(float, Object)>();
        private static readonly Dictionary<(Type, string), MethodInfo> methodCache = new Dictionary<(Type, string), MethodInfo>();

        public static int ActiveCoroutines => coroutines.Count(c => !c.Done);

        public static int TweenCount => tweens.Count;

        public static string QuiescenceBlocker()
        {
            if (coroutines.Any(c => !c.Done)) return "coroutine " + string.Join(",", coroutines.Where(c => !c.Done).Select(c => c.Root?.GetType().Name));
            string t = DescribeActiveTweens().FirstOrDefault();
            if (t != null) return "tween " + t;
            if (invokes.Count > 0) return "invoke " + invokes[0].method;
            if (destroyQueue.Count > 0) return "destroy";
            if (HeadlessAsync.PendingDelays > 0) return "delay";
            if (FrameContext.Pending > 0) return "async";
            return null;
        }

        public static IEnumerable<string> DescribeActiveTweens() =>
            tweens.Where(t => !t.killed && t.loops >= 0 && !t.completed && t.HasLogic).Select(t =>
                $"{t.GetType().Name} cb=[{(t.onComplete != null ? "C:" + t.onComplete.Method.DeclaringType?.Name + "." + t.onComplete.Method.Name : "")} {(t.onUpdate != null ? "U:" + t.onUpdate.Method.DeclaringType?.Name + "." + t.onUpdate.Method.Name : "")} {(t.onKill != null ? "K:" + t.onKill.Method.DeclaringType?.Name + "." + t.onKill.Method.Name : "")}] dur={t.duration} delay={t.delay} el={t.elapsed} paused={t.paused} inSeq={t.inSequence} started={t.started} done={t.completed} target={t.target?.GetType().Name}");

        public static int ActiveTweens => tweens.Count(t => !t.killed && t.loops >= 0);

        /// <summary>
        /// Nothing pending that can still change game state: no running coroutine, no finite tween with
        /// a callback (the shim never applies tweened values, so callback-free tweens are pure visuals —
        /// the UI keeps restarting short ones forever), no scheduled Invoke, async delay, posted async
        /// continuation or deferred Destroy.
        /// </summary>
        public static bool Quiescent
        {
            get
            {
                foreach (CoroutineState c in coroutines)
                {
                    if (!c.Done)
                    {
                        return false;
                    }
                }
                foreach (Tween t in tweens)
                {
                    if (!t.killed && t.loops >= 0 && !t.completed && t.HasLogic)
                    {
                        return false;
                    }
                }
                return invokes.Count == 0 && destroyQueue.Count == 0 && HeadlessAsync.PendingDelays == 0 && FrameContext.Pending == 0;
            }
        }

        /// <summary>Clears the whole scene and clock (between independent simulations).</summary>
        public static void Reset()
        {
            Time = UnscaledTime = 0f;
            FrameCount = 0;
            Errors.Clear();
            ErrorsDropped = 0;
            AllGameObjects.Clear();
            behaviours.Clear();
            updatables.Clear();
            behavioursVersion++;
            awakened.Clear();
            pendingStart.Clear();
            coroutines.Clear();
            tweens.Clear();
            invokes.Clear();
            destroyQueue.Clear();
            HeadlessAsync.Reset();
            FrameContext.Reset();
            FrameContext.Install();
        }

        // ---- frame loop -----------------------------------------------------------------------

        /// <summary>Diagnostics: when set, counts per frame what keeps the world from being quiescent.</summary>
        public static Dictionary<string, int> BlockerStats;

        public static void Step()
        {
            if (BlockerStats != null)
            {
                string b = QuiescenceBlocker() ?? "(quiescent)";
                int cut = b.IndexOf(" dur=", StringComparison.Ordinal);
                b = cut > 0 ? b.Substring(0, cut) : b;
                BlockerStats.TryGetValue(b, out int n);
                BlockerStats[b] = n + 1;
            }
            FrameCount++;
            int frames = MergeIdleFrames ? IdleFrames() : 1;
            MergedFrames += frames - 1;
            FrameDelta = DeltaTime * frames;
            float dt = FrameDelta * TimeScale;
            Time += dt;
            UnscaledTime += FrameDelta;
            try
            {
                RunFrame(dt);
            }
            finally
            {
                FrameDelta = DeltaTime;
            }
        }

        private static void RunFrame(float dt)
        {
            if (Profile == null)
            {
                RunPendingStarts();
                CallOnAll("Update");
                RunInvokes();
                HeadlessAsync.Tick();
                FrameContext.Drain();
                ResumeCoroutines(endOfFrame: false);
                Tween.TickAll(tweens, dt);
                CallOnAll("LateUpdate");
                ResumeCoroutines(endOfFrame: true);
                ProcessDestroyQueue();
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            RunPendingStarts();
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            CallOnAll("Update");
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            RunInvokes();
            HeadlessAsync.Tick();
            FrameContext.Drain();
            ResumeCoroutines(endOfFrame: false);
            long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
            Tween.TickAll(tweens, dt);
            long t4 = System.Diagnostics.Stopwatch.GetTimestamp();
            CallOnAll("LateUpdate");
            long t5 = System.Diagnostics.Stopwatch.GetTimestamp();
            ResumeCoroutines(endOfFrame: true);
            ProcessDestroyQueue();
            long t6 = System.Diagnostics.Stopwatch.GetTimestamp();
            Profile[0] += t1 - t0; Profile[1] += t2 - t1; Profile[2] += t3 - t2; Profile[3] += t4 - t3; Profile[4] += t5 - t4; Profile[5] += t6 - t5;
        }

        /// <summary>
        /// How many fixed frames the coming frame may stand for: 1 unless everything pending is a timed
        /// wait (see <see cref="MergeIdleFrames"/>); then the frames until the soonest timed event, which
        /// fires in the merged frame exactly as it would have in the last of the fixed ones.
        /// </summary>
        private static int IdleFrames()
        {
            float step = DeltaTime * TimeScale;
            if (step <= 0f || FrameContext.Pending > 0)
            {
                return NoMerge("async continuation");
            }
            foreach (MonoBehaviour mb in pendingStart)
            {
                // inactive behaviours wait in pendingStart until activated; only startable ones matter
                if (!mb.destroyed && mb.isActiveAndEnabled)
                {
                    return NoMerge("pending Start");
                }
            }
            double soonest = double.MaxValue; // scaled seconds
            foreach (CoroutineState c in coroutines)
            {
                if (c.Done)
                {
                    continue;
                }
                switch (c.Wait)
                {
                    case WaitKind.Seconds:
                        soonest = Math.Min(soonest, c.ResumeTime - Time);
                        break;
                    case WaitKind.Coroutine:
                        break; // resumes when that coroutine ends, which is counted itself
                    case WaitKind.Tween:
                        if (c.WaitTween == null || c.WaitTween is Sequence)
                        {
                            return NoMerge("coroutine waits on a sequence");
                        }
                        soonest = Math.Min(soonest, Tween.SecondsToNextEvent(c.WaitTween));
                        break;
                    case WaitKind.NextFrame:
                        if (c.Stack.Count == 0 || !TimedLoopRemaining(c.Stack.Peek(), out float left))
                        {
                            return NoMerge("per-frame loop " + (c.Stack.Count == 0 ? "?" : c.Stack.Peek().GetType().FullName));
                        }
                        soonest = Math.Min(soonest, left);
                        break;
                    default:
                        return NoMerge("coroutine wait " + c.Wait); // end of frame, custom predicates
                }
            }
            foreach (Tween t in tweens)
            {
                if (t.killed || t.completed || t.paused || t.inSequence || t.loops < 0 || !t.HasLogic)
                {
                    continue;
                }
                if (t is Sequence)
                {
                    return NoMerge("logic sequence");
                }
                soonest = Math.Min(soonest, Tween.SecondsToNextEvent(t));
            }
            foreach (var (at, _, _) in invokes)
            {
                soonest = Math.Min(soonest, at - Time);
            }
            foreach (var (at, _) in destroyQueue)
            {
                soonest = Math.Min(soonest, at - Time);
            }
            double delay = HeadlessAsync.SecondsToNextDelay();
            if (delay < double.MaxValue)
            {
                soonest = Math.Min(soonest, delay * TimeScale);
            }
            if (soonest == double.MaxValue || soonest <= step)
            {
                return NoMerge(soonest == double.MaxValue ? "nothing timed pending" : "next event this frame");
            }
            int merged = Math.Clamp((int)Math.Ceiling((soonest - 1e-6) / step), 1, MaxMergedFrames);
            if (MergeStats != null)
            {
                MergeStats.TryGetValue("merged", out int c);
                MergeStats["merged"] = c + 1;
            }
            return merged;
        }

        private static readonly Dictionary<Type, (System.Reflection.FieldInfo total, System.Reflection.FieldInfo time, System.Reflection.FieldInfo unscaled)> timedLoops =
            new Dictionary<Type, (System.Reflection.FieldInfo, System.Reflection.FieldInfo, System.Reflection.FieldInfo)>();

        /// <summary>
        /// The game's StaticTool.WaitForSeconds(total): <c>while (time &lt; total) { time += Time.deltaTime; yield return null; }</c>.
        /// Seconds left of such an iterator; false for any other per-frame loop (unknown: no merging).
        /// </summary>
        private static bool TimedLoopRemaining(IEnumerator it, out float left)
        {
            left = 0f;
            Type t = it?.GetType();
            if (t?.DeclaringType?.Name != "StaticTool" || !t.Name.StartsWith("<WaitForSeconds>", StringComparison.Ordinal))
            {
                return false;
            }
            if (!timedLoops.TryGetValue(t, out var f))
            {
                const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                f = (t.GetField("totalTime", all), t.GetFields(all).FirstOrDefault(x => x.Name.StartsWith("<time>", StringComparison.Ordinal)), t.GetField("ignoreTimeScale", all));
                timedLoops[t] = f;
            }
            if (f.total == null || f.time == null || f.unscaled == null || (bool)f.unscaled.GetValue(it))
            {
                return false; // the unscaled variant adds Time.unscaledTime, not a delta: leave it alone
            }
            left = (float)f.total.GetValue(it) - (float)f.time.GetValue(it);
            return true;
        }

        /// <summary>Steps frames until <paramref name="done"/> holds; returns the frames used, or -1 on timeout.</summary>
        public static int RunUntil(Func<bool> done, int maxFrames)
        {
            for (int i = 0; i < maxFrames; i++)
            {
                if (done())
                {
                    return i;
                }
                Step();
            }
            return done() ? maxFrames : -1;
        }

        private static void RunPendingStarts()
        {
            if (pendingStart.Count == 0)
            {
                return;
            }
            var batch = pendingStart.ToList();
            pendingStart.Clear();
            foreach (MonoBehaviour mb in batch)
            {
                if (!mb.destroyed && mb.isActiveAndEnabled)
                {
                    object r = Call(mb, "Start");
                    if (r is IEnumerator e)
                    {
                        StartCoroutine(mb, e);
                    }
                }
                else if (!mb.destroyed)
                {
                    pendingStart.Add(mb);
                }
            }
        }

        private static void CallOnAll(string method)
        {
            if (UpdateTypes.Count == 0)
            {
                return;
            }
            if (updatablesVersion != behavioursVersion || updateTypesCount != UpdateTypes.Count)
            {
                updatables.Clear();
                foreach (MonoBehaviour b in behaviours)
                {
                    if (UpdateTypes.Contains(b.GetType().Name))
                    {
                        updatables.Add(b);
                    }
                }
                updatablesVersion = behavioursVersion;
                updateTypesCount = UpdateTypes.Count;
            }
            if (updatables.Count == 0)
            {
                return;
            }
            foreach (MonoBehaviour mb in updatables.ToArray())
            {
                if (!mb.destroyed && mb.isActiveAndEnabled && awakened.Contains(mb) && !pendingStart.Contains(mb))
                {
                    Call(mb, method);
                }
            }
        }

        private static void RunInvokes()
        {
            for (int i = 0; i < invokes.Count; i++)
            {
                var (at, target, method) = invokes[i];
                if (Time + 1e-6f < at)
                {
                    continue;
                }
                invokes.RemoveAt(i--);
                if (!target.destroyed)
                {
                    Call(target, method);
                }
            }
        }

        internal static void ScheduleInvoke(MonoBehaviour target, string method, float delay) =>
            invokes.Add((Time + delay, target, method));

        // ---- coroutines -----------------------------------------------------------------------

        internal static Coroutine StartCoroutine(MonoBehaviour owner, IEnumerator routine)
        {
            if (routine == null)
            {
                return null;
            }
            var state = new CoroutineState { Owner = owner, Root = routine };
            state.Handle = new Coroutine { state = state };
            state.Stack.Push(routine);
            coroutines.Add(state);
            Advance(state);
            return state.Handle;
        }

        internal static void StopCoroutine(IEnumerator routine)
        {
            foreach (CoroutineState s in coroutines.Where(c => c.Root == routine))
            {
                s.Done = true;
            }
        }

        internal static void StopCoroutine(Coroutine routine)
        {
            if (routine?.state != null)
            {
                routine.state.Done = true;
            }
        }

        internal static void StopAllCoroutines(MonoBehaviour owner)
        {
            foreach (CoroutineState s in coroutines.Where(c => c.Owner == (object)owner))
            {
                s.Done = true;
            }
        }

        private static void ResumeCoroutines(bool endOfFrame)
        {
            int count = coroutines.Count;
            for (int i = 0; i < count && i < coroutines.Count; i++)
            {
                CoroutineState s = coroutines[i];
                if (s.Done)
                {
                    continue;
                }
                if (s.Owner is not null && (s.Owner.destroyed || !s.Owner.gameObject.activeInHierarchy))
                {
                    s.Done = true;
                    continue;
                }
                if (Ready(s, endOfFrame))
                {
                    Advance(s);
                }
            }
            if (endOfFrame)
            {
                coroutines.RemoveAll(c => c.Done);
            }
        }

        private static bool Ready(CoroutineState s, bool endOfFrame)
        {
            switch (s.Wait)
            {
                case WaitKind.EndOfFrame:
                    return endOfFrame;
                case WaitKind.NextFrame:
                    return !endOfFrame && FrameCount >= s.ResumeFrame;
                case WaitKind.Seconds:
                    return !endOfFrame && Time + 1e-6f >= s.ResumeTime;
                case WaitKind.Coroutine:
                    return !endOfFrame && (s.WaitCoroutine.state == null || s.WaitCoroutine.state.Done);
                case WaitKind.Custom:
                    return !endOfFrame && !s.WaitCustom.keepWaiting;
                case WaitKind.Tween:
                    return !endOfFrame && (s.WaitTween.killed || s.WaitTween.completed);
                default:
                    return !endOfFrame;
            }
        }

        private static void Advance(CoroutineState s)
        {
            s.Wait = WaitKind.None;
            while (!s.Done)
            {
                if (s.Stack.Count == 0)
                {
                    s.Done = true;
                    return;
                }
                IEnumerator top = s.Stack.Peek();
                bool moved;
                try
                {
                    moved = top.MoveNext();
                }
                catch (Exception ex)
                {
                    Error("coroutine " + (s.Owner?.GetType().Name ?? "?"), ex);
                    s.Done = true;
                    return;
                }
                if (!moved)
                {
                    s.Stack.Pop();
                    continue;
                }
                switch (top.Current)
                {
                    case null:
                        s.Wait = WaitKind.NextFrame;
                        s.ResumeFrame = FrameCount + 1;
                        return;
                    case WaitForSeconds w:
                        s.Wait = WaitKind.Seconds;
                        s.ResumeTime = Time + w.seconds;
                        return;
                    case WaitForEndOfFrame:
                        s.Wait = WaitKind.EndOfFrame;
                        return;
                    case Coroutine c:
                        if (c.state == null || c.state.Done)
                        {
                            continue;
                        }
                        s.Wait = WaitKind.Coroutine;
                        s.WaitCoroutine = c;
                        return;
                    case IEnumerator nested:
                        s.Stack.Push(nested);
                        continue;
                    case CustomYieldInstruction custom:
                        s.Wait = WaitKind.Custom;
                        s.WaitCustom = custom;
                        return;
                    case TweenYield ty:
                        s.Wait = WaitKind.Tween;
                        s.WaitTween = ty.tween;
                        return;
                    default:
                        s.Wait = WaitKind.NextFrame;
                        s.ResumeFrame = FrameCount + 1;
                        return;
                }
            }
        }

        internal static void AddTween(Tween t) => tweens.Add(t);

        internal static void RemoveTween(Tween t) => tweens.Remove(t);

        internal static IEnumerable<Tween> LiveTweens => tweens.Where(t => !t.killed);

        // ---- objects and lifecycle ------------------------------------------------------------

        internal static void Register(GameObject go) => AllGameObjects.Add(go);

        internal static GameObject AdoptOrphan(Component c)
        {
            var go = new GameObject(c.GetType().Name);
            go.components.Add(c);
            c.owner = go;
            return go;
        }

        internal static Component AddComponent(GameObject go, Type type)
        {
            foreach (Type required in RequiredComponents(type))
            {
                if (go.GetComponent(required) is null)
                {
                    AddComponent(go, required);
                }
            }
            if (type == typeof(Transform))
            {
                return go.transform;
            }
            if (typeof(Transform).IsAssignableFrom(type))
            {
                return ReplaceTransform(go, type);
            }
            var c = (Component)Activator.CreateInstance(type, nonPublic: true);
            c.owner = go;
            UnitySerializer.InitializeSerializableDefaults(c);
            go.components.Add(c);
            if (c is MonoBehaviour mb)
            {
                behaviours.Add(mb);
                behavioursVersion++;
                if (go.activeInHierarchy)
                {
                    Awaken(mb);
                }
            }
            return c;
        }

        /// <summary>Adding a RectTransform swaps out the plain Transform, keeping hierarchy and pose (Unity behaviour).</summary>
        private static Transform ReplaceTransform(GameObject go, Type type)
        {
            Transform old = go.transform;
            if (type.IsInstanceOfType(old))
            {
                return old;
            }
            var t = (Transform)Activator.CreateInstance(type, nonPublic: true);
            t.owner = go;
            t.localPosition = old.localPosition;
            t.localRotation = old.localRotation;
            t.localScale = old.localScale;
            t.parentTransform = old.parentTransform;
            if (old.parentTransform is not null)
            {
                int i = old.parentTransform.children.IndexOf(old);
                old.parentTransform.children[i] = t;
            }
            foreach (Transform child in old.children)
            {
                child.parentTransform = t;
                t.children.Add(child);
            }
            go.components[0] = t;
            return t;
        }

        private static IEnumerable<Type> RequiredComponents(Type type)
        {
            foreach (CustomAttributeData a in type.GetCustomAttributesData())
            {
                if (a.AttributeType.Name != "RequireComponent")
                {
                    continue;
                }
                foreach (CustomAttributeTypedArgument arg in a.ConstructorArguments)
                {
                    if (arg.Value is Type t && t != typeof(Transform))
                    {
                        yield return t;
                    }
                }
            }
        }

        /// <summary>Awake then OnEnable (if enabled), and queue Start — Unity's order on activation.</summary>
        private static void Awaken(MonoBehaviour mb)
        {
            if (awakened.Add(mb))
            {
                Call(mb, "Awake");
                if (mb.destroyed)
                {
                    return;
                }
                if (mb.enabled)
                {
                    Call(mb, "OnEnable");
                }
                pendingStart.Add(mb);
            }
            else if (mb.enabled)
            {
                Call(mb, "OnEnable");
            }
        }

        internal static void OnActiveChanged(GameObject go, bool wasActive)
        {
            bool isActive = go.activeInHierarchy;
            if (isActive == wasActive)
            {
                return;
            }
            foreach (GameObject g in SelfAndDescendants(go))
            {
                foreach (MonoBehaviour mb in g.components.OfType<MonoBehaviour>().ToList())
                {
                    if (mb.destroyed)
                    {
                        continue;
                    }
                    if (isActive)
                    {
                        Awaken(mb);
                    }
                    else if (mb.enabled && awakened.Contains(mb))
                    {
                        Call(mb, "OnDisable");
                    }
                }
            }
        }

        internal static void OnBehaviourEnabledChanged(Behaviour b, bool enabled)
        {
            if (b is not MonoBehaviour mb || mb.destroyed || !mb.gameObject.activeInHierarchy)
            {
                return;
            }
            if (!awakened.Contains(mb))
            {
                return;
            }
            Call(mb, enabled ? "OnEnable" : "OnDisable");
        }

        private static IEnumerable<GameObject> SelfAndDescendants(GameObject go)
        {
            yield return go;
            foreach (Transform child in go.transform.children.ToList())
            {
                foreach (GameObject g in SelfAndDescendants(child.gameObject))
                {
                    yield return g;
                }
            }
        }

        internal static void ScheduleDestroy(Object obj, float delay)
        {
            if (!Object.IsNull(obj))
            {
                destroyQueue.Add((Time + delay, obj));
            }
        }

        private static void ProcessDestroyQueue()
        {
            if (destroyQueue.Count == 0)
            {
                return;
            }
            var due = destroyQueue.Where(d => Time + 1e-6f >= d.at).ToList();
            destroyQueue.RemoveAll(d => Time + 1e-6f >= d.at);
            foreach (var (_, obj) in due)
            {
                DestroyNow(obj);
            }
        }

        internal static void DestroyNow(Object obj)
        {
            if (Object.IsNull(obj))
            {
                return;
            }
            switch (obj)
            {
                case GameObject go:
                    foreach (Transform child in go.transform.children.ToList())
                    {
                        DestroyNow(child.gameObject);
                    }
                    foreach (Component c in go.components.ToList())
                    {
                        DestroyComponent(c);
                    }
                    go.transform.parentTransform?.children.Remove(go.transform);
                    go.destroyed = true;
                    AllGameObjects.Remove(go);
                    break;
                case Component c:
                    DestroyComponent(c);
                    c.owner?.components.Remove(c);
                    break;
                default:
                    obj.destroyed = true;
                    break;
            }
        }

        private static void DestroyComponent(Component c)
        {
            if (c is MonoBehaviour mb && awakened.Contains(mb))
            {
                if (mb.enabled && mb.gameObject.activeInHierarchy)
                {
                    Call(mb, "OnDisable");
                }
                Call(mb, "OnDestroy");
            }
            c.destroyed = true;
            if (c is MonoBehaviour b)
            {
                behaviours.Remove(b);
                behavioursVersion++;
                pendingStart.Remove(b);
                StopAllCoroutines(b);
            }
        }

        internal static IEnumerable<T> FindObjects<T>(bool includeInactive) where T : Object
        {
            foreach (GameObject go in AllGameObjects.ToList())
            {
                if (go.destroyed || (!includeInactive && !go.activeInHierarchy))
                {
                    continue;
                }
                if (go is T self)
                {
                    yield return self;
                }
                foreach (Component c in go.components)
                {
                    if (c is T t && !c.destroyed)
                    {
                        yield return t;
                    }
                }
            }
        }

        internal static Object Instantiate(Object original, Transform parent)
        {
            switch (original)
            {
                case null:
                    throw new ArgumentException("The Object you want to instantiate is null.");
                case GameObject go:
                    return UnitySerializer.CloneHierarchy(go, parent);
                case Component c:
                    GameObject clone = UnitySerializer.CloneHierarchy(c.gameObject, parent);
                    int index = c.gameObject.components.IndexOf(c);
                    return clone.components[index];
                case ScriptableObject so:
                    var copy = (ScriptableObject)Activator.CreateInstance(so.GetType(), nonPublic: true);
                    UnitySerializer.CopySerializedFields(so, copy, new Dictionary<object, object>());
                    copy.name = so.name + "(Clone)";
                    return copy;
                default:
                    return original;
            }
        }

        /// <summary>Activates components of a freshly built hierarchy in Unity's order (parents first).</summary>
        internal static void AwakenHierarchy(GameObject root)
        {
            if (!root.activeInHierarchy)
            {
                return;
            }
            foreach (GameObject g in SelfAndDescendants(root))
            {
                if (!g.activeInHierarchy)
                {
                    continue;
                }
                foreach (MonoBehaviour mb in g.components.OfType<MonoBehaviour>().ToList())
                {
                    if (!mb.destroyed && !awakened.Contains(mb))
                    {
                        Awaken(mb);
                    }
                }
            }
        }

        internal static void RegisterBehaviour(MonoBehaviour mb)
        {
            behaviours.Add(mb);
            behavioursVersion++;
        }

        // ---- reflection helpers ---------------------------------------------------------------

        internal static MethodInfo FindMethod(Type type, string name)
        {
            if (methodCache.TryGetValue((type, name), out MethodInfo m))
            {
                return m;
            }
            for (Type t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
            {
                m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (m != null)
                {
                    break;
                }
            }
            methodCache[(type, name)] = m;
            return m;
        }

        private static object Call(MonoBehaviour mb, string method)
        {
            MethodInfo m = FindMethod(mb.GetType(), method);
            if (m == null)
            {
                return null;
            }
            try
            {
                return m.Invoke(mb, null);
            }
            catch (TargetInvocationException ex)
            {
                Error($"{mb.GetType().Name}.{method}", ex.InnerException ?? ex);
                return null;
            }
        }

        public static void Error(string where, Exception ex)
        {
            if (Errors.Count < MaxErrors)
            {
                Errors.Add($"{where}: {ex}");
            }
            else
            {
                ErrorsDropped++;
            }
        }
    }
}
