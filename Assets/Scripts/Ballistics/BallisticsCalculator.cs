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
        [Tooltip("Ballistic Coefficient (G1) or Form Factor (G7) depending on dragModel")]
        public float ballisticCoeff = 0.505f;
        [Tooltip("Bullet diameter in mm")]
        public float bulletDiamMM = 7.82f;
        [Tooltip("Barrel twist rate — 1 turn per X mm")]
        public float barrelTwistMM = 285.75f; // 1:11.25" or 1:12" = 304.8 mm
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
    /// Analytical point-mass ballistic solver for range cards, turret clicks, and zeroing tables.
    /// Shares atmospheric and drag math with BallisticMath.
    /// </summary>
    public class BallisticsCalculator : MonoBehaviour
    {
        public BulletProfile bullet = new BulletProfile();
        public EnvironmentData environment = new EnvironmentData();

        public BallisticsResult Calculate(float rangeMeters)
        {
            var r = new BallisticsResult();
            rangeMeters = Mathf.Clamp(rangeMeters, 1f, 2000f);

            float massKg = bullet.bulletMassGrams * 1e-3f;
            float diamM = bullet.bulletDiamMM * 1e-3f;
            float twistM = bullet.barrelTwistMM * 1e-3f;
            float crossAreaM2 = Mathf.PI * 0.25f * diamM * diamM;

            float rhoAir = BallisticMath.CalculateAirDensity(
                environment.temperatureC,
                environment.pressureHPa,
                environment.humidityPercent,
                environment.altitudeMeters);
            float sos = BallisticMath.CalculateSpeedOfSound(environment.temperatureC);

            float windAngleRad = environment.windAngleDeg * Mathf.Deg2Rad;
            float crossWindMS = environment.windSpeedMS * Mathf.Sin(windAngleRad);
            float headWindMS = environment.windSpeedMS * Mathf.Cos(windAngleRad);

            float sAngRad = environment.shootingAngleDeg * Mathf.Deg2Rad;
            float cosAng = Mathf.Cos(sAngRad);
            float sinAng = Mathf.Sin(sAngRad);

            float vx = bullet.muzzleVelocityMS * cosAng;
            float vy = bullet.muzzleVelocityMS * sinAng;
            float x = 0f;
            float y = 0f;
            float t = 0f;

            const float dt = 0.002f;
            const int maxIter = 15000;

            for (int iter = 0; iter < maxIter && x < rangeMeters && t < 20f; iter++)
            {
                float v = Mathf.Sqrt(vx * vx + vy * vy);
                if (v < 30f) break;

                float mach = v / sos;
                float cd = BallisticMath.GetG1Cd(mach);
                float dragK = (rhoAir * cd * crossAreaM2) / (2f * massKg * bullet.ballisticCoeff);

                float relVx = vx - headWindMS;
                float relVy = vy;
                float relV = Mathf.Sqrt(relVx * relVx + relVy * relVy);

                float ax = -dragK * relV * relVx;
                float ay = -BallisticMath.StandardGravity - dragK * relV * relVy;

                vx += ax * dt;
                vy += ay * dt;
                x += vx * dt;
                y += vy * dt;
                t += dt;
            }

            float rangeYards = rangeMeters * BallisticMath.MetersToYards;

            r.dropMM = y * 1000f;
            r.dropCM = y * 100f;
            r.dropMOA = BallisticMath.InchesToMOA(Mathf.Abs(r.dropMM) * BallisticMath.MMToInches, rangeYards);
            if (y < 0f) r.dropMOA = -r.dropMOA;

            r.timeOfFlightS = t;
            r.velocityMS = Mathf.Sqrt(vx * vx + vy * vy);
            r.energyJoules = 0.5f * massKg * r.velocityMS * r.velocityMS;

            float lagTime = t - rangeMeters / (bullet.muzzleVelocityMS * cosAng);
            float driftM = crossWindMS * lagTime;
            r.windDriftMM = driftM * 1000f;
            r.windDriftMOA = BallisticMath.InchesToMOA(Mathf.Abs(r.windDriftMM) * BallisticMath.MMToInches, rangeYards);
            if (driftM < 0f) r.windDriftMOA = -r.windDriftMOA;

            float sg = BallisticMath.CalculateGreenhillSG(massKg, diamM, twistM);
            float spinDriftM = 0.0127f * (sg + 1.2f) * Mathf.Pow(t, 1.83f);
            r.spinDriftMM = spinDriftM * 1000f;
            r.spinDriftMOA = BallisticMath.InchesToMOA(r.spinDriftMM * BallisticMath.MMToInches, rangeYards);

            float latRad = environment.latitudeDeg * Mathf.Deg2Rad;
            const float omega = 7.2921e-5f;
            float coriolisM = omega * Mathf.Sin(latRad) * rangeMeters * t * 0.5f;
            r.coriolisMM = coriolisM * 1000f;
            r.coriolisMOA = BallisticMath.InchesToMOA(r.coriolisMM * BallisticMath.MMToInches, rangeYards);

            r.totalDriftMOA = r.windDriftMOA + r.spinDriftMOA + r.coriolisMOA;
            return r;
        }
    }
}
