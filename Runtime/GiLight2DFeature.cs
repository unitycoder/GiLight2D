using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Random = UnityEngine.Random;

namespace GiLight2D
{
    public partial class GiLight2DFeature : ScriptableRendererFeature
    {
        private const string k_BlitShader  = "Hidden/GiLight2D/Blit";
        private const string k_JfaShader   = "Hidden/GiLight2D/JumpFlood";
        private const string k_GiShader    = "Hidden/GiLight2D/Gi";
        private const string k_BlurShader  = "Hidden/GiLight2D/Blur";
        private const string k_DistShader  = "Hidden/GiLight2D/Distance";
		
        private static readonly int s_MainTexId         = Shader.PropertyToID("_MainTex");
        private static readonly int s_NoiseOffsetId     = Shader.PropertyToID("_NoiseOffset");
        private static readonly int s_NoiseTexId        = Shader.PropertyToID("_NoiseTex");
        private static readonly int s_UvScaleId         = Shader.PropertyToID("_UvScale");
        private static readonly int s_FalloffId         = Shader.PropertyToID("_Falloff");
        private static readonly int s_IntensityId       = Shader.PropertyToID("_Intensity");
        private static readonly int s_IntensityBounceId = Shader.PropertyToID("_IntensityBounce");
        private static readonly int s_SamplesId         = Shader.PropertyToID("_Samples");
        private static readonly int s_OffsetId          = Shader.PropertyToID("_Offset");
        private static readonly int s_ColorTexId        = Shader.PropertyToID("_ColorTex");
        private static readonly int s_DistTexId         = Shader.PropertyToID("_DistTex");
        private static readonly int s_AspectId          = Shader.PropertyToID("_Aspect");
        private static readonly int s_StepSizeId        = Shader.PropertyToID("_StepSize");
        private static readonly int s_ScaleId           = Shader.PropertyToID("_Scale");
        private static readonly int s_StepId            = Shader.PropertyToID("_Step");
        private static readonly int s_AlphaTexId        = Shader.PropertyToID("_AlphaTex");
        private static readonly int s_ATexId            = Shader.PropertyToID("_ATex");
        private static readonly int s_BTexId            = Shader.PropertyToID("_BTex");
		
        private static List<ShaderTagId> k_ShaderTags;
        private static Mesh              k_ScreenMesh;
        private static Texture2D         k_Noise;

        [SerializeField]
        private RenderPassEvent     _event = RenderPassEvent.BeforeRenderingOpaques;
        [SerializeField]
        [Tooltip("Which objects should be rendered as Gi.")]
        private LayerMask           _mask = new LayerMask() { value = -1 };
        [SerializeField]
        [Tooltip("Enable depth stencil buffer for Gi objects rendering. Allows mask interaction and z culling.")]
        private bool                _depthStencil = true;
		
        [Tooltip("How many rays to emit from each pixel.")]
        [SerializeField]
        private int                 _rays = 100;
        [SerializeField]
        public TraceOptions         _traceOptions = new TraceOptions();
        [SerializeField]
        //[Tooltip("Light falloff, ")]
        private Optional<RangeFloat> _falloff = new Optional<RangeFloat>(new RangeFloat(new Vector2(.01f, 1f), 1f), false);
        [SerializeField]
        [Tooltip("Final light intensity, basically color multiplier")]
        private Optional<RangeFloat> _intensity = new Optional<RangeFloat>(new RangeFloat(new Vector2(.0f, 3f), 1f), false);
        [SerializeField]
        [Tooltip("Distance map additional impact for raytracing.")]
        private Optional<RangeFloat> _distOffset = new Optional<RangeFloat>(new RangeFloat(new Vector2(.0f, .1f), .0f), false);
        [SerializeField]
        private NoiseOptions        _noiseOptions = new NoiseOptions();
        private Vector2Int          _noiseResolution;
        [SerializeField]
        [Tooltip("Additional orthographic camera space, to make objects visible outside of the camera frame.")]
        private Optional<RangeFloat> _border = new Optional<RangeFloat>(new RangeFloat(new Vector2(.0f, 3f), 0f), false);
        [SerializeField]
        private ScaleModeData       _scaleMode = new ScaleModeData();
		
