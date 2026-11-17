using UnityEngine;
using VRAtis.Ballistics;

namespace VRAtis.Environment
{
    /// <summary>
    /// Orchestrates GPU-instanced/procedural single-drop rain aligned with EnvironmentManager.windSpeedMS:
    /// 1) GPU near randomized raindrops around shooter/camera
    /// 2) GPU mid-valley randomized downpour volume
    /// 3) Ground water splash ripples around player
    /// 4) Overcast storm lighting & skybox darkening (no fog)
    /// </summary>
    public class RainWeatherController : MonoBehaviour
    {
        private static readonly int RainIntensityPropId = Shader.PropertyToID("_RainIntensity");
        private static readonly int RainWindXZPropId = Shader.PropertyToID("_RainWindXZ");
        private static readonly int RainAnchorPosPropId = Shader.PropertyToID("_RainAnchorPos");

        [Header("Referanslar")]
        [SerializeField] private EnvironmentManager environmentManager;
        [SerializeField] private DynamicTimeOfDay timeOfDayController;
        [SerializeField] private Transform cameraTransform;

        [Header("GPU Yağmur Katmanları ve Zemin Sıçramaları")]
        [SerializeField] private MeshRenderer nearRainRenderer;
        [SerializeField] private MeshRenderer midRainRenderer;
        [SerializeField] private ParticleSystem groundSplashParticles;

        [Header("Yağmur Sesi (Doğal Stereo + Rüzgar Etkileşimi)")]
        [SerializeField] private AudioClip rainAudioClip;
        [SerializeField] private AudioSource rainAudioSource;
        [SerializeField] private AudioLowPassFilter rainLowPassFilter;
        [Range(0f, 1f)]
        [SerializeField] private float rainMaxVolume = 0.34f;
        [SerializeField] private Vector2 rainLowPassRangeHz = new Vector2(9200f, 18000f);
        [Range(0f, 0.3f)]
        [SerializeField] private float rainWindPanAmount = 0.14f;

        [Header("Yoğunluk ve Geçiş Ayarları")]
        [SerializeField] private float transitionDurationSec = 1.6f;
        [SerializeField] private float splashMaxEmissionRate = 260f;

        [Header("Rüzgar ve Yerleşim")]
        [Tooltip("Yağmurun yatay rüzgar vektöründen ne kadar yatık sürükleneceğini belirler.")]
        [Range(0.5f, 3.0f)]
        [SerializeField] private float windDriftMultiplier = 1.45f;
        [SerializeField] private float nearAnchorForwardMeters = 4.5f;
        [SerializeField] private float nearAnchorHeightOffset = 4.5f;
        [SerializeField] private float midAnchorForwardMeters = 22.0f;
        [SerializeField] private float midAnchorHeightOffset = 7.5f;

        private float _rainIntensity01;
        private float _lastAppliedIntensity = -1f;
        private float _clearHumidityPercent = 45f;
        private bool _humidityCaptured;

        private MaterialPropertyBlock _nearPropBlock;
        private MaterialPropertyBlock _midPropBlock;
        private float _rainAudioSeed;
        private float _smoothedAudioPan;
        private bool _audioPlaybackStarted;

        public float CurrentRainIntensity01 => _rainIntensity01;

        private void Awake()
        {
            if (environmentManager == null)
                environmentManager = EnvironmentManager.Instance != null
                    ? EnvironmentManager.Instance
                    : FindAnyObjectByType<EnvironmentManager>();

            if (timeOfDayController == null)
                timeOfDayController = FindAnyObjectByType<DynamicTimeOfDay>();

            if (cameraTransform == null && Camera.main != null)
                cameraTransform = Camera.main.transform;

            _nearPropBlock = new MaterialPropertyBlock();
            _midPropBlock = new MaterialPropertyBlock();
            _rainAudioSeed = Random.Range(13f, 173f);

            ConfigureRainAudioSource();
            StopAllEmittersImmediate();
        }

        private void OnDisable()
        {
            SetRendererActiveAndZero(nearRainRenderer, ref _nearPropBlock);
            SetRendererActiveAndZero(midRainRenderer, ref _midPropBlock);

            if (timeOfDayController != null)
                timeOfDayController.SetRainWeatherFactor(0f);

            PauseRainAudioImmediate();
        }

