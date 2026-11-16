using UnityEngine;
using VRAtis.Ballistics;

namespace VRAtis.Weapons
{
    /// <summary>
    /// Handles lighting-aware photorealistic AR-15 muzzle flash (minimal in daylight, crisp at dusk,
    /// bright at night with forward-directed spotlight so zero glare spills onto the sight glass/camera)
    /// and a single dissipating gunpowder vapor cloud at the barrel tip.
    /// </summary>
    public class RifleVisualEffects : MonoBehaviour
    {
        private static readonly int IntensityId = Shader.PropertyToID("_Intensity");
        private static readonly int TileIndexId = Shader.PropertyToID("_TileIndex");

        [Header("Referans Noktaları")]
        [SerializeField] private Transform muzzleTransform;
        [SerializeField] private Transform ejectionPoint;

        [Header("Namlu Alevi (Işık Koşuluna Duyarlı 2x2 Atlas Kartları)")]
        [SerializeField] private Light muzzlePointLight;
        [SerializeField] private float flashDurationSec = 0.040f;
        [Tooltip("Gündüz (Daylight=1) namlu alevi şiddeti.")]
        [Range(0f, 1f)]
        [SerializeField] private float daylightFlashIntensity = 0.22f;
        [Tooltip("Alacakaranlık (Daylight=0.35) namlu alevi şiddeti.")]
        [SerializeField] private float duskFlashIntensity = 1.05f;
        [Tooltip("Gece (Daylight=0) namlu alevi şiddeti.")]
        [SerializeField] private float nightFlashIntensity = 2.55f;

        [SerializeField] private float daylightLightIntensity = 0.0f;
        [SerializeField] private float duskLightIntensity = 2.8f;
        [SerializeField] private float nightLightIntensity = 12.0f;
        [SerializeField] private float flashLightRange = 7.5f;
        [SerializeField] private Color flashLightColor = new Color(1f, 0.74f, 0.32f, 1f);

        [Header("Namlu Alevi Mesh ve Materyaller")]
        [SerializeField] private Transform flashRootTransform;
        [SerializeField] private float flashSizeScale = 1.0f;
        [SerializeField] private Material flash3DMaterial;
        [SerializeField] private Material flashCrownMaterial;

        [Header("Barut Dumanı Bulutu (Anlık Bulut - Sürekli Tütsü Yok)")]
        [SerializeField] private bool enableGunpowderSmoke = true;
        [SerializeField] private int cloudPuffsPerShot = 10;
        [SerializeField] private Color cloudColor = new Color(0.76f, 0.79f, 0.83f, 0.115f);
        [SerializeField] private Material smokeMaterial;

        private float _flashTimer;
        private float _activeDuration;
        private float _activeLightPeak;
        private float _activeShaderPeak;

        private Renderer[] _flashRenderers;
        private MaterialPropertyBlock _flashPropBlock;

        private ParticleSystem _muzzleCloudPS;
        private ParticleSystem _ejectionCloudPS;

        private static Material _sharedPlumeMat;
        private static Material _sharedCrownMat;
        private static Material _sharedSmokeMat;
        private static Texture2D _sharedSmokeTex;

        private void Awake()
        {
            ResolveMuzzleTransform();

            _flashPropBlock = new MaterialPropertyBlock();
            EnsureFlashComponents();
            if (enableGunpowderSmoke)
                EnsureCloudSmokeSystems();

            SetFlashActive(false);
        }

