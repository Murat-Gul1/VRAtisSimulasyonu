using UnityEngine;

namespace VRAtis.Ballistics
{
    public enum DragModel
    {
        G1,
        G7
    }

    /// <summary>
    /// Stateless ballistic math, atmospheric physics, drag tables (G1 & G7),
    /// unit conversions (MOA / MRAD / metric), and bore-zeroing solver.
    /// </summary>
    public static class BallisticMath
    {
        public const float StandardGravity = 9.80665f;      // m/s^2
        public const float GasConstantDryAir = 287.058f;    // J/(kg*K)
        public const float GasConstantWaterVapor = 461.495f;// J/(kg*K)
        public const float GrainToKg = 0.00006479891f;
        public const float InchToMeters = 0.0254f;
        public const float MetersToYards = 1.0936133f;
        public const float MMToInches = 0.03937008f;

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
        /// accounting for sight height above bore, gravity, and aerodynamic drag.
        /// </summary>
        public static float SolveZeroingBoreAngleRad(
            float muzzleVelocityMS,
            float bulletMassKg,
            float referenceAreaM2,
            DragModel dragModel,
            float formFactorOrInverseBC,
            float sightHeightMeters,
            float zeroDistanceMeters,
            float airDensity,
            float speedOfSound,
            float gravity)
        {
            if (zeroDistanceMeters <= 1f || muzzleVelocityMS <= 10f)
                return 0f;

            float simTime = 0f;
            float simX = 0f;
            float simZ = 0f;
            float simVx = muzzleVelocityMS;
            float simVz = 0f;
            const float dt = 0.001f;

            while (simX < zeroDistanceMeters && simTime < 5f)
            {
                float speed = Mathf.Sqrt(simVx * simVx + simVz * simVz);
                if (speed < 10f) break;

                float mach = speed / speedOfSound;
                float cd = GetCd(dragModel, mach) * formFactorOrInverseBC;
                float dragAccelMag = (0.5f * airDensity * referenceAreaM2 * cd) / bulletMassKg * (speed * speed);

                float dragAx = -dragAccelMag * (simVx / speed);
                float dragAz = -dragAccelMag * (simVz / speed);

                simVx += dragAx * dt;
                simVz += (dragAz - gravity) * dt;
                simX += simVx * dt;
                simZ += simVz * dt;
                simTime += dt;
            }

            float totalVerticalComp = sightHeightMeters + Mathf.Abs(simZ);
            return Mathf.Atan(totalVerticalComp / zeroDistanceMeters);
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
