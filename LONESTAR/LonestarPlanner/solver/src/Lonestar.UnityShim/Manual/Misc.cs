using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public partial class Debug
    {
        /// <summary>Optional sink for Debug.Log output (null = discard).</summary>
        public static Action<string> LogSink;

        public static void Log(object message) => LogSink?.Invoke("[log] " + message);

        public static void LogWarning(object message) => LogSink?.Invoke("[warn] " + message);

        public static void LogWarning(object message, Object context) => LogWarning(message);

        /// <summary>The game logs errors for states it considers broken; the engine treats them as failures.</summary>
        public static void LogError(object message)
        {
            UnityRuntime.AddError("Debug.LogError: " + message);
            LogSink?.Invoke("[error] " + message);
        }

        public static void LogError(object message, Object context) => LogError(message);
    }

    public partial class Time
    {
        public static float deltaTime => UnityRuntime.FrameDelta * UnityRuntime.TimeScale;

        public static float unscaledDeltaTime => UnityRuntime.FrameDelta;

        public static float time => UnityRuntime.Time;

        public static float unscaledTime => UnityRuntime.UnscaledTime;

        public static int frameCount => UnityRuntime.FrameCount;

        public static float timeScale
        {
            get => UnityRuntime.TimeScale;
            set => UnityRuntime.TimeScale = value;
        }
    }

    public partial class Application
    {
        public static string PersistentDataPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LonestarHeadless");

        public static string persistentDataPath => PersistentDataPath;

        public static bool isPlaying => true;

        public static bool isFocused => true;

        public static int targetFrameRate { get; set; } = 60;

        public static SystemLanguage systemLanguage => SystemLanguage.English;

        public static void Quit()
        {
        }

        public static void OpenURL(string url)
        {
        }
    }
}

namespace UnityEngine.Events
{
    public partial class UnityEventBase
    {
        public void RemoveAllListeners() => ClearListeners();

        internal virtual void ClearListeners()
        {
        }
    }

    public partial class UnityEvent : UnityEventBase
    {
        private readonly List<UnityAction> listeners = new List<UnityAction>();

        public void AddListener(UnityAction call) => listeners.Add(call);

        public void RemoveListener(UnityAction call) => listeners.Remove(call);

        internal override void ClearListeners() => listeners.Clear();

        public void Invoke()
        {
            foreach (UnityAction a in listeners.ToArray())
            {
                a();
            }
        }
    }

    public partial class UnityEvent<T0> : UnityEventBase
    {
        private readonly List<UnityAction<T0>> listeners = new List<UnityAction<T0>>();

        public void AddListener(UnityAction<T0> call) => listeners.Add(call);

        public void RemoveListener(UnityAction<T0> call) => listeners.Remove(call);

        internal override void ClearListeners() => listeners.Clear();

        public void Invoke(T0 a0)
        {
            foreach (UnityAction<T0> a in listeners.ToArray())
            {
                a(a0);
            }
        }
    }

    public partial class UnityEvent<T0, T1> : UnityEventBase
    {
        private readonly List<UnityAction<T0, T1>> listeners = new List<UnityAction<T0, T1>>();

        public void AddListener(UnityAction<T0, T1> call) => listeners.Add(call);

        public void RemoveListener(UnityAction<T0, T1> call) => listeners.Remove(call);

        public void Invoke(T0 a0, T1 a1)
        {
            foreach (UnityAction<T0, T1> a in listeners.ToArray())
            {
                a(a0, a1);
            }
        }
    }

    public partial class UnityEvent<T0, T1, T2, T3> : UnityEventBase
    {
        private readonly List<UnityAction<T0, T1, T2, T3>> listeners = new List<UnityAction<T0, T1, T2, T3>>();

        public void AddListener(UnityAction<T0, T1, T2, T3> call) => listeners.Add(call);

        public void RemoveListener(UnityAction<T0, T1, T2, T3> call) => listeners.Remove(call);

        public void Invoke(T0 a0, T1 a1, T2 a2, T3 a3)
        {
            foreach (UnityAction<T0, T1, T2, T3> a in listeners.ToArray())
            {
                a(a0, a1, a2, a3);
            }
        }
    }
}

namespace UnityEngine.Networking
{
    [ShimLenient]
    public partial class UnityWebRequest : System.IDisposable
    {
        public void Dispose()
        {
        }
    }
}
