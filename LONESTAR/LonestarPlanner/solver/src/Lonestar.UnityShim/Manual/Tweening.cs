using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DG.Tweening
{
    /// <summary>
    /// DOTween on the virtual clock. Visual effects (moving, fading) are not applied; what matters
    /// is that callbacks (OnStart/OnUpdate/OnComplete/OnKill, sequence callbacks) fire at the same
    /// virtual time and in the same order as in the game, because game logic hangs off some of them.
    /// </summary>
    [ShimLenient]
    public abstract partial class Tween
    {
        public object target;
        public string stringId;
        public float duration;
        public float delay;
        public int loops = 1;
        public bool autoKill = true;
        public bool isRelative;
        public bool playedOnce;

        internal float elapsed;
        internal bool started;
        internal bool completed;
        internal bool killed;
        internal bool paused;
        internal bool inSequence;

        public TweenCallback onComplete;
        public TweenCallback onKill;
        public TweenCallback onStart;
        public TweenCallback onPlay;
        public TweenCallback onUpdate;
        public TweenCallback onStepComplete;
        public TweenCallback onRewind;

        /// <summary>Writes the tween's end value to its target when it completes (transform shortcuts).</summary>
        internal Action applyEnd;

        public bool active => !killed;

        /// <summary>True when finishing this tween runs game code (any callback set that is not purely cosmetic).</summary>
        internal virtual bool HasLogic =>
            Logic(onComplete) || Logic(onKill) || Logic(onStart) || Logic(onPlay) || Logic(onUpdate) ||
            Logic(onStepComplete) || Logic(onRewind);

        /// <summary>
        /// UIManager's floating text (CreateTextFlow*) and text punch (TextEffectIn) callbacks only move,
        /// rescale or pool the text object, UtilsClass.PostProcessing* only animates screen effects,
        /// MaterialProcessController restarts its shader-parameter loop forever; nothing in the
        /// battle reads them. Waiting for them was ~40% of all simulated frames (and the material
        /// loop kept every battle end from ever settling), so they do not hold up quiescence (they
        /// still run on later frames).
        /// </summary>
        private static bool Logic(Delegate cb)
        {
            if (cb == null)
            {
                return false;
            }
            System.Reflection.MethodInfo m = cb.Method;
            if (m.DeclaringType?.Name == "MaterialProcessController")
            {
                return false;
            }
            string owner = m.DeclaringType?.DeclaringType?.Name;
            if (owner == "UIManager"
                && (m.Name.StartsWith("<CreateTextFlow", StringComparison.Ordinal) || m.Name.StartsWith("<TextEffectIn", StringComparison.Ordinal)))
            {
                return false;
            }
            if (owner == "UtilsClass" && m.Name.StartsWith("<PostProcessing", StringComparison.Ordinal))
            {
                return false;
            }
            return true;
        }

        public float position => elapsed;

        /// <summary>Total length; infinite loops count as a single pass for sequencing purposes.</summary>
        internal virtual float FullDuration => delay + duration * Math.Max(1, loops);

        /// <summary>Scaled seconds until this tween next runs a callback-carrying step (start, or completion).</summary>
        internal static double SecondsToNextEvent(Tween t)
        {
            if (!t.started)
            {
                return t.delay - t.elapsed;
            }
            return t.delay + t.duration * Math.Max(1, t.loops) - t.elapsed;
        }

        internal static void TickAll(List<Tween> all, float dt)
        {
            foreach (Tween t in all.ToList())
            {
                if (!t.killed && !t.inSequence && !t.paused)
                {
                    t.Advance(dt);
                }
            }
            all.RemoveAll(t => t.killed);
        }

        internal virtual void Advance(float dt)
        {
            if (completed)
            {
                return;
            }
            elapsed += dt;
            if (!started && elapsed + 1e-6f >= delay)
            {
                started = true;
                playedOnce = true;
                Invoke(onPlay);
                Invoke(onStart);
            }
            if (started)
            {
                Invoke(onUpdate);
            }
            if (loops >= 0 && elapsed + 1e-6f >= delay + duration * Math.Max(1, loops))
            {
                Finish();
            }
        }

        internal void Finish()
        {
            if (completed || killed)
            {
                return;
            }
            if (!started)
            {
                started = true;
                Invoke(onPlay);
                Invoke(onStart);
            }
            completed = true;
            if (applyEnd != null)
            {
                try
                {
                    applyEnd();
                }
                catch (Exception ex)
                {
                    UnityRuntime.Error("tween end value", ex);
                }
            }
            OnFinishedInternal();
            Invoke(onStepComplete);
            Invoke(onComplete);
            if (autoKill)
            {
                Kill();
            }
        }

        internal virtual void OnFinishedInternal()
        {
        }

        internal void Kill()
        {
            if (killed)
            {
                return;
            }
            killed = true;
            Invoke(onKill);
        }

        internal static void Invoke(TweenCallback cb)
        {
            if (cb == null)
            {
                return;
            }
            try
            {
                cb();
            }
            catch (Exception ex)
            {
                UnityRuntime.Error("tween callback", ex);
            }
        }
    }

    [ShimLenient]
    public partial class Tweener : Tween
    {
    }

    /// <summary>Concrete tween for generated entry points whose declared return type is abstract.</summary>
    public sealed class ShimTweener : Tweener
    {
    }

    [ShimLenient]
    public partial class Sequence : Tween
    {
        private readonly List<(float at, Tween tween)> items = new List<(float, Tween)>();
        private readonly List<(float at, TweenCallback cb, bool fired)> callbacks = new List<(float, TweenCallback, bool)>();
        private float lastAppendStart;

        internal float Length => Math.Max(
            items.Count == 0 ? 0f : items.Max(i => i.at + i.tween.FullDuration),
            callbacks.Count == 0 ? 0f : callbacks.Max(c => c.at));

        internal override float FullDuration => delay + Length * Math.Max(1, loops);

        internal override bool HasLogic
        {
            get
            {
                if (base.HasLogic)
                {
                    return true;
                }
                foreach (var (_, cb, fired) in callbacks)
                {
                    if (cb != null && !fired)
                    {
                        return true;
                    }
                }
                foreach (var (_, tween) in items)
                {
                    if (!tween.killed && !tween.completed && tween.HasLogic)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        internal void Insert(float at, Tween t)
        {
            t.inSequence = true;
            items.Add((at, t));
            duration = Length;
        }

        internal void AppendTween(Tween t)
        {
            lastAppendStart = Length;
            Insert(lastAppendStart, t);
        }

        internal void JoinTween(Tween t) => Insert(lastAppendStart, t);

        internal void AppendCallbackAt(float at, TweenCallback cb)
        {
            callbacks.Add((at, cb, false));
            duration = Length;
        }

        internal void AppendCallbackAtEnd(TweenCallback cb) => AppendCallbackAt(Length, cb);

        internal void AppendIntervalAtEnd(float interval)
        {
            float end = Length + interval;
            callbacks.Add((end, null, false));
            duration = Length;
        }

        internal override void Advance(float dt)
        {
            if (completed)
            {
                return;
            }
            elapsed += dt;
            if (!started && elapsed + 1e-6f >= delay)
            {
                started = true;
                playedOnce = true;
                Invoke(onPlay);
                Invoke(onStart);
            }
            if (!started)
            {
                return;
            }
            float local = elapsed - delay;
            foreach (var (at, tween) in items)
            {
                if (tween.killed || tween.completed || local + 1e-6f < at)
                {
                    continue;
                }
                float into = local - at;
                tween.Advance(into - tween.elapsed);
            }
            for (int i = 0; i < callbacks.Count; i++)
            {
                var (at, cb, fired) = callbacks[i];
                if (!fired && local + 1e-6f >= at)
                {
                    callbacks[i] = (at, cb, true);
                    Invoke(cb);
                }
            }
            Invoke(onUpdate);
            if (loops >= 0 && local + 1e-6f >= Length)
            {
                Finish();
            }
        }

        internal override void OnFinishedInternal()
        {
            foreach (var (_, tween) in items)
            {
                tween.Finish();
            }
            for (int i = 0; i < callbacks.Count; i++)
            {
                var (at, cb, fired) = callbacks[i];
                if (!fired)
                {
                    callbacks[i] = (at, cb, true);
                    Invoke(cb);
                }
            }
        }
    }

    namespace Core
    {
        [ShimLenient]
        public partial class TweenerCore<T1, T2, TPlugOptions> : Tweener
        {
            public TPlugOptions plugOptions;
        }
    }

    /// <summary>Creates live tweens for generated DOTween entry points (DOMove, DOFade, DOTween.To, ...).</summary>
    public static class TweenFactory
    {
        public static T Make<T>(float duration, object target) where T : Tween, new()
        {
            var t = new T { duration = duration, target = target };
            UnityRuntime.AddTween(t);
            return t;
        }
    }

    /// <summary>
    /// Transform shortcuts that land on their end value when they complete. Intermediate values are
    /// never applied (nothing renders), but the end state matters: e.g. hand cards re-tween toward
    /// their holder until transform.position equals it, which never happens if the move is a no-op.
    /// </summary>
    [ShimLenient]
    public static partial class ShortcutExtensions
    {
        public static Core.TweenerCore<Vector3, Vector3, Plugins.Options.VectorOptions> DOMove(this Transform target, Vector3 endValue, float duration, bool snapping = false) =>
            Landing(target, duration, () => target.position = endValue);

        public static Core.TweenerCore<Vector3, Vector3, Plugins.Options.VectorOptions> DOMoveX(this Transform target, float endValue, float duration, bool snapping = false) =>
            Landing(target, duration, () => { Vector3 p = target.position; target.position = new Vector3(endValue, p.y, p.z); });

        public static Core.TweenerCore<Vector3, Vector3, Plugins.Options.VectorOptions> DOLocalMove(this Transform target, Vector3 endValue, float duration, bool snapping = false) =>
            Landing(target, duration, () => target.localPosition = endValue);

        public static Core.TweenerCore<Vector3, Vector3, Plugins.Options.VectorOptions> DOLocalMoveX(this Transform target, float endValue, float duration, bool snapping = false) =>
            Landing(target, duration, () => { Vector3 p = target.localPosition; target.localPosition = new Vector3(endValue, p.y, p.z); });

        public static Core.TweenerCore<Vector3, Vector3, Plugins.Options.VectorOptions> DOLocalMoveY(this Transform target, float endValue, float duration, bool snapping = false) =>
            Landing(target, duration, () => { Vector3 p = target.localPosition; target.localPosition = new Vector3(p.x, endValue, p.z); });

        public static Core.TweenerCore<Vector3, Vector3, Plugins.Options.VectorOptions> DOScale(this Transform target, Vector3 endValue, float duration) =>
            Landing(target, duration, () => target.localScale = endValue);

        public static Core.TweenerCore<Vector3, Vector3, Plugins.Options.VectorOptions> DOScale(this Transform target, float endValue, float duration) =>
            Landing(target, duration, () => target.localScale = new Vector3(endValue, endValue, endValue));

        private static Core.TweenerCore<Vector3, Vector3, Plugins.Options.VectorOptions> Landing(Transform target, float duration, Action apply)
        {
            var t = TweenFactory.Make<Core.TweenerCore<Vector3, Vector3, Plugins.Options.VectorOptions>>(duration, target);
            t.applyEnd = () =>
            {
                if (!UnityEngine.Object.IsNull(target))
                {
                    apply();
                }
            };
            return t;
        }
    }

    /// <summary>What a coroutine yields to wait for a tween (Tween.WaitForCompletion).</summary>
    public sealed class TweenYield
    {
        internal readonly Tween tween;

        public TweenYield(Tween tween)
        {
            this.tween = tween;
        }
    }

    [ShimLenient]
    public partial class DOTween
    {
        public static Ease defaultEaseType = Ease.OutQuad;

        public static Sequence Sequence() => TweenFactory.Make<Sequence>(0f, null);

        public static int Kill(object targetOrId, bool complete = false) => KillWhere(t => Matches(t, targetOrId), complete);

        public static int Kill(object target, object id, bool complete = false) =>
            KillWhere(t => Matches(t, target) && Matches(t, id), complete);

        public static int Complete(object targetOrId, bool withCallbacks = false)
        {
            var hits = UnityRuntime.LiveTweens.Where(t => Matches(t, targetOrId) && !t.inSequence).ToList();
            foreach (Tween t in hits)
            {
                t.Finish();
            }
            return hits.Count;
        }

        private static bool Matches(Tween t, object targetOrId) =>
            ReferenceEquals(t.target, targetOrId) || (targetOrId is string s && t.stringId == s) || Equals(t.target, targetOrId);

        private static int KillWhere(Func<Tween, bool> predicate, bool complete)
        {
            var hits = UnityRuntime.LiveTweens.Where(t => !t.inSequence && predicate(t)).ToList();
            foreach (Tween t in hits)
            {
                if (complete)
                {
                    t.Finish();
                }
                t.Kill();
            }
            return hits.Count;
        }
    }

    [ShimLenient]
    public static partial class TweenSettingsExtensions
    {
        public static T OnComplete<T>(this T t, TweenCallback action) where T : Tween
        {
            t.onComplete = action;
            return t;
        }

        public static T OnKill<T>(this T t, TweenCallback action) where T : Tween
        {
            t.onKill = action;
            return t;
        }

        public static T OnStart<T>(this T t, TweenCallback action) where T : Tween
        {
            t.onStart = action;
            return t;
        }

        public static T OnPlay<T>(this T t, TweenCallback action) where T : Tween
        {
            t.onPlay = action;
            return t;
        }

        public static T OnUpdate<T>(this T t, TweenCallback action) where T : Tween
        {
            t.onUpdate = action;
            return t;
        }

        public static T OnStepComplete<T>(this T t, TweenCallback action) where T : Tween
        {
            t.onStepComplete = action;
            return t;
        }

        public static T OnRewind<T>(this T t, TweenCallback action) where T : Tween
        {
            t.onRewind = action;
            return t;
        }

        public static T SetDelay<T>(this T t, float delay) where T : Tween
        {
            t.delay = delay;
            return t;
        }

        public static T SetLoops<T>(this T t, int loops, LoopType loopType) where T : Tween
        {
            t.loops = loops;
            return t;
        }

        public static T SetAutoKill<T>(this T t, bool autoKillOnCompletion) where T : Tween
        {
            t.autoKill = autoKillOnCompletion;
            return t;
        }

        public static T SetId<T>(this T t, string stringId) where T : Tween
        {
            t.stringId = stringId;
            return t;
        }

        public static T SetTarget<T>(this T t, object target) where T : Tween
        {
            t.target = target;
            return t;
        }

        public static Sequence Append(this Sequence s, Tween t)
        {
            UnityRuntime.RemoveTween(t);
            s.AppendTween(t);
            return s;
        }

        public static Sequence Join(this Sequence s, Tween t)
        {
            UnityRuntime.RemoveTween(t);
            s.JoinTween(t);
            return s;
        }

        public static Sequence Insert(this Sequence s, float atPosition, Tween t)
        {
            UnityRuntime.RemoveTween(t);
            s.Insert(atPosition, t);
            return s;
        }

        public static Sequence AppendCallback(this Sequence s, TweenCallback callback)
        {
            s.AppendCallbackAtEnd(callback);
            return s;
        }

        public static Sequence InsertCallback(this Sequence s, float atPosition, TweenCallback callback)
        {
            s.AppendCallbackAt(atPosition, callback);
            return s;
        }

        public static Sequence AppendInterval(this Sequence s, float interval)
        {
            s.AppendIntervalAtEnd(interval);
            return s;
        }
    }

    [ShimLenient]
    public static partial class TweenExtensions
    {
        public static void Kill(this Tween t, bool complete = false)
        {
            if (t == null)
            {
                return;
            }
            if (complete)
            {
                t.Finish();
            }
            t.Kill();
        }

        public static bool IsActive(this Tween t) => t != null && !t.killed;

        public static bool IsComplete(this Tween t) => t != null && t.completed;

        public static bool IsPlaying(this Tween t) => t != null && !t.killed && !t.completed && !t.paused;

        public static bool IsInitialized(this Tween t) => t != null && t.started;

        public static float Duration(this Tween t, bool includeLoops = true) => t.duration;

        public static T Pause<T>(this T t) where T : Tween
        {
            if (t != null)
            {
                t.paused = true;
            }
            return t;
        }

        public static T Play<T>(this T t) where T : Tween
        {
            if (t != null)
            {
                t.paused = false;
            }
            return t;
        }

        public static TweenYield WaitForCompletion(this Tween t) => new TweenYield(t);
    }
}
