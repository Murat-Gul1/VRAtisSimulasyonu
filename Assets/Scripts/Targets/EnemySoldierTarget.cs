using System;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using VRAtis.Ballistics;
using Random = UnityEngine.Random;

namespace VRAtis.Targets
{
    /// <summary>
    /// Animated tactical KSK enemy soldier target driven by Unity's zero-GC PlayableGraph
    /// (Idle / Walk / Run / Death clips baked on the Mixamo skeleton).
    /// Sprints & patrols laterally across its assigned range lane, reacts to ballistic hits
    /// (headshot instant takedown, chest 2-shot, limb 3-shot), collapses on death, and auto-respawns.
    /// </summary>
    public class EnemySoldierTarget : MonoBehaviour
    {
        private enum AiState
        {
            IdleScan,
            PatrolWalk,
            TacticalSprint,
            Dead
        }

        private const int AnimIdle = 0;
        private const int AnimWalk = 1;
        private const int AnimRun = 2;
        private const int AnimDeath = 3;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        private static int _nextGraphId;

        [Header("Animasyon Klipleri (PlayableGraph)")]
        [SerializeField] private Animator soldierAnimator;
        [SerializeField] private AnimationClip idleClip;
        [SerializeField] private AnimationClip walkClip;
        [SerializeField] private AnimationClip runClip;
        [SerializeField] private AnimationClip deathClip;

        [Header("Hareket ve Taktik Şaşırtma / Hücum")]
        [SerializeField] private float minWalkSpeedMetersPerSec = 1.35f;
        [SerializeField] private float maxWalkSpeedMetersPerSec = 2.15f;
        [SerializeField] private float minJogSpeedMetersPerSec = 2.75f;
        [SerializeField] private float maxJogSpeedMetersPerSec = 3.85f;
        [SerializeField] private float minBurstSprintSpeedMetersPerSec = 4.90f;
        [SerializeField] private float maxBurstSprintSpeedMetersPerSec = 6.35f;
        [SerializeField] private float speedAcceleration = 15.0f;
        [SerializeField] private float turnSpeedDegPerSec = 540f;
        [SerializeField] private float minPauseSeconds = 0.22f;
        [SerializeField] private float maxPauseSeconds = 0.68f;
        [SerializeField] private float assaultStopDistanceMeters = 3.8f;
        [SerializeField] private float assaultBreachResetSeconds = 1.25f;

        [Header("Can ve Vuruş Bölgeleri")]
        [SerializeField] private float maxHealth = 100f;
        [SerializeField] private float headDamage = 100f;
        [SerializeField] private float torsoDamage = 100f;
        [SerializeField] private float limbDamage = 55f;
        [SerializeField] private float deathLingerSeconds = 4.0f;
        [SerializeField] private float despawnSinkSeconds = 0.85f;
        [SerializeField] private bool removeAfterDeath = true;

        [Header("Ses ve Kan / Vuruş Efektleri")]
        [SerializeField] private AudioClip[] bodyHitClips;
        [SerializeField] private AudioClip[] headHitClips;
        [SerializeField] private AudioClip[] deathFallClips;
        [SerializeField] private AudioClip runningFootstepLoopClip;
        [Range(0f, 1f)]
        [SerializeField] private float runAudioVolume = 0.90f;
        [SerializeField] private float runAudioMinDistance = 4f;
        [SerializeField] private float runAudioMaxDistance = 75f;
        [SerializeField] private bool useSpeedOfSoundDelay = true;
        [Range(0f, 1f)]
        [SerializeField] private float hitVolume = 0.92f;
        [SerializeField] private float minAudibleDistance = 40f;
        [SerializeField] private float maxAudibleDistance = 1000f;

        public event Action<EnemySoldierTarget> OnSoldierRemoved;

        public bool IsDead => _aiState == AiState.Dead;
        public bool IsAssaultMode => _assaultMode;
        public float CurrentHealth => _currentHealth;
        public int KillCount { get; private set; }

        private PlayableGraph _graph;
        private AnimationMixerPlayable _mixer;
        private AnimationClipPlayable[] _clipPlayables;
        private float[] _clipWeights;
        private int _targetAnimIndex = AnimRun;
        private float _animBlendSpeed = 9f;
        private bool _graphInitialized;

        private AiState _aiState = AiState.TacticalSprint;
        private float _currentHealth = 100f;
        private float _stateTimer;
        private float _feintDecisionTimer;
        private float _deathElapsed;
        private float _flinchSlowTimer;
        private float _breachHoldTimer;
        private bool _breachReached;
        private float _lastHitTime = -10f;
        private Vector3 _deathKnockbackVel;
        private Vector3 _deathGroundPos;

        private float _currentSpeed;
        private float _targetSpeed = 4.6f;
        private float _lateralDirSign = 1f;
        private float _depthBiasOffset;

        private Vector3 _laneCenterPos;
        private Vector3 _shooterOriginPos = new Vector3(2.01f, 0f, -3.783f);
        private Vector3 _laneRight = Vector3.right;
        private Vector3 _laneForward = Vector3.forward;
        private float _patrolHalfWidthMeters = 2.5f;
        private float _patrolDepthJitterMeters = 1.0f;
        private bool _assaultMode;
        private Vector3 _currentWaypoint;
        private bool _patrolConfigured;

        private Collider[] _hitColliders;
        private ParticleSystem _bloodMistPs;
        private ParticleSystem _bloodDropletPs;
        private Transform _bloodPoolTransform;
        private MeshRenderer _bloodPoolRenderer;
        private MaterialPropertyBlock _bloodPoolMpb;
        private static Texture2D _sharedBloodDropTex;
        private static Texture2D _sharedBloodPoolTex;
        private static Material _sharedBloodParticleMat;
        private static Material _sharedBloodPoolMat;

        private AudioSource[] _audioVoices;
        private AudioSource _runAudioSource;
        private int _nextVoiceIdx;
        private Transform _listenerTransform;

        private void Awake()
        {
            if (soldierAnimator == null)
                soldierAnimator = GetComponentInChildren<Animator>(true);

            _hitColliders = GetComponentsInChildren<Collider>(true);
            PrewarmBloodEffects();
            BuildAudioPool();
            EnsurePlayableGraph();
        }

        private void OnEnable()
        {
            EnsurePlayableGraph();
            if (_graphInitialized && _graph.IsValid())
                _graph.Play();
        }

        private void OnDisable()
        {
            if (_graphInitialized && _graph.IsValid())
                _graph.Stop();
            if (_runAudioSource != null && _runAudioSource.isPlaying)
                _runAudioSource.Stop();
        }

