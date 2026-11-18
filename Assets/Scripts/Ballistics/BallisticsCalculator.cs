using UnityEngine;

namespace VRAtis.Ballistics
{
    [System.Serializable]
    public class BulletProfile
    {
        [Header("Bullet Identity")]
        public string bulletName = "7.62x51 M118LR 175gr";

        [Header("Metric / Ballistic Values")]
        [Tooltip("Muzzle velocity in m/s")]
        public float muzzleVelocityMS = 792.5f;
        [Tooltip("Bullet mass in grams")]
        public float bulletMassGrams = 11.34f; // 175 gr
        [Tooltip("Drag model used with ballisticCoeff (imperial BC lb/in^2).")]
        public DragModel dragModel = DragModel.G1;
        [Tooltip("Standard imperial Ballistic Coefficient (e.g. 0.505 for G1 or 0.243 for G7).")]
        public float ballisticCoeff = 0.505f;
        [Tooltip("Bullet diameter in mm")]
        public float bulletDiamMM = 7.82f;
        [Tooltip("Barrel twist rate — 1 turn per X mm")]
        public float barrelTwistMM = 304.8f; // 1:12" = 304.8 mm
        [Tooltip("Optic centerline height above bore in cm")]
        public float sightHeightCM = 7.05f;
        [Tooltip("Zero distance in meters (0 = raw barrel elevation without sight zeroing)")]
        public float zeroDistanceMeters = 100f;
    }

    [System.Serializable]
    public class EnvironmentData
    {
        [Header("Atmosphere")]
        public float temperatureC = 15f;
        public float pressureHPa = 1013.25f;
        public float humidityPercent = 50f;
        public float altitudeMeters = 0f;

        [Header("Wind")]
        public float windSpeedMS = 4.47f;
        [Tooltip("0 = head wind, 90 = full-value cross wind")]
        public float windAngleDeg = 90f;

        [Header("Location")]
        public float latitudeDeg = 41f;
        public float shootingAngleDeg = 0f;
    }

    [System.Serializable]
    public class BallisticsResult
    {
        [Header("Vertical (gravity + drag)")]
        public float dropMM;
        public float dropCM;
        public float dropMOA;

        [Header("Horizontal drift — Wind")]
        public float windDriftMM;
        public float windDriftMOA;

        [Header("Horizontal drift — Spin")]
        public float spinDriftMM;
        public float spinDriftMOA;

        [Header("Horizontal drift — Coriolis")]
        public float coriolisMM;
        public float coriolisMOA;

        [Header("Total horizontal")]
        public float totalDriftMOA;

        [Header("Terminal data")]
        public float timeOfFlightS;
        public float velocityMS;
        public float energyJoules;
    }

    /// <summary>
    /// Analytical point-mass RK4 ballistic solver for range cards, turret clicks, and zeroing tables.
    /// Shares atmospheric, drag, form-factor, and zeroing math with BallisticMath & BallisticBullet.
    /// </summary>
    public class BallisticsCalculator : MonoBehaviour
    {
        public BulletProfile bullet = new BulletProfile();
        public EnvironmentData environment = new EnvironmentData();
        [Tooltip("Açıkken mevcut atmosfer ve rüzgâr değerlerini sahnedeki EnvironmentManager'dan okur.")]
        public bool syncFromEnvironmentManager = true;

