using UnityEngine;

namespace VRAtis.Ballistics
{
    public enum DragModel
    {
        G1,
        G7
    }

    public enum AmmoPreset
    {
        Nato762_M118LR_175gr,
        Nato556_M855_62gr,
        Custom
    }

    public struct AmmoPresetSpec
    {
        public string Name;
        public float BulletWeightGrain;
        public float CaliberInch;
        public float MuzzleVelocityMS;
        public DragModel DragModel;
        public float FormFactor;
        public float TwistRateInch;
    }

    /// <summary>
    /// Stateless ballistic math, atmospheric physics, drag tables (G1 & G7),
    /// unit conversions (MOA / MRAD / metric), and RK4 bore-zeroing solver.
    /// </summary>
    public static class BallisticMath
    {
        public const float StandardGravity = 9.80665f;      // m/s^2
        public const float EarthAngularSpeedRadS = 7.2921159e-5f; // rad/s
        public const float GasConstantDryAir = 287.058f;    // J/(kg*K)
        public const float GasConstantWaterVapor = 461.495f;// J/(kg*K)
        public const float GrainToKg = 0.00006479891f;
        public const float InchToMeters = 0.0254f;
        public const float MetersToYards = 1.0936133f;
        public const float MMToInches = 0.03937008f;

        public static AmmoPresetSpec GetAmmoPresetSpec(AmmoPreset preset)
        {
            if (preset == AmmoPreset.Nato556_M855_62gr)
            {
                return new AmmoPresetSpec
                {
                    Name = "5.56x45 NATO M855 62gr",
                    BulletWeightGrain = 62f,
                    CaliberInch = 0.224f,
                    MuzzleVelocityMS = 940f,
                    DragModel = DragModel.G7,
                    FormFactor = 1.169f, // G7 BC ~ 0.151 (SD 0.1765 / 0.151)
                    TwistRateInch = 7f
                };
            }

            return new AmmoPresetSpec
            {
                Name = "7.62x51 M118LR 175gr",
                BulletWeightGrain = 175f,
                CaliberInch = 0.308f,
                MuzzleVelocityMS = 792.5f,
                DragModel = DragModel.G7,
                FormFactor = 1.085f, // G7 BC ~ 0.243 (SD 0.2635 / 0.243)
                TwistRateInch = 12f
            };
        }

        /// <summary>
        /// Axial moment of inertia Ix for a spitzer boat-tail rifle projectile (~0.42 * m * r^2).
        /// </summary>
        public static float ComputeAxialMomentOfInertia(float massKg, float diameterM)
        {
            float r = diameterM * 0.5f;
            return Mathf.Max(1e-10f, 0.42f * massKg * r * r);
        }

        /// <summary>
        /// Converts sectional density and standard imperial ballistic coefficient (lb/in^2)
        /// into dimensionless form factor i = SD / BC.
        /// </summary>
        public static float FormFactorFromImperialBC(float bulletWeightGrain, float caliberInch, float ballisticCoefficient)
        {
            float sd = (bulletWeightGrain / 7000f) / Mathf.Max(1e-5f, caliberInch * caliberInch);
            return sd / Mathf.Max(0.02f, ballisticCoefficient);
        }

        private static readonly (float mach, float cd)[] G1Table =
        {
            (0.00f, 0.228f),
            (0.50f, 0.228f),
            (0.70f, 0.258f),
            (0.85f, 0.303f),
            (0.90f, 0.343f),
            (0.95f, 0.463f),
            (1.00f, 0.623f),
            (1.05f, 0.703f),
            (1.10f, 0.743f),
            (1.20f, 0.703f),
            (1.50f, 0.613f),
            (2.00f, 0.513f),
            (3.00f, 0.413f)
        };