        private void OnDestroy()
        {
            if (_graphInitialized && _graph.IsValid())
            {
                _graph.Destroy();
                _graphInitialized = false;
            }
        }

        /// <summary>
        /// Assigns this soldier's patrol lane center, lateral evasion half-width (total span = 2 * halfWidth),
        /// depth jitter, shooter origin, and whether Assault/Charge Mode (Hücum Modu) is active.
        /// </summary>
        public void ConfigurePatrolZone(
            Vector3 laneCenterPos,
            Vector3 shooterOriginPos,
            Vector3 rangeForward,
            Vector3 rangeRight,
            float lateralHalfWidthMeters,
            float depthJitterMeters,
            bool assaultMode = false)
        {
            _laneCenterPos = new Vector3(laneCenterPos.x, 0f, laneCenterPos.z);
            _shooterOriginPos = new Vector3(shooterOriginPos.x, 0f, shooterOriginPos.z);
            _laneForward = rangeForward.sqrMagnitude > 1e-4f ? new Vector3(rangeForward.x, 0f, rangeForward.z).normalized : Vector3.forward;
            _laneRight = rangeRight.sqrMagnitude > 1e-4f ? new Vector3(rangeRight.x, 0f, rangeRight.z).normalized : Vector3.right;
            _patrolHalfWidthMeters = Mathf.Clamp(lateralHalfWidthMeters, 0.5f, 15.0f);
            _patrolDepthJitterMeters = Mathf.Clamp(depthJitterMeters, 0.25f, 3.0f);
            _assaultMode = assaultMode;
            _patrolConfigured = true;

            ResetSoldierState();
        }

        /// <summary>
        /// Revives the soldier upright, restores full health, and starts an immediate evasive run or assault charge.
        /// </summary>
        public void ResetSoldierState()
        {
            EnsurePlayableGraph();
            _currentHealth = maxHealth;
            _flinchSlowTimer = 0f;
            _deathElapsed = 0f;
            _deathKnockbackVel = Vector3.zero;
            _breachHoldTimer = 0f;
            _breachReached = false;
            _lastHitTime = -10f;

            if (_bloodMistPs != null) _bloodMistPs.Clear(true);
            if (_bloodDropletPs != null) _bloodDropletPs.Clear(true);
            if (_bloodPoolTransform != null)
                _bloodPoolTransform.gameObject.SetActive(false);

            SetHitCollidersEnabled(true);

            if (_patrolConfigured)
            {
                float startLat = UnityEngine.Random.Range(-_patrolHalfWidthMeters * 0.55f, _patrolHalfWidthMeters * 0.55f);
                Vector3 spawnPos = _laneCenterPos + _laneRight * startLat;
                spawnPos.y = 0f;
                transform.position = spawnPos;
            }
            else
            {
                Vector3 p = transform.position;
                p.y = 0f;
                transform.position = p;
            }

            _lateralDirSign = UnityEngine.Random.value < 0.5f ? -1f : 1f;
            _depthBiasOffset = UnityEngine.Random.Range(-_patrolDepthJitterMeters * 0.5f, _patrolDepthJitterMeters * 0.5f);

            // Start immediately moving at an unpredictable combat pace
            PickNextDeceptiveMove(forceBurstSprint: true, reverseDirection: false);
            _currentSpeed = _targetSpeed;

            Vector3 toGoal = _currentWaypoint - transform.position;
            toGoal.y = 0f;
            if (toGoal.sqrMagnitude > 1e-4f)
                transform.rotation = Quaternion.LookRotation(toGoal.normalized, Vector3.up);

            SnapAnimationImmediate(_aiState == AiState.PatrolWalk ? AnimWalk : AnimRun);
        }

        public void HandleBulletImpact(SoldierHitZone zone, in BulletImpactInfo impact)
        {
            if (_aiState == AiState.Dead)
                return;

            // Ignore duplicate bone-collider hits from the same penetrating bullet within 45 ms
            if (Time.time - _lastHitTime < 0.045f)
                return;
            _lastHitTime = Time.time;

            float dmg = zone == SoldierHitZone.Head
                ? headDamage
                : (zone == SoldierHitZone.Torso ? torsoDamage : limbDamage);

            _currentHealth = Mathf.Max(0f, _currentHealth - dmg);
            bool isFatal = _currentHealth <= 0f;

            // Erupt directional arterial blood mist & gravity-falling crimson droplets right from bullet impact point!
            TriggerBloodImpact(in impact, zone, isFatal);

            AudioClip[] clips = (zone == SoldierHitZone.Head && headHitClips != null && headHitClips.Length > 0)
                ? headHitClips
                : bodyHitClips;
            PlayDelayedImpactAudio(clips, impact.HitPoint, extraDelaySec: 0f, volumeScale: 1.0f);

            if (isFatal)
            {
                KillCount++;
                EnterDeathState(in impact);
                Debug.Log($"[Asker Vuruşu - ETKİSİZ] {name} | Bölge={zone} | Mesafe={impact.DistanceTraveledMeters:F1}m | Enerji={impact.KineticEnergyJoules:F0}J");
            }
            else
            {
                // Non-fatal limb hit: bleeds heavily, staggers briefly, cuts direction & surges evasively
                _flinchSlowTimer = 0.24f;
                _breachReached = false;
                PickNextDeceptiveMove(forceBurstSprint: true, reverseDirection: true);
                SetTargetAnimation(AnimRun, blendSpeed: 12f);
                Debug.Log($"[Asker Vuruşu - YARALI] {name} | Bölge={zone} | Kalan Can={_currentHealth:F0} | Mesafe={impact.DistanceTraveledMeters:F1}m");
            }
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f)
                return;

            UpdateAnimationPlayables(dt);
            UpdateRunAudio(dt);

            if (!_patrolConfigured)
                return;

            if (_aiState == AiState.Dead)
            {
                UpdateDeathAndRemoval(dt);
                return;
            }

            if (_flinchSlowTimer > 0f)
                _flinchSlowTimer = Mathf.Max(0f, _flinchSlowTimer - dt);

            // In Assault Mode, if soldier breached close-range perimeter, hold briefly facing player then reset wave
            if (_assaultMode && _breachReached)
            {
                FaceShooterSmooth(dt, 1.0f);
                _currentSpeed = Mathf.MoveTowards(_currentSpeed, 0f, speedAcceleration * dt);
                _breachHoldTimer -= dt;
                if (_breachHoldTimer <= 0f)
                {
                    Debug.Log($"[Asker Hücumu] {name} yakın temas hattına ulaştı — başlangıç mesafesinden yeni hücum başlıyor.");
                    ResetSoldierState();
                }
                return;
            }

