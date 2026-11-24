using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace UnityEngine
{
    public partial class Object
    {
        private static int nextInstanceId;

        private readonly int instanceId = ++nextInstanceId;

        private string objectName = "";

        /// <summary>Set once the object is destroyed; from then on it compares equal to null, as in Unity.</summary>
        internal bool destroyed;

        public string name
        {
            get => GetObjectName();
            set => SetObjectName(value);
        }

        protected virtual string GetObjectName() => objectName;

        protected virtual void SetObjectName(string value) => objectName = value ?? "";

        public int GetInstanceID() => instanceId;

        internal static bool IsNull(Object o) => ReferenceEquals(o, null) || o.destroyed;

        public static bool operator ==(Object a, Object b)
        {
            bool aNull = IsNull(a);
            bool bNull = IsNull(b);
            if (aNull || bNull)
            {
                return aNull && bNull;
            }
            return ReferenceEquals(a, b);
        }

        public static bool operator !=(Object a, Object b) => !(a == b);

        public static implicit operator bool(Object exists) => !IsNull(exists);

        public override bool Equals(object other) => other is Object o ? this == o : other == null && destroyed;

        public override int GetHashCode() => instanceId;

        public override string ToString() => $"{name} ({GetType().Name})";

        public static void Destroy(Object obj) => Destroy(obj, 0f);

        public static void Destroy(Object obj, float t) => UnityRuntime.ScheduleDestroy(obj, t);

        public static void DestroyImmediate(Object obj) => UnityRuntime.DestroyNow(obj);

        public static void DontDestroyOnLoad(Object target)
        {
        }

        public static T FindObjectOfType<T>() where T : Object => UnityRuntime.FindObjects<T>(includeInactive: false).FirstOrDefault();

        public static T[] FindObjectsOfType<T>(bool includeInactive) where T : Object => UnityRuntime.FindObjects<T>(includeInactive).ToArray();

        public static T Instantiate<T>(T original) where T : Object => (T)UnityRuntime.Instantiate(original, null);

        public static T Instantiate<T>(T original, Transform parent) where T : Object => (T)UnityRuntime.Instantiate(original, parent);

        public static Object Instantiate(Object original) => UnityRuntime.Instantiate(original, null);

        public static Object Instantiate(Object original, Transform parent) => UnityRuntime.Instantiate(original, parent);
    }

    public sealed partial class GameObject : Object
    {
        internal readonly List<Component> components = new List<Component>();

        private bool active = true;

        public string tag { get; set; } = "Untagged";

        public int layer { get; set; }

        public GameObject()
            : this("New Game Object")
        {
        }

        public GameObject(string name)
            : this(name, inScene: true, rectTransform: false)
        {
        }

        /// <summary>
        /// <paramref name="inScene"/> = false builds a prefab template: invisible to Find* and never
        /// awakened; only its Instantiate clones join the scene.
        /// </summary>
        internal GameObject(string name, bool inScene, bool rectTransform)
        {
            this.name = name;
            IsTemplate = !inScene;
            Transform t = rectTransform ? (Transform)Activator.CreateInstance(typeof(RectTransform), nonPublic: true) : new Transform();
            t.owner = this;
            components.Add(t);
            if (inScene)
            {
                UnityRuntime.Register(this);
            }
        }

        internal bool IsTemplate { get; }

        public GameObject(string name, params Type[] components)
            : this(name)
        {
            foreach (Type t in components)
            {
                AddComponent(t);
            }
        }

        public Transform transform => (Transform)components[0];

        public GameObject gameObject => this;

        public bool activeSelf => active;

        public bool activeInHierarchy => active && (transform.parent is null || transform.parent.gameObject.activeInHierarchy);

        public void SetActive(bool value)
        {
            if (active == value)
            {
                return;
            }
            bool wasActive = activeInHierarchy;
            active = value;
            UnityRuntime.OnActiveChanged(this, wasActive);
        }

        /// <summary>Changes the flag without lifecycle callbacks (used while building clones).</summary>
        internal void SetActiveSilently(bool value) => active = value;

        public T AddComponent<T>() where T : Component => (T)AddComponent(typeof(T));

        public Component AddComponent(Type type) => UnityRuntime.AddComponent(this, type);

        public T GetComponent<T>()
        {
            foreach (Component c in components)
            {
                if (c is T t && !c.destroyed)
                {
                    return t;
                }
            }
            return default;
        }

        public Component GetComponent(Type type) => components.FirstOrDefault(c => type.IsInstanceOfType(c) && !c.destroyed);

        public T[] GetComponents<T>() => components.Where(c => !c.destroyed).OfType<T>().ToArray();

        public T GetComponentInChildren<T>() => GetComponentInChildren<T>(false);

        public T GetComponentInChildren<T>(bool includeInactive) => GetComponentsInChildren<T>(includeInactive).FirstOrDefault();

        public T[] GetComponentsInChildren<T>() => GetComponentsInChildren<T>(false);

        public T[] GetComponentsInChildren<T>(bool includeInactive)
        {
            var result = new List<T>();
            CollectInChildren(transform, includeInactive, result);
            return result.ToArray();
        }

        internal static void CollectInChildren<T>(Transform t, bool includeInactive, List<T> result)
        {
            if (!includeInactive && !t.gameObject.activeInHierarchy)
            {
                return;
            }
            result.AddRange(t.gameObject.components.Where(c => !c.destroyed).OfType<T>());
            foreach (Transform child in t.children.ToList())
            {
                CollectInChildren(child, includeInactive, result);
            }
        }

        public T GetComponentInParent<T>() => GetComponentInParent<T>(false);

        public T GetComponentInParent<T>(bool includeInactive)
        {
            for (Transform t = transform; t is not null; t = t.parent)
            {
                if (!includeInactive && !t.gameObject.activeInHierarchy)
                {
                    continue;
                }
                T found = t.gameObject.GetComponent<T>();
                if (found != null)
                {
                    return found;
                }
            }
            return default;
        }

        public static GameObject Find(string name) =>
            UnityRuntime.AllGameObjects.FirstOrDefault(g => !g.destroyed && g.activeInHierarchy && g.name == name);
    }

    public partial class Component : Object
    {
        internal GameObject owner;

        public GameObject gameObject => owner ??= UnityRuntime.AdoptOrphan(this);

        public Transform transform => gameObject.transform;

        public string tag
        {
            get => gameObject.tag;
            set => gameObject.tag = value;
        }

        protected override string GetObjectName() => gameObject.name;

        protected override void SetObjectName(string value) => gameObject.name = value;

        public T GetComponent<T>() => gameObject.GetComponent<T>();

        public Component GetComponent(Type type) => gameObject.GetComponent(type);

        public T[] GetComponents<T>() => gameObject.GetComponents<T>();

        public T GetComponentInChildren<T>() => gameObject.GetComponentInChildren<T>(false);

        public T GetComponentInChildren<T>(bool includeInactive) => gameObject.GetComponentInChildren<T>(includeInactive);

        public T GetComponentInParent<T>() => gameObject.GetComponentInParent<T>(false);

        public T GetComponentInParent<T>(bool includeInactive) => gameObject.GetComponentInParent<T>(includeInactive);

        public T[] GetComponentsInChildren<T>() => gameObject.GetComponentsInChildren<T>(false);

        public T[] GetComponentsInChildren<T>(bool includeInactive) => gameObject.GetComponentsInChildren<T>(includeInactive);

        public void GetComponentsInChildren<T>(List<T> results)
        {
            results.Clear();
            GameObject.CollectInChildren(transform, false, results);
        }
    }

    public partial class Behaviour : Component
    {
        private bool isEnabled = true;

        public bool enabled
        {
            get => isEnabled;
            set
            {
                if (isEnabled == value)
                {
                    return;
                }
                isEnabled = value;
                UnityRuntime.OnBehaviourEnabledChanged(this, value);
            }
        }

        public bool isActiveAndEnabled => isEnabled && gameObject.activeInHierarchy;

        /// <summary>Changes the flag without lifecycle callbacks (used while building clones).</summary>
        internal void SetEnabledSilently(bool value) => isEnabled = value;
    }

    public partial class MonoBehaviour : Behaviour
    {
        public Coroutine StartCoroutine(IEnumerator routine) => UnityRuntime.StartCoroutine(this, routine);

        public Coroutine StartCoroutine(string methodName) => StartCoroutine(methodName, null);

        public Coroutine StartCoroutine(string methodName, object value)
        {
            var method = UnityRuntime.FindMethod(GetType(), methodName);
            object result = method?.Invoke(this, method.GetParameters().Length == 0 ? null : new[] { value });
            return result is IEnumerator e ? StartCoroutine(e) : null;
        }

        public void StopCoroutine(IEnumerator routine) => UnityRuntime.StopCoroutine(routine);

        public void StopCoroutine(Coroutine routine) => UnityRuntime.StopCoroutine(routine);

        public void StopAllCoroutines() => UnityRuntime.StopAllCoroutines(this);

        public void Invoke(string methodName, float time) => UnityRuntime.ScheduleInvoke(this, methodName, time);
    }

    public partial class ScriptableObject : Object
    {
        public static T CreateInstance<T>() where T : ScriptableObject => (T)Activator.CreateInstance(typeof(T), nonPublic: true);
    }

    public partial class TextAsset : Object
    {
        private readonly string content;

        public TextAsset()
            : this("")
        {
        }

        public TextAsset(string text)
        {
            content = text ?? "";
        }

        public string text => content;
    }

    public partial class Resources
    {
        /// <summary>Supplies assets by Resources path; installed by the engine host.</summary>
        public static Func<string, Type, Object> Provider;

        public static T Load<T>(string path) where T : Object => Provider?.Invoke(path, typeof(T)) as T;

        public static Object Load(string path, Type type) => Provider?.Invoke(path, type);

        public static ResourceRequest LoadAsync<T>(string path) where T : Object => Shim.Ret<ResourceRequest>();

        public static AsyncOperation UnloadUnusedAssets() => Shim.Ret<AsyncOperation>();
    }
}
