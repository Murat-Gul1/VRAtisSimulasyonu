using UnityEngine;

namespace VRAtis.Weapons
{
    /// <summary>
    /// Keeps the parallax Red Dot collimator shader's local forward/up axes synced
    /// with the rifle's aim transform and smoothly drives LED reticle power on/off during ADS.
    /// </summary>
    [ExecuteAlways]
    public class RedDotSightController : MonoBehaviour
    {
        private static readonly int ForwardAxisId = Shader.PropertyToID("_ForwardAxis");
        private static readonly int UpAxisId = Shader.PropertyToID("_UpAxis");
        private static readonly int DotColorId = Shader.PropertyToID("_DotColor");
        private static readonly int RingEnableId = Shader.PropertyToID("_RingEnable");
        private static readonly int ReticlePowerId = Shader.PropertyToID("_ReticlePower");

        [SerializeField] private Transform rifleForwardReference;
        [ColorUsage(true, true)]
        [SerializeField] private Color dotColor = new Color(3.6f, 0.16f, 0.08f, 1f);
        [SerializeField] private bool showOuterRing;

        [Header("Red Dot Gücü (Nişan Alma LED Kontrolü)")]
        [Tooltip("Açıkken Red Dot sürekli yanık kalır (paralaks gölgelendiricisi yalnızca nişan hattından bakınca noktayı gösterir).")]
        [SerializeField] private bool alwaysKeepReticleOn = true;

        [Range(0f, 1f)]
        [SerializeField] private float reticlePower = 1f;

        private Renderer _renderer;
        private MaterialPropertyBlock _propBlock;

        public bool AlwaysKeepReticleOn
        {
            get => alwaysKeepReticleOn;
            set
            {
                alwaysKeepReticleOn = value;
                UpdateShaderVectors();
            }
        }

        public float ReticlePower => alwaysKeepReticleOn ? 1f : reticlePower;

        private void Awake()
        {
            if (Application.isPlaying && !alwaysKeepReticleOn)
                reticlePower = 0f;
        }

        private void OnEnable()
        {
            _renderer = GetComponent<Renderer>();
            if (_propBlock == null)
                _propBlock = new MaterialPropertyBlock();
            UpdateShaderVectors();
        }

        private void LateUpdate()
        {
            UpdateShaderVectors();
        }

        public void SetReference(Transform rifleRef)
        {
            rifleForwardReference = rifleRef;
            UpdateShaderVectors();
        }

        public void SetReticlePower(float power01)
        {
            reticlePower = Mathf.Clamp01(power01);
            UpdateShaderVectors();
        }

        private void UpdateShaderVectors()
        {
            if (_renderer == null)
                _renderer = GetComponent<Renderer>();
            if (_renderer == null)
                return;

            if (_propBlock == null)
                _propBlock = new MaterialPropertyBlock();

            Transform refT = rifleForwardReference != null ? rifleForwardReference : transform;
            Vector3 localFwd = transform.InverseTransformDirection(refT.forward).normalized;
            Vector3 localUp = transform.InverseTransformDirection(refT.up).normalized;

            _renderer.GetPropertyBlock(_propBlock);
            _propBlock.SetVector(ForwardAxisId, new Vector4(localFwd.x, localFwd.y, localFwd.z, 0f));
            _propBlock.SetVector(UpAxisId, new Vector4(localUp.x, localUp.y, localUp.z, 0f));
            _propBlock.SetColor(DotColorId, dotColor);
            _propBlock.SetFloat(RingEnableId, showOuterRing ? 1f : 0f);
            _propBlock.SetFloat(ReticlePowerId, alwaysKeepReticleOn ? 1f : reticlePower);
            _renderer.SetPropertyBlock(_propBlock);
        }
    }
}
