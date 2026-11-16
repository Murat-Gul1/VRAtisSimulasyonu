using UnityEngine;
using VRAtis.Ballistics;

namespace VRAtis.Weapons
{
    /// <summary>
    /// Handles lighting-aware muzzle flash (barely visible in daylight, moderate at dusk, bright at night)
    /// and subtle wind-reactive gunpowder smoke puffs from muzzle and ejection port.
    /// </summary>
    public class RifleVisualEffects : MonoBehaviour
    {
        private static readonly int IntensityId = Shader.PropertyToID("_Intensity");
        private static readonly int IsSideFinId = Shader.PropertyToID("_IsSideFin");

        [Header("Referans Noktaları")]
        [SerializeField] private Transform muzzleTransform;
        [SerializeField] private Transform ejectionPoint;

        [Header("Namlu Alevi (Işık Koşuluna Duyarlı)")]
        [SerializeField] private Light muzzlePointLight;
        [SerializeField] private float flashDurationSec = 0.042f;
        [Tooltip("Gündüz (Daylight=1) namlu alevi görünürlüğü (gerçekçi AR-15 için çok düşük ~0.10).")]
        [Range(0f, 1f)]
        [SerializeField] private float daylightFlashIntensity = 0.10f;
        [Tooltip("Alacakaranlık (Daylight=0.35) alev şiddeti.")]
        [SerializeField] private float duskFlashIntensity = 0.85f;
        [Tooltip("Gece (Daylight=0) alev şiddeti.")]
        [SerializeField] private float nightFlashIntensity = 2.60f;

        [SerializeField] private float daylightLightIntensity = 0.35f;
        [SerializeField] private float nightLightIntensity = 14.0f;
        [SerializeField] private float flashLightRange = 7.5f;
        [SerializeField] private Color flashLightColor = new Color(1f, 0.72f, 0.28f, 1f);

        [Header("Görsel Alev Konisi ve Yıldızı")]
        [SerializeField] private Transform flashRootTransform;
        [SerializeField] private float flashSizeScale = 1.0f;
        [SerializeField] private Material sideFlashMaterial;
        [SerializeField] private Material starFlashMaterial;

        [Header("Barut Dumanı (Rüzgâra Duyarlı)")]
        [SerializeField] private bool enableGunpowderSmoke = true;
        [SerializeField] private int muzzleSmokeBurstCount = 5;
        [SerializeField] private Color smokeColor = new Color(0.76f, 0.78f, 0.82f, 0.14f);
        [SerializeField] private Material smokeMaterial;

        private float _flashTimer;
        private float _activeLightPeak;
        private float _activeShaderPeak;

        private Renderer[] _flashRenderers;
        private MaterialPropertyBlock _flashPropBlock;

        private ParticleSystem _muzzleSmokePS;
        private ParticleSystem _barrelWispPS;
        private ParticleSystem _ejectionSmokePS;
        private float _wispTimer;

        private static Material _sharedSideFlashMat;
        private static Material _sharedStarFlashMat;
        private static Material _sharedSmokeMat;
        private static Texture2D _sharedSmokeTex;

        private void Awake()
        {
            if (muzzleTransform == null)
                muzzleTransform = transform;

            _flashPropBlock = new MaterialPropertyBlock();
            EnsureFlashComponents();
            if (enableGunpowderSmoke)
                EnsureSmokeSystems();

            SetFlashActive(false);
        }

        private void Update()
        {
            float dt = Time.deltaTime;

            if (_flashTimer > 0f)
            {
                _flashTimer -= dt;
                if (_flashTimer <= 0f)
                {
                    SetFlashActive(false);
                }
                else
                {
                    float norm = _flashTimer / Mathf.Max(0.001f, flashDurationSec);
                    float decay = norm * norm;

                    if (muzzlePointLight != null)
                        muzzlePointLight.intensity = _activeLightPeak * decay;

                    ApplyFlashShaderIntensity(_activeShaderPeak * decay);
                }
            }

            if (_wispTimer > 0f && _barrelWispPS != null)
            {
                _wispTimer -= dt;
                if (_wispTimer <= 0f)
                    _barrelWispPS.Stop(false, ParticleSystemStopBehavior.StopEmitting);
            }

            SyncSmokeWind();
        }

