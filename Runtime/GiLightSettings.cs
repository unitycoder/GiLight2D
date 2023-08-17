using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GiLight2D
{
    [Serializable, VolumeComponentMenu("NullTale/GiLight")]
    public sealed class GiLightSettings : VolumeComponent, IPostProcessComponent
    {
        public ClampedIntParameter   m_Rays       = new ClampedIntParameter(33, 1, 120);
        public ClampedFloatParameter m_Intensity  = new ClampedFloatParameter(1, 0, 3);
        public MinFloatParameter     m_Distance   = new MinFloatParameter(33, 0);
        public FloatParameter        m_Aspect     = new FloatParameter(0);
        public ClampedIntParameter   m_Steps      = new ClampedIntParameter(5, 1, 5);
        public ClampedFloatParameter m_Scale      = new ClampedFloatParameter(1, 0, 2);
        public NoiseTextureParameter m_NoiseTex   = new NoiseTextureParameter();
        public Vector2Parameter      m_NoiseVel   = new Vector2Parameter(new Vector2(0, 0));
        public ClampedFloatParameter m_NoiseScale = new ClampedFloatParameter(0, 0, 1);
        public ClampedFloatParameter m_Blur       = new ClampedFloatParameter(0, 0, 1);
        
        // =======================================================================
        [Serializable]
        public sealed class NoiseTextureParameter : VolumeParameter<NoiseTexture>
        {
        }

        public enum NoiseTexture
        {
            None,
            Random,
            LinesH,
            LinesV,
            Checker,
        }
    
        // =======================================================================
        // Can be used to skip rendering if false
        public bool IsActive() => active;

        public bool IsTileCompatible() => false;
    }
}