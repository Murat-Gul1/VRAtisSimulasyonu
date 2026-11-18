using System;
using UnityEngine;

namespace VRAtis.Ballistics
{
    /// <summary>
    /// URP-compatible 6-DOF RK4 ballistic projectile adapted for mobile VR.
    /// Supports object pooling, G7/G1 drag, Magnus, spin drift, yaw of repose,
    /// transonic buffeting, earth curvature, barrel heating, and terminal ballistics.
    /// </summary>
    public class BallisticBullet : MonoBehaviour
    {
        [Header("Mermi Profili")]
        [SerializeField] private AmmoPreset ammoPreset = AmmoPreset.Nato762_M118LR_175gr;
        [SerializeField] private float bulletWeightGrain = 175f;
        [SerializeField] private float caliberInch = 0.308f;
        [SerializeField] private float baseMuzzleVelocityMS = 792.5f;
        [SerializeField] private DragModel dragModel = DragModel.G7;
        [SerializeField] private float g7FormFactor = 1.085f;

        [Header("6-DOF Aerodinamik Sabitler (STANAG 4355 / Carlucci-McCoy)")]
        [SerializeField] private float twistRateInch = 12f;
        [SerializeField] private float cMAlpha = 3.10f;
        [SerializeField] private float cNAlpha = 1.85f;
        [SerializeField] private float cNPa = -0.10f;
        [SerializeField] private float cLP = -0.012f;
        [Tooltip("Açıkken mermi kütlesi ve çapından gerçekçi sivri uçlu silindir atalet momenti otomatik hesaplanır.")]
        [SerializeField] private bool autoComputeAxialInertia = true;
        [SerializeField] private float axialMomentOfInertia = 0.000000073f;

        [Header("Sıfırlama (Zeroing), İntegrasyon ve Namlu")]
        [SerializeField] private float sightHeightCM = 7.05f;
        [SerializeField] private float zeroDistanceMeters = 100f;
        [Tooltip("Her FixedUpdate karesinde yapılacak RK4 alt-adım sayısı (4 adım = 5 ms çözünürlük).")]
        [Range(1, 8)]
        [SerializeField] private int rk4SubSteps = 4;
        [SerializeField] private bool simulateBarrelHeating = true;

        [Header("Terminal Balistik")]
        [SerializeField] private LayerMask hitMask = ~0;
        [SerializeField] private float ricochetAngleThreshold = 70f;
        [SerializeField] private float retainedEnergyOnRicochet = 0.55f;
        [SerializeField] private float maxFlightTimeSeconds = 8f;

        [Header("Görsel ve İz (URP Uyumlu)")]
        [SerializeField] private Vector3 visualRotationOffset = Vector3.zero;
        [SerializeField] private GameObject tracerVisual;
        [SerializeField] private Light tracerLight;
        [SerializeField] private bool alwaysShowTracer = true;

        [Header("Canlı Balistik Veriler (Sadece Okunur)")]
        public Vector3 velocity;
        public float flightTime;
        public float distanceTraveled;
        public float currentSpinRad;
        public float currentMuzzleVelocity;

        // Barrel heat tracker across shots
        public static int GlobalShotCounter { get; private set; }
        public static float LastShotTime { get; private set; } = -999f;
        private static readonly RaycastHit[] RayHitBuffer = new RaycastHit[8];

        private float _bulletMassKg;
        private float _bulletDiameterM;
        private float _referenceAreaM2;
        private float _cachedBoreAngleRad;
        private float _cachedAirDensity = -1f;
        private float _cachedSpeedOfSound = -1f;
        private float _cachedMuzzleVelocity = -1f;
        private float _cachedZeroDistance = -1f;
        private float _cachedSightHeight = -1f;

        private bool _isActive;
        private Transform _shooterRoot;
        private Action<BallisticBullet> _onReturnToPool;
        private TrailRenderer _trailRenderer;

        public static void ResetBarrelHeat()
        {
            GlobalShotCounter = 0;
            LastShotTime = -999f;
        }

