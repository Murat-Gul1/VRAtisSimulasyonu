using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VRAtis.WorldMap
{
    /// <summary>
    /// Controls the player's armed, banner-carrying commander figure on the Warband-style world map.
    /// Uses WorldMapPathfinder (A* + Line-of-Sight smoothing) so clicking across rivers or around
    /// cliffs automatically routes the commander cleanly over the nearest bridge, with a subtle
    /// Bannerlord-style golden route preview line on the ground.
    /// </summary>
    [DisallowMultipleComponent]
    public class WorldMapPlayerParty : MonoBehaviour
    {
        private static readonly int IsMovingHash = Animator.StringToHash("IsMoving");

        [Header("Referanslar")]
        [SerializeField] private WorldMapCameraController mapCamera;
        [SerializeField] private Transform visualRoot;
        [SerializeField] private Animator animator;
        [SerializeField] private Transform bannerClothTransform;

        [Header("Harita Hareketi")]
        [SerializeField] private float moveSpeed = 19f;
        [SerializeField] private float turnSpeedDeg = 480f;
        [SerializeField] private float arrivalDistance = 0.45f;
        [SerializeField] private float seaLevelY = 18.0f;
        [SerializeField] private float maxPathSlopeDeg = 23.5f;
        [SerializeField] private float hardCliffSlopeDeg = 42f;
        [SerializeField] private Vector2 mapBoundsMin = new Vector2(-970f, -970f);
        [SerializeField] private Vector2 mapBoundsMax = new Vector2(900f, 880f);

        [Header("Rota Çizgisi (Warband Tarzı)")]
        [SerializeField] private bool showRoutePreview = false;
        [SerializeField] private Color routeLineColor = new Color(0.98f, 0.78f, 0.28f, 0.78f);
        [SerializeField] private float routeLineWidth = 1.15f;
        [SerializeField] private float routeGroundOffset = 0.55f;

        [Header("Bayrak Rüzgârı & Yedek Yürüyüş Efekti")]
        [SerializeField] private bool enableFallbackWalkBob = true;
        [SerializeField] private float walkBobFrequency = 9.5f;
        [SerializeField] private float walkBobAmplitude = 0.06f;
        [SerializeField] private float bannerWindSpeed = 3.2f;
        [SerializeField] private float bannerWindAngleDeg = 4.5f;

        public event Action<Vector3> OnDestinationReached;

        private readonly List<Vector3> _waypoints = new List<Vector3>(32);
        private readonly List<Vector3> _routeRenderPoints = new List<Vector3>(128);
        private int _waypointIndex;
        private Vector3 _destination;
        private bool _hasDestination;
        private float _walkPhase;
        private Vector3 _visualBaseLocalPos;
        private Quaternion _bannerBaseLocalRot;
        private bool _hasActiveAnimator;
        private bool _lastAnimatorMoving;

        private WorldMapPathfinder _pathfinder;
        private LineRenderer _routeLine;
        private Material _routeLineMat;
        private bool _repathedThisTrip;

        public bool IsMoving => _hasDestination;
        public Vector3 Destination => _destination;
        public int CurrentPathWaypointCount => _waypoints.Count;

        private void Awake()
        {
            if (mapCamera == null)
                mapCamera = FindAnyObjectByType<WorldMapCameraController>();

            if (animator == null)
                animator = GetComponentInChildren<Animator>(true);

            _hasActiveAnimator = animator != null && animator.runtimeAnimatorController != null;

            if (visualRoot != null)
                _visualBaseLocalPos = visualRoot.localPosition;

            if (bannerClothTransform != null)
                _bannerBaseLocalRot = bannerClothTransform.localRotation;

            float startY = SampleGroundHeight(transform.position.x, transform.position.z);
            transform.position = new Vector3(transform.position.x, startY, transform.position.z);

            _destination = transform.position;
            EnsureRouteLine();
            SyncAnimatorState(force: true);
        }

        private void Start()
        {
            // Build pathfinder in Start() after all WorldMapBridgeZone.OnEnable() calls have registered
            EnsurePathfinderBuilt();
        }

        private void OnEnable()
        {
            if (mapCamera == null)
                mapCamera = FindAnyObjectByType<WorldMapCameraController>();

            if (mapCamera != null)
                mapCamera.OnCursorConfirmPressed += HandleCursorConfirmed;
        }

        private void OnDisable()
        {
            if (mapCamera != null)
                mapCamera.OnCursorConfirmPressed -= HandleCursorConfirmed;
        }

        private void OnDestroy()
        {
            if (_routeLineMat != null)
                Destroy(_routeLineMat);
        }

        private void Update()
        {
            float dt = Mathf.Clamp(Time.unscaledDeltaTime, 0.0001f, 0.05f);

            UpdateMovement(dt);
            SyncAnimatorState(force: false);
            UpdateBannerWind();
            UpdateRoutePreview();
        }

        public void MoveTo(Vector3 worldPoint)
        {
            EnsurePathfinderBuilt();

            float clampedX = Mathf.Clamp(worldPoint.x, mapBoundsMin.x, mapBoundsMax.x);
            float clampedZ = Mathf.Clamp(worldPoint.z, mapBoundsMin.y, mapBoundsMax.y);
            Vector3 desiredTarget = new Vector3(clampedX, 0f, clampedZ);

            Vector3 flatDelta = desiredTarget - transform.position;
            flatDelta.y = 0f;
            if (flatDelta.sqrMagnitude <= (arrivalDistance * arrivalDistance))
            {
                StopMovement();
                return;
            }

            if (_pathfinder != null && _pathfinder.FindPath(transform.position, desiredTarget, _waypoints) && _waypoints.Count > 0)
            {
                _waypointIndex = 0;
                _destination = _waypoints[_waypoints.Count - 1];
                _hasDestination = true;
                _repathedThisTrip = false;
            }
            else
            {
                StopMovement();
            }
        }

        public void StopMovement()
        {
            _hasDestination = false;
            _waypoints.Clear();
            _waypointIndex = 0;
            _destination = transform.position;
            _repathedThisTrip = false;
            if (_routeLine != null)
                _routeLine.positionCount = 0;
        }

        public IReadOnlyList<Vector3> GetActiveWaypoints() => _waypoints;

        private void EnsurePathfinderBuilt()
        {
            if (_pathfinder != null && _pathfinder.IsBuilt)
                return;

            _pathfinder = new WorldMapPathfinder(mapBoundsMin, mapBoundsMax, seaLevelY, maxPathSlopeDeg);
            _pathfinder.Build(Terrain.activeTerrain);
        }

        private void HandleCursorConfirmed(Vector3 cursorWorldPos)
        {
            MoveTo(cursorWorldPos);
        }

        private void UpdateMovement(float dt)
        {
            if (_hasDestination && _waypointIndex < _waypoints.Count)
            {
                float remainingStep = moveSpeed * dt;
                Vector3 pos = transform.position;
                Vector3 lastMoveDir = transform.forward;
                bool moved = false;

                while (remainingStep > 0.0001f && _waypointIndex < _waypoints.Count)
                {
                    Vector3 wp = _waypoints[_waypointIndex];
                    Vector3 toWp = wp - pos;
                    toWp.y = 0f;
                    float dist = toWp.magnitude;
                    bool isFinalWp = _waypointIndex == _waypoints.Count - 1;
                    float reachTol = isFinalWp ? arrivalDistance : 0.75f;

                    if (dist <= reachTol)
                    {
                        if (isFinalWp)
                        {
                            float finalY = SampleGroundHeight(wp.x, wp.z);
                            pos = new Vector3(wp.x, finalY, wp.z);
                            transform.position = pos;
                            _hasDestination = false;
                            _waypoints.Clear();
                            _waypointIndex = 0;
                            OnDestinationReached?.Invoke(transform.position);
                            return;
                        }

                        _waypointIndex++;
                        continue;
                    }

                    Vector3 dir = toWp / dist;
                    lastMoveDir = dir;
                    float step = Mathf.Min(dist, remainingStep);
                    Vector3 next = pos + dir * step;

                    if (!IsPointWalkable(next.x, next.z))
                    {
                        // Auto-repath once around any unexpected steep bump or shoreline instead of getting stuck!
                        if (!_repathedThisTrip && _pathfinder != null &&
                            _pathfinder.FindPath(pos, _destination, _waypoints) && _waypoints.Count > 0)
                        {
                            _repathedThisTrip = true;
                            _waypointIndex = 0;
                            return;
                        }

                        StopMovement();
                        return;
                    }

                    next.y = SampleGroundHeight(next.x, next.z);
                    pos = next;
                    remainingStep -= step;
                    moved = true;

                    if (step >= dist - 0.01f && !isFinalWp)
                        _waypointIndex++;
                }

                if (moved)
                {
                    transform.position = pos;
                    if (lastMoveDir.sqrMagnitude > 1e-6f)
                    {
                        Quaternion targetRot = Quaternion.LookRotation(lastMoveDir, Vector3.up);
                        transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRot, turnSpeedDeg * dt);
                    }

                    if (!_hasActiveAnimator && enableFallbackWalkBob && visualRoot != null)
                    {
                        _walkPhase += dt * walkBobFrequency;
                        float bobY = Mathf.Abs(Mathf.Sin(_walkPhase)) * walkBobAmplitude;
                        visualRoot.localPosition = _visualBaseLocalPos + new Vector3(0f, bobY, 0f);
                    }
                }
                return;
            }

            if (!_hasActiveAnimator && visualRoot != null && (visualRoot.localPosition - _visualBaseLocalPos).sqrMagnitude > 1e-6f)
            {
                visualRoot.localPosition = Vector3.Lerp(visualRoot.localPosition, _visualBaseLocalPos, 1f - Mathf.Exp(-14f * dt));
            }
        }

        private void EnsureRouteLine()
        {
            if (!showRoutePreview || _routeLine != null)
                return;

            GameObject lineGo = new GameObject("WorldMap_RoutePreview");
            lineGo.transform.SetParent(null, false);

            _routeLine = lineGo.AddComponent<LineRenderer>();
            _routeLine.useWorldSpace = true;
            _routeLine.loop = false;
            _routeLine.numCapVertices = 4;
            _routeLine.numCornerVertices = 4;
            _routeLine.startWidth = routeLineWidth;
            _routeLine.endWidth = routeLineWidth * 0.75f;
            _routeLine.shadowCastingMode = ShadowCastingMode.Off;
            _routeLine.receiveShadows = false;
            _routeLine.alignment = LineAlignment.View;
            _routeLine.positionCount = 0;

            Shader unlitShader = Shader.Find("Universal Render Pipeline/Unlit")
                ?? Shader.Find("Sprites/Default")
                ?? Shader.Find("Unlit/Color");

            if (unlitShader != null)
            {
                _routeLineMat = new Material(unlitShader);
                if (_routeLineMat.HasProperty("_Surface"))
                    _routeLineMat.SetFloat("_Surface", 1f); // Transparent on URP Unlit
                if (_routeLineMat.HasProperty("_BaseColor"))
                    _routeLineMat.SetColor("_BaseColor", routeLineColor);
                if (_routeLineMat.HasProperty("_Color"))
                    _routeLineMat.SetColor("_Color", routeLineColor);
                _routeLineMat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                _routeLineMat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                _routeLineMat.SetInt("_ZWrite", 0);
                _routeLineMat.renderQueue = 3010;
                _routeLine.sharedMaterial = _routeLineMat;
            }

            _routeLine.startColor = routeLineColor;
            _routeLine.endColor = new Color(routeLineColor.r, routeLineColor.g, routeLineColor.b, routeLineColor.a * 0.45f);
        }

        private void UpdateRoutePreview()
        {
            if (_routeLine == null)
                return;

            if (!showRoutePreview || !_hasDestination || _waypointIndex >= _waypoints.Count)
            {
                if (_routeLine.positionCount != 0)
                    _routeLine.positionCount = 0;
                return;
            }

            _routeRenderPoints.Clear();
            Vector3 prev = transform.position;
            _routeRenderPoints.Add(new Vector3(prev.x, SampleGroundHeight(prev.x, prev.z) + routeGroundOffset, prev.z));

            // Subdivide long segments slightly so the route line hugs hills and bridge arches cleanly
            const float maxSegLen = 8f;
            for (int i = _waypointIndex; i < _waypoints.Count; i++)
            {
                Vector3 next = _waypoints[i];
                float segDist = Vector2.Distance(new Vector2(prev.x, prev.z), new Vector2(next.x, next.z));
                int subs = Mathf.Clamp(Mathf.CeilToInt(segDist / maxSegLen), 1, 24);
                for (int s = 1; s <= subs; s++)
                {
                    float t = s / (float)subs;
                    float x = Mathf.Lerp(prev.x, next.x, t);
                    float z = Mathf.Lerp(prev.z, next.z, t);
                    float y = SampleGroundHeight(x, z) + routeGroundOffset;
                    _routeRenderPoints.Add(new Vector3(x, y, z));
                }
                prev = next;
            }

            _routeLine.positionCount = _routeRenderPoints.Count;
            for (int i = 0; i < _routeRenderPoints.Count; i++)
                _routeLine.SetPosition(i, _routeRenderPoints[i]);
        }

        private void SyncAnimatorState(bool force)
        {
            if (!_hasActiveAnimator || animator == null)
                return;

            if (!force && _lastAnimatorMoving == _hasDestination)
                return;

            _lastAnimatorMoving = _hasDestination;
            animator.SetBool(IsMovingHash, _hasDestination);
        }

        private bool IsPointWalkable(float x, float z)
        {
            if (WorldMapBridgeZone.TryGetBridgeHeight(x, z, out _))
                return true;

            Terrain terrain = Terrain.activeTerrain;
            if (terrain == null || terrain.terrainData == null)
                return true;

            float y = terrain.SampleHeight(new Vector3(x, 0f, z)) + terrain.transform.position.y;
            if (y < seaLevelY + 0.35f)
                return false;

            TerrainData td = terrain.terrainData;
            float nx = Mathf.Clamp01((x - terrain.transform.position.x) / td.size.x);
            float nz = Mathf.Clamp01((z - terrain.transform.position.z) / td.size.z);
            return td.GetSteepness(nx, nz) <= hardCliffSlopeDeg;
        }

        private static float SampleGroundHeight(float x, float z)
        {
            float terrainY = 0f;
            Terrain terrain = Terrain.activeTerrain;
            if (terrain != null)
                terrainY = terrain.SampleHeight(new Vector3(x, 0f, z)) + terrain.transform.position.y;

            if (WorldMapBridgeZone.TryGetBridgeHeight(x, z, out float bridgeY))
                return Mathf.Max(terrainY, bridgeY);

            return terrainY;
        }

        private void UpdateBannerWind()
        {
            if (bannerClothTransform == null)
                return;

            float speedFactor = _hasDestination ? 1.85f : 1.0f;
            float sway = Mathf.Sin(Time.unscaledTime * bannerWindSpeed * speedFactor) * bannerWindAngleDeg;
            bannerClothTransform.localRotation = _bannerBaseLocalRot * Quaternion.Euler(0f, sway, 0f);
        }
    }
}