            if (_aiState == AiState.IdleScan)
            {
                _currentSpeed = Mathf.MoveTowards(_currentSpeed, 0f, speedAcceleration * 1.4f * dt);
                FaceShooterSmooth(dt, 0.85f);

                _stateTimer -= dt;
                if (_stateTimer <= 0f)
                    PickNextDeceptiveMove(forceBurstSprint: Random.value < 0.65f, reverseDirection: Random.value < 0.55f);
                return;
            }

            // Assault Mode close-range safety check so soldier never clips through the VR headset
            if (_assaultMode)
            {
                Vector3 toShooter = _shooterOriginPos - transform.position;
                toShooter.y = 0f;
                if (toShooter.magnitude <= assaultStopDistanceMeters)
                {
                    _breachReached = true;
                    _breachHoldTimer = assaultBreachResetSeconds;
                    _aiState = AiState.IdleScan;
                    SetTargetAnimation(AnimIdle, blendSpeed: 10f);
                    return;
                }
            }

            // Unpredictable mid-run feints: randomly reverse direction, change tempo, or abrupt stutter-stop!
            _feintDecisionTimer -= dt;
            if (_feintDecisionTimer <= 0f)
            {
                ExecuteMidRunFeint();
                if (_aiState == AiState.IdleScan)
                    return;
            }

            Vector3 toTarget = _currentWaypoint - transform.position;
            toTarget.y = 0f;
            float dist = toTarget.magnitude;

            if (dist <= 0.28f)
            {
                // Reached lateral edge or zig-zag point: randomly brief-stop or instantly whip back the other way!
                float pauseChance = _assaultMode ? 0.12f : 0.24f;
                if (Random.value < pauseChance)
                {
                    EnterBriefStutterPause();
                }
                else
                {
                    PickNextDeceptiveMove(forceBurstSprint: false, reverseDirection: true);
                }
                return;
            }

            Vector3 moveDir = toTarget / dist;
            Quaternion desiredRot = Quaternion.LookRotation(moveDir, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, desiredRot, turnSpeedDegPerSec * dt);

            float desiredSpeed = _targetSpeed * (_flinchSlowTimer > 0f ? 0.50f : 1.0f);
            _currentSpeed = Mathf.MoveTowards(_currentSpeed, desiredSpeed, speedAcceleration * dt);

            float step = Mathf.Min(dist, _currentSpeed * dt);
            Vector3 nextPos = transform.position + moveDir * step;
            nextPos.y = 0f;
            transform.position = nextPos;
        }