        private static readonly (float mach, float cd)[] G7Table =
        {
            (0.00f, 0.1198f),
            (0.50f, 0.1197f),
            (0.70f, 0.1264f),
            (0.85f, 0.1513f),
            (0.90f, 0.1864f),
            (0.95f, 0.2817f),
            (1.00f, 0.3765f),
            (1.05f, 0.4042f),
            (1.10f, 0.4034f),
            (1.20f, 0.3887f),
            (1.50f, 0.3404f),
            (2.00f, 0.2780f),
            (2.50f, 0.2330f),
            (3.00f, 0.1991f),
            (4.00f, 0.1550f)
        };

        public static float GetCd(DragModel model, float mach)
        {
            return model == DragModel.G7 ? GetG7Cd(mach) : GetG1Cd(mach);
        }

        public static float GetG1Cd(float mach) => LookupDragTable(G1Table, mach);

        public static float GetG7Cd(float mach) => LookupDragTable(G7Table, mach);

        private static float LookupDragTable((float mach, float cd)[] table, float mach)
        {
            if (mach <= 0f)
                return 0f;

            for (int i = 1; i < table.Length; i++)
            {
                if (mach < table[i].mach)
                {
                    float m0 = table[i - 1].mach;
                    float m1 = table[i].mach;
                    float t = (mach - m0) / (m1 - m0);
                    return Mathf.Lerp(table[i - 1].cd, table[i].cd, t);
                }
            }

            return table[table.Length - 1].cd;
        }

        /// <summary>
        /// Moist air density (kg/m^3) using ISA altitude pressure and Magnus vapor pressure formula.
        /// </summary>
        public static float CalculateAirDensity(float temperatureC, float pressureHPa, float humidityPercent, float altitudeMeters)
        {
            float tempK = Mathf.Max(150f, temperatureC + 273.15f);
            float altFactor = Mathf.Max(0.05f, 1f - 0.0000226f * altitudeMeters);
            float altPressPa = pressureHPa * 100f * Mathf.Pow(altFactor, 5.256f);

            float eSat = 611.2f * Mathf.Exp(17.67f * temperatureC / (temperatureC + 243.5f));
            float ePart = Mathf.Clamp01(humidityPercent * 0.01f) * eSat;
            float pDry = altPressPa - ePart;

            float rho = (pDry / (GasConstantDryAir * tempK)) + (ePart / (GasConstantWaterVapor * tempK));
            return Mathf.Clamp(rho, 0.5f, 1.4f);
        }

        public static float CalculateSpeedOfSound(float temperatureC)
        {
            float tempK = Mathf.Max(150f, temperatureC + 273.15f);
            return Mathf.Sqrt(1.4f * GasConstantDryAir * tempK);
        }

        public static float CalculateGreenhillSG(float massKg, float diamM, float twistM)
        {
            const float rhoCore = 11340f; // kg/m^3 lead-core approx
            float bulletL = massKg / (rhoCore * Mathf.PI * 0.25f * diamM * diamM);
            float lCal = bulletL / diamM;
            float twistCal = twistM / diamM;
            float diamMM = diamM * 1000f;

            float sg = (30f * massKg * 1000f) /
                       (twistCal * twistCal * diamMM * diamMM * diamMM * lCal * (1f + lCal * lCal));
            return Mathf.Max(sg, 1.0f);
        }