        [SerializeField]
        private Output              _output = new Output();
        //[SerializeField]
        //[Tooltip("Texture with objects mask, can be useful for lights combination.")]
        //private Optional<string>    _solidTexture = new Optional<string>("_GiSolidTex", false);
        [SerializeField]
        private BlurOptions         _blurOptions = new BlurOptions();
        [Header("Debug")]
        [SerializeField]
        [Tooltip("Override final output for debug purposes.")]
        private DebugOutput         _outputOverride = DebugOutput.None;
		
        [SerializeField]
        [Tooltip("Run render feature in scene view project window.")]
        private bool                _runInSceneView;
		
		
        [SerializeField]
        private ShaderCollection    _shaders = new ShaderCollection();
		
        private GiPass              _giPass;
		
        private Material _blitMat;
        private Material _jfaMat;
        private Material _distMat;
        private Material _giMat;
        private Material _blurMat;
		
        private RenderTextureDescriptor _rtDesc = new RenderTextureDescriptor(0, 0, GraphicsFormat.None, 0, 0);
        private Vector2Int              _rtRes  = Vector2Int.zero;

        private bool ForceTextureOutput => _output._finalBlit == FinalBlit.Texture;
        private bool HasGiBorder        => _border.Enabled && _border.Value.Value > 0f;
        private bool CleanEdges         => _traceOptions._enable && _traceOptions._cleanEdges && _traceOptions._bounces > 0;
        //private bool HasAlphaTexture    => _traceOptions._bounces > 0 || _solidTexture.Enabled;
		
        public int   Samples   { get => _rays;               set => _rays = value; }
        public float Falloff   { get => _falloff.Value.Value;   set => _falloff.Value.Value = value; }
        public float Intensity { get => _intensity.Value.Value; set => _intensity.Value.Value = value; }
        public float Scale     { get => _scaleMode._ratio;      set => _scaleMode._ratio = value; }
        public NoiseMode Noise
        {
            get => _noiseOptions._noiseMode;
            set
            {
                if (_noiseOptions._noiseMode == value)
                    return;

                _setNoiseState(value);
            }
        }

        public float Border
        {
            get => _border.Enabled ? _border.Value.Value : 0f;
            set
            {
                _border.Enabled     = value > 0f;
                _border.Value.Value = value;
            }
        }

        public float NoiseScale
        {
            get => _noiseOptions._noiseScale;
            set
            {
                _noiseOptions._noiseScale = value;
				
                _initNoise();
            }
        }

        // =======================================================================
        public class RenderTarget
        {
            public RTHandle Handle;
            public int      Id;
			
            private bool    _allocated;
            
            // =======================================================================
            public RenderTarget Allocate(RenderTexture rt, string name)
            {
                Handle = RTHandles.Alloc(rt, name);
                Id     = Shader.PropertyToID(name);
				
                return this;
            }
			
            public RenderTarget Allocate(string name)
            {
                Handle = _alloc(name);
                Id     = Shader.PropertyToID(name);
				
                return this;
            }
			
            public void Get(CommandBuffer cmd, in RenderTextureDescriptor desc)
            {
                _allocated = true;
                cmd.GetTemporaryRT(Id, desc);
            }
			
            public void Release(CommandBuffer cmd)
            {
                if (_allocated == false)
                    return;
                
                _allocated = false;
                cmd.ReleaseTemporaryRT(Id);
            }
        }

        public class RenderTargetFlip
        {
            public RenderTarget From => _isFlipped ? _a : _b;
            public RenderTarget To   => _isFlipped ? _b : _a;
			
            private bool         _isFlipped;
            private RenderTarget _a;
            private RenderTarget _b;
			
            // =======================================================================
            public RenderTargetFlip(RenderTarget a, RenderTarget b)
            {
                _a = a;
                _b = b;
            }
			
            public void Flip()
            {
                _isFlipped = !_isFlipped;
            }
			
            public void Release(CommandBuffer cmd)
            {
                _a.Release(cmd);
                _b.Release(cmd);
            }