        private void Awake()
        {
            ApplyAmmoPresetIfNeeded();
            RecomputePhysicalConstants();
            _trailRenderer = GetComponentInChildren<TrailRenderer>();
        }

        private void OnValidate()
        {
            ApplyAmmoPresetIfNeeded();
            RecomputePhysicalConstants();
        }

        public void ApplyAmmoPreset(AmmoPreset preset)
        {
            ammoPreset = preset;
            ApplyAmmoPresetIfNeeded();
            RecomputePhysicalConstants();
        }

        private void ApplyAmmoPresetIfNeeded()
        {
            if (ammoPreset == AmmoPreset.Custom)
                return;

            AmmoPresetSpec spec = BallisticMath.GetAmmoPresetSpec(ammoPreset);
            bulletWeightGrain = spec.BulletWeightGrain;
            caliberInch = spec.CaliberInch;
            baseMuzzleVelocityMS = spec.MuzzleVelocityMS;
            dragModel = spec.DragModel;
            g7FormFactor = spec.FormFactor;
            twistRateInch = spec.TwistRateInch;
        }

        private void RecomputePhysicalConstants()
        {
            _bulletMassKg = Mathf.Max(1e-5f, bulletWeightGrain * BallisticMath.GrainToKg);
            _bulletDiameterM = Mathf.Max(1e-4f, caliberInch * BallisticMath.InchToMeters);
            float radius = _bulletDiameterM * 0.5f;
            _referenceAreaM2 = Mathf.PI * radius * radius;
            if (autoComputeAxialInertia)
                axialMomentOfInertia = BallisticMath.ComputeAxialMomentOfInertia(_bulletMassKg, _bulletDiameterM);
        }

        /// <summary>
        /// Initializes and launches the bullet from <paramref name="muzzlePosition"/> along <paramref name="aimRotation"/>
        /// with optional scope turret offsets in radians and optional measured sight height / shooter root filter.
        /// </summary>
        public void Launch(
            Vector3 muzzlePosition,
            Quaternion aimRotation,
            float turretElevationRad = 0f,
            float turretWindageRad = 0f,
            Action<BallisticBullet> returnToPoolCallback = null,
            float? sightHeightCmOverride = null,
            Transform shooterRoot = null,
            AmmoPreset? presetOverride = null)
        {
            _onReturnToPool = returnToPoolCallback;
            _shooterRoot = shooterRoot;

            if (presetOverride.HasValue && presetOverride.Value != AmmoPreset.Custom)
                ammoPreset = presetOverride.Value;

            if (sightHeightCmOverride.HasValue)
                sightHeightCM = Mathf.Max(0f, sightHeightCmOverride.Value);

            ApplyAmmoPresetIfNeeded();
            RecomputePhysicalConstants();

            // Barrel heating & cooldown (1 shot of heat dissipates every 3 seconds of idle time)
            if (simulateBarrelHeating)
            {
                float timeSinceLastShot = Time.time - LastShotTime;
                if (timeSinceLastShot > 3f)
                {
                    int cooldownShots = Mathf.FloorToInt(timeSinceLastShot / 3f);
                    GlobalShotCounter = Mathf.Max(0, GlobalShotCounter - cooldownShots);
                }
                // Cap barrel thermal state at 12 rapid shots so muzzle velocity stays physically realistic
                GlobalShotCounter = Mathf.Min(GlobalShotCounter + 1, 12);
                LastShotTime = Time.time;

                float harmonicShiftMOA = (GlobalShotCounter - 1) * 0.15f;
                float extraVelocity = (GlobalShotCounter - 1) * 2.0f;
                currentMuzzleVelocity = baseMuzzleVelocityMS + extraVelocity;
                LaunchWithHeat(muzzlePosition, aimRotation, turretElevationRad, turretWindageRad, harmonicShiftMOA);
            }
            else
            {
                currentMuzzleVelocity = baseMuzzleVelocityMS;
                LaunchWithHeat(muzzlePosition, aimRotation, turretElevationRad, turretWindageRad, 0f);
            }
        }