        /// <summary>
        /// Computes the upward bore elevation angle (radians) required to zero at <paramref name="zeroDistanceMeters"/>
        /// using 4th-order Runge-Kutta integration and iterative secant refinement.
        /// </summary>
        public static float SolveZeroingBoreAngleRad(
            float muzzleVelocityMS,
            float bulletMassKg,
            float referenceAreaM2,
            DragModel dragModel,
            float formFactor,
            float sightHeightMeters,
            float zeroDistanceMeters,
            float airDensity,
            float speedOfSound,
            float gravity)
        {
            if (zeroDistanceMeters <= 1f || muzzleVelocityMS <= 10f)
                return 0f;

            float safeFormFactor = Mathf.Max(0.05f, formFactor);
            float dragScale = (0.5f * airDensity * referenceAreaM2 * safeFormFactor) / Mathf.Max(1e-6f, bulletMassKg);

            Vector2 CalcAccel(Vector2 vel)
            {
                float speed = vel.magnitude;
                if (speed < 0.01f)
                    return new Vector2(0f, -gravity);

                float mach = speed / Mathf.Max(100f, speedOfSound);
                float cd = GetCd(dragModel, mach);
                float dragDecel = dragScale * cd * speed;
                return new Vector2(-dragDecel * vel.x, -dragDecel * vel.y - gravity);
            }

            float SimulateHeightAtZero(float angleRad)
            {
                Vector2 pos = Vector2.zero;
                Vector2 vel = new Vector2(Mathf.Cos(angleRad), Mathf.Sin(angleRad)) * muzzleVelocityMS;
                const float dt = 0.005f;
                float t = 0f;

                while (pos.x < zeroDistanceMeters && t < 5f && vel.x > 5f)
                {
                    Vector2 k1v = CalcAccel(vel);
                    Vector2 v1 = vel + k1v * (dt * 0.5f);
                    Vector2 k2v = CalcAccel(v1);
                    Vector2 v2 = vel + k2v * (dt * 0.5f);
                    Vector2 k3v = CalcAccel(v2);
                    Vector2 v3 = vel + k3v * dt;
                    Vector2 k4v = CalcAccel(v3);

                    Vector2 nextPos = pos + ((vel + 2f * v1 + 2f * v2 + v3) / 6f) * dt;
                    Vector2 nextVel = vel + ((k1v + 2f * k2v + 2f * k3v + k4v) / 6f) * dt;
                    t += dt;

                    if (nextPos.x >= zeroDistanceMeters)
                    {
                        float frac = (zeroDistanceMeters - pos.x) / Mathf.Max(1e-5f, nextPos.x - pos.x);
                        return Mathf.Lerp(pos.y, nextPos.y, frac);
                    }

                    pos = nextPos;
                    vel = nextVel;
                }

                return pos.y;
            }

            float approxTime = zeroDistanceMeters / muzzleVelocityMS;
            float angle = Mathf.Atan((sightHeightMeters + 0.5f * gravity * approxTime * approxTime) / zeroDistanceMeters);

            for (int iter = 0; iter < 4; iter++)
            {
                float yAtZero = SimulateHeightAtZero(angle);
                float errorM = sightHeightMeters - yAtZero;
                if (Mathf.Abs(errorM) < 0.00005f)
                    break;

                angle += Mathf.Atan2(errorM, zeroDistanceMeters);
            }

            return angle;
        }

        public static float InchesToMOA(float inches, float rangeYards)
        {
            if (rangeYards <= 0f) return 0f;
            return inches / (rangeYards * 1.04720f / 100f);
        }

        public static float MOAToMM(float moa, float rangeMeters) => moa * rangeMeters * 2.908f / 100f;

        public static float MOAToCM(float moa, float rangeMeters) => MOAToMM(moa, rangeMeters) * 0.1f;

        public static float MOAToRadians(float moa) => moa * (Mathf.PI / (180f * 60f));

        public static float MRADToRadians(float mrad) => mrad * 0.001f;

        public static float RangeYards(float targetSizeInches, float measuredMOA)
        {
            if (measuredMOA <= 0f) return 0f;
            return (targetSizeInches / measuredMOA) * 95.5f;
        }

        public static float RangeMeters(float targetSizeCM, float measuredMOA)
        {
            if (measuredMOA <= 0f) return 0f;
            return (targetSizeCM / measuredMOA) * 27.77f;
        }

        public static float TargetMOA(float targetSizeCM, float rangeMeters)
        {
            if (rangeMeters <= 0f) return 0f;
            return targetSizeCM / (rangeMeters * 0.03600f);
        }
    }
}
