using UnityEngine;
using VRAtis.Ballistics;

namespace VRAtis.Targets
{
    public enum SoldierHitZone
    {
        Head = 0,
        Torso = 1,
        Limb = 2
    }

    /// <summary>
    /// Attached to animated bone collider GameObjects on the KSK enemy soldier so BallisticBullet
    /// hits accurately distinguish Headshot, Torso, and Limb impacts.
    /// Lives in its own file so Unity serializes its MonoScript cleanly inside prefabs.
    /// </summary>
    public class EnemySoldierHitbox : MonoBehaviour, IBulletImpactTarget
    {
        [SerializeField] private EnemySoldierTarget soldierTarget;
        [SerializeField] private SoldierHitZone hitZone = SoldierHitZone.Torso;

        public SoldierHitZone HitZone => hitZone;

        public void Configure(EnemySoldierTarget target, SoldierHitZone zone)
        {
            soldierTarget = target;
            hitZone = zone;
        }

        public void OnBulletImpact(in BulletImpactInfo impact)
        {
            if (soldierTarget == null)
                soldierTarget = GetComponentInParent<EnemySoldierTarget>();
            if (soldierTarget == null)
                return;

            soldierTarget.HandleBulletImpact(hitZone, in impact);
        }
    }
}
