using UnityEngine;
using VRAtis.Ballistics;

namespace VRAtis.Environment
{
    /// <summary>
    /// Synthesizes a multi-layered, non-monotonous outdoor wind soundscape driven in real time
    /// by EnvironmentManager.windSpeedMS and head orientation inside VR headphones.
    /// Layer 1 (Calm): Two dual-deck valley air beds slowly breathing out of phase.
    /// Layer 2 (Gust): Two coprime-length cold gust beds with Perlin swell modulation and LP filtering.
    /// </summary>
    public class WindAudioController : MonoBehaviour
    {
        [Header("Referanslar")]
        [SerializeField] private EnvironmentManager environmentManager;
        [SerializeField] private Transform headCameraTransform;

        [Header("Rüzgar Ses Klipleri (2 Sakin Vadi + 2 Şiddetli Esinti)")]
        [SerializeField] private AudioClip[] calmWindClips;
        [SerializeField] private AudioClip[] gustWindClips;

        [Header("Genel Denge (Atış Sesleriyle Uyumlu)")]
        [Range(0f, 1.5f)]
        [SerializeField] private float masterWindVolume = 0.22f;

        [Range(0f, 1.5f)]
        [SerializeField] private float calmLayerGain = 0.72f;

        [Range(0f, 1.5f)]
        [SerializeField] private float gustLayerGain = 0.95f;

        [Header("Rüzgar Hızı Tepki Eğrisi (m/sn)")]
        [Tooltip("Bu hızın altında ortam neredeyse tamamen sessiz kalır.")]
        [SerializeField] private float calmStartSpeedMS = 0.2f;
        [Tooltip("Sakin vadi esintisinin tam dolgunluğa ulaştığı hız (m/sn).")]
        [SerializeField] private float calmFullSpeedMS = 6.0f;
        [Tooltip("Soğuk/şiddetli rüzgar uğultusunun devreye girmeye başladığı hız (m/sn).")]
        [SerializeField] private float gustStartSpeedMS = 4.2f;
        [Tooltip("Şiddetli rüzgar uğultusunun tam güce ulaştığı hız (m/sn).")]
        [SerializeField] private float gustFullSpeedMS = 13.0f;

        [Header("Doğal Dalgalanma ve VR Kulak Yönü Tepkisi")]
        [Tooltip("Tekdüzelikten kaçınmak için doğal Perlin rüzgar dalgaları ekler.")]
        [SerializeField] private bool enableNaturalGustWaves = true;

        [Range(0f, 0.45f)]
        [SerializeField] private float gustWaveAmplitude = 0.22f;

        [Tooltip("VR'da kafanı rüzgara karşı çevirdiğinde kulaklarda hafif stereo ve ton farkı verir.")]
        [SerializeField] private bool enableHeadDirectionBinaural = true;

        [Range(0f, 0.45f)]
        [SerializeField] private float maxWindStereoPan = 0.28f;

        private struct WindDeck
        {
            public AudioSource Source;
            public AudioLowPassFilter LowPass;
            public float PhaseOffset;
        }

        private WindDeck[] _calmDecks;
        private WindDeck[] _gustDecks;
        private float _smoothedSpeedMS;
        private float _smoothedPan;
        private float _gustSeedA;
        private float _gustSeedB;

        private void Awake()
        {
            if (environmentManager == null)
                environmentManager = EnvironmentManager.Instance != null
                    ? EnvironmentManager.Instance
                    : FindAnyObjectByType<EnvironmentManager>();

            if (headCameraTransform == null && Camera.main != null)
                headCameraTransform = Camera.main.transform;

            _gustSeedA = Random.Range(11f, 97f);
            _gustSeedB = Random.Range(103f, 241f);

            BuildAudioDecks();
        }

        private void OnEnable()
        {
            ResumeActiveClips();
        }

        private void OnDisable()
        {
            PauseDecks(_calmDecks);
            PauseDecks(_gustDecks);
        }

