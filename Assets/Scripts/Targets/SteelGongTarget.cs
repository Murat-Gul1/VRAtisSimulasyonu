using UnityEngine;
using VRAtis.Ballistics;

namespace VRAtis.Targets
{
    /// <summary>
    /// Articulated AR500 steel gong target mounted by twin chains to a ground stand.
    /// Responds to ballistic impact momentum with damped pendulum swing, lateral chain twist,
    /// bullet lead-splash marks attached to the swinging plate, brief visible hit sparks,
    /// and speed-of-sound delayed valley-audible steel clang audio.
    /// </summary>
    public class SteelGongTarget : MonoBehaviour
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        [Header("Hedef Hiyerarşisi")]
        [SerializeField] private Transform gongPivot;
        [SerializeField] private Transform gongPlateTransform;
        [SerializeField] private Transform standFrameTransform;
        [Tooltip("Salınım menteşesi yerel dönüş ekseni (genelde gongPivot'un sağ ekseni).")]
        [SerializeField] private Vector3 localSwingAxis = Vector3.right;

        [Header("Fiziksel Sarkaç Ayarları")]
        [Tooltip("Zincir üst askı noktasından plaka ağırlık merkezine etkin sarkaç boyu (metre).")]
        [SerializeField] private float pendulumLengthMeters = 0.62f;
        [Tooltip("AR500 plaka kütlesi (kg).")]
        [SerializeField] private float plateMassKg = 12f;
        [SerializeField] private float angularDamping = 0.85f;
        [SerializeField] private float maxSwingAngleDeg = 72f;
        [SerializeField] private float lateralTwistStiffness = 26f;
        [SerializeField] private float lateralTwistDamping = 3.2f;

        [Header("Kurşun İzi & Kıvılcım (Zero-GC Havuz)")]
        [SerializeField] private int maxSplashMarks = 28;
        [SerializeField] private float splashMarkDiameterMeters = 0.052f;
        [SerializeField] private float sparkFlashDurationSec = 0.085f;
        [SerializeField] private float sparkSizeMeters = 0.16f;

        [Header("3D Ses ve Ses Hızı Gecikmesi")]
        [SerializeField] private AudioClip[] gongHitClips;
        [SerializeField] private AudioClip[] frameClankClips;
        [SerializeField] private bool useSpeedOfSoundDelay = true;
        [Range(0f, 1f)]
        [SerializeField] private float gongVolume = 1.0f;
        [Range(0f, 1f)]
        [SerializeField] private float frameVolume = 0.72f;
        [SerializeField] private Vector2 pitchRange = new Vector2(0.95f, 1.05f);
        [SerializeField] private float minAudibleDistance = 45f;
        [SerializeField] private float maxAudibleDistance = 1200f;

        public int HitCount { get; private set; }
        public float LastImpactDistanceMeters { get; private set; }
        public float LastImpactEnergyJoules { get; private set; }

        private Quaternion _restPivotLocalRot = Quaternion.identity;
        private Vector3 _resolvedLocalAxis = Vector3.right;
        private Vector3 _resolvedLocalTwistAxis = Vector3.up;
        private float _swingAngleRad;
        private float _swingVelRad;
        private float _twistAngleRad;
        private float _twistVelRad;
        private float _frameShudderTimer;
        private Quaternion _restFrameLocalRot = Quaternion.identity;

        private Transform[] _splashMarks;
        private int _nextSplashIndex;
        private static Material _sharedSplashMaterial;

        private Transform _sparkTransform;
        private Renderer _sparkRenderer;
        private float _sparkTimer;
        private static Material _sharedSparkMaterial;

        private AudioSource[] _audioVoices;
        private int _nextVoiceIndex;
        private int _lastGongClipIdx = -1;
        private int _lastFrameClipIdx = -1;
        private Transform _listenerTransform;

        private void Awake()
        {
            ResolveDefaults();
            ConfigurePendulumAxis();
            PrewarmSplashMarks();
            PrewarmHitSpark();
            BuildAudioPool();
        }

        private void ResolveDefaults()
        {
            if (gongPivot == null)
                gongPivot = FindChildByName(transform, "Gong_Pivot");
            if (gongPlateTransform == null)
                gongPlateTransform = FindChildByName(transform, "AR500_GongPlate");
            if (standFrameTransform == null)
                standFrameTransform = FindChildByName(transform, "Stand_Frame");

            if (gongPivot != null)
                _restPivotLocalRot = gongPivot.localRotation;
            if (standFrameTransform != null)
                _restFrameLocalRot = standFrameTransform.localRotation;

            RefreshGeometryMetrics();

            if (Camera.main != null)
                _listenerTransform = Camera.main.transform;
        }

