using UnityEngine;
using VRAtis.Ballistics;

namespace VRAtis.Targets
{
    /// <summary>
    /// Attached to AR500_GongPlate or Stand_Frame colliders so BallisticBullet impact events
    /// route cleanly to either gong pendulum swing + ring or frame ricochet clank.
    /// Must live in its own script file so Unity serializes its MonoScript reference into prefabs.
    /// </summary>
    public class SteelGongHitReceiver : MonoBehaviour, IBulletImpactTarget
    {
        [SerializeField] private SteelGongTarget gongTarget;
        [SerializeField] private bool isStandFrame;

        public void Configure(SteelGongTarget target, bool standFrame)
        {
            gongTarget = target;
            isStandFrame = standFrame;
        }

        public void OnBulletImpact(in BulletImpactInfo impact)
        {
            if (gongTarget == null)
                gongTarget = GetComponentInParent<SteelGongTarget>();
            if (gongTarget == null)
                return;

            if (isStandFrame)
                gongTarget.HandleFrameImpact(in impact);
            else
                gongTarget.HandlePlateImpact(in impact);
        }
    }
}