        private void LaunchWithHeat(
            Vector3 muzzlePosition,
            Quaternion aimRotation,
            float turretElevationRad,
            float turretWindageRad,
            float harmonicShiftMOA)
        {
            float twistRateM = twistRateInch * BallisticMath.InchToMeters;
            float initialSpinHz = currentMuzzleVelocity / Mathf.Max(0.01f, twistRateM);
            currentSpinRad = initialSpinHz * 2f * Mathf.PI;

            float boreAngleRad = GetOrSolveBoreAngleRad() + BallisticMath.MOAToRadians(harmonicShiftMOA);
            float totalElevDeg = (boreAngleRad + turretElevationRad) * Mathf.Rad2Deg;
            float totalWindDeg = turretWindageRad * Mathf.Rad2Deg;

            Vector3 forward = aimRotation * Vector3.forward;
            Vector3 right = aimRotation * Vector3.right;
            Vector3 up = aimRotation * Vector3.up;

            Vector3 fireDirection = Quaternion.AngleAxis(-totalElevDeg, right) * forward;
            fireDirection = Quaternion.AngleAxis(totalWindDeg, up) * fireDirection;

            velocity = fireDirection.normalized * currentMuzzleVelocity;
            flightTime = 0f;
            distanceTraveled = 0f;

            // Spawn just beyond the muzzle crown so close-range targets (>= 0.5m) are hit cleanly
            transform.position = muzzlePosition + fireDirection.normalized * 0.05f;
            UpdateVisualRotation();
            UpdateTracerState();

            _isActive = true;
            gameObject.SetActive(true);

            if (_trailRenderer != null)
                _trailRenderer.Clear();
        }

        private float GetOrSolveBoreAngleRad()
        {
            EnvironmentManager env = EnvironmentManager.Instance;
            float rho = env != null ? env.CurrentAirDensity : 1.225f;
            float sos = env != null ? env.CurrentSpeedOfSound : 340.3f;
            float grav = env != null ? env.gravity : BallisticMath.StandardGravity;

            if (!Mathf.Approximately(rho, _cachedAirDensity) ||
                !Mathf.Approximately(sos, _cachedSpeedOfSound) ||
                !Mathf.Approximately(currentMuzzleVelocity, _cachedMuzzleVelocity) ||
                !Mathf.Approximately(zeroDistanceMeters, _cachedZeroDistance) ||
                !Mathf.Approximately(sightHeightCM, _cachedSightHeight))
            {
                _cachedBoreAngleRad = BallisticMath.SolveZeroingBoreAngleRad(
                    currentMuzzleVelocity,
                    _bulletMassKg,
                    _referenceAreaM2,
                    dragModel,
                    g7FormFactor,
                    sightHeightCM * 0.01f,
                    zeroDistanceMeters,
                    rho,
                    sos,
                    grav);

                _cachedAirDensity = rho;
                _cachedSpeedOfSound = sos;
                _cachedMuzzleVelocity = currentMuzzleVelocity;
                _cachedZeroDistance = zeroDistanceMeters;
                _cachedSightHeight = sightHeightCM;
            }

            return _cachedBoreAngleRad;
        }

        private void UpdateTracerState()
        {
            bool enableTracer = alwaysShowTracer ||
                                (EnvironmentManager.Instance != null &&
                                 (EnvironmentManager.Instance.isRaining || EnvironmentManager.Instance.isNight));

            if (tracerVisual != null)
                tracerVisual.SetActive(enableTracer);

            if (tracerLight != null)
                tracerLight.enabled = enableTracer && EnvironmentManager.Instance != null &&
                                     (EnvironmentManager.Instance.isRaining || EnvironmentManager.Instance.isNight);
        }

