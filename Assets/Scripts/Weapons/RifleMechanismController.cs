using System.Collections.Generic;
using UnityEngine;

namespace VRAtis.Weapons
{
    /// <summary>
    /// Animates the AR-15 bolt carrier cycle and first-shot dust cover opening,
    /// and ejects pooled 5.56 brass casings from the ejection port on every shot.
    /// </summary>
    public class RifleMechanismController : MonoBehaviour
    {
        [Header("Hareketli Silah Parçaları")]
        [SerializeField] private Transform boltCarrierTransform;
        [SerializeField] private Transform dustCoverTransform;
        [SerializeField] private Transform ejectionPoint;

        [Header("Sürgü (Bolt) Hareketi")]
        [Tooltip("Ateş edildiğinde sürgünün geri gideceği mesafe (metre).")]
        [SerializeField] private float boltTravelMeters = 0.052f;
        [Tooltip("Sürgünün geriye gidiş süresi (sn).")]
        [SerializeField] private float boltBackDurationSec = 0.022f;
        [Tooltip("Sürgünün ileriye dönüş süresi (sn).")]
        [SerializeField] private float boltForwardDurationSec = 0.055f;
        [SerializeField] private Vector3 boltSlideLocalAxis = new Vector3(0f, 0f, -1f);

        [Header("Toz Kapağı (İlk Atışta Açılır ve Açık Kalır)")]
        [SerializeField] private float dustCoverOpenAngleDeg = 115f;
        [SerializeField] private float dustCoverOpenDurationSec = 0.045f;
        [SerializeField] private Vector3 dustCoverHingeAxis = new Vector3(0f, 0f, 1f);

        [Header("Boş Kovan Fırlatma (Object Pool)")]
        [SerializeField] private EjectedCasing casingPrefab;
        [SerializeField] private int casingPoolSize = 16;
        [Tooltip("Ejeksiyon portuna göre yerel fırlatma yönü (+X sağ, +Y yukarı, -Z hafif geri).")]
        [SerializeField] private Vector3 localEjectDirection = new Vector3(1.0f, 0.52f, -0.35f);
        [SerializeField] private float ejectSpeedMin = 2.6f;
        [SerializeField] private float ejectSpeedMax = 3.5f;
        [SerializeField] private float spinSpeedDegPerSec = 720f;

        private Vector3 _boltHomeLocalPos;
        private bool _boltCycling;
        private float _boltTimer;

        private Quaternion _dustCoverClosedLocalRot;
        private bool _dustCoverOpened;
        private bool _dustCoverAnimating;
        private float _dustCoverTimer;

        private readonly Queue<EjectedCasing> _casingPool = new Queue<EjectedCasing>();

        private void Awake()
        {
            if (boltCarrierTransform != null)
                _boltHomeLocalPos = boltCarrierTransform.localPosition;

            if (dustCoverTransform != null)
                _dustCoverClosedLocalRot = dustCoverTransform.localRotation;

            WarmCasingPool();
        }

        private void Update()
        {
            UpdateBoltCycle(Time.deltaTime);
            UpdateDustCoverOpen(Time.deltaTime);
        }

        /// <summary>
        /// Call from RifleWeapon.onWeaponFired UnityEvent.
        /// </summary>
        public void OnWeaponFired()
        {
            // Dust cover flips open once on the first shot and stays open until manually closed
            if (!_dustCoverOpened && dustCoverTransform != null)
            {
                _dustCoverOpened = true;
                _dustCoverAnimating = true;
                _dustCoverTimer = 0f;
            }

            // Bolt carrier cycles on every shot (restarts cleanly on rapid fire)
            if (boltCarrierTransform != null)
            {
                _boltCycling = true;
                _boltTimer = 0f;
            }

            EjectCasing();
        }

        public void CloseDustCover()
        {
            _dustCoverOpened = false;
            _dustCoverAnimating = false;
            if (dustCoverTransform != null)
                dustCoverTransform.localRotation = _dustCoverClosedLocalRot;
        }

        private void UpdateBoltCycle(float dt)
        {
            if (!_boltCycling || boltCarrierTransform == null)
                return;

            _boltTimer += dt;
            float totalDuration = boltBackDurationSec + boltForwardDurationSec;

            if (_boltTimer >= totalDuration)
            {
                boltCarrierTransform.localPosition = _boltHomeLocalPos;
                _boltCycling = false;
                return;
            }

            float travelRatio;
            if (_boltTimer <= boltBackDurationSec)
            {
                float t = _boltTimer / Mathf.Max(0.001f, boltBackDurationSec);
                travelRatio = Mathf.Sin(t * Mathf.PI * 0.5f);
            }
            else
            {
                float t = (_boltTimer - boltBackDurationSec) / Mathf.Max(0.001f, boltForwardDurationSec);
                travelRatio = 1f - (t * t);
            }

            boltCarrierTransform.localPosition = _boltHomeLocalPos + boltSlideLocalAxis.normalized * (boltTravelMeters * travelRatio);
        }

        private void UpdateDustCoverOpen(float dt)
        {
            if (!_dustCoverAnimating || dustCoverTransform == null)
                return;

            _dustCoverTimer += dt;
            float t = Mathf.Clamp01(_dustCoverTimer / Mathf.Max(0.001f, dustCoverOpenDurationSec));
            // Slight ease-out snap
            float eased = 1f - (1f - t) * (1f - t);
            dustCoverTransform.localRotation = _dustCoverClosedLocalRot * Quaternion.AngleAxis(dustCoverOpenAngleDeg * eased, dustCoverHingeAxis.normalized);

            if (t >= 1f)
                _dustCoverAnimating = false;
        }

        private void EjectCasing()
        {
            if (ejectionPoint == null || casingPrefab == null)
                return;

            EjectedCasing casing = _casingPool.Count > 0 ? _casingPool.Dequeue() : CreateCasingInstance();
            if (casing == null)
                return;

            Vector3 jitterDir = localEjectDirection + new Vector3(
                Random.Range(-0.12f, 0.12f),
                Random.Range(-0.10f, 0.14f),
                Random.Range(-0.12f, 0.12f));

            Vector3 worldVel = ejectionPoint.TransformDirection(jitterDir.normalized) * Random.Range(ejectSpeedMin, ejectSpeedMax);
            Vector3 angularVel = new Vector3(
                Random.Range(-spinSpeedDegPerSec, spinSpeedDegPerSec),
                Random.Range(spinSpeedDegPerSec * 0.5f, spinSpeedDegPerSec),
                Random.Range(-spinSpeedDegPerSec * 0.6f, spinSpeedDegPerSec * 0.6f));

            casing.Launch(ejectionPoint.position, ejectionPoint.rotation, worldVel, angularVel, ReturnCasingToPool);
        }

        private void WarmCasingPool()
        {
            if (casingPrefab == null)
                return;

            for (int i = 0; i < casingPoolSize; i++)
            {
                EjectedCasing c = CreateCasingInstance();
                c.gameObject.SetActive(false);
                _casingPool.Enqueue(c);
            }
        }

        private EjectedCasing CreateCasingInstance()
        {
            EjectedCasing instance = Instantiate(casingPrefab);
            instance.name = "PooledBrassCasing556";
            return instance;
        }

        private void ReturnCasingToPool(EjectedCasing casing)
        {
            if (casing != null)
                _casingPool.Enqueue(casing);
        }
    }
}
