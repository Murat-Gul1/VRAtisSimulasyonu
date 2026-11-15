using UnityEngine;
using VRAtis.Ballistics;

namespace VRAtis.Targets
{
    /// <summary>
    /// Simple visual feedback on test targets: spawns a persistent hit marker and briefly flashes color.
    /// </summary>
    public class TestTargetFeedback : MonoBehaviour, IBulletImpactTarget
    {
        [SerializeField] private Color hitFlashColor = new Color(1f, 0.25f, 0.1f, 1f);
        [SerializeField] private float flashDurationSec = 0.18f;
        [SerializeField] private int maxHitMarkers = 30;

        private Renderer _renderer;
        private Color _originalColor = Color.white;
        private float _flashTimer;
        private int _markerCount;
        private static Material _sharedMarkerMaterial;

        private void Awake()
        {
            _renderer = GetComponent<Renderer>();
            if (_renderer != null && _renderer.material != null)
                _originalColor = _renderer.material.color;
        }

        private void Update()
        {
            if (_flashTimer > 0f && _renderer != null)
            {
                _flashTimer -= Time.deltaTime;
                if (_flashTimer <= 0f)
                    _renderer.material.color = _originalColor;
            }
        }

        public void OnBulletImpact(in BulletImpactInfo impact)
        {
            if (_renderer != null)
            {
                _renderer.material.color = hitFlashColor;
                _flashTimer = flashDurationSec;
            }

            if (_markerCount < maxHitMarkers)
            {
                _markerCount++;
                GameObject mark = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                mark.name = "BulletHitMark";
                Destroy(mark.GetComponent<Collider>());
                mark.transform.position = impact.HitPoint + impact.HitNormal * 0.01f;
                mark.transform.localScale = Vector3.one * 0.06f;
                mark.transform.SetParent(transform, true);

                Material mat = GetMarkerMaterial();
                if (mat != null)
                    mark.GetComponent<MeshRenderer>().sharedMaterial = mat;
            }
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