        private void FixedUpdate()
        {
            if (!_isActive)
                return;

            int subSteps = Mathf.Clamp(rk4SubSteps, 1, 8);
            float subDt = Time.fixedDeltaTime / subSteps;
            EnvironmentManager env = EnvironmentManager.Instance;

            for (int step = 0; step < subSteps; step++)
            {
                Vector3 currentPos = transform.position;
                Vector3 nextPos = currentPos;
                Vector3 nextVel = velocity;
                float nextSpin = currentSpinRad;

                StepRk4(ref nextPos, ref nextVel, ref nextSpin, distanceTraveled, subDt);

                velocity = nextVel;
                currentSpinRad = nextSpin;

                if (env != null && env.isRaining)
                {
                    float hitChancePerStep = (env.rainIntensityPercent * 0.01f) * subDt;
                    if (UnityEngine.Random.value < hitChancePerStep)
                    {
                        velocity += new Vector3(
                            UnityEngine.Random.Range(-0.5f, 0.5f),
                            UnityEngine.Random.Range(-0.5f, 0.5f),
                            0f);
                    }
                }

                if (TryRaycastSegmentNonShooter(currentPos, nextPos, out RaycastHit hit))
                {
                    HandleTerminalBallistics(hit);
                    return;
                }

                distanceTraveled += Vector3.Distance(currentPos, nextPos);
                transform.position = nextPos;
                flightTime += subDt;

                if (transform.position.y < -100f || flightTime >= maxFlightTimeSeconds || velocity.sqrMagnitude < 2500f)
                {
                    UpdateVisualRotation();
                    Despawn();
                    return;
                }
            }

            UpdateVisualRotation();
        }

        private void StepRk4(ref Vector3 pos, ref Vector3 vel, ref float spinRad, float distTraveled, float dt)
        {
            float halfDt = dt * 0.5f;

            Vector3 k1v = Calculate6DofDerivatives(vel, spinRad, pos, distTraveled, out float ks1);
            Vector3 v1 = vel + k1v * halfDt;
            Vector3 k2v = Calculate6DofDerivatives(v1, spinRad + ks1 * halfDt, pos + vel * halfDt, distTraveled, out float ks2);
            Vector3 v2 = vel + k2v * halfDt;
            Vector3 k3v = Calculate6DofDerivatives(v2, spinRad + ks2 * halfDt, pos + v1 * halfDt, distTraveled, out float ks3);
            Vector3 v3 = vel + k3v * dt;
            Vector3 k4v = Calculate6DofDerivatives(v3, spinRad + ks3 * dt, pos + v2 * dt, distTraveled, out float ks4);

            pos += ((vel + 2f * v1 + 2f * v2 + v3) / 6f) * dt;
            vel += ((k1v + 2f * k2v + 2f * k3v + k4v) / 6f) * dt;
            spinRad += ((ks1 + 2f * ks2 + 2f * ks3 + ks4) / 6f) * dt;
        }

        private bool TryRaycastSegmentNonShooter(Vector3 start, Vector3 end, out RaycastHit nearestValidHit)
        {
            nearestValidHit = default;
            Vector3 delta = end - start;
            float dist = delta.magnitude;
            if (dist < 1e-5f)
                return false;

            int count = Physics.RaycastNonAlloc(start, delta / dist, RayHitBuffer, dist, hitMask, QueryTriggerInteraction.Ignore);
            if (count <= 0)
                return false;

            float bestDist = float.MaxValue;
            bool found = false;

            for (int i = 0; i < count; i++)
            {
                RaycastHit candidate = RayHitBuffer[i];
                if (_shooterRoot != null && candidate.collider != null && candidate.collider.transform.IsChildOf(_shooterRoot))
                    continue;

                if (candidate.distance < bestDist)
                {
                    bestDist = candidate.distance;
                    nearestValidHit = candidate;
                    found = true;
                }
            }

            return found;
        }