        private void Update()
        {
            if (environmentManager == null)
            {
                environmentManager = EnvironmentManager.Instance;
                if (environmentManager == null)
                    return;
            }

            if (cameraTransform == null && Camera.main != null)
                cameraTransform = Camera.main.transform;

            if (!_humidityCaptured)
            {
                _clearHumidityPercent = environmentManager.HumidityPercent;
                _humidityCaptured = true;
            }

            float target = environmentManager.isRaining ? 1f : 0f;
            float step = Time.deltaTime / Mathf.Max(0.25f, transitionDurationSec);
            _rainIntensity01 = Mathf.MoveTowards(_rainIntensity01, target, step);

            if (!Mathf.Approximately(_rainIntensity01, _lastAppliedIntensity))
            {
                _lastAppliedIntensity = _rainIntensity01;
                ApplyWeatherState(_rainIntensity01);
            }

            if (_rainIntensity01 > 0.002f)
            {
                UpdateRainVolumesAndSplashes();
            }
        }

        private void ApplyWeatherState(float intensity01)
        {
            if (timeOfDayController != null)
                timeOfDayController.SetRainWeatherFactor(intensity01);

            if (environmentManager != null)
            {
                float targetHumidity = Mathf.Lerp(_clearHumidityPercent, 92f, intensity01);
                environmentManager.SetAtmosphere(
                    environmentManager.TemperatureC,
                    environmentManager.PressureHPa,
                    targetHumidity,
                    environmentManager.AltitudeMeters);
            }

            bool active = intensity01 > 0.002f;
            if (nearRainRenderer != null)
                nearRainRenderer.enabled = active;
            if (midRainRenderer != null)
                midRainRenderer.enabled = active;

            SetSplashRate(groundSplashParticles, splashMaxEmissionRate * intensity01);

            if (!active)
                PauseRainAudioImmediate();
        }

        private void UpdateRainVolumesAndSplashes()
        {
            Vector3 wind = environmentManager != null ? environmentManager.windSpeedMS : Vector3.zero;
            Vector4 windXZ = new Vector4(wind.x * windDriftMultiplier, wind.z * windDriftMultiplier, 0f, 0f);

            Vector3 camPos = cameraTransform != null ? cameraTransform.position : new Vector3(1.8f, 1.5f, -4.3f);
            Vector3 camFlatFwd = cameraTransform != null
                ? new Vector3(cameraTransform.forward.x, 0f, cameraTransform.forward.z)
                : Vector3.forward;

            camFlatFwd = camFlatFwd.sqrMagnitude > 0.001f ? camFlatFwd.normalized : Vector3.forward;

            Vector3 nearAnchor = camPos + camFlatFwd * nearAnchorForwardMeters;
            nearAnchor.y = Mathf.Max(4.0f, camPos.y + nearAnchorHeightOffset);
            UpdateGpuRainLayer(nearRainRenderer, ref _nearPropBlock, nearAnchor, windXZ, _rainIntensity01);

            Vector3 midAnchor = camPos + camFlatFwd * midAnchorForwardMeters;
            midAnchor.y = Mathf.Max(6.5f, camPos.y + midAnchorHeightOffset);
            UpdateGpuRainLayer(midRainRenderer, ref _midPropBlock, midAnchor, windXZ, _rainIntensity01);

            if (groundSplashParticles != null)
            {
                Vector3 splashCenter = camPos + camFlatFwd * 5.0f;
                groundSplashParticles.transform.position = new Vector3(splashCenter.x, 0.03f, splashCenter.z);
            }

            UpdateRainAudio(wind, _rainIntensity01);
        }

        private static void UpdateGpuRainLayer(
            MeshRenderer renderer,
            ref MaterialPropertyBlock propBlock,
            Vector3 anchorPos,
            Vector4 windXZ,
            float intensity01)
        {
            if (renderer == null)
                return;

            if (propBlock == null)
                propBlock = new MaterialPropertyBlock();

            renderer.transform.position = anchorPos;
            renderer.enabled = intensity01 > 0.002f;

            renderer.GetPropertyBlock(propBlock);
            propBlock.SetFloat(RainIntensityPropId, intensity01);
            propBlock.SetVector(RainWindXZPropId, windXZ);
            propBlock.SetVector(RainAnchorPosPropId, new Vector4(anchorPos.x, anchorPos.y, anchorPos.z, 0f));
            renderer.SetPropertyBlock(propBlock);
        }

