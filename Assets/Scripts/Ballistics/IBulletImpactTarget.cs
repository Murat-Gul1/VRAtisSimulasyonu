using UnityEngine;

namespace VRAtis.Ballistics
{
    public struct BulletImpactInfo
    {
        public Vector3 HitPoint;
        public Vector3 HitNormal;
        public Vector3 ImpactVelocity;
        public float KineticEnergyJoules;
        public float BulletMassKg;
        public float DistanceTraveledMeters;
        public float FlightTimeSeconds;
        public float ImpactAngleDeg;
    }

    /// <summary>
    /// Implement on targets (steel plates, dummies, reactive targets) to receive real terminal ballistic impact data.
    /// </summary>
    public interface IBulletImpactTarget
    {
        void OnBulletImpact(in BulletImpactInfo impact);
    }
}