        /// <summary>
        /// Call from RifleWeapon.onWeaponFired UnityEvent.
        /// </summary>
        public void PlayMuzzleFlash()
        {
            float daylight = EnvironmentManager.Instance != null
                ? EnvironmentManager.Instance.EffectiveDaylightFactor
                : 1.0f;

            // Evaluate daylight (1.0) -> dusk (0.35) -> night (0.0) curve
            if (daylight >= 0.35f)
            {
                float t = Mathf.InverseLerp(1.0f, 0.35f, daylight);
                _activeShaderPeak = Mathf.Lerp(daylightFlashIntensity, duskFlashIntensity, t);
            }
            else
            {
                float t = Mathf.InverseLerp(0.35f, 0.0f, daylight);
                _activeShaderPeak = Mathf.Lerp(duskFlashIntensity, nightFlashIntensity, t);
            }

            _activeLightPeak = Mathf.Lerp(nightLightIntensity, daylightLightIntensity, Mathf.Pow(daylight, 0.7f));
            _flashTimer = flashDurationSec;

            SetFlashActive(true);
            ApplyFlashShaderIntensity(_activeShaderPeak);

            if (muzzlePointLight != null)
            {
                muzzlePointLight.enabled = _activeLightPeak > 0.05f;
                muzzlePointLight.intensity = _activeLightPeak;
            }

            if (flashRootTransform != null)
            {
                float roll = Random.Range(0f, 360f);
                float sizeJitter = flashSizeScale * Random.Range(0.85f, 1.15f);
                // Slightly smaller flame during bright day, slightly fuller at night
                float timeScale = Mathf.Lerp(1.15f, 0.70f, daylight);
                flashRootTransform.localRotation = Quaternion.Euler(0f, 0f, roll);
                flashRootTransform.localScale = Vector3.one * (sizeJitter * timeScale);
            }

            EmitGunpowderSmoke();
        }

        private void EmitGunpowderSmoke()
        {
            if (!enableGunpowderSmoke)
                return;

            if (_muzzleSmokePS != null)
                _muzzleSmokePS.Emit(muzzleSmokeBurstCount);

            if (_barrelWispPS != null)
            {
                _wispTimer = 1.4f;
                if (!_barrelWispPS.isPlaying)
                    _barrelWispPS.Play();
            }

            if (_ejectionSmokePS != null)
                _ejectionSmokePS.Emit(2);
        }

        private void SyncSmokeWind()
        {
            if (!enableGunpowderSmoke)
                return;

            Vector3 wind = EnvironmentManager.Instance != null ? EnvironmentManager.Instance.windSpeedMS : Vector3.zero;
            Vector3 drift = new Vector3(wind.x * 0.45f, 0.22f, wind.z * 0.45f);

            ApplyWorldWind(_muzzleSmokePS, drift);
            ApplyWorldWind(_barrelWispPS, drift + new Vector3(0f, 0.08f, 0f));
            ApplyWorldWind(_ejectionSmokePS, drift);
        }

        private static void ApplyWorldWind(ParticleSystem ps, Vector3 worldForce)
        {
            if (ps == null)
                return;
            var vel = ps.velocityOverLifetime;
            vel.x = worldForce.x;
            vel.y = worldForce.y;
            vel.z = worldForce.z;
        }

        private void ApplyFlashShaderIntensity(float intensity)
        {
            if (_flashRenderers == null)
                return;

            for (int i = 0; i < _flashRenderers.Length; i++)
            {
                Renderer r = _flashRenderers[i];
                if (r == null) continue;
                r.GetPropertyBlock(_flashPropBlock);
                _flashPropBlock.SetFloat(IntensityId, intensity);
                r.SetPropertyBlock(_flashPropBlock);
            }
        }