        private Vector3 Calculate6DofDerivatives(Vector3 vVec, float spinRad, Vector3 currentPos, float distTraveled, out float spinDamping)
        {
            EnvironmentManager env = EnvironmentManager.Instance;
            float gravity = env != null ? env.gravity : BallisticMath.StandardGravity;
            float rho = env != null ? env.CurrentAirDensity : 1.225f;
            float speedOfSound = env != null ? env.CurrentSpeedOfSound : 340.3f;
            float earthRadius = env != null ? env.earthRadiusMeters : 6371000f;
            Vector3 windVec = env != null ? env.GetWindAtAltitude(currentPos.y) : Vector3.zero;

            Vector3 relVel = vVec - windVec;
            float relSpeed = relVel.magnitude;

            if (relSpeed < 0.01f)
            {
                spinDamping = 0f;
                return new Vector3(0f, -gravity, 0f);
            }

            float mach = relSpeed / speedOfSound;
            float cd = BallisticMath.GetCd(dragModel, mach) * g7FormFactor;

            // 1. Aerodynamic drag
            float dragMag = (0.5f * rho * _referenceAreaM2 * cd) / _bulletMassKg * (relSpeed * relSpeed);
            Vector3 vHat = relVel / relSpeed;
            Vector3 dragAccel = -dragMag * vHat;

            // 2. Spin damping (STANAG 4355)
            float safeInertia = Mathf.Max(1e-10f, axialMomentOfInertia);
            spinDamping = (0.25f * rho * relSpeed * _referenceAreaM2 * (_bulletDiameterM * _bulletDiameterM) * cLP * spinRad) / safeInertia;

            // 3. Yaw of repose (McCoy / STANAG 4355 Eq. 11.4)
            float yawOfReposeRad = (8f * safeInertia * spinRad * gravity) /
                                   (Mathf.PI * rho * Mathf.Pow(_bulletDiameterM, 3f) * Mathf.Pow(relSpeed, 3f) * cMAlpha);
            yawOfReposeRad = Mathf.Clamp(yawOfReposeRad, -0.05f, 0.05f);

            Vector3 xVec = vHat;
            Vector3 rightDir = Vector3.Cross(Vector3.up, xVec);
            if (rightDir.sqrMagnitude > 1e-6f)
            {
                xVec = (xVec + rightDir.normalized * yawOfReposeRad).normalized;
            }

            // 4. Magnus acceleration
            Vector3 xCrossV = Vector3.Cross(xVec, relVel);
            Vector3 magnusAccel = (0.25f * rho * _referenceAreaM2 * (_bulletDiameterM * _bulletDiameterM) * cNPa * spinRad * xCrossV) / _bulletMassKg;

            // 5. Normal force / Spin Drift lift (STANAG 4355 Eq. 11.5: dimensionless alpha_R)
            Vector3 alphaVec = xVec - Vector3.Dot(xVec, vHat) * vHat;
            Vector3 liftAccel = (0.5f * rho * _referenceAreaM2 * (relSpeed * relSpeed) * cNAlpha * alphaVec) / _bulletMassKg;

            // 6. Transonic buffeting (Mach 0.8 - 1.2)
            Vector3 buffetingAccel = Vector3.zero;
            if (mach <= 1.2f && mach >= 0.8f)
            {
                float intensity = (1.2f - mach) * 0.05f;
                float buffetingMag = intensity * (0.5f * rho * _referenceAreaM2 / _bulletMassKg) * (relSpeed * relSpeed);
                float noiseX = Mathf.PerlinNoise(0f, flightTime * 50f) - 0.5f;
                float noiseY = Mathf.PerlinNoise(flightTime * 50f, 0f) - 0.5f;

                Vector3 lateral = Vector3.Cross(Vector3.up, vHat);
                if (lateral.sqrMagnitude > 1e-6f)
                {
                    lateral.Normalize();
                    Vector3 vertical = Vector3.Cross(vHat, lateral).normalized;
                    buffetingAccel = (lateral * noiseX + vertical * noiseY) * (buffetingMag * 0.5f);
                }
            }

            // 7. Earth curvature gravity tilt (spherical surface frame)
            float earthAngleRad = distTraveled / Mathf.Max(1000f, earthRadius);
            Vector3 gravityAccel = new Vector3(0f, -gravity * Mathf.Cos(earthAngleRad), -gravity * Mathf.Sin(earthAngleRad));

            // 8. Coriolis & Eotvos acceleration (Unity LH frame: +2 * Cross(Omega, v))
            Vector3 coriolisAccel = env != null
                ? 2f * Vector3.Cross(env.GetEarthAngularVelocityUnity(), relVel)
                : Vector3.zero;

            return gravityAccel + dragAccel + magnusAccel + liftAccel + buffetingAccel + coriolisAccel;
        }