        private void FaceShooterSmooth(float dt, float turnFactor)
        {
            Vector3 toShooter = _shooterOriginPos - transform.position;
            toShooter.y = 0f;
            if (toShooter.sqrMagnitude > 1e-4f)
            {
                Quaternion lookShooter = Quaternion.LookRotation(toShooter.normalized, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(transform.rotation, lookShooter, turnSpeedDegPerSec * turnFactor * dt);
            }
        }

        private void EnterBriefStutterPause()
        {
            _aiState = AiState.IdleScan;
            _stateTimer = Random.Range(minPauseSeconds, maxPauseSeconds);
            SetTargetAnimation(AnimIdle, blendSpeed: 9.5f);
        }

        /// <summary>
        /// Fires at random mid-run intervals to fool the shooter:
        /// sudden direction reversal (fake-out), sudden speed surge/drop, or abrupt stutter-stop.
        /// </summary>
        private void ExecuteMidRunFeint()
        {
            float roll = Random.value;
            if (_assaultMode)
            {
                if (roll < 0.48f)
                {
                    // Sharp diagonal zig-zag cut while rushing player
                    PickNextDeceptiveMove(forceBurstSprint: Random.value < 0.55f, reverseDirection: true);
                }
                else if (roll < 0.90f)
                {
                    // Sudden speed burst or deceptive slow-down without losing forward charge
                    AssignRandomCombatTempo(forceBurstSprint: false);
                    RefreshAssaultWaypoint();
                    ScheduleNextFeintTimer();
                }
                else
                {
                    EnterBriefStutterPause();
                }
            }
            else
            {
                if (roll < 0.44f)
                {
                    // Classic fake-out: whip around mid-corridor before reaching the edge!
                    PickNextDeceptiveMove(forceBurstSprint: Random.value < 0.50f, reverseDirection: true);
                }
                else if (roll < 0.80f)
                {
                    // Keep direction but abruptly accelerate to burst sprint or drop to slow tactical stalk
                    AssignRandomCombatTempo(forceBurstSprint: false);
                    ScheduleNextFeintTimer();
                }
                else
                {
                    // Sudden freeze-feint so shooter leads ahead and misses!
                    EnterBriefStutterPause();
                }
            }
        }

        private void PickNextDeceptiveMove(bool forceBurstSprint, bool reverseDirection)
        {
            Vector3 relFromCenter = transform.position - _laneCenterPos;
            float curLat = Vector3.Dot(relFromCenter, _laneRight);

            if (reverseDirection)
            {
                // Always cut back inside if near edge, otherwise invert current lateral direction
                if (curLat >= _patrolHalfWidthMeters * 0.72f)
                    _lateralDirSign = -1f;
                else if (curLat <= -_patrolHalfWidthMeters * 0.72f)
                    _lateralDirSign = 1f;
                else
                    _lateralDirSign = -_lateralDirSign;
            }
            else if (Mathf.Abs(curLat) >= _patrolHalfWidthMeters * 0.80f)
            {
                _lateralDirSign = curLat >= 0f ? -1f : 1f;
            }

            AssignRandomCombatTempo(forceBurstSprint);

            if (_assaultMode)
            {
                RefreshAssaultWaypoint();
            }
            else
            {
                // Run toward a random distance inside the chosen corridor (e.g., ±2.5m for 5m total span)
                float minTargetLat = _patrolHalfWidthMeters * 0.35f;
                float targetLat = _lateralDirSign * Random.Range(minTargetLat, _patrolHalfWidthMeters);
                // Ensure the chosen waypoint is meaningfully away from current position
                if (Mathf.Abs(targetLat - curLat) < Mathf.Min(0.85f, _patrolHalfWidthMeters * 0.75f))
                    targetLat = _lateralDirSign * _patrolHalfWidthMeters;

                _depthBiasOffset = Random.Range(-_patrolDepthJitterMeters, _patrolDepthJitterMeters);
                _currentWaypoint = _laneCenterPos + _laneRight * targetLat + _laneForward * _depthBiasOffset;
                _currentWaypoint.y = 0f;
            }

            ScheduleNextFeintTimer();
        }

        private void RefreshAssaultWaypoint()
        {
            Vector3 toShooter = _shooterOriginPos - transform.position;
            toShooter.y = 0f;
            float distToShooter = toShooter.magnitude;
            if (distToShooter <= assaultStopDistanceMeters + 0.2f)
            {
                _currentWaypoint = transform.position;
                return;
            }

            Vector3 fwdToShooter = toShooter / distToShooter;
            Vector3 rightOfAssault = new Vector3(fwdToShooter.z, 0f, -fwdToShooter.x).normalized;

            // Advance aggressively toward the player while zig-zagging within the chosen lateral span.
            // Smoothly taper zig-zag width under 14m so the soldier converges directly into the player's sight!
            float taper = Mathf.Clamp01((distToShooter - assaultStopDistanceMeters) / 11f);
            float effectiveHalfW = Mathf.Max(0.45f, _patrolHalfWidthMeters * Mathf.Lerp(0.28f, 1.0f, taper));

            // Keep lateral offset bounded around the direct lane-to-shooter centerline
            Vector3 laneToShooter = _shooterOriginPos - _laneCenterPos;
            laneToShooter.y = 0f;
            Vector3 axisFwd = laneToShooter.sqrMagnitude > 1e-4f ? laneToShooter.normalized : -_laneForward;
            Vector3 axisRight = new Vector3(axisFwd.z, 0f, -axisFwd.x).normalized;

            float advanceStep = Mathf.Clamp(Random.Range(3.2f, 7.5f), 2.0f, Mathf.Max(2.0f, distToShooter - assaultStopDistanceMeters + 0.25f));
            float curForwardDist = Vector3.Dot(transform.position - _laneCenterPos, axisFwd);
            float maxForwardDist = Mathf.Max(0.5f, laneToShooter.magnitude - assaultStopDistanceMeters);
            float nextForwardDist = Mathf.Min(maxForwardDist, curForwardDist + advanceStep);

            float nextLat = _lateralDirSign * Random.Range(effectiveHalfW * 0.45f, effectiveHalfW);
            _currentWaypoint = _laneCenterPos + axisFwd * nextForwardDist + axisRight * nextLat;
            _currentWaypoint.y = 0f;
        }

        private void AssignRandomCombatTempo(bool forceBurstSprint)
        {
            if (forceBurstSprint)
            {
                _targetSpeed = Random.Range(minBurstSprintSpeedMetersPerSec, maxBurstSprintSpeedMetersPerSec);
                _aiState = AiState.TacticalSprint;
                SetTargetAnimation(AnimRun, blendSpeed: 10.5f);
                return;
            }

            float r = Random.value;
            float walkThreshold = _assaultMode ? 0.16f : 0.28f;
            float jogThreshold = _assaultMode ? 0.48f : 0.60f;

            if (r < walkThreshold)
            {
                // Deceptive slow tactical walk / stalk
                _targetSpeed = Random.Range(minWalkSpeedMetersPerSec, maxWalkSpeedMetersPerSec);
                _aiState = AiState.PatrolWalk;
                SetTargetAnimation(AnimWalk, blendSpeed: 8.5f);
            }
            else if (r < jogThreshold)
            {
                // Medium combat jog (sudden drop from sprint fools lead aiming)
                _targetSpeed = Random.Range(minJogSpeedMetersPerSec, maxJogSpeedMetersPerSec);
                _aiState = AiState.TacticalSprint;
                SetTargetAnimation(AnimRun, blendSpeed: 9.0f);
            }
            else
            {
                // Full explosive burst sprint!
                _targetSpeed = Random.Range(minBurstSprintSpeedMetersPerSec, maxBurstSprintSpeedMetersPerSec);
                _aiState = AiState.TacticalSprint;
                SetTargetAnimation(AnimRun, blendSpeed: 10.5f);
            }
        }

        private void ScheduleNextFeintTimer()
        {
            // Tight corridors (1-5m) get snappier feints; wider corridors allow fuller bursts mixed with surprise cuts
            float widthFactor = Mathf.Clamp(_patrolHalfWidthMeters / 3.0f, 0.72f, 1.45f);
            float minT = (_assaultMode ? 0.42f : 0.38f) * widthFactor;
            float maxT = (_assaultMode ? 1.45f : 1.55f) * widthFactor;
            _feintDecisionTimer = Random.Range(minT, maxT);
        }

        private void EnterDeathState(in BulletImpactInfo impact)
        {
            _aiState = AiState.Dead;
            _deathElapsed = 0f;
            _currentSpeed = 0f;
            SetHitCollidersEnabled(false);

            if (_runAudioSource != null && _runAudioSource.isPlaying)
                _runAudioSource.Stop();

            // Face toward incoming shot direction so the backward Death collapse falls away from the bullet,
            // and apply realistic kinetic knockback slide along bullet direction!
            Vector3 shotDir = impact.ImpactVelocity;
            shotDir.y = 0f;
            if (shotDir.sqrMagnitude > 1f)
            {
                Vector3 shotDirN = shotDir.normalized;
                transform.rotation = Quaternion.LookRotation(-shotDirN, Vector3.up);
                _deathKnockbackVel = shotDirN * 1.85f;
            }
            else
            {
                _deathKnockbackVel = -transform.forward * 1.40f;
            }

            _deathGroundPos = new Vector3(transform.position.x, 0f, transform.position.z);

            // Prepare ground blood pool beneath where the fallen torso lands
            if (_bloodPoolTransform != null)
            {
                Vector3 poolPos = _deathGroundPos - transform.forward * 0.55f;
                poolPos.y = 0.018f;
                _bloodPoolTransform.position = poolPos;
                _bloodPoolTransform.rotation = Quaternion.Euler(90f, UnityEngine.Random.Range(0f, 360f), 0f);
                _bloodPoolTransform.localScale = Vector3.one * 0.14f;
                SetBloodPoolAlpha(0f);
                _bloodPoolTransform.gameObject.SetActive(true);
            }

            // Schedule heavy body-on-grass thud right when shoulders/gear strike ground (~0.56s into Death clip)
            if (deathFallClips != null && deathFallClips.Length > 0)
            {
                PlayDelayedImpactAudio(deathFallClips, _deathGroundPos, extraDelaySec: 0.56f, volumeScale: 0.88f);
            }

            if (_graphInitialized && _clipPlayables != null && _clipPlayables[AnimDeath].IsValid())
            {
                _clipPlayables[AnimDeath].SetTime(0.0);
                _clipPlayables[AnimDeath].SetSpeed(1.0);
            }
            SetTargetAnimation(AnimDeath, blendSpeed: 15f);
        }

        private void UpdateDeathAndRemoval(float dt)
        {
            _deathElapsed += dt;

            // 1. Damped kinetic knockback slide during initial collapse (0..0.55s)
            if (_deathKnockbackVel.sqrMagnitude > 0.002f)
            {
                Vector3 pos = transform.position + _deathKnockbackVel * dt;
                pos.y = 0f;
                transform.position = pos;
                _deathGroundPos = pos;
                _deathKnockbackVel *= Mathf.Exp(-5.5f * dt);

                if (_bloodPoolTransform != null)
                {
                    Vector3 poolPos = _deathGroundPos - transform.forward * 0.52f;
                    poolPos.y = 0.018f;
                    _bloodPoolTransform.position = poolPos;
                }
            }

            // 2. Smoothly grow dark crimson blood pool beneath fallen torso (0.50s .. 2.70s)
            if (_bloodPoolTransform != null && _deathElapsed >= 0.48f && _deathElapsed < deathLingerSeconds)
            {
                float growT = Mathf.Clamp01((_deathElapsed - 0.48f) / 2.25f);
                float eased = 1f - (1f - growT) * (1f - growT);
                float poolSize = Mathf.Lerp(0.16f, 1.12f, eased);
                _bloodPoolTransform.localScale = new Vector3(poolSize, poolSize, 1f);
                SetBloodPoolAlpha(Mathf.Lerp(0f, 0.88f, Mathf.Clamp01((_deathElapsed - 0.48f) / 0.65f)));
            }

            // 3. After lying dead for deathLingerSeconds, smoothly sink/dissolve out and remove!
            if (_deathElapsed >= deathLingerSeconds)
            {
                float sinkDur = Mathf.Max(0.15f, despawnSinkSeconds);
                float sinkT = Mathf.Clamp01((_deathElapsed - deathLingerSeconds) / sinkDur);
                float smoothSink = sinkT * sinkT * (3f - 2f * sinkT);

                Vector3 p = _deathGroundPos;
                p.y = Mathf.Lerp(0f, -0.78f, smoothSink);
                transform.position = p;

                if (_bloodPoolTransform != null)
                {
                    // Keep pool pinned at ground Y=0.018 while fading out cleanly
                    Vector3 poolPos = _deathGroundPos - transform.forward * 0.52f;
                    poolPos.y = 0.018f;
                    _bloodPoolTransform.position = poolPos;
                    SetBloodPoolAlpha(Mathf.Lerp(0.88f, 0f, smoothSink));
                }

                if (sinkT >= 1f)
                {
                    if (_bloodPoolTransform != null)
                        _bloodPoolTransform.gameObject.SetActive(false);

                    if (removeAfterDeath)
                    {
                        gameObject.SetActive(false);
                        OnSoldierRemoved?.Invoke(this);
                    }
                    else
                    {
                        ResetSoldierState();
                    }
                }
            }
        }

        private void EnsurePlayableGraph()
        {
            if (_graphInitialized && _graph.IsValid())
                return;

            if (soldierAnimator == null)
                soldierAnimator = GetComponentInChildren<Animator>(true);
            if (soldierAnimator == null)
                return;

            soldierAnimator.applyRootMotion = false;
            soldierAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            _graph = PlayableGraph.Create($"KSK_Soldier_{++_nextGraphId}");
            _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);

            _mixer = AnimationMixerPlayable.Create(_graph, 4);
            _clipPlayables = new AnimationClipPlayable[4];
            _clipWeights = new float[4];

            AnimationClip[] clips = { idleClip, walkClip, runClip, deathClip };
            for (int i = 0; i < 4; i++)
            {
                AnimationClip c = clips[i] != null ? clips[i] : idleClip;
                if (c == null) continue;

                AnimationClipPlayable playable = AnimationClipPlayable.Create(_graph, c);
                playable.SetApplyFootIK(false);
                playable.SetApplyPlayableIK(false);
                if (i != AnimDeath)
                {
                    // Slight random phase offset so 6 soldiers don't run in robotic lockstep!
                    playable.SetTime(Random.Range(0f, Mathf.Max(0.1f, c.length)));
                }
                else
                {
                    playable.SetDuration(c.length);
                }

                _clipPlayables[i] = playable;
                _graph.Connect(playable, 0, _mixer, i);
                _mixer.SetInputWeight(i, i == AnimRun ? 1f : 0f);
                _clipWeights[i] = i == AnimRun ? 1f : 0f;
            }

            AnimationPlayableOutput animOut = AnimationPlayableOutput.Create(_graph, "SoldierAnim", soldierAnimator);
            animOut.SetSourcePlayable(_mixer);
            _graph.Play();
            _graphInitialized = true;
        }

