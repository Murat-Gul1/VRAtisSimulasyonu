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
        [Header("Mermi (M118LR 175gr Varsayılan)")]
        [SerializeField] private float bulletWeightGrain = 175f;
        [SerializeField] private float caliberInch = 0.308f;
        [SerializeField] private float baseMuzzleVelocityMS = 792.5f;
        [SerializeField] private DragModel dragModel = DragModel.G7;
        [SerializeField] private float g7FormFactor = 1.04f;

        [Header("6-DOF Aerodinamik Sabitler (Carlucci/McCoy)")]
        [SerializeField] private float twistRateInch = 12f;
        [SerializeField] private float cMAlpha = 3.10f;
        [SerializeField] private float cNAlpha = 2.00f;
        [SerializeField] private float cNPa = -0.10f;
        [SerializeField] private float cLP = -0.012f;
        [SerializeField] private float axialMomentOfInertia = 0.000015f;

        [Header("Sıfırlama (Zeroing) ve Geometri")]
        [SerializeField] private float sightHeightCM = 7.05f;
        [SerializeField] private float zeroDistanceMeters = 100f;

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

        private float _bulletMassKg;
        private float _bulletDiameterM;
        private float _referenceAreaM2;
        private float _cachedBoreAngleRad;
        private float _cachedAirDensity = -1f;
        private float _cachedSpeedOfSound = -1f;
        private float _cachedMuzzleVelocity = -1f;
        private float _cachedZeroDistance = -1f;

        private bool _isActive;
        private Action<BallisticBullet> _onReturnToPool;
        private TrailRenderer _trailRenderer;

        public static void ResetBarrelHeat()
        {
            GlobalShotCounter = 0;
            LastShotTime = -999f;
        }

        private void Awake()
        {
            RecomputePhysicalConstants();
            _trailRenderer = GetComponentInChildren<TrailRenderer>();
        }

        private void OnValidate()
        {
            RecomputePhysicalConstants();
        }

        private void RecomputePhysicalConstants()
        {
            _bulletMassKg = bulletWeightGrain * BallisticMath.GrainToKg;
            _bulletDiameterM = caliberInch * BallisticMath.InchToMeters;
            float radius = _bulletDiameterM * 0.5f;
            _referenceAreaM2 = Mathf.PI * radius * radius;
        }

        /// <summary>
        /// Initializes and launches the bullet from <paramref name="muzzlePosition"/> along <paramref name="aimRotation"/>
        /// with optional scope turret offsets in radians.
        /// </summary>
        public void Launch(
            Vector3 muzzlePosition,
            Quaternion aimRotation,
            float turretElevationRad = 0f,
            float turretWindageRad = 0f,
            Action<BallisticBullet> returnToPoolCallback = null)
        {
            _onReturnToPool = returnToPoolCallback;
            RecomputePhysicalConstants();

            // Barrel heating & cooldown (1 shot of heat dissipates every 3 seconds of idle time)
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

            // Start slightly ahead of muzzle to avoid hitting shooter colliders at t=0
            transform.position = muzzlePosition + fireDirection.normalized * 0.15f;
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
                !Mathf.Approximately(zeroDistanceMeters, _cachedZeroDistance))
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

            float dt = Time.fixedDeltaTime;

            // 4th-order Runge-Kutta (RK4) integration
            Vector3 v1 = velocity;
            float s1 = currentSpinRad;
            Vector3 k1 = Calculate6DofDerivatives(v1, s1, transform.position, distanceTraveled, out float ks1);

            Vector3 v2 = velocity + k1 * (dt * 0.5f);
            float s2 = currentSpinRad + ks1 * (dt * 0.5f);
            Vector3 k2 = Calculate6DofDerivatives(v2, s2, transform.position + v1 * (dt * 0.5f), distanceTraveled, out float ks2);

            Vector3 v3 = velocity + k2 * (dt * 0.5f);
            float s3 = currentSpinRad + ks2 * (dt * 0.5f);
            Vector3 k3 = Calculate6DofDerivatives(v3, s3, transform.position + v2 * (dt * 0.5f), distanceTraveled, out float ks3);

            Vector3 v4 = velocity + k3 * dt;
            float s4 = currentSpinRad + ks3 * dt;
            Vector3 k4 = Calculate6DofDerivatives(v4, s4, transform.position + v3 * dt, distanceTraveled, out float ks4);

            velocity += ((k1 + 2f * k2 + 2f * k3 + k4) / 6f) * dt;
            currentSpinRad += ((ks1 + 2f * ks2 + 2f * ks3 + ks4) / 6f) * dt;

            // Raindrop stochastic perturbation
            EnvironmentManager env = EnvironmentManager.Instance;
            if (env != null && env.isRaining)
            {
                float hitChancePerStep = (env.rainIntensityPercent * 0.01f) * dt;
                if (UnityEngine.Random.value < hitChancePerStep)
                {
                    velocity += new Vector3(
                        UnityEngine.Random.Range(-0.5f, 0.5f),
                        UnityEngine.Random.Range(-0.5f, 0.5f),
                        0f);
                }
            }

            Vector3 currentPos = transform.position;
            Vector3 nextPos = currentPos + velocity * dt;

            if (distanceTraveled > 1.0f && Physics.Linecast(currentPos, nextPos, out RaycastHit hit, hitMask, QueryTriggerInteraction.Ignore))
            {
                HandleTerminalBallistics(hit);
                return;
            }

            distanceTraveled += Vector3.Distance(currentPos, nextPos);
            transform.position = nextPos;
            flightTime += dt;
            UpdateVisualRotation();

            if (transform.position.y < -100f || flightTime >= maxFlightTimeSeconds || velocity.sqrMagnitude < 2500f)
            {
                Despawn();
            }
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

            // 2. Spin damping
            spinDamping = (0.25f * rho * relSpeed * _referenceAreaM2 * (_bulletDiameterM * _bulletDiameterM) * cLP * spinRad) / axialMomentOfInertia;

            // 3. Yaw of repose (clamped to prevent divergence at near-vertical low speed)
            float yawOfReposeRad = (8f * axialMomentOfInertia * spinRad * gravity) /
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

            // 5. Normal force (Lift / Spin Drift)
            Vector3 alphaVec = xVec - Vector3.Dot(xVec, vHat) * vHat;
            float liftScale = _bulletDiameterM;
            Vector3 liftAccel = (0.5f * rho * _referenceAreaM2 * (relSpeed * relSpeed) * cNAlpha * alphaVec * liftScale) / _bulletMassKg;

            // 6. Transonic buffeting (Mach 0.8 - 1.2)
            Vector3 buffetingAccel = Vector3.zero;
            if (mach <= 1.2f && mach >= 0.8f)
            {
                float intensity = (1.2f - mach) * 0.05f;
                float buffetingMag = intensity * (0.5f * rho * _referenceAreaM2 / _bulletMassKg) * (relSpeed * relSpeed);
                float noiseX = Mathf.PerlinNoise(0f, flightTime * 50f) - 0.5f;
                float noiseY = Mathf.PerlinNoise(flightTime * 50f, 0f) - 0.5f;

                Vector3 lateral = Vector3.Cross(Vector3.up, vHat).normalized;
                Vector3 vertical = Vector3.Cross(vHat, lateral).normalized;
                buffetingAccel = (lateral * noiseX + vertical * noiseY) * (buffetingMag * 0.5f);
            }

            // 7. Earth curvature gravity tilt
            float earthAngleRad = distTraveled / Mathf.Max(1000f, earthRadius);
            Vector3 gravityAccel = new Vector3(0f, -gravity * Mathf.Cos(earthAngleRad), -gravity * Mathf.Sin(earthAngleRad));

            return gravityAccel + dragAccel + magnusAccel + liftAccel + buffetingAccel;
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
