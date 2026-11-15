using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
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
        [Header("Referanslar")]
        [SerializeField] private Esp32RifleReceiver esp32Receiver;
        [Tooltip("Merminin çıkacağı namlu ucu Transform'u.")]
        [SerializeField] private Transform muzzleTransform;
        [Tooltip("BallisticBullet bileşeni taşıyan mermi prefab'ı (boşsa runtime'da minimal obje üretilir).")]
        [SerializeField] private BallisticBullet bulletPrefab;

        [Header("Geri Tepme (Görsel Child Pivot)")]
        [Tooltip("ESP32 ana rotasyonunu bozmamak için silah modelini taşıyan alt Transform.")]
        [SerializeField] private Transform recoilPivot;
        [SerializeField] private float recoilKickBackMeters = 0.035f;
        [SerializeField] private float recoilPitchUpDeg = 2.2f;
        [SerializeField] private float recoilRecoverySpeed = 14f;

        [Header("Ateşleme Ayarları")]
        [SerializeField] private int initialPoolSize = 16;
        [SerializeField] private bool allowEditorTestFire = true;

        [Header("Olaylar (Adım 3: Namlu Alevi / Ses için)")]
        public UnityEvent onWeaponFired;

        private readonly Queue<BallisticBullet> _bulletPool = new Queue<BallisticBullet>();
        private static Material _sharedTracerMaterial;
        private Vector3 _recoilPosOffset;
        private float _recoilPitchOffset;
        private float _recoilPosVel;
        private float _recoilPitchVel;

        private void Awake()
        {
            if (esp32Receiver == null)
                esp32Receiver = FindAnyObjectByType<Esp32RifleReceiver>();

            if (muzzleTransform == null)
                muzzleTransform = transform;

            WarmPool();
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
            if (allowEditorTestFire && Application.isEditor)
            {
                bool keyFire = Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame;
                bool mouseFire = Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;
                if (keyFire || mouseFire)
                {
                    Fire();
                }
            }
#endif
            UpdateRecoilSpring();
        }

        private void HandleEspShot(uint shotNumber)
        {
            Fire();
        }

        public void Fire()
        {
            BallisticBullet bullet = AcquireBullet();
            bullet.Launch(
                muzzleTransform.position,
                muzzleTransform.rotation,
                0f,
                0f,
                ReturnBulletToPool);

            // Apply spring impulse to child recoil pivot
            _recoilPosOffset.z -= recoilKickBackMeters;
            _recoilPitchOffset -= recoilPitchUpDeg;

            onWeaponFired?.Invoke();
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

            // Lightweight visible projectile & tracer trail for immediate testing
            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            visual.name = "BulletVisual";
            visual.transform.SetParent(go.transform, false);
            visual.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            visual.transform.localScale = new Vector3(0.025f, 0.06f, 0.025f);
            Destroy(visual.GetComponent<Collider>());

            Material tracerMat = GetSharedTracerMaterial();
            if (tracerMat != null)
            {
                visual.GetComponent<MeshRenderer>().sharedMaterial = tracerMat;
            }

            TrailRenderer trail = go.AddComponent<TrailRenderer>();
            trail.time = 0.25f;
            trail.startWidth = 0.025f;
            trail.endWidth = 0.002f;
            trail.minVertexDistance = 0.2f;
            if (tracerMat != null)
                trail.sharedMaterial = tracerMat;

            return go.AddComponent<BallisticBullet>();
        }

        private static Material GetSharedTracerMaterial()
        {
            if (_sharedTracerMaterial != null)
                return _sharedTracerMaterial;

            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
                shader = Shader.Find("Unlit/Color");

            if (shader == null)
                return null;

            _sharedTracerMaterial = new Material(shader);
            _sharedTracerMaterial.color = new Color(1f, 0.72f, 0.15f, 1f);
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