        private void SetTargetAnimation(int animIndex, float blendSpeed)
        {
            _targetAnimIndex = Mathf.Clamp(animIndex, 0, 3);
            _animBlendSpeed = Mathf.Max(2f, blendSpeed);
        }

        private void SnapAnimationImmediate(int animIndex)
        {
            _targetAnimIndex = Mathf.Clamp(animIndex, 0, 3);
            if (!_graphInitialized || !_graph.IsValid())
                return;

            for (int i = 0; i < 4; i++)
            {
                _clipWeights[i] = (i == _targetAnimIndex) ? 1f : 0f;
                _mixer.SetInputWeight(i, _clipWeights[i]);
            }
        }

        private void UpdateAnimationPlayables(float dt)
        {
            if (!_graphInitialized || !_graph.IsValid())
                return;

            // Smooth crossfade weights across Idle / Walk / Run / Death
            float weightSum = 0f;
            for (int i = 0; i < 4; i++)
            {
                float targetW = (i == _targetAnimIndex) ? 1f : 0f;
                _clipWeights[i] = Mathf.MoveTowards(_clipWeights[i], targetW, _animBlendSpeed * dt);
                weightSum += _clipWeights[i];
            }

            // Sync Walk & Run animation playback speed with actual ground velocity so footwork matches acceleration/deceleration
            if (_clipPlayables[AnimWalk].IsValid())
                _clipPlayables[AnimWalk].SetSpeed(Mathf.Clamp(_currentSpeed / 1.75f, 0.70f, 1.35f));
            if (_clipPlayables[AnimRun].IsValid())
                _clipPlayables[AnimRun].SetSpeed(Mathf.Clamp(_currentSpeed / 4.35f, 0.72f, 1.48f));

            float invSum = weightSum > 1e-4f ? (1f / weightSum) : 1f;
            for (int i = 0; i < 4; i++)
            {
                _mixer.SetInputWeight(i, _clipWeights[i] * invSum);

                if (!_clipPlayables[i].IsValid())
                    continue;

                AnimationClip clip = _clipPlayables[i].GetAnimationClip();
                if (clip == null || clip.length <= 0.01f)
                    continue;

                double curTime = _clipPlayables[i].GetTime();
                if (i == AnimDeath)
                {
                    if (curTime >= clip.length)
                        _clipPlayables[i].SetTime(clip.length - 0.001);
                }
                else
                {
                    // Explicit manual wrap guarantees seamless looping even if FBX clip loop flag wasn't set
                    if (curTime >= clip.length)
                        _clipPlayables[i].SetTime(curTime % clip.length);
                }
            }
        }