        private void EnsureFlashComponents()
        {
            if (muzzlePointLight == null)
            {
                GameObject lightGo = new GameObject("MuzzleFlashLight");
                lightGo.transform.SetParent(muzzleTransform, false);
                lightGo.transform.localPosition = new Vector3(0f, 0f, 0.08f);
                muzzlePointLight = lightGo.AddComponent<Light>();
                muzzlePointLight.type = LightType.Point;
                muzzlePointLight.color = flashLightColor;
                muzzlePointLight.range = flashLightRange;
                muzzlePointLight.shadows = LightShadows.None;
            }

            if (flashRootTransform == null)
            {
                GameObject flashGo = new GameObject("MuzzleFlashVisual");
                flashGo.transform.SetParent(muzzleTransform, false);
                flashGo.transform.localPosition = new Vector3(0f, 0f, 0.01f);

                Material sideMat = sideFlashMaterial != null ? sideFlashMaterial : GetSideFlashMaterial();
                Material starMat = starFlashMaterial != null ? starFlashMaterial : GetStarFlashMaterial();

                // 3 intersecting side flame cones extending forward (+Z)
                for (int i = 0; i < 3; i++)
                {
                    GameObject fin = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    fin.name = $"FlameCone_{i}";
                    Destroy(fin.GetComponent<Collider>());
                    fin.transform.SetParent(flashGo.transform, false);
                    // Rotate so local X (u 0..1) runs along +Z forward
                    fin.transform.localRotation = Quaternion.Euler(0f, -90f, 0f) * Quaternion.Euler(i * 60f, 0f, 0f);
                    fin.transform.localPosition = new Vector3(0f, 0f, 0.085f);
                    fin.transform.localScale = new Vector3(0.17f, 0.085f, 1f);
                    MeshRenderer mr = fin.GetComponent<MeshRenderer>();
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                    if (sideMat != null)
                        mr.sharedMaterial = sideMat;
                }

                // Front 4-prong flash-hider star disc
                GameObject star = GameObject.CreatePrimitive(PrimitiveType.Quad);
                star.name = "FlameStarFront";
                Destroy(star.GetComponent<Collider>());
                star.transform.SetParent(flashGo.transform, false);
                star.transform.localPosition = new Vector3(0f, 0f, 0.02f);
                star.transform.localScale = new Vector3(0.11f, 0.11f, 1f);
                MeshRenderer starMr = star.GetComponent<MeshRenderer>();
                starMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                starMr.receiveShadows = false;
                if (starMat != null)
                    starMr.sharedMaterial = starMat;

                flashRootTransform = flashGo.transform;
            }

            _flashRenderers = flashRootTransform.GetComponentsInChildren<Renderer>(true);
        }

        private void EnsureSmokeSystems()
        {
            Material smokeMat = smokeMaterial != null ? smokeMaterial : GetSharedSmokeMaterial();
            if (smokeMat != null && smokeMat.GetTexture("_BaseMap") == null)
                smokeMat.SetTexture("_BaseMap", GetOrCreateSmokeTexture());

            // 1. Initial quick muzzle blast smoke puff
            _muzzleSmokePS = CreateSmokeSystem("MuzzleGunpowderSmoke", muzzleTransform, smokeMat,
                startSpeedMin: 0.7f, startSpeedMax: 1.6f,
                startSizeMin: 0.055f, startSizeMax: 0.10f,
                lifetimeMin: 0.7f, lifetimeMax: 1.3f,
                rateOverTime: 0f, maxParticles: 24, coneAngle: 22f, sizeGrowth: 2.6f);

            // 2. Thin residual wisp rising from hot barrel after shot
            _barrelWispPS = CreateSmokeSystem("BarrelHotWisp", muzzleTransform, smokeMat,
                startSpeedMin: 0.08f, startSpeedMax: 0.22f,
                startSizeMin: 0.028f, startSizeMax: 0.048f,
                lifetimeMin: 0.9f, lifetimeMax: 1.5f,
                rateOverTime: 6f, maxParticles: 16, coneAngle: 12f, sizeGrowth: 2.2f);
            _barrelWispPS.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            // 3. Subtle gas wisp from ejection port
            if (ejectionPoint != null)
            {
                _ejectionSmokePS = CreateSmokeSystem("EjectionPortSmoke", ejectionPoint, smokeMat,
                    startSpeedMin: 0.25f, startSpeedMax: 0.55f,
                    startSizeMin: 0.024f, startSizeMax: 0.042f,
                    lifetimeMin: 0.45f, lifetimeMax: 0.85f,
                    rateOverTime: 0f, maxParticles: 10, coneAngle: 28f, sizeGrowth: 2.0f);
                _ejectionSmokePS.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
            }
        }

