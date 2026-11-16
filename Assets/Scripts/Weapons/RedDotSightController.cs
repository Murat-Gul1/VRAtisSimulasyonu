using UnityEngine;

namespace VRAtis.Weapons
{
    /// <summary>
    /// Keeps the parallax Red Dot collimator shader's local forward/up axes synced
    /// with the rifle's aim transform regardless of imported mesh axis orientation.
    /// </summary>
    [ExecuteAlways]
    public class RedDotSightController : MonoBehaviour
    {
        private static readonly int ForwardAxisId = Shader.PropertyToID("_ForwardAxis");
        private static readonly int UpAxisId = Shader.PropertyToID("_UpAxis");
        private static readonly int DotColorId = Shader.PropertyToID("_DotColor");
        private static readonly int RingEnableId = Shader.PropertyToID("_RingEnable");

        [SerializeField] private Transform rifleForwardReference;
        [ColorUsage(true, true)]
        [SerializeField] private Color dotColor = new Color(8f, 0.25f, 0.15f, 1f);
        [SerializeField] private bool showOuterRing;

        private Renderer _renderer;
        private MaterialPropertyBlock _propBlock;

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
            _renderer.SetPropertyBlock(_propBlock);
        }
    }
}