        private void SetHitCollidersEnabled(bool enabled)
        {
            if (_hitColliders == null) return;
            for (int i = 0; i < _hitColliders.Length; i++)
            {
                if (_hitColliders[i] != null)
                    _hitColliders[i].enabled = enabled;
            }
        }

        private void PrewarmBloodEffects()
        {
            Material bloodMat = GetBloodParticleMaterial();

            // 1. Arterial Crimson Mist Cloud (visible even at long rifle range through Red Dot)
            GameObject mistGo = new GameObject("SoldierBloodMist");
            mistGo.transform.SetParent(transform, false);
            _bloodMistPs = mistGo.AddComponent<ParticleSystem>();
            _bloodMistPs.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var mistMain = _bloodMistPs.main;
            mistMain.playOnAwake = false;
            mistMain.loop = false;
            mistMain.simulationSpace = ParticleSystemSimulationSpace.World;
            mistMain.maxParticles = 64;
            mistMain.gravityModifier = 0.18f;
            var mistEmit = _bloodMistPs.emission;
            mistEmit.enabled = false;
            var mistSize = _bloodMistPs.sizeOverLifetime;
            mistSize.enabled = true;
            mistSize.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                new Keyframe(0f, 0.45f),
                new Keyframe(0.25f, 1.00f),
                new Keyframe(1f, 1.85f)));
            var mistColor = _bloodMistPs.colorOverLifetime;
            mistColor.enabled = true;
            Gradient mistGrad = new Gradient();
            mistGrad.SetKeys(
                new[] {
                    new GradientColorKey(new Color(0.55f, 0.025f, 0.025f), 0f),
                    new GradientColorKey(new Color(0.32f, 0.012f, 0.012f), 1f)
                },
                new[] {
                    new GradientAlphaKey(0.82f, 0f),
                    new GradientAlphaKey(0.58f, 0.35f),
                    new GradientAlphaKey(0.0f, 1f)
                });
            mistColor.color = mistGrad;
            var mistLimit = _bloodMistPs.limitVelocityOverLifetime;
            mistLimit.enabled = true;
            mistLimit.dampen = 0.22f;
            mistLimit.limit = 0.35f;
            var mistRend = mistGo.GetComponent<ParticleSystemRenderer>();
            mistRend.renderMode = ParticleSystemRenderMode.Billboard;
            mistRend.sharedMaterial = bloodMat;
            mistRend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mistRend.receiveShadows = false;

            // 2. High-Velocity Directional Blood Droplet Spray (arcs cleanly with gravity!)
            GameObject dropGo = new GameObject("SoldierBloodDroplets");
            dropGo.transform.SetParent(transform, false);
            _bloodDropletPs = dropGo.AddComponent<ParticleSystem>();
            _bloodDropletPs.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var dropMain = _bloodDropletPs.main;
            dropMain.playOnAwake = false;
            dropMain.loop = false;
            dropMain.simulationSpace = ParticleSystemSimulationSpace.World;
            dropMain.maxParticles = 128;
            dropMain.gravityModifier = 1.65f;
            var dropEmit = _bloodDropletPs.emission;
            dropEmit.enabled = false;
            var dropColor = _bloodDropletPs.colorOverLifetime;
            dropColor.enabled = true;
            Gradient dropGrad = new Gradient();
            dropGrad.SetKeys(
                new[] {
                    new GradientColorKey(new Color(0.42f, 0.014f, 0.014f), 0f),
                    new GradientColorKey(new Color(0.22f, 0.008f, 0.008f), 1f)
                },
                new[] {
                    new GradientAlphaKey(0.96f, 0f),
                    new GradientAlphaKey(0.85f, 0.65f),
                    new GradientAlphaKey(0.0f, 1f)
                });
            dropColor.color = dropGrad;
            var dropRend = dropGo.GetComponent<ParticleSystemRenderer>();
            dropRend.renderMode = ParticleSystemRenderMode.Stretch;
            dropRend.velocityScale = 0.045f;
            dropRend.lengthScale = 1.35f;
            dropRend.sharedMaterial = bloodMat;
            dropRend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            dropRend.receiveShadows = false;

            // 3. Expanding Dark Crimson Ground Blood Pool (under fallen body)
            GameObject poolGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            poolGo.name = "SoldierGroundBloodPool";
            Collider col = poolGo.GetComponent<Collider>();
            if (col != null) Destroy(col);
            poolGo.transform.SetParent(transform, false);
            _bloodPoolRenderer = poolGo.GetComponent<MeshRenderer>();
            _bloodPoolRenderer.sharedMaterial = GetBloodPoolMaterial();
            _bloodPoolRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _bloodPoolRenderer.receiveShadows = true;
            _bloodPoolMpb = new MaterialPropertyBlock();
            _bloodPoolTransform = poolGo.transform;
            poolGo.SetActive(false);
        }

        private void TriggerBloodImpact(in BulletImpactInfo impact, SoldierHitZone zone, bool isFatal)
        {
            Vector3 hitPos = impact.HitPoint;
            Vector3 entryDir = impact.HitNormal.sqrMagnitude > 1e-4f
                ? impact.HitNormal.normalized
                : -impact.ImpactVelocity.normalized;
            Vector3 exitDir = impact.ImpactVelocity.sqrMagnitude > 1e-4f
                ? impact.ImpactVelocity.normalized
                : -entryDir;
            Vector3 exitPos = hitPos + exitDir * (zone == SoldierHitZone.Head ? 0.16f : 0.24f);

            float scaleMul = zone == SoldierHitZone.Head ? 1.45f : (isFatal ? 1.18f : 0.90f);

            // Emit entry splash + high-velocity exit spray mist
            if (_bloodMistPs != null)
            {
                int entryMist = Mathf.RoundToInt(5 * scaleMul);
                int exitMist = Mathf.RoundToInt(8 * scaleMul);
                EmitBloodMistBurst(hitPos + entryDir * 0.03f, entryDir, entryMist, 0.7f, 2.2f, 0.18f * scaleMul, 0.34f * scaleMul);
                EmitBloodMistBurst(exitPos, exitDir, exitMist, 1.4f, 4.2f, 0.22f * scaleMul, 0.42f * scaleMul);
            }

            // Emit directional gravity-arcing blood droplets from both entry & exit wounds
            if (_bloodDropletPs != null)
            {
                int entryDrops = Mathf.RoundToInt(12 * scaleMul);
                int exitDrops = Mathf.RoundToInt(20 * scaleMul);
                EmitBloodDropletBurst(hitPos + entryDir * 0.02f, entryDir, entryDrops, 1.4f, 3.8f, 0.42f, 0.035f, 0.075f);
                EmitBloodDropletBurst(exitPos, exitDir, exitDrops, 2.6f, 6.4f, 0.36f, 0.040f, 0.090f);
            }
        }

        private void EmitBloodMistBurst(Vector3 origin, Vector3 dir, int count, float minSpeed, float maxSpeed, float minSize, float maxSize)
        {
            for (int i = 0; i < count; i++)
            {
                Vector3 spread = (dir + UnityEngine.Random.insideUnitSphere * 0.58f).normalized;
                var ep = new ParticleSystem.EmitParams
                {
                    position = origin + UnityEngine.Random.insideUnitSphere * 0.04f,
                    velocity = spread * UnityEngine.Random.Range(minSpeed, maxSpeed),
                    startSize = UnityEngine.Random.Range(minSize, maxSize),
                    startLifetime = UnityEngine.Random.Range(0.42f, 0.74f),
                    rotation = UnityEngine.Random.Range(0f, 360f),
                    startColor = new Color32(150, 8, 8, 220)
                };
                _bloodMistPs.Emit(ep, 1);
            }
        }

        private void EmitBloodDropletBurst(Vector3 origin, Vector3 dir, int count, float minSpeed, float maxSpeed, float coneSpread, float minSize, float maxSize)
        {
            for (int i = 0; i < count; i++)
            {
                Vector3 spread = (dir + UnityEngine.Random.insideUnitSphere * coneSpread + Vector3.up * UnityEngine.Random.Range(0.05f, 0.28f)).normalized;
                var ep = new ParticleSystem.EmitParams
                {
                    position = origin + UnityEngine.Random.insideUnitSphere * 0.03f,
                    velocity = spread * UnityEngine.Random.Range(minSpeed, maxSpeed),
                    startSize = UnityEngine.Random.Range(minSize, maxSize),
                    startLifetime = UnityEngine.Random.Range(0.45f, 0.90f),
                    rotation = UnityEngine.Random.Range(0f, 360f),
                    startColor = new Color32(128, 6, 6, 248)
                };
                _bloodDropletPs.Emit(ep, 1);
            }
        }

        private void SetBloodPoolAlpha(float alpha)
        {
            if (_bloodPoolRenderer == null || _bloodPoolMpb == null)
                return;
            Color c = new Color(0.26f, 0.012f, 0.012f, Mathf.Clamp01(alpha));
            _bloodPoolMpb.SetColor(BaseColorId, c);
            _bloodPoolMpb.SetColor(ColorId, c);
            _bloodPoolRenderer.SetPropertyBlock(_bloodPoolMpb);
        }

        private void BuildAudioPool()
        {
            _audioVoices = new AudioSource[2];
            AnimationCurve rolloff = new AnimationCurve(
                new Keyframe(0f, 1.0f),
                new Keyframe(minAudibleDistance, 1.0f),
                new Keyframe(150f, 0.72f),
                new Keyframe(350f, 0.48f),
                new Keyframe(maxAudibleDistance, 0.08f));

            for (int i = 0; i < _audioVoices.Length; i++)
            {
                GameObject vGo = new GameObject($"SoldierHitAudio_{i}");
                vGo.transform.SetParent(transform, false);
                AudioSource src = vGo.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.loop = false;
                src.spatialBlend = 0.70f;
                src.dopplerLevel = 0f;
                src.minDistance = minAudibleDistance;
                src.maxDistance = maxAudibleDistance;
                src.rolloffMode = AudioRolloffMode.Custom;
                src.SetCustomCurve(AudioSourceCurveType.CustomRolloff, rolloff);
                _audioVoices[i] = src;
            }

            // Dedicated 3D spatialized heavy-gear footstep / running loop audio
            GameObject runGo = new GameObject("SoldierRunAudio");
            runGo.transform.SetParent(transform, false);
            runGo.transform.localPosition = new Vector3(0f, 0.35f, 0f);
            _runAudioSource = runGo.AddComponent<AudioSource>();
            _runAudioSource.clip = runningFootstepLoopClip;
            _runAudioSource.playOnAwake = false;
            _runAudioSource.loop = true;
            _runAudioSource.spatialBlend = 1.0f;
            _runAudioSource.dopplerLevel = 0.15f;
            _runAudioSource.volume = 0f;
            _runAudioSource.minDistance = Mathf.Max(1f, runAudioMinDistance);
            _runAudioSource.maxDistance = Mathf.Max(10f, runAudioMaxDistance);
            _runAudioSource.rolloffMode = AudioRolloffMode.Custom;
            _runAudioSource.SetCustomCurve(AudioSourceCurveType.CustomRolloff, new AnimationCurve(
                new Keyframe(0f, 1.0f),
                new Keyframe(Mathf.Max(1f, runAudioMinDistance), 1.0f),
                new Keyframe(16f, 0.78f),
                new Keyframe(34f, 0.40f),
                new Keyframe(55f, 0.14f),
                new Keyframe(Mathf.Max(10f, runAudioMaxDistance), 0.0f)));
        }

        private void UpdateRunAudio(float dt)
        {
            if (_runAudioSource == null || runningFootstepLoopClip == null)
                return;

            bool isActivelyMoving = _patrolConfigured
                && _aiState != AiState.Dead
                && _aiState != AiState.IdleScan
                && !(_assaultMode && _breachReached)
                && _currentSpeed >= 0.40f;

            if (isActivelyMoving)
            {
                if (_runAudioSource.clip != runningFootstepLoopClip)
                    _runAudioSource.clip = runningFootstepLoopClip;

                if (!_runAudioSource.isPlaying)
                {
                    // Random phase start so multiple soldiers sound like a real squad rather than a synced echo
                    _runAudioSource.time = Random.Range(0f, Mathf.Max(0.05f, runningFootstepLoopClip.length * 0.9f));
                    _runAudioSource.Play();
                }

                // Scale loudness & cadence pitch dynamically with real ground speed (slow walk -> full burst sprint)
                float speedNorm = Mathf.Clamp01((_currentSpeed - 0.8f) / 4.8f);
                float targetVol = runAudioVolume * Mathf.Lerp(0.36f, 1.00f, speedNorm);
                float targetPitch = Mathf.Lerp(0.76f, 1.18f, speedNorm);

                _runAudioSource.volume = Mathf.MoveTowards(_runAudioSource.volume, targetVol, 5.5f * dt);
                _runAudioSource.pitch = Mathf.MoveTowards(_runAudioSource.pitch, targetPitch, 2.8f * dt);
            }
            else if (_runAudioSource.isPlaying)
            {
                _runAudioSource.volume = Mathf.MoveTowards(_runAudioSource.volume, 0f, 8.5f * dt);
                if (_runAudioSource.volume <= 0.01f)
                {
                    _runAudioSource.volume = 0f;
                    _runAudioSource.Stop();
                }
            }
        }

        private void PlayDelayedImpactAudio(AudioClip[] clips, Vector3 hitWorldPos, float extraDelaySec = 0f, float volumeScale = 1f)
        {
            if (_audioVoices == null || _audioVoices.Length == 0 || clips == null || clips.Length == 0)
                return;

            AudioClip clip = clips[UnityEngine.Random.Range(0, clips.Length)];
            if (clip == null) return;

            AudioSource src = _audioVoices[_nextVoiceIdx];
            _nextVoiceIdx = (_nextVoiceIdx + 1) % _audioVoices.Length;
            src.transform.position = hitWorldPos;
            src.clip = clip;
            src.pitch = UnityEngine.Random.Range(0.94f, 1.06f);
            src.volume = hitVolume * volumeScale;

            float delaySec = Mathf.Max(0f, extraDelaySec);
            if (useSpeedOfSoundDelay)
            {
                if (_listenerTransform == null && Camera.main != null)
                    _listenerTransform = Camera.main.transform;
                float dist = _listenerTransform != null ? Vector3.Distance(hitWorldPos, _listenerTransform.position) : 25f;
                float sos = EnvironmentManager.Instance != null ? Mathf.Max(200f, EnvironmentManager.Instance.CurrentSpeedOfSound) : 340.3f;
                delaySec += Mathf.Clamp(dist / sos, 0f, 4f);
            }

            if (delaySec > 0.005f)
                src.PlayDelayed(delaySec);
            else
                src.Play();
        }

        private static Material GetBloodParticleMaterial()
        {
            if (_sharedBloodParticleMat != null)
                return _sharedBloodParticleMat;

            if (_sharedBloodDropTex == null)
                _sharedBloodDropTex = BuildProceduralBloodTexture(64, organicPool: false);

            Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null)
                shader = Shader.Find("Sprites/Default");
            if (shader == null)
                return null;

            _sharedBloodParticleMat = new Material(shader);
            ConfigureAlphaBlendUrpParticleMat(_sharedBloodParticleMat, _sharedBloodDropTex, Color.white);
            return _sharedBloodParticleMat;
        }

        private static Material GetBloodPoolMaterial()
        {
            if (_sharedBloodPoolMat != null)
                return _sharedBloodPoolMat;

            if (_sharedBloodPoolTex == null)
                _sharedBloodPoolTex = BuildProceduralBloodTexture(128, organicPool: true);

            Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null)
                shader = Shader.Find("Sprites/Default");
            if (shader == null)
                return null;

            _sharedBloodPoolMat = new Material(shader);
            ConfigureAlphaBlendUrpParticleMat(_sharedBloodPoolMat, _sharedBloodPoolTex, new Color(0.26f, 0.012f, 0.012f, 0.88f));
            return _sharedBloodPoolMat;
        }

        private static void ConfigureAlphaBlendUrpParticleMat(Material mat, Texture2D tex, Color tint)
        {
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
            if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
            if (mat.HasProperty(BaseColorId)) mat.SetColor(BaseColorId, tint);
            if (mat.HasProperty(ColorId)) mat.SetColor(ColorId, tint);
            if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f); // Transparent
            if (mat.HasProperty("_Blend")) mat.SetFloat("_Blend", 0f);     // Alpha
            if (mat.HasProperty("_SrcBlend")) mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (mat.HasProperty("_DstBlend")) mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 0f);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        private static Texture2D BuildProceduralBloodTexture(int size, bool organicPool)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            Color32[] pixels = new Color32[size * size];
            float invHalf = 2f / size;

            for (int y = 0; y < size; y++)
            {
                float ny = (y + 0.5f) * invHalf - 1f;
                for (int x = 0; x < size; x++)
                {
                    float nx = (x + 0.5f) * invHalf - 1f;
                    float r = Mathf.Sqrt(nx * nx + ny * ny);
                    float ang = Mathf.Atan2(ny, nx);

                    float edgeWarp = organicPool
                        ? (0.14f * Mathf.Sin(ang * 3f + 0.6f) + 0.09f * Mathf.Cos(ang * 5f - 1.1f) + 0.06f * Mathf.Sin(ang * 7f + 2.2f))
                        : (0.08f * Mathf.Sin(ang * 3f) + 0.05f * Mathf.Cos(ang * 5f));

                    float effectiveR = r / Mathf.Max(0.45f, 0.88f + edgeWarp);
                    float alpha = organicPool
                        ? Mathf.Clamp01(1f - Mathf.SmoothStep(0.62f, 1.0f, effectiveR))
                        : Mathf.Clamp01(Mathf.Pow(Mathf.Max(0f, 1f - effectiveR), 1.65f));

                    // Slight dark core richness for organic wet blood depth
                    float coreShade = Mathf.Lerp(0.72f, 1.0f, Mathf.Clamp01(effectiveR));
                    byte rgb = (byte)Mathf.RoundToInt(255f * coreShade);
                    byte a = (byte)Mathf.RoundToInt(255f * alpha);
                    pixels[y * size + x] = new Color32(rgb, rgb, rgb, a);
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            return tex;
        }
    }
}