        private void Update()
        {
            if (environmentManager == null)
            {
                environmentManager = EnvironmentManager.Instance;
                if (environmentManager == null)
                    return;
            }

            if (headCameraTransform == null && Camera.main != null)
                headCameraTransform = Camera.main.transform;

            Vector3 windVec = environmentManager.windSpeedMS;
            float targetSpeed = windVec.magnitude;

            // Smooth sudden Inspector / weather changes so sound never jumps abruptly
            _smoothedSpeedMS = Mathf.Lerp(_smoothedSpeedMS, targetSpeed, 1f - Mathf.Exp(-3.5f * Time.unscaledDeltaTime));

            float time = Time.unscaledTime;
            float wave = 1f;
            if (enableNaturalGustWaves)
            {
                float p1 = Mathf.PerlinNoise(_gustSeedA + time * 0.14f, 0.37f) - 0.5f;
                float p2 = Mathf.PerlinNoise(0.73f, _gustSeedB + time * 0.31f) - 0.5f;
                wave = Mathf.Clamp(1f + (p1 * 1.3f + p2 * 0.7f) * gustWaveAmplitude * 2f, 0.55f, 1.45f);
            }

            float effectiveSpeed = _smoothedSpeedMS * wave;

            // Subtle VR directional panning + ear brightness based on where head faces vs wind vector
            float targetPan = 0f;
            float headFacingWindFactor = 0.5f;
            if (enableHeadDirectionBinaural && headCameraTransform != null && targetSpeed > 0.25f)
            {
                Vector3 horizontalWind = new Vector3(windVec.x, 0f, windVec.z);
                if (horizontalWind.sqrMagnitude > 0.01f)
                {
                    Vector3 windIncomingDir = -horizontalWind.normalized; // Direction wind comes FROM
                    Vector3 headRight = new Vector3(headCameraTransform.right.x, 0f, headCameraTransform.right.z).normalized;
                    Vector3 headFwd = new Vector3(headCameraTransform.forward.x, 0f, headCameraTransform.forward.z).normalized;

                    float sideDot = Vector3.Dot(headRight, windIncomingDir);
                    targetPan = Mathf.Clamp(sideDot * maxWindStereoPan, -maxWindStereoPan, maxWindStereoPan);

                    // Looking directly into wind or sideways gives crisp ear rush; wind behind head is slightly warmer
                    headFacingWindFactor = Mathf.Clamp01(Vector3.Dot(headFwd, windIncomingDir) * 0.5f + 0.5f);
                }
            }
            _smoothedPan = Mathf.Lerp(_smoothedPan, targetPan, 1f - Mathf.Exp(-6f * Time.unscaledDeltaTime));

            UpdateCalmLayer(effectiveSpeed, time, headFacingWindFactor);
            UpdateGustLayer(effectiveSpeed, time, headFacingWindFactor);
        }

        private void UpdateCalmLayer(float speedMS, float time, float headFacingFactor)
        {
            if (_calmDecks == null || _calmDecks.Length == 0)
                return;

            float baseNorm = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(calmStartSpeedMS, calmFullSpeedMS, speedMS));
            // Keep calm valley floor gently present during moderate/high wind rather than vanishing completely
            float targetVol = baseNorm * masterWindVolume * calmLayerGain;
            float pitch = Mathf.Lerp(0.95f, 1.07f, Mathf.Clamp01(speedMS / 14f));
            float lpCutoff = Mathf.Lerp(1150f, 3600f, baseNorm) + Mathf.Lerp(-280f, 450f, headFacingFactor);

            UpdateDeckGroup(_calmDecks, targetVol, pitch, lpCutoff, cycleSeconds: 21f, time: time);
        }

        private void UpdateGustLayer(float speedMS, float time, float headFacingFactor)
        {
            if (_gustDecks == null || _gustDecks.Length == 0)
                return;

            float gustNorm = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(gustStartSpeedMS, gustFullSpeedMS, speedMS));
            float targetVol = gustNorm * gustNorm * 0.65f + gustNorm * 0.35f;
            targetVol *= masterWindVolume * gustLayerGain;

            float pitch = Mathf.Lerp(0.92f, 1.12f, gustNorm);
            float lpCutoff = Mathf.Lerp(950f, 18500f, gustNorm) + Mathf.Lerp(-500f, 900f, headFacingFactor);