            public void Get(CommandBuffer cmd, in RenderTextureDescriptor desc)
            {
                _a.Get(cmd, desc);
                _b.Get(cmd, desc);
            }
        }
        
        public class RenderTargetPostProcess
        {
            private RenderTargetFlip _flip;
            private RTHandle         _result;
            private Material         _giMat;
            private int              _passes;
            private int              _passesLeft;
			
            // =======================================================================
            public RenderTargetPostProcess(RenderTarget a, RenderTarget b)
            {
                _flip = new RenderTargetFlip(a, b);
            }

            public void Setup(CommandBuffer cmd, in RenderTextureDescriptor desc, RTHandle output, int passes, Material giMat)
            {
                _passes = passes;
                _passesLeft = passes;
                _result = output;
                _giMat = giMat;
                
                if (passes > 0)
                {
                    // draw gi to the tmp flip texture
                    _flip.Get(cmd, in desc);
                    
                    cmd.SetRenderTarget(_flip.To.Handle.nameID);
                    cmd.DrawMesh(k_ScreenMesh, Matrix4x4.identity, _giMat, 0, 0);
                }
                else
                {
                    // no post process added, draw gi to the output
                    cmd.SetRenderTarget(_result.nameID);
                    cmd.DrawMesh(k_ScreenMesh, Matrix4x4.identity, _giMat, 0, 0);
                }
            }

            public void Apply(CommandBuffer cmd, Material mat, int pass = 0)
            {
                // draw in output or tmp render target
                _flip.Flip();
                _passesLeft --;
                
                cmd.SetGlobalTexture(s_MainTexId, _flip.From.Handle.nameID);
                cmd.SetRenderTarget(_passesLeft > 0 ? _flip.To.Handle.nameID : _result.nameID);
                cmd.DrawMesh(k_ScreenMesh, Matrix4x4.identity, mat, 0, pass);
            }
            
            public void Release(CommandBuffer cmd)
            {
                if (_passes > 0)
                {
                    _flip.Release(cmd);
                }
            }
        }

        [Serializable]
        public class BlurOptions
        {
            public bool     _enable;
            public BlurMode _mode = BlurMode.Cross;
            [Tooltip("If disabled step will be set to one pixel")]
            public Optional<RangeFloat> _step = new Optional<RangeFloat>(new RangeFloat(new Vector2(0f, 0.01f), 0.003f), true);
        }
        
        [Serializable]
        public class ScaleModeData
        {
            public ScaleMode _scaleMode;
            public float     _ratio  = 1f;
            public int       _height = 240;
        }

        [Serializable]
        public class Output
        {
            [Tooltip("Where to store Gi result. If the final result is a camera, then could be applied a post processing.")]
            public FinalBlit _finalBlit = FinalBlit.Camera;
            [Tooltip("Global name of output texture.")]
            public string _outputGlobalTexture = "_GiTex";
            /*[Tooltip("Base objects texture with alpha mask")]
            public Optional<string> _outputBufferTexture = new Optional<string>("_GiBufferTex", false);*/
        }
		
        [Serializable]
        public class NoiseOptions
        {
            public NoiseMode _noiseMode = NoiseMode.Shader;
            [Range(0.01f, 1f)]
            public float _noiseScale = 1f;
        }
        
        [Serializable]
        public class TraceOptions
        {
            public bool  _enable = true;
            [Tooltip("Override edges with initial color")]
            public bool  _cleanEdges;
            [Range(0, 3)]
            public int   _bounces = 1;
            public float _intencity = 1f;
        }

        [Serializable]
        public class ShaderCollection
        {
            public Shader _blit;
            public Shader _jfa;
            public Shader _gi;
            public Shader _dist;
            public Shader _blur;
        }
		
        public enum ScaleMode
        {
            None,
            Scale,
            Fixed,
        }
		
        public enum NoiseMode
        {
            Dynamic = 0,
            Static  = 1,
            Shader  = 2,
            None    = 3
        }

        public enum DebugOutput
        {
            None,
            Objects,
            Flood,
            Distance
        }

        public enum FinalBlit
        {
            Texture,
            Camera
        }

        public enum BlurMode
        {
            Horizontal,
            Vertial,
            Cross,
            Box
        }
        
