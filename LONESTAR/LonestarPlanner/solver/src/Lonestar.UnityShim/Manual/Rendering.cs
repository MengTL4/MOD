using System;
using System.Collections.Generic;

namespace UnityEngine.Rendering
{
    /// <summary>
    /// The game's post-processing profile (scene-only, not exported) has the overrides its effects
    /// animate; this stand-in hands out one component per type on request. Without it
    /// UtilsClass.PostProcessingChromaticAberration threw in every frame of its tweens.
    /// Only visuals read these values.
    /// </summary>
    [ShimLenient]
    public partial class VolumeProfile
    {
        private readonly Dictionary<Type, VolumeComponent> components = new Dictionary<Type, VolumeComponent>();

        public bool TryGet<T>(out T component) where T : VolumeComponent
        {
            if (!components.TryGetValue(typeof(T), out VolumeComponent c))
            {
                c = Activator.CreateInstance<T>();
                components[typeof(T)] = c;
            }
            component = (T)c;
            return true;
        }
    }
}