        public void RefreshGeometryMetrics()
        {
            if (gongPivot != null && gongPlateTransform != null)
            {
                float measuredLen = Vector3.Distance(gongPivot.position, gongPlateTransform.position);
                if (measuredLen > 0.1f)
                    pendulumLengthMeters = measuredLen;
            }
        }

        public void ResetVisualState()
        {
            HitCount = 0;
            LastImpactDistanceMeters = 0f;
            LastImpactEnergyJoules = 0f;
            _swingAngleRad = 0f;
            _swingVelRad = 0f;
            _twistAngleRad = 0f;
            _twistVelRad = 0f;
            _frameShudderTimer = 0f;
            _sparkTimer = 0f;

            if (gongPivot != null)
                gongPivot.localRotation = _restPivotLocalRot;
            if (standFrameTransform != null)
                standFrameTransform.localRotation = _restFrameLocalRot;
            if (_sparkTransform != null)
                _sparkTransform.gameObject.SetActive(false);

            if (_splashMarks != null)
            {
                for (int i = 0; i < _splashMarks.Length; i++)
                {
                    if (_splashMarks[i] != null)
                        _splashMarks[i].gameObject.SetActive(false);
                }
            }

            ConfigurePendulumAxis();
            RefreshGeometryMetrics();
        }

        private void ConfigurePendulumAxis()
        {
            if (gongPivot != null)
            {
                _restPivotLocalRot = gongPivot.localRotation;
                Vector3 worldCrossbar = transform.right;
                Vector3 localFromRight = gongPivot.InverseTransformDirection(worldCrossbar);
                _resolvedLocalAxis = localFromRight.sqrMagnitude > 1e-4f
                    ? localFromRight.normalized
                    : (localSwingAxis.sqrMagnitude > 1e-4f ? localSwingAxis.normalized : Vector3.right);

                Vector3 localFromUp = gongPivot.InverseTransformDirection(Vector3.up);
                _resolvedLocalTwistAxis = localFromUp.sqrMagnitude > 1e-4f
                    ? localFromUp.normalized
                    : Vector3.up;
            }
            else
            {
                _resolvedLocalAxis = localSwingAxis.sqrMagnitude > 1e-4f ? localSwingAxis.normalized : Vector3.right;
                _resolvedLocalTwistAxis = Vector3.up;
            }
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f)
                return;

            // Sub-step pendulum ODE lightly so frame-rate swings remain smooth even at low frame times
            if (gongPivot != null && (Mathf.Abs(_swingAngleRad) > 1e-4f || Mathf.Abs(_swingVelRad) > 1e-4f ||
                                      Mathf.Abs(_twistAngleRad) > 1e-4f || Mathf.Abs(_twistVelRad) > 1e-4f))
            {
                float g = EnvironmentManager.Instance != null ? EnvironmentManager.Instance.gravity : 9.80665f;
                float effectiveLen = Mathf.Max(0.25f, pendulumLengthMeters * 0.85f);

                int steps = dt > 0.016f ? 2 : 1;
                float stepDt = dt / steps;
                float maxRad = maxSwingAngleDeg * Mathf.Deg2Rad;

                for (int i = 0; i < steps; i++)
                {
                    float swingAccel = -(g / effectiveLen) * Mathf.Sin(_swingAngleRad) - angularDamping * _swingVelRad;
                    _swingVelRad += swingAccel * stepDt;
                    _swingAngleRad += _swingVelRad * stepDt;

                    if (Mathf.Abs(_swingAngleRad) > maxRad)
                    {
                        _swingAngleRad = Mathf.Sign(_swingAngleRad) * maxRad;
                        if (Mathf.Sign(_swingVelRad) == Mathf.Sign(_swingAngleRad))
                            _swingVelRad *= -0.35f; // Top chain limit bounce
                    }

                    float twistAccel = -lateralTwistStiffness * _twistAngleRad - lateralTwistDamping * _twistVelRad;
                    _twistVelRad += twistAccel * stepDt;
                    _twistAngleRad += _twistVelRad * stepDt;
                }

                if (Mathf.Abs(_swingAngleRad) < 0.0005f && Mathf.Abs(_swingVelRad) < 0.002f &&
                    Mathf.Abs(_twistAngleRad) < 0.0005f && Mathf.Abs(_twistVelRad) < 0.002f)
                {
                    _swingAngleRad = 0f;
                    _swingVelRad = 0f;
                    _twistAngleRad = 0f;
                    _twistVelRad = 0f;
                    gongPivot.localRotation = _restPivotLocalRot;
                }
                else
                {
                    Quaternion swingQ = Quaternion.AngleAxis(_swingAngleRad * Mathf.Rad2Deg, _resolvedLocalAxis);
                    Quaternion twistQ = Quaternion.AngleAxis(_twistAngleRad * Mathf.Rad2Deg, _resolvedLocalTwistAxis);
                    gongPivot.localRotation = _restPivotLocalRot * swingQ * twistQ;
                }
            }