            UpdateDeckGroup(_gustDecks, targetVol, pitch, lpCutoff, cycleSeconds: 17f, time: time);
        }

        private void UpdateDeckGroup(WindDeck[] decks, float groupVolume, float basePitch, float lpCutoff, float cycleSeconds, float time)
        {
            bool shouldPlay = groupVolume > 0.003f;

            for (int i = 0; i < decks.Length; i++)
            {
                AudioSource src = decks[i].Source;
                if (src == null || src.clip == null)
                    continue;

                if (!shouldPlay)
                {
                    if (src.isPlaying)
                        src.Pause();
                    src.volume = 0f;
                    continue;
                }

                if (!src.isPlaying)
                    src.UnPause();

                if (!src.isPlaying)
                    src.Play();

                // If 2 decks share the layer, gently cross-breathe between clip #1 and #2 so no loop seam or cadence ever repeats
                float layerShare = 1f;
                if (decks.Length > 1)
                {
                    float phase = (time / Mathf.Max(6f, cycleSeconds)) * Mathf.PI * 2f + decks[i].PhaseOffset;
                    // Equal-power weight around 0.25 .. 0.95 so both textures weave naturally together
                    layerShare = 0.58f + 0.34f * Mathf.Sin(phase);
                }

                src.volume = Mathf.Clamp01(groupVolume * layerShare);
                src.pitch = Mathf.Clamp(basePitch + (i == 0 ? -0.01f : 0.012f), 0.7f, 1.4f);
                src.panStereo = _smoothedPan;

                if (decks[i].LowPass != null)
                    decks[i].LowPass.cutoffFrequency = Mathf.Clamp(lpCutoff, 600f, 22000f);
            }
        }

        private void BuildAudioDecks()
        {
            _calmDecks = CreateDeckBank("WindCalmDeck", calmWindClips, phaseStride: Mathf.PI);
            _gustDecks = CreateDeckBank("WindGustDeck", gustWindClips, phaseStride: Mathf.PI * 0.85f);
            ResumeActiveClips();
        }

        private WindDeck[] CreateDeckBank(string prefix, AudioClip[] clips, float phaseStride)
        {
            if (clips == null || clips.Length == 0)
                return new WindDeck[0];

            int count = Mathf.Min(2, clips.Length);
            WindDeck[] decks = new WindDeck[count];

            for (int i = 0; i < count; i++)
            {
                AudioClip clip = clips[i];
                Transform existing = transform.Find($"{prefix}_{i}");
                GameObject go = existing != null ? existing.gameObject : new GameObject($"{prefix}_{i}");
                if (existing == null)
                    go.transform.SetParent(transform, false);

                AudioSource src = go.GetComponent<AudioSource>();
                if (src == null)
                    src = go.AddComponent<AudioSource>();

                src.clip = clip;
                src.loop = true;
                src.playOnAwake = false;
                src.spatialBlend = 0f; // 2D stereo ambient field + manual VR head panStereo
                src.dopplerLevel = 0f;
                src.volume = 0f;
                src.priority = 200; // Lower priority than foreground gunshots (0-128) so shots never drop

                AudioLowPassFilter lp = go.GetComponent<AudioLowPassFilter>();
                if (lp == null)
                    lp = go.AddComponent<AudioLowPassFilter>();
                lp.cutoffFrequency = 2500f;
                lp.lowpassResonanceQ = 0.85f;

                decks[i] = new WindDeck
                {
                    Source = src,
                    LowPass = lp,
                    PhaseOffset = i * phaseStride
                };
            }

            return decks;
        }

        private void ResumeActiveClips()
        {
            StartDecksAtStaggeredOffsets(_calmDecks);
            StartDecksAtStaggeredOffsets(_gustDecks);
        }

        private static void StartDecksAtStaggeredOffsets(WindDeck[] decks)
        {
            if (decks == null)
                return;

            for (int i = 0; i < decks.Length; i++)
            {
                AudioSource src = decks[i].Source;
                if (src != null && src.clip != null && !src.isPlaying)
                {
                    float offsetFrac = (i * 0.43f + Random.Range(0.02f, 0.25f)) % 0.9f;
                    src.time = src.clip.length * offsetFrac;
                    src.Play();
                }
            }
        }

        private static void PauseDecks(WindDeck[] decks)
        {
            if (decks == null)
                return;

            for (int i = 0; i < decks.Length; i++)
            {
                if (decks[i].Source != null && decks[i].Source.isPlaying)
                    decks[i].Source.Pause();
            }
        }
    }
}