        private void Update()
        {
            if (_flashTimer > 0f)
            {
                _flashTimer -= Time.deltaTime;
                if (_flashTimer <= 0f)
                {
                    SetFlashActive(false);
                }
                else
                {
                    float norm = Mathf.Clamp01(_flashTimer / Mathf.Max(0.001f, _activeDuration));
                    float decay = norm * norm;

                    if (muzzlePointLight != null)
                        muzzlePointLight.intensity = _activeLightPeak * decay;

                    ApplyFlashShaderIntensity(_activeShaderPeak * decay);
                }
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

            if (daylight >= 0.35f)
            {
                float t = Mathf.InverseLerp(1.0f, 0.35f, daylight);
                _activeShaderPeak = Mathf.Lerp(daylightFlashIntensity, duskFlashIntensity, t);
                _activeLightPeak = Mathf.Lerp(daylightLightIntensity, duskLightIntensity, t);
            }
            else
            {
                float t = Mathf.InverseLerp(0.35f, 0.0f, daylight);
                _activeShaderPeak = Mathf.Lerp(duskFlashIntensity, nightFlashIntensity, t);
                _activeLightPeak = Mathf.Lerp(duskLightIntensity, nightLightIntensity, t);
            }

            // Shorter, tiny flash in sunlight; slightly fuller duration at dusk/night
            _activeDuration = Mathf.Lerp(flashDurationSec * 1.05f, flashDurationSec * 0.62f, daylight);
            _flashTimer = _activeDuration;

            SetFlashActive(true);
            int plumeTile = Random.Range(0, 4);
            int crownTile = Random.Range(0, 4);
            ApplyFlashShaderProperties(_activeShaderPeak, plumeTile, crownTile);

            if (muzzlePointLight != null)
            {
                muzzlePointLight.enabled = _activeLightPeak > 0.05f;
                muzzlePointLight.intensity = _activeLightPeak;
            }

            if (flashRootTransform != null)
            {
                float roll = Random.Range(0f, 360f);
                float sizeJitter = flashSizeScale * Random.Range(0.88f, 1.12f);
                float timeScale = Mathf.Lerp(1.12f, 0.62f, daylight);
                flashRootTransform.localPosition = new Vector3(0f, 0f, 0.004f);
                flashRootTransform.localRotation = Quaternion.Euler(0f, 0f, roll);
                flashRootTransform.localScale = Vector3.one * (sizeJitter * timeScale);
            }

            EmitSmokeCloudPuff();
        }

        private void EmitSmokeCloudPuff()
        {
            if (!enableGunpowderSmoke)
                return;

            if (_muzzleCloudPS != null)
                _muzzleCloudPS.Emit(cloudPuffsPerShot);

            if (_ejectionCloudPS != null)
                _ejectionCloudPS.Emit(3);
        }

        private void SyncSmokeWind()
        {
            if (!enableGunpowderSmoke)
                return;

            Vector3 wind = EnvironmentManager.Instance != null ? EnvironmentManager.Instance.windSpeedMS : Vector3.zero;
            Vector3 drift = new Vector3(wind.x * 0.40f, 0.14f, wind.z * 0.40f);

            ApplyWorldWind(_muzzleCloudPS, drift);
            ApplyWorldWind(_ejectionCloudPS, drift);
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

        private void ResolveMuzzleTransform()
        {
            if (muzzleTransform == null)
                muzzleTransform = transform;

            // Ensure Muzzle aligns cleanly with AR15_WithRedDot/MuzzlePoint if present in hierarchy
            Transform muzzlePoint = FindChildByName(transform, "MuzzlePoint");
            if (muzzlePoint != null && muzzleTransform != muzzlePoint)
            {
                if (Vector3.Distance(muzzleTransform.position, muzzlePoint.position) > 0.015f)
                    muzzleTransform.position = muzzlePoint.position;

                muzzleTransform.rotation = Quaternion.LookRotation(transform.forward, transform.up);
            }
        }

        private static Transform FindChildByName(Transform root, string targetName)
        {
            if (root == null) return null;
            if (root.name == targetName) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform hit = FindChildByName(root.GetChild(i), targetName);
                if (hit != null) return hit;
            }
            return null;
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

        private void ApplyFlashShaderProperties(float intensity, int plumeTile, int crownTile)
        {
            if (_flashRenderers == null)
                return;

            for (int i = 0; i < _flashRenderers.Length; i++)
            {
                Renderer r = _flashRenderers[i];
                if (r == null) continue;
                bool isCrown = r.gameObject.name.Contains("Crown");
                r.GetPropertyBlock(_flashPropBlock);
                _flashPropBlock.SetFloat(IntensityId, intensity);
                _flashPropBlock.SetFloat(TileIndexId, isCrown ? crownTile : plumeTile);
                r.SetPropertyBlock(_flashPropBlock);
            }
        }

        private void EnsureFlashComponents()
        {
            // Forward-directed SpotLight slightly ahead of barrel crown: lights targets/ground ahead
            // without reflecting backwards off RedDot_Glass into Main Camera
            if (muzzlePointLight == null)
            {
                Transform existingLight = muzzleTransform.Find("MuzzleFlashLight");
                if (existingLight != null)
                    muzzlePointLight = existingLight.GetComponent<Light>();
            }

            if (muzzlePointLight == null)
            {
                GameObject lightGo = new GameObject("MuzzleFlashLight");
                lightGo.transform.SetParent(muzzleTransform, false);
                muzzlePointLight = lightGo.AddComponent<Light>();
            }

            muzzlePointLight.transform.SetParent(muzzleTransform, false);
            muzzlePointLight.transform.localPosition = new Vector3(0f, 0f, 0.16f);
            muzzlePointLight.transform.localRotation = Quaternion.identity;
            muzzlePointLight.type = LightType.Spot;
            muzzlePointLight.spotAngle = 115f;
            muzzlePointLight.innerSpotAngle = 55f;
            muzzlePointLight.color = flashLightColor;
            muzzlePointLight.range = flashLightRange;
            muzzlePointLight.shadows = LightShadows.None;

            if (flashRootTransform != null && flashRootTransform.parent != muzzleTransform)
            {
                flashRootTransform.SetParent(muzzleTransform, false);
                flashRootTransform.localPosition = new Vector3(0f, 0f, 0.004f);
                flashRootTransform.localRotation = Quaternion.identity;
            }

            if (flashRootTransform == null)
            {
                // Clean runtime fallback if AR15SetupTool hasn't instantiated MuzzleFlashVisual3D yet
                GameObject flashGo = new GameObject("MuzzleFlashVisual");
                flashGo.transform.SetParent(muzzleTransform, false);
                flashGo.transform.localPosition = new Vector3(0f, 0f, 0.004f);

                Material plumeMat = flash3DMaterial != null ? flash3DMaterial : GetSharedPlumeMaterial();
                for (int i = 0; i < 3; i++)
                {
                    GameObject fin = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    fin.name = $"FlashPlumeCards_{i}";
                    Destroy(fin.GetComponent<Collider>());
                    fin.transform.SetParent(flashGo.transform, false);
                    fin.transform.localRotation = Quaternion.Euler(0f, -90f, 0f) * Quaternion.Euler(i * 60f, 0f, 0f);
                    fin.transform.localPosition = new Vector3(0f, 0f, 0.056f);
                    fin.transform.localScale = new Vector3(0.115f, 0.09f, 1f);
                    MeshRenderer mr = fin.GetComponent<MeshRenderer>();
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                    if (plumeMat != null)
                        mr.sharedMaterial = plumeMat;
                }

                flashRootTransform = flashGo.transform;
            }
            else
            {
                Material plumeMat = flash3DMaterial != null ? flash3DMaterial : GetSharedPlumeMaterial();
                Material crownMat = flashCrownMaterial != null ? flashCrownMaterial : (flash3DMaterial != null ? flash3DMaterial : GetSharedCrownMaterial());

                foreach (MeshRenderer mr in flashRootTransform.GetComponentsInChildren<MeshRenderer>(true))
                {
                    bool isCrown = mr.gameObject.name.Contains("Crown");
                    Material targetMat = isCrown ? crownMat : plumeMat;
                    if (targetMat != null)
                        mr.sharedMaterial = targetMat;
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                }
            }

            _flashRenderers = flashRootTransform.GetComponentsInChildren<Renderer>(true);
        }

        private void EnsureCloudSmokeSystems()
        {
            Material mat = smokeMaterial != null ? smokeMaterial : GetSharedSmokeMaterial();
            if (mat != null && mat.GetTexture("_BaseMap") == null)
            {
                mat.SetTexture("_BaseMap", GetOrCreateSmokeTexture());
            }

            // Clean up any legacy stretched/continuous emitters if re-initialized
            DestroyChildIfExists(muzzleTransform, "MuzzlePowderSparks");
            DestroyChildIfExists(muzzleTransform, "MuzzleGasJet");
            DestroyChildIfExists(muzzleTransform, "BarrelHotWisp");

            // Single expanding gunpowder smoke cloud right ahead of muzzle tip
            _muzzleCloudPS = CreateCloudSmokeSystem("MuzzleSmokeCloud", muzzleTransform, mat,
                color: cloudColor,
                startSpeedMin: 0.55f, startSpeedMax: 1.45f,
                startSizeMin: 0.045f, startSizeMax: 0.085f,
                lifetimeMin: 0.85f, lifetimeMax: 1.35f,
                maxParticles: 32, coneAngle: 26f, sizeGrowth: 3.6f,
                noiseStrength: 0.14f);

            // Subtle brief puff from ejection port when bolt opens
            if (ejectionPoint != null)
            {
                DestroyChildIfExists(ejectionPoint, "EjectionPortSmoke");
                _ejectionCloudPS = CreateCloudSmokeSystem("EjectionPortCloud", ejectionPoint, mat,
                    color: new Color(cloudColor.r, cloudColor.g, cloudColor.b, cloudColor.a * 0.65f),
                    startSpeedMin: 0.18f, startSpeedMax: 0.40f,
                    startSizeMin: 0.024f, startSizeMax: 0.045f,
                    lifetimeMin: 0.38f, lifetimeMax: 0.68f,
                    maxParticles: 10, coneAngle: 30f, sizeGrowth: 2.5f,
                    noiseStrength: 0.08f);
                _ejectionCloudPS.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
            }
        }

        private static void DestroyChildIfExists(Transform parent, string childName)
        {
            if (parent == null) return;
            Transform child = parent.Find(childName);
            if (child != null)
                Destroy(child.gameObject);
        }

        private ParticleSystem CreateCloudSmokeSystem(
            string goName,
            Transform parent,
            Material mat,
            Color color,
            float startSpeedMin,
            float startSpeedMax,
            float startSizeMin,
            float startSizeMax,
            float lifetimeMin,
            float lifetimeMax,
            int maxParticles,
            float coneAngle,
            float sizeGrowth,
            float noiseStrength)
        {
            GameObject go = new GameObject(goName);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0f, 0.015f);

            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = maxParticles;
            main.startLifetime = new ParticleSystem.MinMaxCurve(lifetimeMin, lifetimeMax);
            main.startSpeed = new ParticleSystem.MinMaxCurve(startSpeedMin, startSpeedMax);
            main.startSize = new ParticleSystem.MinMaxCurve(startSizeMin, startSizeMax);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = color;

            // Emit strictly via Emit() bursts on each shot; never stream continuously
            var emission = ps.emission;
            emission.enabled = false;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = coneAngle;
            shape.radius = 0.010f;

            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;

            // Strong air drag slows the blast rapidly ~20cm past the muzzle into a hovering cloud
            var drag = ps.limitVelocityOverLifetime;
            drag.enabled = true;
            drag.dampen = 0.72f;
            drag.limit = 0.35f;

            var rotOverLife = ps.rotationOverLifetime;
            rotOverLife.enabled = true;
            rotOverLife.z = new ParticleSystem.MinMaxCurve(-38f * Mathf.Deg2Rad, 38f * Mathf.Deg2Rad);

            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = noiseStrength;
            noise.frequency = 1.15f;
            noise.scrollSpeed = 0.35f;
            noise.damping = true;
            noise.octaveCount = 1;
            noise.quality = ParticleSystemNoiseQuality.Low;

            var sizeOverLife = ps.sizeOverLifetime;
            sizeOverLife.enabled = true;
            AnimationCurve sizeCurve = new AnimationCurve(
                new Keyframe(0f, 0.45f),
                new Keyframe(0.28f, sizeGrowth * 0.65f),
                new Keyframe(1f, sizeGrowth));
            sizeOverLife.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

            var colorOverLife = ps.colorOverLifetime;
            colorOverLife.enabled = true;
            Gradient grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(1f, 0.12f),
                    new GradientAlphaKey(0.55f, 0.48f),
                    new GradientAlphaKey(0f, 1f)
                });
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

