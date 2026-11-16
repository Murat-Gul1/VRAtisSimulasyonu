using UnityEngine;
using UnityEngine.Rendering;
using VRAtis.Ballistics;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace VRAtis.Environment
{
    /// <summary>
    /// Dynamically drives sun angle, shadow direction, sky/fog atmosphere,
    /// and syncs daylightFactor/isNight to EnvironmentManager so mountain shadows
    /// and weapon muzzle flash/tracers react realistically to the time of day.
    /// </summary>
    [ExecuteAlways]
    public class DynamicTimeOfDay : MonoBehaviour
    {
        [Header("Güneş ve Saat")]
        [SerializeField] private Light sunLight;
        [Tooltip("0-24 arası gün saati. 16.0-17.2 arası arkadaki dağın gölgesini atış noktasına düşürür.")]
        [Range(0f, 24f)]
        [SerializeField] private float timeOfDayHours = 16.45f;

        [Tooltip("Güneş ışığının ileri yön açısı (derece). ~15 = Güneş arkadaki dağın arkasından öne vurur, dağın gölgesini atış noktasına düşürür.")]
        [Range(0f, 360f)]
        [SerializeField] private float sunAzimuthOffsetDeg = 15f;

        [Header("Otomatik Zaman Akışı")]
        [SerializeField] private bool autoAdvanceTime;
        [Tooltip("Otomatik akış hızı (saat / saniye).")]
        [SerializeField] private float hoursPerSecond = 0.25f;

        [Header("Atmosferik Sis (Uzak Dağ Derinliği)")]
        [SerializeField] private bool controlFog = true;
        [SerializeField] private float dayFogDensity = 0.00055f;
        [SerializeField] private float duskFogDensity = 0.00095f;
        [SerializeField] private float nightFogDensity = 0.0014f;

        [Header("Güneş ve Gökyüzü Renkleri")]
        [SerializeField] private Color noonSunColor = new Color(1.0f, 0.96f, 0.90f);
        [SerializeField] private Color goldenSunColor = new Color(1.0f, 0.62f, 0.30f);
        [SerializeField] private Color nightMoonColor = new Color(0.45f, 0.58f, 0.85f);

        [SerializeField] private Color dayFogColor = new Color(0.68f, 0.78f, 0.88f);
        [SerializeField] private Color goldenFogColor = new Color(0.75f, 0.56f, 0.44f);
        [SerializeField] private Color nightFogColor = new Color(0.05f, 0.08f, 0.14f);

        [Header("Dinamik Gökyüzü ve Bulut Akışı")]
        [SerializeField] private Vector2 baseCloudDriftSpeed = new Vector2(0.00045f, 0.00020f);
        [SerializeField] private float cloudWindSpeedMultiplier = 0.00022f;

        private static readonly int SkyActiveId = Shader.PropertyToID("_VRAtisSkyActive");
        private static readonly int SkyDaylightId = Shader.PropertyToID("_VRAtisSkyDaylight");
        private static readonly int SkyGoldenMixId = Shader.PropertyToID("_VRAtisSkyGoldenMix");
        private static readonly int SkySunDirId = Shader.PropertyToID("_VRAtisSkySunDir");
        private static readonly int SkySunColorId = Shader.PropertyToID("_VRAtisSkySunColor");
        private static readonly int SkyZenithColorId = Shader.PropertyToID("_VRAtisSkyZenithColor");
        private static readonly int SkyHorizonColorId = Shader.PropertyToID("_VRAtisSkyHorizonColor");
        private static readonly int SkyGroundColorId = Shader.PropertyToID("_VRAtisSkyGroundColor");
        private static readonly int SkyCloudLitColorId = Shader.PropertyToID("_VRAtisSkyCloudLitColor");
        private static readonly int SkyCloudShadowColorId = Shader.PropertyToID("_VRAtisSkyCloudShadowColor");
        private static readonly int SkyWindOffsetId = Shader.PropertyToID("_VRAtisSkyWindOffset");

        private Vector2 _cloudWindOffset = new Vector2(0.12f, 0.08f);

        private void Awake()
        {
            FindSunLightIfMissing();
            ApplyTimeOfDay();
        }

        private void OnValidate()
        {
            FindSunLightIfMissing();
            ApplyTimeOfDay();
        }

        private void Update()
        {
            if (Application.isPlaying)
            {
#if ENABLE_INPUT_SYSTEM
                if (Keyboard.current != null)
                {
                    if (Keyboard.current.digit1Key.wasPressedThisFrame) { timeOfDayHours = 7.2f; autoAdvanceTime = false; }
                    else if (Keyboard.current.digit2Key.wasPressedThisFrame) { timeOfDayHours = 12.5f; autoAdvanceTime = false; }
                    else if (Keyboard.current.digit3Key.wasPressedThisFrame) { timeOfDayHours = 17.1f; autoAdvanceTime = false; }
                    else if (Keyboard.current.digit4Key.wasPressedThisFrame) { timeOfDayHours = 18.6f; autoAdvanceTime = false; }
                    else if (Keyboard.current.digit5Key.wasPressedThisFrame) { timeOfDayHours = 21.5f; autoAdvanceTime = false; }
                    else if (Keyboard.current.tKey.wasPressedThisFrame) { autoAdvanceTime = !autoAdvanceTime; }
                }
#endif

                if (autoAdvanceTime)
                {
                    timeOfDayHours = Mathf.Repeat(timeOfDayHours + hoursPerSecond * Time.deltaTime, 24f);
                }

                Vector3 envWind = EnvironmentManager.Instance != null ? EnvironmentManager.Instance.windSpeedMS : Vector3.zero;
                Vector2 windDrift = baseCloudDriftSpeed + new Vector2(envWind.x, envWind.z) * cloudWindSpeedMultiplier;
                _cloudWindOffset += windDrift * Time.deltaTime;
                _cloudWindOffset.x = Mathf.Repeat(_cloudWindOffset.x, 100f);
                _cloudWindOffset.y = Mathf.Repeat(_cloudWindOffset.y, 100f);
            }

            ApplyTimeOfDay();
        }

        public float CurrentTimeOfDayHours => timeOfDayHours;
        public bool AutoAdvanceTime => autoAdvanceTime;

        public void SetTimeOfDay(float hours)
        {
            timeOfDayHours = Mathf.Repeat(hours, 24f);
            ApplyTimeOfDay();
        }

        public void ApplyTimePreset(float hours)
        {
            timeOfDayHours = Mathf.Repeat(hours, 24f);
            autoAdvanceTime = false;
            ApplyTimeOfDay();
        }

        public void SetAutoAdvanceTime(bool enabled)
        {
            autoAdvanceTime = enabled;
        }

        private void FindSunLightIfMissing()
        {
            if (sunLight != null)
                return;

            Light[] lights = FindObjectsByType<Light>(FindObjectsSortMode.None);
            foreach (Light l in lights)
            {
                if (l.type == LightType.Directional)
                {
                    sunLight = l;
                    break;
                }
            }
        }

        private void ApplyTimeOfDay()
        {
            if (sunLight == null)
                return;

            // Sun elevation: 6:00 -> 0 deg (sunrise), 12:00 -> 90 deg, 18:00 -> 180 deg (sunset)
            float sunProgress = (timeOfDayHours - 6f) / 12f; // 0 at 6am, 1 at 6pm
            float pitchDeg = sunProgress * 180f;
            float elevationSin = Mathf.Sin(sunProgress * Mathf.PI); // >0 during day, <=0 at night

            // Keep sun slightly offset diagonally so mountains cast long dramatic shadows across the range
            float yawDeg = sunAzimuthOffsetDeg + (sunProgress - 0.5f) * 55f;

            bool isDay = elevationSin > -0.02f;
            if (isDay)
            {
                float clampedPitch = Mathf.Clamp(elevationSin * 68f, 3f, 68f);
                sunLight.transform.rotation = Quaternion.Euler(clampedPitch, yawDeg, 0f);
            }
            else
            {
                // Moonlight from high opposite angle
                sunLight.transform.rotation = Quaternion.Euler(35f, yawDeg + 180f, 0f);
            }

            // Compute normalized daylight [0=night, ~0.35=dusk/dawn, 1=noon]
            float daylight = Mathf.Clamp01(Mathf.InverseLerp(-0.08f, 0.55f, elevationSin));
            float goldenMix = Mathf.Clamp01(1f - Mathf.Abs(daylight - 0.32f) / 0.32f);

            Color sunCol = daylight > 0.08f
                ? Color.Lerp(noonSunColor, goldenSunColor, goldenMix)
                : nightMoonColor;

            float sunIntensity = daylight > 0.08f
                ? Mathf.Lerp(0.45f, 2.2f, Mathf.Pow(daylight, 0.65f))
                : 0.22f;

            sunLight.color = sunCol;
            sunLight.intensity = sunIntensity;

            // Ambient sky/ground lighting
            RenderSettings.ambientMode = AmbientMode.Trilight;
            Color skyAmb = Color.Lerp(new Color(0.05f, 0.08f, 0.15f), new Color(0.45f, 0.62f, 0.85f), daylight);
            skyAmb = Color.Lerp(skyAmb, new Color(0.55f, 0.36f, 0.28f), goldenMix * 0.6f);
            Color eqAmb = Color.Lerp(new Color(0.04f, 0.05f, 0.09f), new Color(0.38f, 0.42f, 0.46f), daylight);
            Color grdAmb = Color.Lerp(new Color(0.02f, 0.03f, 0.04f), new Color(0.18f, 0.17f, 0.14f), daylight);
            RenderSettings.ambientSkyColor = skyAmb;
            RenderSettings.ambientEquatorColor = eqAmb;
            RenderSettings.ambientGroundColor = grdAmb;

            Color fogCol = Color.Lerp(nightFogColor, dayFogColor, daylight);
            fogCol = Color.Lerp(fogCol, goldenFogColor, goldenMix * 0.75f);

            if (controlFog)
            {
                RenderSettings.fog = true;
                RenderSettings.fogMode = FogMode.ExponentialSquared;
                RenderSettings.fogColor = fogCol;
                RenderSettings.fogDensity = daylight > 0.15f
                    ? Mathf.Lerp(duskFogDensity, dayFogDensity, daylight)
                    : nightFogDensity;
            }

            if (EnvironmentManager.Instance != null)
            {
                EnvironmentManager.Instance.daylightFactor = daylight;
                EnvironmentManager.Instance.isNight = daylight <= 0.15f;
            }

            // Sync skybox & volumetric cloud lighting cleanly without dirtying material assets
            Vector3 skySunDir = isDay
                ? -sunLight.transform.forward
                : (Quaternion.Euler(-15f, yawDeg, 0f) * Vector3.back);

            Color zenithCol = Color.Lerp(new Color(0.015f, 0.025f, 0.055f), new Color(0.12f, 0.34f, 0.72f), daylight);
            zenithCol = Color.Lerp(zenithCol, new Color(0.15f, 0.24f, 0.48f), goldenMix * 0.65f);

            Color cloudLit = Color.Lerp(new Color(0.15f, 0.20f, 0.32f), new Color(1.04f, 1.02f, 0.98f), daylight);
            cloudLit = Color.Lerp(cloudLit, new Color(1.06f, 0.68f, 0.40f), goldenMix * 0.85f);

            Color cloudShadow = Color.Lerp(new Color(0.04f, 0.06f, 0.11f), new Color(0.46f, 0.56f, 0.70f), daylight);
            cloudShadow = Color.Lerp(cloudShadow, new Color(0.38f, 0.30f, 0.42f), goldenMix * 0.75f);

            Shader.SetGlobalFloat(SkyActiveId, 1.0f);
            Shader.SetGlobalFloat(SkyDaylightId, daylight);
            Shader.SetGlobalFloat(SkyGoldenMixId, goldenMix);
            Shader.SetGlobalVector(SkySunDirId, new Vector4(skySunDir.x, skySunDir.y, skySunDir.z, 0f));
            Shader.SetGlobalColor(SkySunColorId, sunCol);
            Shader.SetGlobalColor(SkyZenithColorId, zenithCol);
            Shader.SetGlobalColor(SkyHorizonColorId, fogCol);
            Shader.SetGlobalColor(SkyGroundColorId, grdAmb);
            Shader.SetGlobalColor(SkyCloudLitColorId, cloudLit);
            Shader.SetGlobalColor(SkyCloudShadowColorId, cloudShadow);
            Shader.SetGlobalVector(SkyWindOffsetId, new Vector4(_cloudWindOffset.x, _cloudWindOffset.y, 0f, 0f));
        }
    }
}
