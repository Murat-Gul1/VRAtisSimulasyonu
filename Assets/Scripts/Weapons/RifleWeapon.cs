using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using VRAtis.Ballistics;
using VRAtis.Hardware;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace VRAtis.Weapons
{
    /// <summary>
    /// Bridges ESP32 trigger shots (and optional editor test input) to pooled BallisticBullet firing
    /// and spring-damper recoil on a visual child pivot.
    /// </summary>
    public class RifleWeapon : MonoBehaviour
    {
        private enum ReloadPhase
        {
            None,
            EjectWait,
            Inserting,
            BoltReleaseWait
        }

        [Header("Referanslar")]
        [SerializeField] private Esp32RifleReceiver esp32Receiver;
        [Tooltip("Merminin çıkacağı namlu ucu Transform'u.")]
        [SerializeField] private Transform muzzleTransform;
        [Tooltip("Nişangâh yüksekliği (sightHeightCM) ölçümü için Red Dot merkez noktası (boşsa RedDotAnchor otomatik aranır).")]
        [SerializeField] private Transform sightReferenceTransform;
        [Tooltip("BallisticBullet bileşeni taşıyan mermi prefab'ı (boşsa runtime'da minimal obje üretilir).")]
        [SerializeField] private BallisticBullet bulletPrefab;
        [SerializeField] private RifleMechanismController mechanismController;

        [Header("Balistik Profili")]
        [Tooltip("Pooled mermilerde kullanılacak mühimmat tipi (7.62 M118LR veya AR-15 5.56 M855).")]
        [SerializeField] private AmmoPreset ammoPreset = AmmoPreset.Nato762_M118LR_175gr;

        [Header("Şarjör ve Mermi (Askeri Standart 30 Mermi - Ekransız)")]
        [SerializeField] private int magazineCapacity = 30;
        [SerializeField] private bool startWithFullMagazine = true;
        [Tooltip("Şarjör boşaldığında otomatik olarak boş şarjörü atıp yenisini takar (gelecekte donanımsal şarjör sensörü gelince kapatılabilir).")]
        [SerializeField] private bool autoReloadOnEmpty = true;
        [Range(0.05f, 1.5f)]
        [SerializeField] private float autoReloadDelaySec = 0.35f;
        [SerializeField] private float ejectToSwapDelaySec = 0.52f;
        [SerializeField] private float insertSeatDurationSec = 0.26f;
        [SerializeField] private float boltReleaseDelaySec = 0.14f;

        [Header("Geri Tepme (Görsel Child Pivot)")]
        [Tooltip("ESP32 ana rotasyonunu bozmamak için silah modelini taşıyan alt Transform.")]
        [SerializeField] private Transform recoilPivot;
        [SerializeField] private float recoilKickBackMeters = 0.035f;
        [SerializeField] private float recoilPitchUpDeg = 2.2f;
        [SerializeField] private float recoilRecoverySpeed = 14f;

        [Header("Ateşleme Ayarları")]
        [SerializeField] private int initialPoolSize = 16;
        [SerializeField] private bool allowEditorTestFire = true;

        [Header("Olaylar (Efekt, Mekanizma ve Ses Kancaları)")]
        public UnityEvent onWeaponFired;
        public UnityEvent onDryFire;
        public UnityEvent onMagazineEjected;
        public UnityEvent onMagazineInserted;
        public UnityEvent onBoltReleased;

        public int CurrentAmmo { get; private set; }
        public int MagazineCapacity => magazineCapacity;
        public bool IsMagazineInserted { get; private set; }
        public bool IsBoltLockedOpen { get; private set; }
        public bool IsReloading => _reloadPhase != ReloadPhase.None;

        private readonly Queue<BallisticBullet> _bulletPool = new Queue<BallisticBullet>();
        private static Material _sharedTracerMaterial;
        private Vector3 _recoilPosOffset;
        private float _recoilPitchOffset;
        private float _recoilPosVel;
        private float _recoilPitchVel;

        private ReloadPhase _reloadPhase = ReloadPhase.None;
        private float _reloadTimer;
        private bool _pendingAutoReload;
        private float _autoReloadTimer;
        private float? _measuredSightHeightCm;

        private void Awake()
        {
            if (esp32Receiver == null)
                esp32Receiver = FindAnyObjectByType<Esp32RifleReceiver>();

            if (mechanismController == null)
                mechanismController = GetComponentInChildren<RifleMechanismController>();

            if (muzzleTransform == null)
                muzzleTransform = transform;

            if (sightReferenceTransform == null)
                sightReferenceTransform = FindDeepChild(transform, "RedDotAnchor");

            if (sightReferenceTransform != null && muzzleTransform != null)
            {
                float heightCm = Vector3.Dot(sightReferenceTransform.position - muzzleTransform.position, muzzleTransform.up) * 100f;
                if (heightCm > 0.5f && heightCm < 25f)
                    _measuredSightHeightCm = heightCm;
            }

            IsMagazineInserted = startWithFullMagazine;
            CurrentAmmo = startWithFullMagazine ? magazineCapacity : 0;
            IsBoltLockedOpen = false;

            WarmPool();
        }

        private static Transform FindDeepChild(Transform parent, string targetName)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.name == targetName)
                    return child;
                Transform nested = FindDeepChild(child, targetName);
                if (nested != null)
                    return nested;
            }
            return null;
        }

        private void OnEnable()
        {
            if (esp32Receiver != null)
                esp32Receiver.OnShotFired += HandleEspShot;
        }

        private void OnDisable()
        {
            if (esp32Receiver != null)
                esp32Receiver.OnShotFired -= HandleEspShot;
        }

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null)
            {
                if (Keyboard.current.gKey.wasPressedThisFrame)
                    Reload();
                else if (Keyboard.current.vKey.wasPressedThisFrame)
                    ToggleMagazine();

                if (allowEditorTestFire && Application.isEditor)
                {
                    bool pointerOverUi = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
                    bool keyFire = Keyboard.current.spaceKey.wasPressedThisFrame;
                    bool mouseFire = !pointerOverUi && Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;
                    if (keyFire || mouseFire)
                        Fire();
                }
            }
            else if (allowEditorTestFire && Application.isEditor)
            {
                bool pointerOverUi = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
                if (!pointerOverUi && Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
                    Fire();
            }
#endif
            UpdateReloadState(Time.deltaTime);
            UpdateRecoilSpring();
        }

        private void HandleEspShot(uint shotNumber)
        {
            Fire();
        }

        public void Fire()
        {
            if (IsReloading || !IsMagazineInserted || CurrentAmmo <= 0)
            {
                onDryFire?.Invoke();
                return;
            }

            CurrentAmmo--;

            BallisticBullet bullet = AcquireBullet();
            bullet.Launch(
                muzzleTransform.position,
                muzzleTransform.rotation,
                0f,
                0f,
                ReturnBulletToPool,
                _measuredSightHeightCm,
                transform.root,
                bulletPrefab == null ? ammoPreset : (AmmoPreset?)null);

            // Apply spring impulse to child recoil pivot
            _recoilPosOffset.z -= recoilKickBackMeters;
            _recoilPitchOffset -= recoilPitchUpDeg;

            if (CurrentAmmo == 0)
            {
                IsBoltLockedOpen = true;
                if (mechanismController != null)
                    mechanismController.LockBoltOpenAfterCycle();

                if (autoReloadOnEmpty)
                {
                    _pendingAutoReload = true;
                    _autoReloadTimer = autoReloadDelaySec;
                }
            }

            onWeaponFired?.Invoke();
        }

        public void Reload()
        {
            if (IsReloading)
                return;

            _pendingAutoReload = false;

            if (IsMagazineInserted)
            {
                EjectMagazine();
                _reloadPhase = ReloadPhase.EjectWait;
                _reloadTimer = ejectToSwapDelaySec;
            }
            else
            {
                InsertFreshMagazine();
            }
        }

        public void ToggleMagazine()
        {
            if (IsReloading)
                return;

            _pendingAutoReload = false;

            if (IsMagazineInserted)
                EjectMagazine();
            else
                InsertFreshMagazine();
        }

        public void EjectMagazine()
        {
            if (!IsMagazineInserted || IsReloading)
                return;

            _pendingAutoReload = false;
            IsMagazineInserted = false;
            CurrentAmmo = 0;
            if (mechanismController != null)
                mechanismController.EjectMagazine(true);
            onMagazineEjected?.Invoke();
        }

        public void InsertFreshMagazine()
        {
            if (IsMagazineInserted || IsReloading)
                return;

            _pendingAutoReload = false;
            BeginInsertFreshMagazine();
        }

        private void BeginInsertFreshMagazine()
        {
            IsMagazineInserted = true;
            CurrentAmmo = magazineCapacity;
            if (mechanismController != null)
                mechanismController.InsertMagazine();
            onMagazineInserted?.Invoke();

            _reloadPhase = ReloadPhase.Inserting;
            _reloadTimer = insertSeatDurationSec;
        }

        private void UpdateReloadState(float dt)
        {
            if (_pendingAutoReload && !IsReloading)
            {
                _autoReloadTimer -= dt;
                if (_autoReloadTimer <= 0f)
                {
                    _pendingAutoReload = false;
                    Reload();
                    return;
                }
            }

            if (_reloadPhase == ReloadPhase.None)
                return;

            _reloadTimer -= dt;
            if (_reloadTimer > 0f)
                return;

            if (_reloadPhase == ReloadPhase.EjectWait)
            {
                BeginInsertFreshMagazine();
            }
            else if (_reloadPhase == ReloadPhase.Inserting)
            {
                if (IsBoltLockedOpen)
                {
                    _reloadPhase = ReloadPhase.BoltReleaseWait;
                    _reloadTimer = boltReleaseDelaySec;
                }
                else
                {
                    _reloadPhase = ReloadPhase.None;
                }
            }
            else if (_reloadPhase == ReloadPhase.BoltReleaseWait)
            {
                IsBoltLockedOpen = false;
                if (mechanismController != null)
                    mechanismController.ReleaseBoltCatch();
                onBoltReleased?.Invoke();
                _reloadPhase = ReloadPhase.None;
            }
        }

        private void WarmPool()
        {
            for (int i = 0; i < initialPoolSize; i++)
            {
                BallisticBullet bullet = CreateBulletInstance();
                bullet.gameObject.SetActive(false);
                _bulletPool.Enqueue(bullet);
            }
        }

        private BallisticBullet AcquireBullet()
        {
            if (_bulletPool.Count > 0)
                return _bulletPool.Dequeue();

            return CreateBulletInstance();
        }

        private BallisticBullet CreateBulletInstance()
        {
            if (bulletPrefab != null)
                return Instantiate(bulletPrefab);

            GameObject go = new GameObject("PooledBallisticBullet");

            // Compact copper-jacketed ballistic projectile without unnatural laser trail streaks
            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            visual.name = "BulletVisual";
            visual.transform.SetParent(go.transform, false);
            visual.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            visual.transform.localScale = new Vector3(0.0065f, 0.014f, 0.0065f);
            Destroy(visual.GetComponent<Collider>());

            Material projectileMat = GetSharedTracerMaterial();
            if (projectileMat != null)
                visual.GetComponent<MeshRenderer>().sharedMaterial = projectileMat;

            BallisticBullet created = go.AddComponent<BallisticBullet>();
            created.ApplyAmmoPreset(ammoPreset);
            return created;
        }

        private static Material GetSharedTracerMaterial()
        {
            if (_sharedTracerMaterial != null)
                return _sharedTracerMaterial;

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
                shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
                return null;

            _sharedTracerMaterial = new Material(shader);
            _sharedTracerMaterial.color = new Color(0.76f, 0.48f, 0.22f, 1f);
            return _sharedTracerMaterial;
        }

        private void ReturnBulletToPool(BallisticBullet bullet)
        {
            if (bullet != null)
                _bulletPool.Enqueue(bullet);
        }

        private void UpdateRecoilSpring()
        {
            if (recoilPivot == null)
                return;

            float dt = Time.deltaTime;
            _recoilPosOffset.z = Mathf.SmoothDamp(_recoilPosOffset.z, 0f, ref _recoilPosVel, 1f / Mathf.Max(1f, recoilRecoverySpeed), Mathf.Infinity, dt);
            _recoilPitchOffset = Mathf.SmoothDamp(_recoilPitchOffset, 0f, ref _recoilPitchVel, 1f / Mathf.Max(1f, recoilRecoverySpeed), Mathf.Infinity, dt);

            recoilPivot.localPosition = _recoilPosOffset;
            recoilPivot.localRotation = Quaternion.Euler(_recoilPitchOffset, 0f, 0f);
        }
    }
}