        // =======================================================================
        public override void Create()
        {
            _giPass = new GiPass() { _owner = this };
            _giPass.Init();

            _validateShaders();
			
            _initMaterials();

            _setNoiseState(_noiseOptions._noiseMode);
			
            if (k_ScreenMesh == null)
            {
                // init triangle
                k_ScreenMesh = new Mesh();
                _initScreenMesh(k_ScreenMesh, Matrix4x4.identity);
            }
			
            if (k_ShaderTags == null)
            {
                k_ShaderTags = new List<ShaderTagId>(new[]
                {
                    new ShaderTagId("SRPDefaultUnlit"),
                    new ShaderTagId("UniversalForward"),
                    new ShaderTagId("UniversalForwardOnly")
                });
            }
			
            // init noise
            _initNoise();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_runInSceneView)
            {
                // in game or scene view
                if (renderingData.cameraData.cameraType != CameraType.Game
                    && renderingData.cameraData.cameraType != CameraType.SceneView)
                    return;
            }
            else
            {
                // in game view only
                if (renderingData.cameraData.cameraType != CameraType.Game)
                    return;
            }
			
            _setupDesc(in renderingData);

            renderer.EnqueuePass(_giPass);
        }

        // =======================================================================
        private void _initMaterials()
        {
            _jfaMat   = new Material(_shaders._jfa);
            _blitMat  = new Material(_shaders._blit);
            
            _blurMat = new Material(_shaders._blur);
            switch (_blurOptions._mode)
            {
                case BlurMode.Horizontal:
                    _blurMat.EnableKeyword("HORIZONTAL");
                    break;
                case BlurMode.Vertial:
                    _blurMat.EnableKeyword("VERTICAL");
                    break;
                case BlurMode.Cross:
                    _blurMat.EnableKeyword("CROSS");
                    break;
                case BlurMode.Box:
                    _blurMat.EnableKeyword("BOX");
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            
            _distMat  = new Material(_shaders._dist);
            if (_distOffset.Enabled)
            {
                _distMat.EnableKeyword("ENABLE_OFFSET");
                _distMat.SetFloat(s_OffsetId, _distOffset.Value.Value);
            }
                
            _giMat    = new Material(_shaders._gi);
            if (_falloff.enabled)
                _giMat.EnableKeyword("FALLOFF_IMPACT");
            if (_intensity.enabled)
                _giMat.EnableKeyword("INTENSITY_IMPACT");
        }
        
        private void _validateShaders()
        {
#if UNITY_EDITOR
            _validate(ref _shaders._blit,	k_BlitShader);
            _validate(ref _shaders._jfa,	k_JfaShader);
            _validate(ref _shaders._gi,		k_GiShader);
            _validate(ref _shaders._blur,	k_BlurShader);
            _validate(ref _shaders._dist,	k_DistShader);
			
            UnityEditor.EditorUtility.SetDirty(this);
            // -----------------------------------------------------------------------
            void _validate(ref Shader shader, string path)
            {
                if (shader != null)
                    return;
				
                shader = Shader.Find(path);
            }
#endif
        }
		
        private void _setupDesc(in RenderingData renderingData)
        {
            var camDesc = renderingData.cameraData.cameraTargetDescriptor;
            _rtRes = _scaleMode._scaleMode switch
            {
                ScaleMode.None => new Vector2Int(camDesc.width, camDesc.height),
			     
                ScaleMode.Scale => new Vector2Int(
                    Mathf.FloorToInt(camDesc.width * _scaleMode._ratio),
                    Mathf.FloorToInt(camDesc.height * _scaleMode._ratio)
                ),

                ScaleMode.Fixed => new Vector2Int(
                    Mathf.FloorToInt((camDesc.width / (float)camDesc.height) * _scaleMode._height),
                    _scaleMode._height
                ),

                _ => throw new ArgumentOutOfRangeException()
            };
			
            _rtDesc.width  = _rtRes.x;
            _rtDesc.height = _rtRes.y;
			
            var ortho   = renderingData.cameraData.camera.orthographicSize;
            var uvScale = _border.Enabled ? (ortho + _border.Value.Value) / ortho : 1f;
            
            // increase resolution for border padding
            if (_border.Enabled)
            {
                var scaleInc = uvScale - 1f;
                _rtDesc.width  += Mathf.FloorToInt(_rtDesc.width * scaleInc);
                _rtDesc.height += Mathf.FloorToInt(_rtDesc.height * scaleInc);;
            }
			
            _giMat.SetVector(s_ScaleId, new Vector4(uvScale, uvScale, 1f, 1f));
            //_alphaMat.SetFloat(s_UvScaleId, 1f / uvScale);
        }
		
        private void _initNoise()
        {
            // try block to fix editor startup error
            try
            {
                var width  = Mathf.CeilToInt(Screen.width * _noiseOptions._noiseScale);
                var height = Mathf.CeilToInt(Screen.height * _noiseOptions._noiseScale);
				
                if (k_Noise != null && width == k_Noise.width && height == k_Noise.height)
                    return;
				
                _noiseResolution.x = width;
                _noiseResolution.y = height;
				
                k_Noise = new Texture2D(width, height, GraphicsFormat.R8_UNorm, 0);
                //k_Noise          = new Texture2D(width, height, GraphicsFormat.R8G8B8A8_UNorm);
                k_Noise.wrapMode   = TextureWrapMode.Repeat;
                k_Noise.filterMode = FilterMode.Bilinear;

                var pixels = width * height;
                var data   = new byte[pixels];
                for (var n = 0; n < pixels; n++)
                    data[n] = (byte)(Random.Range(byte.MinValue, byte.MaxValue));

                k_Noise.SetPixelData(data, 0);
                k_Noise.Apply(false, true);
            }
            catch
            {
                k_Noise = null;
            }
        }
		
        private void _setNoiseState(NoiseMode value)
        {
            // hardcoded state machine
            switch (_noiseOptions._noiseMode)
            {
                case NoiseMode.Dynamic:
                case NoiseMode.Static:
                    _giMat.DisableKeyword("TEXTURE_RANDOM");
                    break;
                case NoiseMode.Shader:
                    _giMat.DisableKeyword("FRAGMENT_RANDOM");
                    break;
                case NoiseMode.None:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            _noiseOptions._noiseMode = value;

            switch (_noiseOptions._noiseMode)
            {
                case NoiseMode.Dynamic:
                case NoiseMode.Static:
                    _giMat.EnableKeyword("TEXTURE_RANDOM");
                    break;
                case NoiseMode.Shader:
                    _giMat.EnableKeyword("FRAGMENT_RANDOM");
                    break;
                case NoiseMode.None:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
		
        private static void _initScreenMesh(Mesh mesh, Matrix4x4 mat)
        {
            mesh.vertices  = _verts(0f);
            mesh.uv        = _texCoords();
            mesh.triangles = new int[3] { 0, 1, 2 };

            mesh.UploadMeshData(true);

            // -----------------------------------------------------------------------
            Vector3[] _verts(float z)
            {
                var r = new Vector3[3];
                for (var i = 0; i < 3; i++)
                {
                    var uv = new Vector2((i << 1) & 2, i & 2);
                    r[i] = mat.MultiplyPoint(new Vector3(uv.x * 2f - 1f, uv.y * 2f - 1f, z));
                }

                return r;
            }

            Vector2[] _texCoords()
            {
                var r = new Vector2[3];
                for (var i = 0; i < 3; i++)
                {
                    if (SystemInfo.graphicsUVStartsAtTop)
                        r[i] = new Vector2((i << 1) & 2, 1.0f - (i & 2));
                    else
                        r[i] = new Vector2((i << 1) & 2, i & 2);
                }

                return r;
            }
        }
		
        private static void _blit(CommandBuffer cmd, RTHandle from, RTHandle to, Material mat, int pass = 0)
        {
            cmd.SetGlobalTexture(s_MainTexId, from.nameID);
            cmd.SetRenderTarget(to.nameID);
            cmd.DrawMesh(k_ScreenMesh, Matrix4x4.identity, mat, 0, pass);
        }

        private static RTHandle _alloc(string id)
        {
            return RTHandles.Alloc(id, name: id);
        }
    }
}