        private ParticleSystem CreateSmokeSystem(
            string goName,
            Transform parent,
            Material mat,
            float startSpeedMin,
            float startSpeedMax,
            float startSizeMin,
            float startSizeMax,
            float lifetimeMin,
            float lifetimeMax,
            float rateOverTime,
            int maxParticles,
            float coneAngle,
            float sizeGrowth)
        {
            GameObject go = new GameObject(goName);
            go.transform.SetParent(parent, false);

            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = rateOverTime > 0f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = maxParticles;
            main.startLifetime = new ParticleSystem.MinMaxCurve(lifetimeMin, lifetimeMax);
            main.startSpeed = new ParticleSystem.MinMaxCurve(startSpeedMin, startSpeedMax);
            main.startSize = new ParticleSystem.MinMaxCurve(startSizeMin, startSizeMax);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = smokeColor;

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = rateOverTime;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = coneAngle;
            shape.radius = 0.008f;

            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;

            var drag = ps.limitVelocityOverLifetime;
            drag.enabled = true;
            drag.dampen = 0.35f;
            drag.limit = 1.5f;

            var sizeOverLife = ps.sizeOverLifetime;
            sizeOverLife.enabled = true;
            AnimationCurve sizeCurve = new AnimationCurve(
                new Keyframe(0f, 0.65f),
                new Keyframe(1f, sizeGrowth));
            sizeOverLife.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

            var colorOverLife = ps.colorOverLifetime;
            colorOverLife.enabled = true;
            Gradient grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.12f), new GradientAlphaKey(0f, 1f) });
            colorOverLife.color = grad;

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            if (mat != null)
                renderer.sharedMaterial = mat;

            return ps;
        }

        private void SetFlashActive(bool active)
        {
            if (muzzlePointLight != null)
                muzzlePointLight.enabled = active && _activeLightPeak > 0.05f;

            if (flashRootTransform != null)
                flashRootTransform.gameObject.SetActive(active);
        }

        private static Material GetSideFlashMaterial()
        {
            if (_sharedSideFlashMat != null)
                return _sharedSideFlashMat;

            Shader shader = Shader.Find("VRAtis/MuzzleFlashURP");
            if (shader == null) return null;

            _sharedSideFlashMat = new Material(shader);
            _sharedSideFlashMat.SetFloat(IsSideFinId, 1f);
            return _sharedSideFlashMat;
        }

        private static Material GetStarFlashMaterial()
        {
            if (_sharedStarFlashMat != null)
                return _sharedStarFlashMat;

            Shader shader = Shader.Find("VRAtis/MuzzleFlashURP");
            if (shader == null) return null;

            _sharedStarFlashMat = new Material(shader);
            _sharedStarFlashMat.SetFloat(IsSideFinId, 0f);
            return _sharedStarFlashMat;
        }

        private static Material GetSharedSmokeMaterial()
        {
            if (_sharedSmokeMat != null)
                return _sharedSmokeMat;

            Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null) shader = Shader.Find("Particles/Standard Unlit");
            if (shader == null) return null;

            _sharedSmokeMat = new Material(shader);
            _sharedSmokeMat.SetFloat("_Surface", 1f); // Transparent
            _sharedSmokeMat.SetFloat("_Blend", 0f);   // Alpha
            _sharedSmokeMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _sharedSmokeMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _sharedSmokeMat.SetInt("_ZWrite", 0);
            _sharedSmokeMat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            _sharedSmokeMat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            _sharedSmokeMat.SetTexture("_BaseMap", GetOrCreateSmokeTexture());
            _sharedSmokeMat.SetColor("_BaseColor", Color.white);
            return _sharedSmokeMat;
        }

        private static Texture2D GetOrCreateSmokeTexture()
        {
            if (_sharedSmokeTex != null)
                return _sharedSmokeTex;

            const int size = 64;
            _sharedSmokeTex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            _sharedSmokeTex.wrapMode = TextureWrapMode.Clamp;

            Color[] pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                float ny = (y + 0.5f) / size * 2f - 1f;
                for (int x = 0; x < size; x++)
                {
                    float nx = (x + 0.5f) / size * 2f - 1f;
                    float r = Mathf.Sqrt(nx * nx + ny * ny);
                    float soft = Mathf.Clamp01(1f - r);
                    // Organic noise variation inside soft radial puff
                    float n = Mathf.PerlinNoise(x * 0.14f + 3.1f, y * 0.14f + 7.4f);
                    float alpha = Mathf.Pow(soft, 2.2f) * Mathf.Lerp(0.65f, 1.0f, n);
                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
            }

            _sharedSmokeTex.SetPixels(pixels);
            _sharedSmokeTex.Apply();
            return _sharedSmokeTex;
        }
    }
}
