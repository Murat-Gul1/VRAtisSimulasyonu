using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRAtis.Targets
{
    public enum TargetKind
    {
        SteelGong = 0,
        MetalHumanSilhouette = 1,
        MovingEnemySoldier = 2
    }

    /// <summary>
    /// Runtime target & sport-shooting drill range manager.
    /// Allows switching between Steel Gong and ground-staked Metal Human Silhouette targets,
    /// in either the classic 25m-500m range or configurable side-by-side drills (0-6 targets,
    /// custom distance presets/adjustments, 1-30m spacing).
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
        [SerializeField] private GameObject metalSilhouettePrefab;
        [SerializeField] private GameObject enemySoldierPrefab;
        [SerializeField] private Transform shooterOriginTransform;

        [Header("Aktif Hedef Tipi & Drill Varsayılanları")]
        [SerializeField] private TargetKind activeTargetKind = TargetKind.SteelGong;
        [SerializeField] private int targetCount = 1;
        [SerializeField] private float distanceMeters = 25f;
        [SerializeField] private float spacingMeters = 5f;
        [SerializeField] private bool isCustomDrillActive = false;
        [SerializeField] private bool soldierAssaultMode = false;

        public event Action OnDrillConfigChanged;

        public TargetKind ActiveTargetKind => activeTargetKind;
        public int TargetCount => targetCount;
        public float DistanceMeters => distanceMeters;
        public float SpacingMeters => spacingMeters;
        public bool IsCustomDrillActive => isCustomDrillActive;
        public bool IsSoldierAssaultMode => soldierAssaultMode;

        private readonly List<SteelGongTarget> _gongDrillPool = new List<SteelGongTarget>(MaxTargetCount);
        private readonly List<SteelGongTarget> _silhouetteDrillPool = new List<SteelGongTarget>(MaxTargetCount);
        private readonly List<EnemySoldierTarget> _soldierDrillPool = new List<EnemySoldierTarget>(MaxTargetCount);
        private readonly List<SteelGongTarget> _classicGongTargets = new List<SteelGongTarget>(4);
        private readonly List<SteelGongTarget> _classicSilhouetteTargets = new List<SteelGongTarget>(4);
        private readonly List<EnemySoldierTarget> _classicSoldierTargets = new List<EnemySoldierTarget>(4);
        private Transform _drillContainer;
        private Transform _classicSilhouetteContainer;
        private Transform _classicSoldierContainer;
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

            GameObject classicSilGo = new GameObject("ClassicSilhouetteTargets_Pool");
            classicSilGo.transform.SetParent(transform, false);
            _classicSilhouetteContainer = classicSilGo.transform;

            GameObject classicSolGo = new GameObject("ClassicSoldierTargets_Pool");
            classicSolGo.transform.SetParent(transform, false);
            _classicSoldierContainer = classicSolGo.transform;

            if (isCustomDrillActive)
                ApplyCustomDrillLayout();
            else
                ApplyClassicRangeLayout();
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
            _classicGongTargets.Clear();
            string[] classicNames = { "SteelGong_25m", "SteelGong_100m", "SteelGong_300m", "SteelGong_500m" };
            foreach (string n in classicNames)
            {
                GameObject go = GameObject.Find(n);
                if (go == null) continue;
                SteelGongTarget target = go.GetComponent<SteelGongTarget>();
                if (target != null)
                    _classicGongTargets.Add(target);
            }
        }

        public void SetTargetKind(TargetKind kind)
        {
            if (activeTargetKind == kind)
            {
                if (isCustomDrillActive)
                    ApplyCustomDrillLayout();
                else
                    ApplyClassicRangeLayout();
                OnDrillConfigChanged?.Invoke();
                return;
            }

            activeTargetKind = kind;
            if (isCustomDrillActive)
                ApplyCustomDrillLayout();
            else
                ApplyClassicRangeLayout();
            OnDrillConfigChanged?.Invoke();
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
            ApplyClassicRangeLayout();
            OnDrillConfigChanged?.Invoke();
        }

        public void SetSoldierAssaultMode(bool assaultEnabled)
        {
            soldierAssaultMode = assaultEnabled;
            if (activeTargetKind == TargetKind.MovingEnemySoldier)
            {
                if (isCustomDrillActive)
                    ApplyCustomDrillLayout();
                else
                    ApplyClassicRangeLayout();
            }
            OnDrillConfigChanged?.Invoke();
        }

        public void ClearAllImpactMarksAndSwings()
        {
            if (isCustomDrillActive)
            {
                if (activeTargetKind == TargetKind.MovingEnemySoldier)
                {
                    for (int i = 0; i < targetCount && i < _soldierDrillPool.Count; i++)
                    {
                        if (_soldierDrillPool[i] != null && _soldierDrillPool[i].gameObject.activeSelf)
                            _soldierDrillPool[i].ResetSoldierState();
                    }
                    return;
                }

                List<SteelGongTarget> activePool = GetPoolForKind(activeTargetKind);
                for (int i = 0; i < targetCount && i < activePool.Count; i++)
                {
                    if (activePool[i] != null && activePool[i].gameObject.activeSelf)
                        activePool[i].ResetVisualState();
                }
            }
            else
            {
                if (activeTargetKind == TargetKind.MovingEnemySoldier && _classicSoldierTargets.Count > 0)
                {
                    for (int i = 0; i < _classicSoldierTargets.Count; i++)
                    {
                        if (_classicSoldierTargets[i] != null && _classicSoldierTargets[i].gameObject.activeSelf)
                            _classicSoldierTargets[i].ResetSoldierState();
                    }
                    return;
                }

                List<SteelGongTarget> classicList = activeTargetKind == TargetKind.MetalHumanSilhouette && _classicSilhouetteTargets.Count > 0
                    ? _classicSilhouetteTargets
                    : _classicGongTargets;
                for (int i = 0; i < classicList.Count; i++)
                {
                    if (classicList[i] != null && classicList[i].gameObject.activeSelf)
                        classicList[i].ResetVisualState();
                }
            }
        }

        private static readonly float[] ClassicSoldierDistances = { 25f, 75f, 150f, 250f };
        private static readonly float[] ClassicSoldierLatOffsets = { 0f, -3.5f, 4.0f, -5.0f };

        private void ApplyClassicRangeLayout()
        {
            SetListActive(_gongDrillPool, false);
            SetListActive(_silhouetteDrillPool, false);
            SetSoldierListActive(_soldierDrillPool, false);

            bool useSoldiers = activeTargetKind == TargetKind.MovingEnemySoldier && enemySoldierPrefab != null;
            bool useSilhouettes = activeTargetKind == TargetKind.MetalHumanSilhouette && metalSilhouettePrefab != null;

            if (useSoldiers)
            {
                SetListActive(_classicGongTargets, false);
                SetListActive(_classicSilhouetteTargets, false);
                EnsureClassicSoldierTargets();
                for (int i = 0; i < _classicSoldierTargets.Count; i++)
                {
                    EnemySoldierTarget sol = _classicSoldierTargets[i];
                    if (sol == null) continue;
                    sol.gameObject.SetActive(true);
                    float dist = i < ClassicSoldierDistances.Length ? ClassicSoldierDistances[i] : 75f;
                    float lat = i < ClassicSoldierLatOffsets.Length ? ClassicSoldierLatOffsets[i] : 0f;
                    Vector3 laneCenter = _originWorldPos + _rangeForward * dist + _rangeRight * lat;
                    laneCenter.y = 0f;
                    float halfW = Mathf.Clamp(spacingMeters * 0.5f, 1.5f, 12f);
                    sol.ConfigurePatrolZone(laneCenter, _originWorldPos, _rangeForward, _rangeRight, halfW, Mathf.Clamp(dist * 0.025f, 0.8f, 2.5f), soldierAssaultMode);
                }
            }
            else if (useSilhouettes)
            {
                SetListActive(_classicGongTargets, false);
                SetSoldierListActive(_classicSoldierTargets, false);
                EnsureClassicSilhouetteTargets();
                for (int i = 0; i < _classicSilhouetteTargets.Count; i++)
                {
                    if (_classicSilhouetteTargets[i] != null)
                    {
                        _classicSilhouetteTargets[i].gameObject.SetActive(true);
                        _classicSilhouetteTargets[i].ResetVisualState();
                    }
                }
            }
            else
            {
                SetListActive(_classicSilhouetteTargets, false);
                SetSoldierListActive(_classicSoldierTargets, false);
                for (int i = 0; i < _classicGongTargets.Count; i++)
                {
                    if (_classicGongTargets[i] != null)
                    {
                        _classicGongTargets[i].gameObject.SetActive(true);
                        _classicGongTargets[i].ResetVisualState();
                    }
                }
            }
        }

        private void ApplyCustomDrillLayout()
        {
            SetListActive(_classicGongTargets, false);
            SetListActive(_classicSilhouetteTargets, false);
            SetSoldierListActive(_classicSoldierTargets, false);

            if (activeTargetKind == TargetKind.MovingEnemySoldier && enemySoldierPrefab != null)
            {
                SetListActive(_gongDrillPool, false);
                SetListActive(_silhouetteDrillPool, false);
                EnsureSoldierPoolCount(targetCount);

                Vector3 centerPosSol = _originWorldPos + _rangeForward * distanceMeters;
                float soldierScale = ComputeSoldierScaleForDistance(distanceMeters);
                // Exact lateral running/evasion corridor width = spacingMeters (e.g. 5m -> +-2.5m left/right)
                float lateralPatrolHalf = Mathf.Clamp(spacingMeters * 0.5f, 0.5f, 15.0f);
                float depthJitter = Mathf.Clamp(lateralPatrolHalf * 0.30f, 0.35f, 2.2f);
                float laneStep = Mathf.Max(spacingMeters + 0.8f, 2.6f);

                for (int i = 0; i < _soldierDrillPool.Count; i++)
                {
                    EnemySoldierTarget sol = _soldierDrillPool[i];
                    if (sol == null) continue;

                    if (i >= targetCount)
                    {
                        sol.gameObject.SetActive(false);
                        continue;
                    }

                    float lateralOffset = (i - (targetCount - 1) * 0.5f) * laneStep;
                    Vector3 lanePos = centerPosSol + _rangeRight * lateralOffset;
                    lanePos.y = 0f;

                    sol.gameObject.name = $"DrillSoldier_{i + 1}_{distanceMeters:F0}m";
                    sol.transform.position = lanePos;
                    sol.transform.localScale = Vector3.one * soldierScale;
                    sol.gameObject.SetActive(true);
                    sol.ConfigurePatrolZone(lanePos, _originWorldPos, _rangeForward, _rangeRight, lateralPatrolHalf, depthJitter, soldierAssaultMode);
                }
                return;
            }

            SetSoldierListActive(_soldierDrillPool, false);

            TargetKind resolvedKind = (activeTargetKind == TargetKind.MetalHumanSilhouette && metalSilhouettePrefab != null)
                ? TargetKind.MetalHumanSilhouette
                : TargetKind.SteelGong;

            List<SteelGongTarget> activePool = GetPoolForKind(resolvedKind);
            List<SteelGongTarget> inactivePool = resolvedKind == TargetKind.MetalHumanSilhouette ? _gongDrillPool : _silhouetteDrillPool;
            SetListActive(inactivePool, false);

            EnsurePoolCount(resolvedKind, targetCount);

            float scale = ComputeTargetScaleForDistance(distanceMeters);
            Vector3 centerPos = _originWorldPos + _rangeForward * distanceMeters;
            string prefix = resolvedKind == TargetKind.MetalHumanSilhouette ? "DrillHumanMetal" : "DrillGong";

            for (int i = 0; i < activePool.Count; i++)
            {
                SteelGongTarget target = activePool[i];
                if (target == null) continue;

                if (i >= targetCount)
                {
                    target.gameObject.SetActive(false);
                    continue;
                }

                float lateralOffset = (i - (targetCount - 1) * 0.5f) * spacingMeters;
                Vector3 pos = centerPos + _rangeRight * lateralOffset;
                pos.y = 0f;

                target.gameObject.name = $"{prefix}_{i + 1}_{distanceMeters:F0}m";
                target.transform.position = pos;
                target.transform.localScale = Vector3.one * scale;

                // Orient each stand slightly inward so its painted front face looks squarely at the shooter
                Vector3 awayFromShooter = pos - _originWorldPos;
                awayFromShooter.y = 0f;
                if (awayFromShooter.sqrMagnitude > 1e-4f)
                    target.transform.rotation = Quaternion.LookRotation(awayFromShooter.normalized, Vector3.up);

                target.gameObject.SetActive(true);
                target.ResetVisualState();
            }
        }

        private void EnsureClassicSilhouetteTargets()
        {
            if (_classicSilhouetteTargets.Count > 0 || metalSilhouettePrefab == null)
                return;

            if (_classicGongTargets.Count > 0)
            {
                for (int i = 0; i < _classicGongTargets.Count; i++)
                {
                    SteelGongTarget src = _classicGongTargets[i];
                    if (src == null) continue;

                    GameObject inst = Instantiate(metalSilhouettePrefab, _classicSilhouetteContainer);
                    inst.name = src.name.Replace("SteelGong", "MetalSilhouette");
                    inst.transform.position = src.transform.position;
                    inst.transform.rotation = src.transform.rotation;
                    inst.transform.localScale = src.transform.localScale;
                    inst.SetActive(false);

                    SteelGongTarget target = inst.GetComponent<SteelGongTarget>();
                    if (target != null)
                        _classicSilhouetteTargets.Add(target);
                }
            }
            else
            {
                float[] dists = { 25f, 100f, 300f, 500f };
                for (int i = 0; i < dists.Length; i++)
                {
                    Vector3 pos = _originWorldPos + _rangeForward * dists[i];
                    pos.y = 0f;
                    GameObject inst = Instantiate(metalSilhouettePrefab, _classicSilhouetteContainer);
                    inst.name = $"MetalSilhouette_{dists[i]:F0}m";
                    inst.transform.position = pos;
                    inst.transform.localScale = Vector3.one * ComputeTargetScaleForDistance(dists[i]);
                    Vector3 away = pos - _originWorldPos;
                    away.y = 0f;
                    if (away.sqrMagnitude > 1e-4f)
                        inst.transform.rotation = Quaternion.LookRotation(away.normalized, Vector3.up);
                    inst.SetActive(false);

                    SteelGongTarget target = inst.GetComponent<SteelGongTarget>();
                    if (target != null)
                        _classicSilhouetteTargets.Add(target);
                }
            }
        }

        private List<SteelGongTarget> GetPoolForKind(TargetKind kind)
        {
            return kind == TargetKind.MetalHumanSilhouette ? _silhouetteDrillPool : _gongDrillPool;
        }

        private void EnsurePoolCount(TargetKind kind, int needed)
        {
            GameObject prefab = kind == TargetKind.MetalHumanSilhouette ? metalSilhouettePrefab : steelGongPrefab;
            if (prefab == null)
            {
                Debug.LogWarning($"[TargetRangeManager] {kind} prefabı atanmamış; drill hedefi oluşturulamıyor.");
                return;
            }

            List<SteelGongTarget> pool = GetPoolForKind(kind);
            string prefix = kind == TargetKind.MetalHumanSilhouette ? "DrillHumanMetal" : "DrillGong";

            while (pool.Count < needed && pool.Count < MaxTargetCount)
            {
                GameObject inst = Instantiate(prefab, _drillContainer);
                inst.name = $"{prefix}_{pool.Count + 1}";
                inst.SetActive(false);
                SteelGongTarget target = inst.GetComponent<SteelGongTarget>();
                if (target != null)
                    pool.Add(target);
            }
        }

        private void EnsureClassicSoldierTargets()
        {
            if (_classicSoldierTargets.Count > 0 || enemySoldierPrefab == null)
                return;

            for (int i = 0; i < ClassicSoldierDistances.Length; i++)
            {
                float dist = ClassicSoldierDistances[i];
                Vector3 pos = _originWorldPos + _rangeForward * dist + _rangeRight * ClassicSoldierLatOffsets[i];
                pos.y = 0f;
                GameObject inst = Instantiate(enemySoldierPrefab, _classicSoldierContainer);
                inst.name = $"EnemySoldier_{dist:F0}m";
                inst.transform.position = pos;
                inst.transform.localScale = Vector3.one * ComputeSoldierScaleForDistance(dist);
                inst.SetActive(false);

                EnemySoldierTarget sol = inst.GetComponent<EnemySoldierTarget>();
                if (sol != null)
                    _classicSoldierTargets.Add(sol);
            }
        }

        private void EnsureSoldierPoolCount(int needed)
        {
            if (enemySoldierPrefab == null)
            {
                Debug.LogWarning("[TargetRangeManager] enemySoldierPrefab atanmamış; hareketli asker oluşturulamıyor.");
                return;
            }

            while (_soldierDrillPool.Count < needed && _soldierDrillPool.Count < MaxTargetCount)
            {
                GameObject inst = Instantiate(enemySoldierPrefab, _drillContainer);
                inst.name = $"DrillSoldier_{_soldierDrillPool.Count + 1}";
                inst.SetActive(false);
                EnemySoldierTarget sol = inst.GetComponent<EnemySoldierTarget>();
                if (sol != null)
                    _soldierDrillPool.Add(sol);
            }
        }

        private static void SetSoldierListActive(List<EnemySoldierTarget> list, bool active)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null)
                    list[i].gameObject.SetActive(active);
            }
        }

        private static float ComputeSoldierScaleForDistance(float distMeters)
        {
            // Keep realistic 1:1 human height (1.82m) at close/medium distances; subtle boost only at long range
            if (distMeters <= 150f) return 1.00f;
            if (distMeters <= 300f) return 1.12f;
            return 1.25f;
        }

        private static void SetListActive(List<SteelGongTarget> list, bool active)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null)
                    list[i].gameObject.SetActive(active);
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
