using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using VRAtis.Ballistics;

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
        [SerializeField] private float torsoDamage = 55f;
        [SerializeField] private float limbDamage = 35f;
        [SerializeField] private float respawnDelaySeconds = 4.2f;

        [Header("Ses ve Vuruş Efekti")]
        [SerializeField] private AudioClip[] bodyHitClips;
        [SerializeField] private AudioClip[] headHitClips;
        [SerializeField] private bool useSpeedOfSoundDelay = true;
        [Range(0f, 1f)]
        [SerializeField] private float hitVolume = 0.92f;
        [SerializeField] private float minAudibleDistance = 40f;
        [SerializeField] private float maxAudibleDistance = 1000f;
        [SerializeField] private float hitBurstDurationSec = 0.11f;
        [SerializeField] private float hitBurstSizeMeters = 0.18f;

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
        private float _respawnTimer;
        private float _flinchSlowTimer;
        private float _breachHoldTimer;
        private bool _breachReached;

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
        private Transform _hitBurstTransform;
        private float _hitBurstTimer;
        private static Material _sharedHitBurstMaterial;

        private AudioSource[] _audioVoices;
        private int _nextVoiceIdx;
        private Transform _listenerTransform;

        private void Awake()
        {
            if (soldierAnimator == null)
                soldierAnimator = GetComponentInChildren<Animator>(true);

            _hitColliders = GetComponentsInChildren<Collider>(true);
            PrewarmHitBurst();
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
            _respawnTimer = 0f;
            _breachHoldTimer = 0f;
            _breachReached = false;
            _hitBurstTimer = 0f;
            if (_hitBurstTransform != null)
                _hitBurstTransform.gameObject.SetActive(false);

            SetHitCollidersEnabled(true);

            if (_patrolConfigured)
            {
                float startLat = Random.Range(-_patrolHalfWidthMeters * 0.55f, _patrolHalfWidthMeters * 0.55f);
                Vector3 spawnPos = _laneCenterPos + _laneRight * startLat;
                spawnPos.y = 0f;
                transform.position = spawnPos;
            }

            _lateralDirSign = Random.value < 0.5f ? -1f : 1f;
            _depthBiasOffset = Random.Range(-_patrolDepthJitterMeters * 0.5f, _patrolDepthJitterMeters * 0.5f);

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

            float dmg = zone == SoldierHitZone.Head
                ? headDamage
                : (zone == SoldierHitZone.Torso ? torsoDamage : limbDamage);

            // High-energy rifle rounds (>2400 J, e.g. close-range 7.62) deal slightly higher chest trauma
            if (zone == SoldierHitZone.Torso && impact.KineticEnergyJoules > 2400f)
                dmg = Mathf.Max(dmg, 65f);

            _currentHealth = Mathf.Max(0f, _currentHealth - dmg);
            TriggerHitBurst(impact.HitPoint + impact.HitNormal * 0.03f, isHeadshot: zone == SoldierHitZone.Head);

            AudioClip[] clips = (zone == SoldierHitZone.Head && headHitClips != null && headHitClips.Length > 0)
                ? headHitClips
                : bodyHitClips;
            PlayDelayedImpactAudio(clips, impact.HitPoint);

            if (_currentHealth <= 0f)
            {
                KillCount++;
                EnterDeathState(in impact);
                Debug.Log($"[Asker Vuruşu - ETKİSİZ] {name} | Bölge={zone} | Mesafe={impact.DistanceTraveledMeters:F1}m | Enerji={impact.KineticEnergyJoules:F0}J");
            }
            else
            {
                // Wounded soldier briefly staggers, immediately cuts direction, and bursts into an evasive sprint!
                _flinchSlowTimer = 0.16f;
                _breachReached = false;
                PickNextDeceptiveMove(forceBurstSprint: true, reverseDirection: true);
                SetTargetAnimation(AnimRun, blendSpeed: 12f);
                Debug.Log($"[Asker Vuruşu] {name} | Bölge={zone} | Kalan Can={_currentHealth:F0} | Mesafe={impact.DistanceTraveledMeters:F1}m");
            }
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f)
                return;

            UpdateAnimationPlayables(dt);
            UpdateHitBurst(dt);

            if (!_patrolConfigured)
                return;

            if (_aiState == AiState.Dead)
            {
                _respawnTimer -= dt;
                if (_respawnTimer <= 0f)
                    ResetSoldierState();
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
            _respawnTimer = respawnDelaySeconds;
            SetHitCollidersEnabled(false);

            // Face toward incoming shot direction briefly so the backward Death collapse falls away from the bullet!
            Vector3 shotDir = impact.ImpactVelocity;
            shotDir.y = 0f;
            if (shotDir.sqrMagnitude > 1f)
                transform.rotation = Quaternion.LookRotation(-shotDir.normalized, Vector3.up);

            if (_graphInitialized && _clipPlayables != null && _clipPlayables[AnimDeath].IsValid())
            {
                _clipPlayables[AnimDeath].SetTime(0.0);
                _clipPlayables[AnimDeath].SetSpeed(1.0);
            }
            SetTargetAnimation(AnimDeath, blendSpeed: 14f);
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

        private void PrewarmHitBurst()
        {
            GameObject burst = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            burst.name = "SoldierHitBurst";
            Collider c = burst.GetComponent<Collider>();
            if (c != null) Destroy(c);
            burst.transform.SetParent(transform, false);
            burst.GetComponent<MeshRenderer>().sharedMaterial = GetHitBurstMaterial();
            burst.SetActive(false);
            _hitBurstTransform = burst.transform;
        }

        private void TriggerHitBurst(Vector3 worldPos, bool isHeadshot)
        {
            if (_hitBurstTransform == null)
                return;

            _hitBurstTransform.position = worldPos;
            float size = hitBurstSizeMeters * (isHeadshot ? 1.35f : 1.0f);
            _hitBurstTransform.localScale = Vector3.one * size;
            _hitBurstTransform.gameObject.SetActive(true);
            _hitBurstTimer = hitBurstDurationSec;
        }

        private void UpdateHitBurst(float dt)
        {
            if (_hitBurstTimer <= 0f || _hitBurstTransform == null)
                return;

            _hitBurstTimer -= dt;
            if (_hitBurstTimer <= 0f)
            {
                _hitBurstTransform.gameObject.SetActive(false);
            }
            else
            {
                float norm = Mathf.Clamp01(_hitBurstTimer / Mathf.Max(0.01f, hitBurstDurationSec));
                _hitBurstTransform.localScale = Vector3.one * (hitBurstSizeMeters * (0.5f + 0.8f * norm));
            }
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
        }

        private void PlayDelayedImpactAudio(AudioClip[] clips, Vector3 hitWorldPos)
        {
            if (_audioVoices == null || _audioVoices.Length == 0 || clips == null || clips.Length == 0)
                return;

            AudioClip clip = clips[Random.Range(0, clips.Length)];
            if (clip == null) return;

            AudioSource src = _audioVoices[_nextVoiceIdx];
            _nextVoiceIdx = (_nextVoiceIdx + 1) % _audioVoices.Length;
            src.transform.position = hitWorldPos;
            src.clip = clip;
            src.pitch = Random.Range(0.94f, 1.06f);
            src.volume = hitVolume;

            float delaySec = 0f;
            if (useSpeedOfSoundDelay)
            {
                if (_listenerTransform == null && Camera.main != null)
                    _listenerTransform = Camera.main.transform;
                float dist = _listenerTransform != null ? Vector3.Distance(hitWorldPos, _listenerTransform.position) : 25f;
                float sos = EnvironmentManager.Instance != null ? Mathf.Max(200f, EnvironmentManager.Instance.CurrentSpeedOfSound) : 340.3f;
                delaySec = Mathf.Clamp(dist / sos, 0f, 4f);
            }

            if (delaySec > 0.005f)
                src.PlayDelayed(delaySec);
            else
                src.Play();
        }

        private static Material GetHitBurstMaterial()
        {
            if (_sharedHitBurstMaterial != null)
                return _sharedHitBurstMaterial;

            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
                shader = Shader.Find("Unlit/Color");
            if (shader == null)
                return null;

            _sharedHitBurstMaterial = new Material(shader);
            Color burstCol = new Color(1.0f, 0.72f, 0.25f, 1f);
            if (_sharedHitBurstMaterial.HasProperty(BaseColorId))
                _sharedHitBurstMaterial.SetColor(BaseColorId, burstCol);
            if (_sharedHitBurstMaterial.HasProperty(ColorId))
                _sharedHitBurstMaterial.SetColor(ColorId, burstCol);
            if (_sharedHitBurstMaterial.HasProperty(EmissionColorId))
                _sharedHitBurstMaterial.SetColor(EmissionColorId, burstCol * 2f);
            return _sharedHitBurstMaterial;
        }
    }
}
