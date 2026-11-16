using System;
using UnityEngine;

namespace VRAtis.Weapons
{
    /// <summary>
    /// Lightweight, zero-Rigidbody ejected brass casing with parabolic gravity,
    /// realistic spin, ground bounce detection, and automatic pool return.
    /// </summary>
    public class EjectedCasing : MonoBehaviour
    {
        [SerializeField] private float gravity = 9.81f;
        [SerializeField] private float bounceRestitution = 0.38f;
        [SerializeField] private float horizontalFrictionOnBounce = 0.55f;
        [SerializeField] private float lingerAfterStopSec = 2.5f;
        [SerializeField] private float maxLifeSec = 5.0f;
        [SerializeField] private float casingRadius = 0.005f;
        [SerializeField] private LayerMask groundMask = ~0;

        private Vector3 _velocity;
        private Vector3 _angularVelDeg;
        private bool _isActive;
        private bool _isResting;
        private float _age;
        private float _restTimer;
        private Action<EjectedCasing> _returnCallback;

        public void Launch(Vector3 startPos, Quaternion startRot, Vector3 initialVelocity, Vector3 angularVelDeg, Action<EjectedCasing> returnToPool)
        {
            transform.SetPositionAndRotation(startPos, startRot);
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

            // Ground / surface bounce check when moving downward
            Vector3 step = nextPos - pos;
            float dist = step.magnitude;
            if (dist > 0.0001f && _velocity.y < 0f &&
                Physics.Raycast(pos, step / dist, out RaycastHit hit, dist + casingRadius, groundMask, QueryTriggerInteraction.Ignore))
            {
                nextPos = hit.point + hit.normal * casingRadius;
                _velocity = Vector3.Reflect(_velocity, hit.normal);
                _velocity.y *= bounceRestitution;
                _velocity.x *= horizontalFrictionOnBounce;
                _velocity.z *= horizontalFrictionOnBounce;
                _angularVelDeg *= 0.45f;

                if (Mathf.Abs(_velocity.y) < 0.35f)
                {
                    _velocity = Vector3.zero;
                    _angularVelDeg = Vector3.zero;
                    _isResting = true;
                    _restTimer = 0f;
                    // Settle casing flat on the surface
                    Vector3 flatForward = Vector3.ProjectOnPlane(transform.forward, hit.normal);
                    if (flatForward.sqrMagnitude < 1e-4f)
                        flatForward = Vector3.Cross(hit.normal, Vector3.right);
                    transform.rotation = Quaternion.LookRotation(flatForward.normalized, hit.normal);
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