        private static Material GetSharedPlumeMaterial()
        {
            if (_sharedPlumeMat != null)
                return _sharedPlumeMat;

            Shader shader = Shader.Find("VRAtis/MuzzleFlashURP");
            if (shader == null) return null;

            _sharedPlumeMat = new Material(shader);
            return _sharedPlumeMat;
        }

        private static Material GetSharedCrownMaterial()
        {
            if (_sharedCrownMat != null)
                return _sharedCrownMat;

            Shader shader = Shader.Find("VRAtis/MuzzleFlashURP");
            if (shader == null) return null;

            _sharedCrownMat = new Material(shader);
            return _sharedCrownMat;
        }

        private static Material GetSharedSmokeMaterial()
        {
            if (_sharedSmokeMat != null)
                return _sharedSmokeMat;

            Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null) shader = Shader.Find("Particles/Standard Unlit");
            if (shader == null) return null;

            _sharedSmokeMat = new Material(shader);
            _sharedSmokeMat.SetFloat("_Surface", 1f);
            _sharedSmokeMat.SetFloat("_Blend", 0f);
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

            const int size = 128;
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
                    float radial = Mathf.Pow(Mathf.Clamp01(1f - r), 2.2f);

                    float n1 = Mathf.PerlinNoise(x * 0.045f + 2.3f, y * 0.045f + 5.7f);
                    float n2 = Mathf.PerlinNoise(x * 0.11f + 11.4f, y * 0.11f + 1.9f);
                    float wispy = Mathf.Clamp01((n1 * 0.65f + n2 * 0.35f) * 1.15f - 0.12f);

                    float alpha = radial * wispy;
                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
            }

            _sharedSmokeTex.SetPixels(pixels);
            _sharedSmokeTex.Apply();
            return _sharedSmokeTex;
        }
    }
}
