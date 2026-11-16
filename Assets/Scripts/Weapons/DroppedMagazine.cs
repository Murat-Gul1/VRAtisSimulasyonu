using System;
using UnityEngine;

namespace VRAtis.Weapons
{
    /// <summary>
    /// Lightweight, zero-Rigidbody dropped AR-15 magazine with center-of-mass pivot,
    /// gravity, gentle tumble, ground bounce, and automatic pool return.
    /// </summary>
    public class DroppedMagazine : MonoBehaviour
    {
        [SerializeField] private float gravity = 9.81f;
        [SerializeField] private float bounceRestitution = 0.22f;
        [SerializeField] private float horizontalFrictionOnBounce = 0.50f;
        [SerializeField] private float lingerAfterStopSec = 6.0f;
        [SerializeField] private float maxLifeSec = 10.0f;
        [SerializeField] private float halfThicknessMeters = 0.016f;
        [SerializeField] private LayerMask groundMask = ~0;

        private Transform _visualChild;
        private Vector3 _centerOffsetLocal;
        private Vector3 _velocity;
        private Vector3 _angularVelDeg;
        private bool _isActive;
        private bool _isResting;
        private float _age;
        private float _restTimer;
        private Action<DroppedMagazine> _returnCallback;

        public static DroppedMagazine CreateFromSource(Transform magazineTransform)
        {
            MeshFilter srcFilter = magazineTransform.GetComponentInChildren<MeshFilter>(true);
            MeshRenderer srcRenderer = magazineTransform.GetComponentInChildren<MeshRenderer>(true);
            if (srcFilter == null || srcFilter.sharedMesh == null || srcRenderer == null)
                return null;

            GameObject root = new GameObject("PooledDroppedMagazine");
            DroppedMagazine mag = root.AddComponent<DroppedMagazine>();

            GameObject visual = new GameObject("MagazineVisual");
            visual.transform.SetParent(root.transform, false);
            visual.transform.localScale = magazineTransform.lossyScale;

            MeshFilter mf = visual.AddComponent<MeshFilter>();
            mf.sharedMesh = srcFilter.sharedMesh;

            MeshRenderer mr = visual.AddComponent<MeshRenderer>();
            mr.sharedMaterials = srcRenderer.sharedMaterials;
            mr.shadowCastingMode = srcRenderer.shadowCastingMode;
            mr.receiveShadows = srcRenderer.receiveShadows;

            // Center visual on its geometric bounds so gravity/rotation pivot around CoM
            Vector3 boundsCenter = Vector3.Scale(srcFilter.sharedMesh.bounds.center, visual.transform.localScale);
            visual.transform.localPosition = -boundsCenter;
            mag._visualChild = visual.transform;
            mag._centerOffsetLocal = boundsCenter;
            root.SetActive(false);
            return mag;
        }

        public void LaunchFromTransform(Transform sourceMag, Vector3 initialVelocity, Vector3 angularVelDeg, Action<DroppedMagazine> returnToPool)
        {
            MeshFilter mf = sourceMag.GetComponentInChildren<MeshFilter>(true);
            Vector3 comWorldPos = (mf != null && mf.sharedMesh != null)
                ? sourceMag.TransformPoint(mf.sharedMesh.bounds.center)
                : sourceMag.position + sourceMag.rotation * _centerOffsetLocal;

            transform.SetPositionAndRotation(comWorldPos, sourceMag.rotation);
            _velocity = initialVelocity;
            _angularVelDeg = angularVelDeg;
            _returnCallback = returnToPool;
            _isActive = true;
            _isResting = false;
            _age = 0f;
            _restTimer = 0f;
            gameObject.SetActive(true);
        }

        private void Update()
        {
            if (!_isActive)
                return;

            float dt = Time.deltaTime;
            _age += dt;
            if (_age >= maxLifeSec)
            {
                Despawn();
                return;
            }

            if (_isResting)
            {
                _restTimer += dt;
                if (_restTimer >= lingerAfterStopSec)
                    Despawn();
                return;
            }

            _velocity.y -= gravity * dt;
            Vector3 pos = transform.position;
            Vector3 nextPos = pos + _velocity * dt;

            Vector3 step = nextPos - pos;
            float dist = step.magnitude;
            if (dist > 0.0001f && _velocity.y < 0f &&
                Physics.Raycast(pos, step / dist, out RaycastHit hit, dist + halfThicknessMeters, groundMask, QueryTriggerInteraction.Ignore))
            {
                nextPos = hit.point + hit.normal * halfThicknessMeters;
                _velocity = Vector3.Reflect(_velocity, hit.normal);
                _velocity.y *= bounceRestitution;
                _velocity.x *= horizontalFrictionOnBounce;
                _velocity.z *= horizontalFrictionOnBounce;
                _angularVelDeg *= 0.35f;

                if (Mathf.Abs(_velocity.y) < 0.45f)
                {
                    _velocity = Vector3.zero;
                    _angularVelDeg = Vector3.zero;
                    _isResting = true;
                    _restTimer = 0f;

                    // Lay magazine flat on its side against the ground normal
                    Vector3 flatForward = Vector3.ProjectOnPlane(transform.forward, hit.normal);
                    if (flatForward.sqrMagnitude < 1e-4f)
                        flatForward = Vector3.Cross(hit.normal, Vector3.right);
                    transform.rotation = Quaternion.LookRotation(flatForward.normalized, hit.normal) * Quaternion.Euler(0f, 0f, 90f);
                }
            }

            transform.position = nextPos;
            if (!_isResting)
            {
                transform.Rotate(_angularVelDeg * dt, Space.Self);
            }
        }

        private void Despawn()
        {
            _isActive = false;
            gameObject.SetActive(false);
            _returnCallback?.Invoke(this);
        }
    }
}
