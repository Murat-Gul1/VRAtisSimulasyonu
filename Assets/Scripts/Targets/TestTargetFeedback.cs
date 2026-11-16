using UnityEngine;
using VRAtis.Ballistics;

namespace VRAtis.Targets
{
    /// <summary>
    /// Zero-allocation visual feedback on test targets: uses MaterialPropertyBlock for flash
    /// and a pre-allocated ring buffer of hit markers so rapid firing never causes GC spikes.
    /// </summary>
    public class TestTargetFeedback : MonoBehaviour, IBulletImpactTarget
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        [SerializeField] private Color hitFlashColor = new Color(1f, 0.25f, 0.1f, 1f);
        [SerializeField] private float flashDurationSec = 0.15f;
        [SerializeField] private int maxHitMarkers = 24;

        private Renderer _renderer;
        private MaterialPropertyBlock _propBlock;
        private Color _originalColor = Color.white;
        private float _flashTimer;

        private Transform[] _markerRingBuffer;
        private int _nextMarkerIndex;
        private static Material _sharedMarkerMaterial;

        private void Awake()
        {
            _renderer = GetComponent<Renderer>();
            _propBlock = new MaterialPropertyBlock();

            if (_renderer != null && _renderer.sharedMaterial != null)
            {
                if (_renderer.sharedMaterial.HasProperty(BaseColorId))
                    _originalColor = _renderer.sharedMaterial.GetColor(BaseColorId);
                else if (_renderer.sharedMaterial.HasProperty(ColorId))
                    _originalColor = _renderer.sharedMaterial.GetColor(ColorId);
            }

            PrewarmMarkers();
        }

        private void PrewarmMarkers()
        {
            _markerRingBuffer = new Transform[Mathf.Max(1, maxHitMarkers)];
            Material mat = GetMarkerMaterial();

            for (int i = 0; i < _markerRingBuffer.Length; i++)
            {
                GameObject mark = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                mark.name = $"HitMark_{i}";
                Collider col = mark.GetComponent<Collider>();
                if (col != null) Destroy(col);

                mark.transform.SetParent(transform, true);
                mark.transform.localScale = Vector3.one * 0.06f;
                if (mat != null)
                    mark.GetComponent<MeshRenderer>().sharedMaterial = mat;

                mark.SetActive(false);
                _markerRingBuffer[i] = mark.transform;
            }
        }

        private void Update()
        {
            if (_flashTimer > 0f && _renderer != null)
            {
                _flashTimer -= Time.deltaTime;
                if (_flashTimer <= 0f)
                {
                    ApplyColor(_originalColor);
                }
            }
        }

        public void OnBulletImpact(in BulletImpactInfo impact)
        {
            if (_renderer != null)
            {
                ApplyColor(hitFlashColor);
                _flashTimer = flashDurationSec;
            }

            if (_markerRingBuffer != null && _markerRingBuffer.Length > 0)
            {
                Transform mark = _markerRingBuffer[_nextMarkerIndex];
                _nextMarkerIndex = (_nextMarkerIndex + 1) % _markerRingBuffer.Length;

                mark.position = impact.HitPoint + impact.HitNormal * 0.01f;
                mark.gameObject.SetActive(true);
            }
        }

        private void ApplyColor(Color c)
        {
            _renderer.GetPropertyBlock(_propBlock);
            _propBlock.SetColor(BaseColorId, c);
            _propBlock.SetColor(ColorId, c);
            _renderer.SetPropertyBlock(_propBlock);
        }

        private static Material GetMarkerMaterial()
        {
            if (_sharedMarkerMaterial != null)
                return _sharedMarkerMaterial;

            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) return null;

            _sharedMarkerMaterial = new Material(shader);
            _sharedMarkerMaterial.color = Color.red;
            return _sharedMarkerMaterial;
        }
    }
}
