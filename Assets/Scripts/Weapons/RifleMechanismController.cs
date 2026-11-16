using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace VRAtis.Weapons
{
    /// <summary>
    /// Animates the AR-15 bolt carrier cycle (including last-round bolt hold-open & bolt catch release),
    /// first-shot dust cover opening, pooled 5.56 brass casing ejection,
    /// and realistic magazine ejection/insertion with pooled dropped magazines.
    /// </summary>
    public class RifleMechanismController : MonoBehaviour
    {
        private enum MagAnimState
        {
            Idle,
            Ejecting,
            Inserting
        }

        [Header("Hareketli Silah Parçaları")]
        [SerializeField] private Transform boltCarrierTransform;
        [SerializeField] private Transform dustCoverTransform;
        [SerializeField] private Transform ejectionPoint;
        [SerializeField] private Transform magazineTransform;
        [SerializeField] private RifleAudioController audioController;

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

        [Header("Şarjör Çıkarma / Takma (30 Mermi Askeri AR-15)")]
        [SerializeField] private float magEjectSlideMeters = 0.14f;
        [SerializeField] private float magEjectDurationSec = 0.16f;
        [SerializeField] private float magInsertStartOffsetMeters = 0.24f;
        [SerializeField] private float magInsertDurationSec = 0.24f;
        [SerializeField] private Vector3 magSlideLocalAxis = new Vector3(0f, -1f, 0f);
        [SerializeField] private int droppedMagPoolSize = 4;

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
        private bool _boltLockedOpen;
        private bool _boltReleasingFromLock;
        private float _boltTimer;

        private Quaternion _dustCoverClosedLocalRot;
        private bool _dustCoverOpened;
        private bool _dustCoverAnimating;
        private float _dustCoverTimer;

        private Vector3 _magHomeLocalPos;
        private Quaternion _magHomeLocalRot;
        private MagAnimState _magAnimState = MagAnimState.Idle;
        private float _magTimer;
        private bool _dropPhysicalMagOnEject = true;

        private readonly Queue<EjectedCasing> _casingPool = new Queue<EjectedCasing>();
        private readonly Queue<DroppedMagazine> _droppedMagPool = new Queue<DroppedMagazine>();
        private Action<Vector3, float> _cachedCasingBounceHandler;

        private void Awake()
        {
            ResolveMagazineFallback();
            if (audioController == null)
                audioController = GetComponent<RifleAudioController>();
            _cachedCasingBounceHandler = HandleCasingGroundImpact;

            if (boltCarrierTransform != null)
                _boltHomeLocalPos = boltCarrierTransform.localPosition;

            if (dustCoverTransform != null)
                _dustCoverClosedLocalRot = dustCoverTransform.localRotation;

            if (magazineTransform != null)
            {
                _magHomeLocalPos = magazineTransform.localPosition;
                _magHomeLocalRot = magazineTransform.localRotation;
            }

            WarmCasingPool();
            WarmDroppedMagPool();
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            UpdateBoltCycle(dt);
            UpdateDustCoverOpen(dt);
            UpdateMagazineAnim(dt);
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
                _boltReleasingFromLock = false;
                _boltCycling = true;
                _boltTimer = 0f;
            }

            EjectCasing();
        }

        /// <summary>
        /// Locks the bolt carrier at its rearward open position after the final round in a 30-round magazine.
        /// </summary>
        public void LockBoltOpenAfterCycle()
        {
            _boltLockedOpen = true;
        }

        /// <summary>
        /// Releases the bolt catch after inserting a fresh magazine so the bolt slams forward cleanly.
        /// </summary>
        public void ReleaseBoltCatch()
        {
            if (!_boltLockedOpen || boltCarrierTransform == null)
                return;

            _boltLockedOpen = false;
            _boltCycling = false;
            _boltReleasingFromLock = true;
            _boltTimer = 0f;
        }

        public void EjectMagazine(bool dropPhysicalMag = true)
        {
            if (magazineTransform == null)
                return;

            _dropPhysicalMagOnEject = dropPhysicalMag;
            _magAnimState = MagAnimState.Ejecting;
            _magTimer = 0f;
            magazineTransform.gameObject.SetActive(true);
            magazineTransform.localPosition = _magHomeLocalPos;
            magazineTransform.localRotation = _magHomeLocalRot;
        }

        public void InsertMagazine()
        {
            if (magazineTransform == null)
                return;

            _magAnimState = MagAnimState.Inserting;
            _magTimer = 0f;
            magazineTransform.gameObject.SetActive(true);
            magazineTransform.localPosition = _magHomeLocalPos + magSlideLocalAxis.normalized * magInsertStartOffsetMeters;
            magazineTransform.localRotation = _magHomeLocalRot;
        }

        public void CloseDustCover()
        {
            _dustCoverOpened = false;
            _dustCoverAnimating = false;
            if (dustCoverTransform != null)
                dustCoverTransform.localRotation = _dustCoverClosedLocalRot;
        }

        private void ResolveMagazineFallback()
        {
            if (magazineTransform == null)
            {
                magazineTransform = FindChildByNames(transform, "reciver1_low.005_3_0", "reciver1_low.005", "AR15_Magazine");
                if (magazineTransform != null && magazineTransform.parent != null)
                {
                    magSlideLocalAxis = magazineTransform.parent.InverseTransformDirection(-transform.up).normalized;
                }
            }
        }

        private static Transform FindChildByNames(Transform root, params string[] names)
        {
            if (root == null) return null;
            for (int i = 0; i < names.Length; i++)
            {
                if (root.name == names[i]) return root;
            }
            for (int c = 0; c < root.childCount; c++)
            {
                Transform found = FindChildByNames(root.GetChild(c), names);
                if (found != null) return found;
            }
            return null;
        }

        private void UpdateBoltCycle(float dt)
        {
            if (boltCarrierTransform == null)
                return;

            if (_boltReleasingFromLock)
            {
                _boltTimer += dt;
                float t = Mathf.Clamp01(_boltTimer / Mathf.Max(0.001f, boltForwardDurationSec));
                float travelRatio = 1f - (t * t);
                boltCarrierTransform.localPosition = _boltHomeLocalPos + boltSlideLocalAxis.normalized * (boltTravelMeters * travelRatio);
                if (t >= 1f)
                {
                    boltCarrierTransform.localPosition = _boltHomeLocalPos;
                    _boltReleasingFromLock = false;
                }
                return;
            }

            if (!_boltCycling)
                return;

            _boltTimer += dt;
            if (_boltTimer <= boltBackDurationSec)
            {
                float t = _boltTimer / Mathf.Max(0.001f, boltBackDurationSec);
                float travelRatio = Mathf.Sin(t * Mathf.PI * 0.5f);
                boltCarrierTransform.localPosition = _boltHomeLocalPos + boltSlideLocalAxis.normalized * (boltTravelMeters * travelRatio);
                return;
            }

            if (_boltLockedOpen)
            {
                // Hold bolt rearward on empty magazine (Last Round Bolt Hold-Open)
                boltCarrierTransform.localPosition = _boltHomeLocalPos + boltSlideLocalAxis.normalized * boltTravelMeters;
                _boltCycling = false;
                return;
            }

            float totalDuration = boltBackDurationSec + boltForwardDurationSec;
            if (_boltTimer >= totalDuration)
            {
                boltCarrierTransform.localPosition = _boltHomeLocalPos;
                _boltCycling = false;
                return;
            }

            float fwdT = (_boltTimer - boltBackDurationSec) / Mathf.Max(0.001f, boltForwardDurationSec);
            float fwdRatio = 1f - (fwdT * fwdT);
            boltCarrierTransform.localPosition = _boltHomeLocalPos + boltSlideLocalAxis.normalized * (boltTravelMeters * fwdRatio);
        }

        private void UpdateDustCoverOpen(float dt)
        {
            if (!_dustCoverAnimating || dustCoverTransform == null)
                return;

            _dustCoverTimer += dt;
            float t = Mathf.Clamp01(_dustCoverTimer / Mathf.Max(0.001f, dustCoverOpenDurationSec));
            float eased = 1f - (1f - t) * (1f - t);
            dustCoverTransform.localRotation = _dustCoverClosedLocalRot * Quaternion.AngleAxis(dustCoverOpenAngleDeg * eased, dustCoverHingeAxis.normalized);

            if (t >= 1f)
                _dustCoverAnimating = false;
        }

        private void UpdateMagazineAnim(float dt)
        {
            if (_magAnimState == MagAnimState.Idle || magazineTransform == null)
                return;

            _magTimer += dt;
            Vector3 slideAxis = magSlideLocalAxis.sqrMagnitude > 1e-4f ? magSlideLocalAxis.normalized : Vector3.down;

            if (_magAnimState == MagAnimState.Ejecting)
            {
                float t = Mathf.Clamp01(_magTimer / Mathf.Max(0.001f, magEjectDurationSec));
                float accel = t * t;
                magazineTransform.localPosition = _magHomeLocalPos + slideAxis * (magEjectSlideMeters * accel);

                if (t >= 1f)
                {
                    if (_dropPhysicalMagOnEject)
                        SpawnDroppedMagazine();

                    magazineTransform.gameObject.SetActive(false);
                    _magAnimState = MagAnimState.Idle;
                }
            }
            else if (_magAnimState == MagAnimState.Inserting)
            {
                float t = Mathf.Clamp01(_magTimer / Mathf.Max(0.001f, magInsertDurationSec));
                // Smooth insertion with a crisp tactile seat at end
                float easeOut = 1f - Mathf.Pow(1f - t, 2.2f);
                float dist = Mathf.Lerp(magInsertStartOffsetMeters, 0f, easeOut);
                magazineTransform.localPosition = _magHomeLocalPos + slideAxis * dist;

                if (t >= 1f)
                {
                    magazineTransform.localPosition = _magHomeLocalPos;
                    magazineTransform.localRotation = _magHomeLocalRot;
                    _magAnimState = MagAnimState.Idle;
                }
            }
        }

        private void SpawnDroppedMagazine()
        {
            if (magazineTransform == null)
                return;

            DroppedMagazine dropped = _droppedMagPool.Count > 0
                ? _droppedMagPool.Dequeue()
                : DroppedMagazine.CreateFromSource(magazineTransform);

            if (dropped == null)
                return;

            Vector3 dropVel = (-transform.up * 1.15f) + (transform.right * Random.Range(-0.18f, 0.22f)) + (transform.forward * Random.Range(-0.12f, 0.12f));
            Vector3 angularVel = new Vector3(
                Random.Range(-70f, 70f),
                Random.Range(-45f, 45f),
                Random.Range(95f, 165f));

            dropped.LaunchFromTransform(magazineTransform, dropVel, angularVel, ReturnDroppedMagToPool);
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

            casing.Launch(ejectionPoint.position, ejectionPoint.rotation, worldVel, angularVel, ReturnCasingToPool, _cachedCasingBounceHandler);
        }

        private void HandleCasingGroundImpact(Vector3 worldPos, float impactStrength)
        {
            if (audioController != null)
                audioController.PlayCasingBounce(worldPos, impactStrength);
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

        private void WarmDroppedMagPool()
        {
            if (magazineTransform == null)
                return;

            for (int i = 0; i < droppedMagPoolSize; i++)
            {
                DroppedMagazine m = DroppedMagazine.CreateFromSource(magazineTransform);
                if (m == null) break;
                _droppedMagPool.Enqueue(m);
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

        private void ReturnDroppedMagToPool(DroppedMagazine mag)
        {
            if (mag != null)
                _droppedMagPool.Enqueue(mag);
        }
    }
}