        public BallisticsResult Calculate(float rangeMeters)
        {
            var r = new BallisticsResult();
            rangeMeters = Mathf.Clamp(rangeMeters, 1f, 2000f);

            float massKg = Mathf.Max(1e-5f, bullet.bulletMassGrams * 1e-3f);
            float massGrain = massKg / BallisticMath.GrainToKg;
            float diamM = Mathf.Max(1e-4f, bullet.bulletDiamMM * 1e-3f);
            float caliberIn = diamM / BallisticMath.InchToMeters;
            float twistM = Mathf.Max(0.01f, bullet.barrelTwistMM * 1e-3f);
            float crossAreaM2 = Mathf.PI * 0.25f * diamM * diamM;
            float formFactor = BallisticMath.FormFactorFromImperialBC(massGrain, caliberIn, bullet.ballisticCoeff);

            EnvironmentManager envMgr = syncFromEnvironmentManager ? EnvironmentManager.Instance : null;
            float tempC = envMgr != null ? envMgr.TemperatureC : environment.temperatureC;
            float pressHPa = envMgr != null ? envMgr.PressureHPa : environment.pressureHPa;
            float humidPct = envMgr != null ? envMgr.HumidityPercent : environment.humidityPercent;
            float altM = envMgr != null ? envMgr.AltitudeMeters : environment.altitudeMeters;
            float gravity = envMgr != null ? envMgr.gravity : BallisticMath.StandardGravity;
            float latitudeDeg = envMgr != null ? envMgr.latitudeDeg : environment.latitudeDeg;

            float rhoAir = BallisticMath.CalculateAirDensity(tempC, pressHPa, humidPct, altM);
            float sos = BallisticMath.CalculateSpeedOfSound(tempC);

            float crossWindMS;
            float headWindMS;
            if (envMgr != null)
            {
                crossWindMS = envMgr.windSpeedMS.x;
                headWindMS = -envMgr.windSpeedMS.z; // +Z wind is tailwind, headwind is -Z
            }
            else
            {
                float windAngleRad = environment.windAngleDeg * Mathf.Deg2Rad;
                crossWindMS = environment.windSpeedMS * Mathf.Sin(windAngleRad);
                headWindMS = environment.windSpeedMS * Mathf.Cos(windAngleRad);
            }

            float sightHeightM = bullet.sightHeightCM * 0.01f;
            float zeroAngleRad = bullet.zeroDistanceMeters > 1f
                ? BallisticMath.SolveZeroingBoreAngleRad(
                    bullet.muzzleVelocityMS,
                    massKg,
                    crossAreaM2,
                    bullet.dragModel,
                    formFactor,
                    sightHeightM,
                    bullet.zeroDistanceMeters,
                    rhoAir,
                    sos,
                    gravity)
                : 0f;

            float sAngRad = (environment.shootingAngleDeg * Mathf.Deg2Rad) + zeroAngleRad;
            float dragScale = (0.5f * rhoAir * crossAreaM2 * formFactor) / massKg;

            Vector2 CalcAccel(Vector2 vel)
            {
                float relVx = vel.x + headWindMS; // headWindMS opposes flight (+X downrange -> +headWind increases relative airspeed)
                float relVy = vel.y;
                float relV = Mathf.Sqrt(relVx * relVx + relVy * relVy);
                if (relV < 0.01f)
                    return new Vector2(0f, -gravity);

                float mach = relV / Mathf.Max(100f, sos);
                float cd = BallisticMath.GetCd(bullet.dragModel, mach);
                float decel = dragScale * cd * relV;
                return new Vector2(-decel * relVx, -decel * relVy - gravity);
            }

            Vector2 pos = Vector2.zero;
            Vector2 velVec = new Vector2(Mathf.Cos(sAngRad), Mathf.Sin(sAngRad)) * bullet.muzzleVelocityMS;
            float t = 0f;
            const float dt = 0.005f;
            const int maxIter = 4000;

            for (int iter = 0; iter < maxIter && pos.x < rangeMeters && t < 20f; iter++)
            {
                if (velVec.magnitude < 30f) break;

                Vector2 k1v = CalcAccel(velVec);
                Vector2 v1 = velVec + k1v * (dt * 0.5f);
                Vector2 k2v = CalcAccel(v1);
                Vector2 v2 = velVec + k2v * (dt * 0.5f);
                Vector2 k3v = CalcAccel(v2);
                Vector2 v3 = velVec + k3v * dt;
                Vector2 k4v = CalcAccel(v3);

                Vector2 nextPos = pos + ((velVec + 2f * v1 + 2f * v2 + v3) / 6f) * dt;
                Vector2 nextVel = velVec + ((k1v + 2f * k2v + 2f * k3v + k4v) / 6f) * dt;
                t += dt;

                if (nextPos.x >= rangeMeters)
                {
                    float frac = (rangeMeters - pos.x) / Mathf.Max(1e-5f, nextPos.x - pos.x);
                    pos = Vector2.Lerp(pos, nextPos, frac);
                    velVec = Vector2.Lerp(velVec, nextVel, frac);
                    t -= dt * (1f - frac);
                    break;
                }

                pos = nextPos;
                velVec = nextVel;
            }

            float rangeYards = rangeMeters * BallisticMath.MetersToYards;
            float dropRelativeToLosM = pos.y - (sightHeightM + rangeMeters * Mathf.Tan(environment.shootingAngleDeg * Mathf.Deg2Rad));

            r.dropMM = dropRelativeToLosM * 1000f;
            r.dropCM = dropRelativeToLosM * 100f;
            r.dropMOA = BallisticMath.InchesToMOA(Mathf.Abs(r.dropMM) * BallisticMath.MMToInches, rangeYards);
            if (dropRelativeToLosM < 0f) r.dropMOA = -r.dropMOA;

            r.timeOfFlightS = t;
            r.velocityMS = velVec.magnitude;
            r.energyJoules = 0.5f * massKg * r.velocityMS * r.velocityMS;

            float lagTime = Mathf.Max(0f, t - rangeMeters / Mathf.Max(1f, bullet.muzzleVelocityMS * Mathf.Cos(sAngRad)));
            float driftM = crossWindMS * lagTime;
            r.windDriftMM = driftM * 1000f;
            r.windDriftMOA = BallisticMath.InchesToMOA(Mathf.Abs(r.windDriftMM) * BallisticMath.MMToInches, rangeYards);
            if (driftM < 0f) r.windDriftMOA = -r.windDriftMOA;

            float sg = BallisticMath.CalculateGreenhillSG(massKg, diamM, twistM);
            float spinDriftM = 0.0127f * (sg + 1.2f) * Mathf.Pow(t, 1.83f);
            r.spinDriftMM = spinDriftM * 1000f;
            r.spinDriftMOA = BallisticMath.InchesToMOA(r.spinDriftMM * BallisticMath.MMToInches, rangeYards);

            float latRad = latitudeDeg * Mathf.Deg2Rad;
            float coriolisM = BallisticMath.EarthAngularSpeedRadS * Mathf.Sin(latRad) * rangeMeters * t;
            r.coriolisMM = coriolisM * 1000f;
            r.coriolisMOA = BallisticMath.InchesToMOA(r.coriolisMM * BallisticMath.MMToInches, rangeYards);

            r.totalDriftMOA = r.windDriftMOA + r.spinDriftMOA + r.coriolisMOA;
            return r;
        }
    }
}