        private void HandleTerminalBallistics(RaycastHit hit)
        {
            float impactSpeed = velocity.magnitude;
            float kineticEnergyJoule = 0.5f * _bulletMassKg * (impactSpeed * impactSpeed);
            float impactAngleDeg = Mathf.Acos(Mathf.Clamp(Vector3.Dot(-velocity.normalized, hit.normal), -1f, 1f)) * Mathf.Rad2Deg;

            distanceTraveled += Vector3.Distance(transform.position, hit.point);

            var impactInfo = new BulletImpactInfo
            {
                HitPoint = hit.point,
                HitNormal = hit.normal,
                ImpactVelocity = velocity,
                KineticEnergyJoules = kineticEnergyJoule,
                BulletMassKg = _bulletMassKg,
                DistanceTraveledMeters = distanceTraveled,
                FlightTimeSeconds = flightTime,
                ImpactAngleDeg = impactAngleDeg
            };

            IBulletImpactTarget target = hit.collider.GetComponentInParent<IBulletImpactTarget>();
            target?.OnBulletImpact(in impactInfo);

            Debug.Log($"[Balistik Vuruş] Hedef: {hit.collider.name} | Mesafe: {distanceTraveled:F1}m | Hız: {impactSpeed:F1} m/s | Enerji: {kineticEnergyJoule:F0} J | Süre: {flightTime:F3}s");

            // Terrain/Ground absorbs the bullet without ricochet
            if (hit.collider is TerrainCollider || hit.collider.tag == "Ground")
            {
                Despawn();
                return;
            }

            // Shallow-angle ricochet
            if (impactAngleDeg >= ricochetAngleThreshold)
            {
                float remainingEnergy = kineticEnergyJoule * retainedEnergyOnRicochet;
                float ricochetSpeed = Mathf.Sqrt((2f * remainingEnergy) / _bulletMassKg);
                velocity = Vector3.Reflect(velocity, hit.normal).normalized * ricochetSpeed;
                transform.position = hit.point + hit.normal * 0.05f;
                UpdateVisualRotation();

                if (velocity.magnitude < 50f)
                    Despawn();
                return;
            }

            // Penetration vs stop
            float resistanceJoule = hit.collider.tag == "SteelArmor" ? 50000f : 300f;
            if (kineticEnergyJoule > resistanceJoule)
            {
                float remainingEnergy = kineticEnergyJoule - resistanceJoule;
                float exitSpeed = Mathf.Sqrt((2f * remainingEnergy) / _bulletMassKg);
                velocity = velocity.normalized * exitSpeed;
                transform.position = hit.point + velocity.normalized * 0.2f;
                UpdateVisualRotation();

                if (velocity.magnitude < 50f)
                    Despawn();
            }
            else
            {
                Despawn();
            }
        }

        private void UpdateVisualRotation()
        {
            if (velocity.sqrMagnitude > 0.001f)
            {
                transform.rotation = Quaternion.LookRotation(velocity.normalized) * Quaternion.Euler(visualRotationOffset);
            }
        }

        private void Despawn()
        {
            _isActive = false;
            if (_onReturnToPool != null)
            {
                gameObject.SetActive(false);
                _onReturnToPool.Invoke(this);
            }
            else
            {
                Destroy(gameObject);
            }
        }
    }
}