            if (_sparkTimer > 0f && _sparkTransform != null)
            {
                _sparkTimer -= dt;
                if (_sparkTimer <= 0f)
                {
                    _sparkTransform.gameObject.SetActive(false);
                }
                else
                {
                    float norm01 = Mathf.Clamp01(_sparkTimer / Mathf.Max(0.01f, sparkFlashDurationSec));
                    float s = sparkSizeMeters * Mathf.Max(0.35f, transform.lossyScale.y) * (0.6f + 0.65f * norm01);
                    _sparkTransform.localScale = Vector3.one * s;
                }
            }

            if (_frameShudderTimer > 0f && standFrameTransform != null)
            {
                _frameShudderTimer -= dt;
                if (_frameShudderTimer <= 0f)
                {
                    standFrameTransform.localRotation = _restFrameLocalRot;
                }
                else
                {
                    float jitter = Mathf.Sin(Time.time * 190f) * 0.45f * Mathf.Clamp01(_frameShudderTimer / 0.14f);
                    standFrameTransform.localRotation = _restFrameLocalRot * Quaternion.Euler(jitter, 0f, 0f);
                }
            }
        }

        public void HandlePlateImpact(in BulletImpactInfo impact)
        {
            HitCount++;
            LastImpactDistanceMeters = impact.DistanceTraveledMeters;
            LastImpactEnergyJoules = impact.KineticEnergyJoules;

            ApplyPendulumImpulse(in impact);
            PlaceSplashMark(gongPlateTransform != null ? gongPlateTransform : transform, in impact, isFrame: false);
            TriggerImpactSpark(impact.HitPoint + impact.HitNormal * 0.02f);
            PlayDelayedImpactAudio(gongHitClips, ref _lastGongClipIdx, gongVolume, impact.HitPoint);
            Debug.Log($"[Gong Vuruşu] {name} | İsabet #{HitCount} | Mesafe={LastImpactDistanceMeters:F1}m | Enerji={LastImpactEnergyJoules:F0}J | SalınımHızı={_swingVelRad:F2} rad/s");
        }

        public void HandleFrameImpact(in BulletImpactInfo impact)
        {
            _frameShudderTimer = 0.14f;
            PlaceSplashMark(standFrameTransform != null ? standFrameTransform : transform, in impact, isFrame: true);
            TriggerImpactSpark(impact.HitPoint + impact.HitNormal * 0.02f);
            PlayDelayedImpactAudio(frameClankClips, ref _lastFrameClipIdx, frameVolume, impact.HitPoint);
            Debug.Log($"[Stand Vuruşu] {name} ayağına isabet | Mesafe={impact.DistanceTraveledMeters:F1}m");
        }

        private void ApplyPendulumImpulse(in BulletImpactInfo impact)
        {
            if (gongPivot == null || gongPlateTransform == null)
                return;

            Vector3 armDir = (gongPlateTransform.position - gongPivot.position);
            float armLen = Mathf.Max(0.25f, armDir.magnitude);
            Vector3 armNorm = armDir / armLen;

            Vector3 velDir = impact.ImpactVelocity.sqrMagnitude > 1f
                ? impact.ImpactVelocity.normalized
                : -impact.HitNormal;

            // Ensure swing direction always moves away along the incoming bullet velocity
            Vector3 worldAxis = gongPivot.TransformDirection(_resolvedLocalAxis).normalized;
            Vector3 testRotDir = (Quaternion.AngleAxis(1f, worldAxis) * armNorm) - armNorm;
            float polarity = Vector3.Dot(testRotDir, velDir) >= 0f ? 1f : -1f;

            // Impulse transfer J = (1 + e) * m * v scaled cleanly onto physical plate inertia I ~ m_plate * L^2
            float bulletSpeed = impact.ImpactVelocity.magnitude;
            float linearImpulse = 1.45f * Mathf.Max(0.002f, impact.BulletMassKg) * bulletSpeed;
            float effectiveMass = Mathf.Max(4f, plateMassKg);
            float leverArm = Mathf.Max(0.25f, Vector3.Distance(gongPivot.position, impact.HitPoint));

            float deltaOmega = polarity * ((linearImpulse * leverArm) / (effectiveMass * armLen * armLen)) * 3.2f;
            _swingVelRad = Mathf.Clamp(_swingVelRad + deltaOmega, -10.5f, 10.5f);

            // Lateral off-center hit introduces asymmetric chain twist
            float scaleFactor = Mathf.Max(0.5f, transform.lossyScale.y);
            float lateralOffset = Vector3.Dot(impact.HitPoint - gongPlateTransform.position, transform.right);
            float plateHalfWidth = Mathf.Max(0.15f, 0.28f * scaleFactor);
            float normalizedLateral = Mathf.Clamp(lateralOffset / plateHalfWidth, -1f, 1f);
            _twistVelRad = Mathf.Clamp(_twistVelRad - normalizedLateral * Mathf.Abs(deltaOmega) * 0.80f, -9f, 9f);
        }

        private void PrewarmSplashMarks()
        {
            Transform parentForMarks = gongPlateTransform != null ? gongPlateTransform : transform;
            int count = Mathf.Max(4, maxSplashMarks);
            _splashMarks = new Transform[count];
            Material mat = GetSplashMaterial();

            for (int i = 0; i < count; i++)
            {
                GameObject mark = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                mark.name = $"LeadSplash_{i}";
                Collider col = mark.GetComponent<Collider>();
                if (col != null) Destroy(col);

                mark.transform.SetParent(parentForMarks, false);
                if (mat != null)
                    mark.GetComponent<MeshRenderer>().sharedMaterial = mat;

                mark.SetActive(false);
                _splashMarks[i] = mark.transform;
            }
        }

        private void PlaceSplashMark(Transform targetTransform, in BulletImpactInfo impact, bool isFrame)
        {
            if (_splashMarks == null || _splashMarks.Length == 0 || targetTransform == null)
                return;

            Transform mark = _splashMarks[_nextSplashIndex];
            _nextSplashIndex = (_nextSplashIndex + 1) % _splashMarks.Length;

            mark.SetParent(targetTransform, true);
            mark.position = impact.HitPoint + impact.HitNormal * 0.0025f;
            mark.rotation = Quaternion.LookRotation(Vector3.Cross(impact.HitNormal, Vector3.up).sqrMagnitude > 1e-4f
                ? Vector3.up
                : Vector3.forward, impact.HitNormal);

            float sizeWorld = isFrame
                ? splashMarkDiameterMeters * 0.65f
                : splashMarkDiameterMeters * Mathf.Max(0.8f, transform.lossyScale.y);

            Vector3 pScale = targetTransform.lossyScale;
            mark.localScale = new Vector3(
                sizeWorld / Mathf.Max(0.05f, Mathf.Abs(pScale.x)),
                0.0022f / Mathf.Max(0.05f, Mathf.Abs(pScale.y)),
                sizeWorld / Mathf.Max(0.05f, Mathf.Abs(pScale.z)));
            mark.gameObject.SetActive(true);
        }

        private void PrewarmHitSpark()
        {
            GameObject spark = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            spark.name = "HitSparkBurst";
            Collider col = spark.GetComponent<Collider>();
            if (col != null) Destroy(col);

            spark.transform.SetParent(transform, false);
            spark.GetComponent<MeshRenderer>().sharedMaterial = GetSparkMaterial();
            spark.SetActive(false);
            _sparkTransform = spark.transform;
            _sparkRenderer = spark.GetComponent<Renderer>();
        }

        private void TriggerImpactSpark(Vector3 worldPos)
        {
            if (_sparkTransform == null)
                return;

            _sparkTransform.position = worldPos;
            _sparkTransform.localScale = Vector3.one * (sparkSizeMeters * Mathf.Max(0.45f, transform.lossyScale.y));
            _sparkTransform.gameObject.SetActive(true);
            _sparkTimer = sparkFlashDurationSec;
        }

        private void BuildAudioPool()
        {
            _audioVoices = new AudioSource[2];
            AnimationCurve customRolloff = new AnimationCurve(
                new Keyframe(0f, 1.0f),
                new Keyframe(minAudibleDistance, 1.0f),
                new Keyframe(150f, 0.72f),
                new Keyframe(350f, 0.52f),
                new Keyframe(600f, 0.36f),
                new Keyframe(maxAudibleDistance, 0.08f));

            Transform audioParent = gongPlateTransform != null ? gongPlateTransform : transform;
            for (int i = 0; i < _audioVoices.Length; i++)
            {
                GameObject voiceGo = new GameObject($"ImpactAudioVoice_{i}");
                voiceGo.transform.SetParent(audioParent, false);

                AudioSource src = voiceGo.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.loop = false;
                src.spatialBlend = 0.72f;
                src.dopplerLevel = 0f;
                src.minDistance = minAudibleDistance;
                src.maxDistance = maxAudibleDistance;
                src.rolloffMode = AudioRolloffMode.Custom;
                src.SetCustomCurve(AudioSourceCurveType.CustomRolloff, customRolloff);
                _audioVoices[i] = src;
            }
        }

        private void PlayDelayedImpactAudio(AudioClip[] clips, ref int lastIdx, float volume, Vector3 hitWorldPos)
        {
            if (_audioVoices == null || _audioVoices.Length == 0 || clips == null || clips.Length == 0)
                return;

            AudioClip clip = SelectRandomClip(clips, ref lastIdx);
            if (clip == null)
                return;

            AudioSource src = _audioVoices[_nextVoiceIndex];
            _nextVoiceIndex = (_nextVoiceIndex + 1) % _audioVoices.Length;

            src.transform.position = hitWorldPos;
            src.clip = clip;
            src.pitch = Random.Range(pitchRange.x, pitchRange.y);
            src.volume = volume;

            float delaySec = 0f;
            if (useSpeedOfSoundDelay)
            {
                if (_listenerTransform == null && Camera.main != null)
                    _listenerTransform = Camera.main.transform;

                float distToListener = _listenerTransform != null
                    ? Vector3.Distance(hitWorldPos, _listenerTransform.position)
                    : LastImpactDistanceMeters;

                float sos = EnvironmentManager.Instance != null
                    ? Mathf.Max(200f, EnvironmentManager.Instance.CurrentSpeedOfSound)
                    : 340.3f;

                delaySec = Mathf.Clamp(distToListener / sos, 0f, 4f);
            }

            if (delaySec > 0.004f)
                src.PlayDelayed(delaySec);
            else
                src.Play();
        }

        private static AudioClip SelectRandomClip(AudioClip[] clips, ref int lastIndex)
        {
            if (clips.Length == 1)
                return clips[0];

            int idx = Random.Range(0, clips.Length);
            if (idx == lastIndex)
                idx = (idx + 1) % clips.Length;
            lastIndex = idx;
            return clips[idx];
        }

        private static Material GetSplashMaterial()
        {
            if (_sharedSplashMaterial != null)
                return _sharedSplashMaterial;

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) return null;

            _sharedSplashMaterial = new Material(shader);
            _sharedSplashMaterial.SetColor(BaseColorId, new Color(0.11f, 0.115f, 0.13f, 1f));
            _sharedSplashMaterial.SetColor(ColorId, new Color(0.11f, 0.115f, 0.13f, 1f));
            if (_sharedSplashMaterial.HasProperty("_Metallic"))
                _sharedSplashMaterial.SetFloat("_Metallic", 0.55f);
            if (_sharedSplashMaterial.HasProperty("_Smoothness"))
                _sharedSplashMaterial.SetFloat("_Smoothness", 0.40f);
            return _sharedSplashMaterial;
        }

        private static Material GetSparkMaterial()
        {
            if (_sharedSparkMaterial != null)
                return _sharedSparkMaterial;

            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) return null;

            _sharedSparkMaterial = new Material(shader);
            Color hdrAmber = new Color(3.2f, 1.45f, 0.25f, 1f);
            _sharedSparkMaterial.SetColor(BaseColorId, hdrAmber);
            _sharedSparkMaterial.SetColor(ColorId, hdrAmber);
            if (_sharedSparkMaterial.HasProperty(EmissionColorId))
                _sharedSparkMaterial.SetColor(EmissionColorId, hdrAmber);
            return _sharedSparkMaterial;
        }

        private static Transform FindChildByName(Transform parent, string targetName)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.name == targetName)
                    return child;
                Transform nested = FindChildByName(child, targetName);
                if (nested != null)
                    return nested;
            }
            return null;
        }
    }
}