        private static void SetRendererActiveAndZero(MeshRenderer renderer, ref MaterialPropertyBlock propBlock)
        {
            if (renderer == null)
                return;

            if (propBlock == null)
                propBlock = new MaterialPropertyBlock();

            renderer.GetPropertyBlock(propBlock);
            propBlock.SetFloat(RainIntensityPropId, 0f);
            renderer.SetPropertyBlock(propBlock);
            renderer.enabled = false;
        }

        private static void SetSplashRate(ParticleSystem ps, float rate)
        {
            if (ps == null)
                return;

            var emission = ps.emission;
            emission.rateOverTime = rate;

            if (rate > 0.1f && !ps.isPlaying)
                ps.Play();
            else if (rate <= 0.1f && ps.isPlaying)
                ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }

        private void ConfigureRainAudioSource()
        {
            if (rainAudioSource == null)
                return;

            if (rainAudioClip != null && rainAudioSource.clip == null)
                rainAudioSource.clip = rainAudioClip;

            rainAudioSource.loop = true;
            rainAudioSource.playOnAwake = false;
            rainAudioSource.spatialBlend = 0f;
            rainAudioSource.dopplerLevel = 0f;
            rainAudioSource.priority = 195;
            rainAudioSource.volume = 0f;

            if (rainLowPassFilter == null)
                rainLowPassFilter = rainAudioSource.GetComponent<AudioLowPassFilter>();
        }

        private void UpdateRainAudio(Vector3 windVec, float intensity01)
        {
            if (rainAudioSource == null)
                return;

            if (rainAudioSource.clip == null && rainAudioClip != null)
                rainAudioSource.clip = rainAudioClip;

            if (rainAudioSource.clip == null)
                return;

            if (!rainAudioSource.isPlaying)
            {
                if (!_audioPlaybackStarted)
                {
                    rainAudioSource.time = rainAudioSource.clip.length * Random.Range(0.05f, 0.85f);
                    rainAudioSource.Play();
                    _audioPlaybackStarted = true;
                }
                else
                {
                    rainAudioSource.UnPause();
                    if (!rainAudioSource.isPlaying)
                        rainAudioSource.Play();
                }
            }

            float windSpeed = windVec.magnitude;
            float gustNorm = Mathf.Clamp01(windSpeed / 12f);

            // Slow natural rain-sheet swell keeps steady ambience from tiring the shooter's ears
            float t = Time.unscaledTime;
            float swellNoise = Mathf.PerlinNoise(_rainAudioSeed + t * 0.13f, 0.41f) - 0.5f;
            float organicSwell = 1f + swellNoise * 0.22f;
            float windGain = Mathf.Lerp(0.92f, 1.12f, gustNorm);

            float envelope = Mathf.SmoothStep(0f, 1f, intensity01);
            rainAudioSource.volume = Mathf.Clamp01(rainMaxVolume * envelope * organicSwell * windGain);

            float targetPan = 0f;
            if (cameraTransform != null && windSpeed > 0.4f)
            {
                Vector3 horizWind = new Vector3(windVec.x, 0f, windVec.z);
                if (horizWind.sqrMagnitude > 0.01f)
                {
                    Vector3 windIncomingDir = -horizWind.normalized;
                    Vector3 headRight = new Vector3(cameraTransform.right.x, 0f, cameraTransform.right.z).normalized;
                    targetPan = Mathf.Clamp(Vector3.Dot(headRight, windIncomingDir) * rainWindPanAmount * gustNorm, -rainWindPanAmount, rainWindPanAmount);
                }
            }

            _smoothedAudioPan = Mathf.Lerp(_smoothedAudioPan, targetPan, 1f - Mathf.Exp(-5f * Time.unscaledDeltaTime));
            rainAudioSource.panStereo = _smoothedAudioPan;

            if (rainLowPassFilter != null)
                rainLowPassFilter.cutoffFrequency = Mathf.Lerp(rainLowPassRangeHz.x, rainLowPassRangeHz.y, gustNorm * intensity01);
        }

        private void PauseRainAudioImmediate()
        {
            if (rainAudioSource == null)
                return;

            rainAudioSource.volume = 0f;
            if (rainAudioSource.isPlaying)
                rainAudioSource.Pause();
        }

        private void StopAllEmittersImmediate()
        {
            SetRendererActiveAndZero(nearRainRenderer, ref _nearPropBlock);
            SetRendererActiveAndZero(midRainRenderer, ref _midPropBlock);
            PauseRainAudioImmediate();

            if (groundSplashParticles == null)
                return;

            var emission = groundSplashParticles.emission;
            emission.rateOverTime = 0f;
            groundSplashParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }
    }
}
