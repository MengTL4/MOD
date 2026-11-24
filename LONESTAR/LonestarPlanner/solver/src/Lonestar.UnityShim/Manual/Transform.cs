using System.Collections;
using System.Collections.Generic;

namespace UnityEngine
{
    /// <summary>
    /// Scene hierarchy plus approximate spatial data. Rotation is stored but ignored when composing
    /// world positions; positions only matter for visuals, never for battle rules.
    /// </summary>
    public partial class Transform : Component, IEnumerable
    {
        internal Transform parentTransform;
        internal readonly List<Transform> children = new List<Transform>();

        public Vector3 localPosition { get; set; }

        public Vector3 localScale { get; set; } = Vector3.one;

        public Quaternion localRotation { get; set; } = Quaternion.identity;

        public Vector3 localEulerAngles
        {
            get => localRotation.eulerAngles;
            set => localRotation = Quaternion.Euler(value);
        }

        public Quaternion rotation
        {
            get => localRotation;
            set => localRotation = value;
        }

        public Vector3 eulerAngles
        {
            get => rotation.eulerAngles;
            set => rotation = Quaternion.Euler(value);
        }

        public Transform parent
        {
            get => parentTransform;
            set => SetParent(value, true);
        }

        public int childCount => children.Count;

        public Vector3 lossyScale => parentTransform is null ? localScale : Vector3.Scale(parentTransform.lossyScale, localScale);

        public Vector3 position
        {
            get => parentTransform is null ? localPosition : parentTransform.TransformPoint(localPosition);
            set => localPosition = parentTransform is null ? value : parentTransform.InverseTransformPoint(value);
        }

        public Vector3 TransformPoint(Vector3 local) => position + Vector3.Scale(lossyScale, local);

        public Vector3 InverseTransformPoint(Vector3 world)
        {
            Vector3 s = lossyScale;
            Vector3 d = world - position;
            return new Vector3(s.x == 0 ? 0 : d.x / s.x, s.y == 0 ? 0 : d.y / s.y, s.z == 0 ? 0 : d.z / s.z);
        }

        public void SetParent(Transform newParent) => SetParent(newParent, true);

        public void SetParent(Transform newParent, bool worldPositionStays)
        {
            if (ReferenceEquals(newParent, parentTransform))
            {
                return;
            }
            Vector3 world = position;
            bool wasActive = gameObject.activeInHierarchy;
            parentTransform?.children.Remove(this);
            parentTransform = newParent;
            newParent?.children.Add(this);
            if (worldPositionStays)
            {
                position = world;
            }
            UnityRuntime.OnActiveChanged(gameObject, wasActive);
        }

        public Transform GetChild(int index) => children[index];

        public Transform Find(string path)
        {
            Transform current = this;
            foreach (string part in path.Split('/'))
            {
                Transform next = null;
                foreach (Transform c in current.children)
                {
                    if (c.gameObject.name == part)
                    {
                        next = c;
                        break;
                    }
                }
                if (next is null)
                {
                    return null;
                }
                current = next;
            }
            return current;
        }

        public int GetSiblingIndex() => parentTransform?.children.IndexOf(this) ?? 0;

        public void SetSiblingIndex(int index)
        {
            if (parentTransform is null)
            {
                return;
            }
            var list = parentTransform.children;
            list.Remove(this);
            list.Insert(Mathf.Clamp(index, 0, list.Count), this);
        }

        public void SetAsFirstSibling() => SetSiblingIndex(0);

        public void SetAsLastSibling() => SetSiblingIndex(int.MaxValue);

        public bool IsChildOf(Transform other)
        {
            for (Transform t = this; t is not null; t = t.parentTransform)
            {
                if (ReferenceEquals(t, other))
                {
                    return true;
                }
            }
            return false;
        }

        public void LookAt(Vector3 worldPosition, Vector3 worldUp)
        {
        }

        public void Rotate(Vector3 axis, float angle)
        {
        }

        public IEnumerator GetEnumerator() => children.ToArray().GetEnumerator();
    }
}
