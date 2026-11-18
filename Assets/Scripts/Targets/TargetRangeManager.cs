using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRAtis.Targets
{
    /// <summary>
    /// Runtime target & sport-shooting drill range manager.
    /// Allows switching between the classic 25m-500m range and configurable side-by-side
    /// steel gong drills (0-6 targets, custom distance presets/adjustments, 1-30m spacing).
    /// </summary>
    public class TargetRangeManager : MonoBehaviour
    {
        public const int MinTargetCount = 0;
        public const int MaxTargetCount = 6;
        public const float MinDistanceMeters = 5f;
        public const float MaxDistanceMeters = 500f;
        public const float MinSpacingMeters = 1f;
        public const float MaxSpacingMeters = 30f;

        [Header("Referanslar")]
        [SerializeField] private GameObject steelGongPrefab;
        [SerializeField] private Transform shooterOriginTransform;

        [Header("Aktif Drill Varsayılanları")]
        [SerializeField] private int targetCount = 1;
        [SerializeField] private float distanceMeters = 25f;
        [SerializeField] private float spacingMeters = 5f;
        [SerializeField] private bool isCustomDrillActive = false;

        public event Action OnDrillConfigChanged;

        public int TargetCount => targetCount;
        public float DistanceMeters => distanceMeters;
        public float SpacingMeters => spacingMeters;
        public bool IsCustomDrillActive => isCustomDrillActive;

        private readonly List<SteelGongTarget> _drillPool = new List<SteelGongTarget>(MaxTargetCount);
        private readonly List<SteelGongTarget> _classicTargets = new List<SteelGongTarget>(4);
        private Transform _drillContainer;
        private Vector3 _originWorldPos = new Vector3(2.01f, 0f, -3.783f);
        private Vector3 _rangeForward = Vector3.forward;
        private Vector3 _rangeRight = Vector3.right;

        private void Awake()
        {
            ResolveShooterLane();
            CollectClassicTargets();

            GameObject containerGo = new GameObject("CustomDrillTargets_Pool");
            containerGo.transform.SetParent(transform, false);
            _drillContainer = containerGo.transform;

            if (isCustomDrillActive)
                ApplyCustomDrillLayout();
        }

        private void ResolveShooterLane()
        {
            if (shooterOriginTransform == null)
            {
                GameObject rifle = GameObject.Find("PlayerRifle");
                if (rifle != null)
                    shooterOriginTransform = rifle.transform;
            }

            if (shooterOriginTransform != null)
            {
                _originWorldPos = new Vector3(shooterOriginTransform.position.x, 0f, shooterOriginTransform.position.z);
                Vector3 fwd = new Vector3(shooterOriginTransform.forward.x, 0f, shooterOriginTransform.forward.z);
                if (fwd.sqrMagnitude > 1e-4f)
                    _rangeForward = fwd.normalized;
            }

            _rangeRight = Vector3.Cross(Vector3.up, _rangeForward).normalized;
        }

        private void CollectClassicTargets()
        {
            _classicTargets.Clear();
            string[] classicNames = { "SteelGong_25m", "SteelGong_100m", "SteelGong_300m", "SteelGong_500m" };
            foreach (string n in classicNames)
            {
                GameObject go = GameObject.Find(n);
                if (go == null) continue;
                SteelGongTarget target = go.GetComponent<SteelGongTarget>();
                if (target != null)
                    _classicTargets.Add(target);
            }
        }

        public void SetTargetCount(int count)
        {
            targetCount = Mathf.Clamp(count, MinTargetCount, MaxTargetCount);
            isCustomDrillActive = true;
            ApplyCustomDrillLayout();
            OnDrillConfigChanged?.Invoke();
        }

        public void SetDistanceMeters(float meters)
        {
            distanceMeters = Mathf.Clamp(Mathf.Round(meters), MinDistanceMeters, MaxDistanceMeters);
            if (targetCount == 0 && !isCustomDrillActive)
                targetCount = 1;
            isCustomDrillActive = true;
            ApplyCustomDrillLayout();
            OnDrillConfigChanged?.Invoke();
        }

        public void NudgeDistanceMeters(float deltaMeters)
        {
            SetDistanceMeters(distanceMeters + deltaMeters);
        }

        public void SetSpacingMeters(float meters)
        {
            spacingMeters = Mathf.Clamp(Mathf.Round(meters), MinSpacingMeters, MaxSpacingMeters);
            if (targetCount == 0 && !isCustomDrillActive)
                targetCount = 1;
            isCustomDrillActive = true;
            ApplyCustomDrillLayout();
            OnDrillConfigChanged?.Invoke();
        }

        public void NudgeSpacingMeters(float deltaMeters)
        {
            SetSpacingMeters(spacingMeters + deltaMeters);
        }

        public void ActivateClassicRange()
        {
            isCustomDrillActive = false;

            for (int i = 0; i < _drillPool.Count; i++)
            {
                if (_drillPool[i] != null)
                    _drillPool[i].gameObject.SetActive(false);
            }

            for (int i = 0; i < _classicTargets.Count; i++)
            {
                if (_classicTargets[i] != null)
                {
                    _classicTargets[i].gameObject.SetActive(true);
                    _classicTargets[i].ResetVisualState();
                }
            }

            OnDrillConfigChanged?.Invoke();
        }

        public void ClearAllImpactMarksAndSwings()
        {
            if (isCustomDrillActive)
            {
                for (int i = 0; i < targetCount && i < _drillPool.Count; i++)
                {
                    if (_drillPool[i] != null && _drillPool[i].gameObject.activeSelf)
                        _drillPool[i].ResetVisualState();
                }
            }
            else
            {
                for (int i = 0; i < _classicTargets.Count; i++)
                {
                    if (_classicTargets[i] != null && _classicTargets[i].gameObject.activeSelf)
                        _classicTargets[i].ResetVisualState();
                }
            }
        }

        private void ApplyCustomDrillLayout()
        {
            for (int i = 0; i < _classicTargets.Count; i++)
            {
                if (_classicTargets[i] != null)
                    _classicTargets[i].gameObject.SetActive(false);
            }

            EnsurePoolCount(targetCount);

            float scale = ComputeTargetScaleForDistance(distanceMeters);
            Vector3 centerPos = _originWorldPos + _rangeForward * distanceMeters;

            for (int i = 0; i < _drillPool.Count; i++)
            {
                SteelGongTarget gong = _drillPool[i];
                if (gong == null) continue;

                if (i >= targetCount)
                {
                    gong.gameObject.SetActive(false);
                    continue;
                }

                float lateralOffset = (i - (targetCount - 1) * 0.5f) * spacingMeters;
                Vector3 pos = centerPos + _rangeRight * lateralOffset;
                pos.y = 0f;

                gong.gameObject.name = $"DrillGong_{i + 1}_{distanceMeters:F0}m";
                gong.transform.position = pos;
                gong.transform.localScale = Vector3.one * scale;

                // Orient each stand slightly inward so its painted front face looks squarely at the shooter
                Vector3 awayFromShooter = pos - _originWorldPos;
                awayFromShooter.y = 0f;
                if (awayFromShooter.sqrMagnitude > 1e-4f)
                    gong.transform.rotation = Quaternion.LookRotation(awayFromShooter.normalized, Vector3.up);

                gong.gameObject.SetActive(true);
                gong.ResetVisualState();
            }
        }

        private void EnsurePoolCount(int needed)
        {
            if (steelGongPrefab == null)
            {
                Debug.LogWarning("[TargetRangeManager] steelGongPrefab atanmamış; drill hedefi oluşturulamıyor.");
                return;
            }

            while (_drillPool.Count < needed && _drillPool.Count < MaxTargetCount)
            {
                GameObject inst = Instantiate(steelGongPrefab, _drillContainer);
                inst.name = $"DrillGong_{_drillPool.Count + 1}";
                inst.SetActive(false);
                SteelGongTarget gong = inst.GetComponent<SteelGongTarget>();
                if (gong != null)
                    _drillPool.Add(gong);
            }
        }

        private static float ComputeTargetScaleForDistance(float distMeters)
        {
            if (distMeters <= 40f) return 1.15f;
            if (distMeters <= 120f) return 1.20f;
            if (distMeters <= 250f) return 1.40f;
            if (distMeters <= 380f) return 1.65f;
            return 2.00f;
        }
    }
}
