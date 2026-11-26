using System.Collections.Generic;
using UnityEngine;

namespace VRAtis.WorldMap
{
    /// <summary>
    /// Fast, zero-allocation (post-bake) 2D A* pathfinder for StoryWorldMap.
    /// Automatically routes the player commander around rivers, seas, and steep alpine cliffs,
    /// cleanly guiding them down the center of bridges to reach the opposite shore.
    /// </summary>
    public sealed class WorldMapPathfinder
    {
        private const float CellSize = 5.0f;
        private const float DiagCost = 1.41421356f;
        private const float ObstacleClearancePenalty1 = 2.10f;
        private const float ObstacleClearancePenalty2 = 0.75f;
        private const float HillSlopePenaltyStartDeg = 12.5f;

        private readonly float _minX;
        private readonly float _minZ;
        private readonly float _seaLevelY;
        private readonly float _maxPlanSlopeDeg;
        private readonly int _width;
        private readonly int _height;
        private readonly int _totalCells;

        private readonly bool[] _walkable;
        private readonly bool[] _isBridge;
        private readonly float[] _cellCost;

        // Reusable A* search buffers (zero GC per query via incrementing _searchStamp)
        private readonly uint[] _openStamp;
        private readonly uint[] _closedStamp;
        private readonly float[] _gScore;
        private readonly int[] _cameFrom;
        private readonly int[] _heapNodes;
        private readonly float[] _heapF;
        private readonly int[] _heapIndex;
        private int _heapCount;
        private uint _searchStamp;

        private readonly List<Vector3> _rawPoints = new List<Vector3>(256);

        private static readonly int[] NeighborDx = { 1, -1, 0, 0, 1, 1, -1, -1 };
        private static readonly int[] NeighborDz = { 0, 0, 1, -1, 1, -1, 1, -1 };
        private static readonly float[] NeighborCost = { 1f, 1f, 1f, 1f, DiagCost, DiagCost, DiagCost, DiagCost };

        public bool IsBuilt { get; private set; }

        public WorldMapPathfinder(Vector2 boundsMin, Vector2 boundsMax, float seaLevelY, float maxPlanSlopeDeg)
        {
            _minX = boundsMin.x - 15f;
            _minZ = boundsMin.y - 15f;
            float maxX = boundsMax.x + 15f;
            float maxZ = boundsMax.y + 15f;
            _seaLevelY = seaLevelY;
            _maxPlanSlopeDeg = Mathf.Clamp(maxPlanSlopeDeg, 16f, 32f);

            _width = Mathf.CeilToInt((maxX - _minX) / CellSize);
            _height = Mathf.CeilToInt((maxZ - _minZ) / CellSize);
            _totalCells = _width * _height;

            _walkable = new bool[_totalCells];
            _isBridge = new bool[_totalCells];
            _cellCost = new float[_totalCells];

            _openStamp = new uint[_totalCells];
            _closedStamp = new uint[_totalCells];
            _gScore = new float[_totalCells];
            _cameFrom = new int[_totalCells];
            _heapNodes = new int[_totalCells];
            _heapF = new float[_totalCells];
            _heapIndex = new int[_totalCells];
        }

        public void Build(Terrain terrain)
        {
            TerrainData td = terrain != null ? terrain.terrainData : null;
            Vector3 tPos = terrain != null ? terrain.transform.position : Vector3.zero;
            Vector3 tSize = td != null ? td.size : new Vector3(1600f, 155f, 1600f);

            // 1. Bake terrain walkability across the entire playable continent (strict slope check + soft hill-skirt avoidance!)
            for (int iz = 0; iz < _height; iz++)
            {
                float wz = CellToWorldZ(iz);

                for (int ix = 0; ix < _width; ix++)
                {
                    int idx = iz * _width + ix;
                    float wx = CellToWorldX(ix);

                    bool canWalk = true;
                    float slopeCost = 1f;
                    if (td != null)
                    {
                        // Check center + 4 half-cell corners so no sharp micro-ridge inside a 5m cell ever sneaks through
                        float maxCellSlope = 0f;
                        float minCellH = float.MaxValue;
                        for (int s = 0; s < 5; s++)
                        {
                            float ox = s == 1 ? -1.8f : (s == 2 ? 1.8f : 0f);
                            float oz = s == 3 ? -1.8f : (s == 4 ? 1.8f : 0f);
                            float nx = Mathf.Clamp01((wx + ox - tPos.x) / tSize.x);
                            float nz = Mathf.Clamp01((wz + oz - tPos.z) / tSize.z);
                            float h = td.GetInterpolatedHeight(nx, nz) + tPos.y;
                            float sl = td.GetSteepness(nx, nz);
                            if (h < minCellH) minCellH = h;
                            if (sl > maxCellSlope) maxCellSlope = sl;
                        }

                        canWalk = minCellH >= (_seaLevelY + 0.65f) && maxCellSlope <= _maxPlanSlopeDeg;
                        if (canWalk && maxCellSlope > HillSlopePenaltyStartDeg)
                        {
                            // Gently steer armies around hilly flanks into flatter valleys unless crossing a gentle saddle is shorter
                            float slopeT = Mathf.Clamp01((maxCellSlope - HillSlopePenaltyStartDeg) / Mathf.Max(1f, _maxPlanSlopeDeg - HillSlopePenaltyStartDeg));
                            slopeCost = 1f + slopeT * slopeT * 3.2f;
                        }
                    }

                    _walkable[idx] = canWalk;
                    _isBridge[idx] = false;
                    _cellCost[idx] = slopeCost;
                }
            }

            // 2. Stamp active 3D bridges cleanly onto the grid: block side parapets first, then carve dead-center roadway
            var bridges = WorldMapBridgeZone.AllBridges;
            for (int b = 0; b < bridges.Count; b++)
            {
                WorldMapBridgeZone bridge = bridges[b];
                if (bridge == null)
                    continue;

                float parapetHalfLen = Mathf.Max(2f, bridge.HalfLength - 1.0f);
                float corridorHalfW = Mathf.Clamp(bridge.HalfWidth - 4.3f, 1.8f, 3.0f);
                float outerWallW = bridge.HalfWidth + 2.5f;

                // Block stone parapet side strips so routes never approach diagonally across the bridge walls
                for (float lz = -parapetHalfLen; lz <= parapetHalfLen; lz += 1.4f)
                {
                    for (float lx = corridorHalfW + 0.8f; lx <= outerWallW; lx += 1.4f)
                    {
                        for (int side = -1; side <= 1; side += 2)
                        {
                            Vector3 wp = bridge.transform.TransformPoint(new Vector3(lx * side, 0f, lz));
                            if (WorldToCell(wp.x, wp.z, out int cx, out int cz))
                            {
                                int idx = cz * _width + cx;
                                _walkable[idx] = false;
                                _isBridge[idx] = false;
                            }
                        }
                    }
                }

                // Carve dead-center timber roadway + generous approach mouth on both banks
                float halfLen = bridge.HalfLength + 8.0f;
                for (float lz = -halfLen; lz <= halfLen; lz += 1.4f)
                {
                    for (float lx = -corridorHalfW; lx <= corridorHalfW; lx += 1.4f)
                    {
                        Vector3 wp = bridge.transform.TransformPoint(new Vector3(lx, 0f, lz));
                        if (WorldToCell(wp.x, wp.z, out int cx, out int cz))
                        {
                            int idx = cz * _width + cx;
                            _walkable[idx] = true;
                            _isBridge[idx] = true;
                            _cellCost[idx] = 0.80f;
                        }
                    }
                }
            }

            // 3. Add soft shoreline / cliff-edge clearance penalty (except on bridges) so routes stay neatly off wet banks
            for (int iz = 1; iz < _height - 1; iz++)
            {
                for (int ix = 1; ix < _width - 1; ix++)
                {
                    int idx = iz * _width + ix;
                    if (!_walkable[idx] || _isBridge[idx])
                        continue;

                    bool nearBlocked1 =
                        !_walkable[idx - 1] || !_walkable[idx + 1] ||
                        !_walkable[idx - _width] || !_walkable[idx + _width] ||
                        !_walkable[idx - _width - 1] || !_walkable[idx - _width + 1] ||
                        !_walkable[idx + _width - 1] || !_walkable[idx + _width + 1];

                    if (nearBlocked1)
                    {
                        _cellCost[idx] = Mathf.Max(_cellCost[idx], 1f + ObstacleClearancePenalty1);
                    }
                    else if (iz > 1 && iz < _height - 2 && ix > 1 && ix < _width - 2)
                    {
                        bool nearBlocked2 =
                            !_walkable[idx - 2] || !_walkable[idx + 2] ||
                            !_walkable[idx - 2 * _width] || !_walkable[idx + 2 * _width];
                        if (nearBlocked2)
                            _cellCost[idx] = Mathf.Max(_cellCost[idx], 1f + ObstacleClearancePenalty2);
                    }
                }
            }

            IsBuilt = true;
        }

        public bool FindPath(Vector3 startWorld, Vector3 goalWorld, List<Vector3> outWaypoints)
        {
            outWaypoints.Clear();
            if (!IsBuilt)
            {
                outWaypoints.Add(new Vector3(goalWorld.x, 0f, goalWorld.z));
                return true;
            }

            Vector3 startFlat = new Vector3(startWorld.x, 0f, startWorld.z);
            Vector3 goalFlat = new Vector3(goalWorld.x, 0f, goalWorld.z);

            // Fast path: unobstructed walkable straight line -> zero A* needed!
            if (IsWalkableExact(goalFlat.x, goalFlat.z) && HasWalkableLineOfSight(startFlat, goalFlat))
            {
                outWaypoints.Add(goalFlat);
                return true;
            }

            if (!WorldToCellClamped(startFlat.x, startFlat.z, out int sx, out int sz))
                return false;
            if (!WorldToCellClamped(goalFlat.x, goalFlat.z, out int gx, out int gz))
                return false;

            if (!FindNearestWalkableCell(ref sx, ref sz, maxRadius: 24))
                return false;
            if (!FindNearestWalkableCell(ref gx, ref gz, maxRadius: 48))
                return false;

            int startIdx = sz * _width + sx;
            int goalIdx = gz * _width + gx;

            if (startIdx == goalIdx)
            {
                Vector3 snapped = IsWalkableExact(goalFlat.x, goalFlat.z)
                    ? goalFlat
                    : new Vector3(CellToWorldX(gx), 0f, CellToWorldZ(gz));
                outWaypoints.Add(snapped);
                return true;
            }

            // Prepare timestamped A* search
            _searchStamp++;
            if (_searchStamp == 0)
            {
                System.Array.Clear(_openStamp, 0, _totalCells);
                System.Array.Clear(_closedStamp, 0, _totalCells);
                _searchStamp = 1;
            }

            _heapCount = 0;
            _gScore[startIdx] = 0f;
            _cameFrom[startIdx] = -1;
            _openStamp[startIdx] = _searchStamp;
            HeapPush(startIdx, Heuristic(sx, sz, gx, gz));

            bool found = false;
            int bestFallbackIdx = startIdx;
            float bestFallbackH = Heuristic(sx, sz, gx, gz);

            while (_heapCount > 0)
            {
                int current = HeapPop();
                if (current == goalIdx)
                {
                    found = true;
                    break;
                }

                _closedStamp[current] = _searchStamp;
                int cx = current % _width;
                int cz = current / _width;
                float currentG = _gScore[current];

                for (int dir = 0; dir < 8; dir++)
                {
                    int nx = cx + NeighborDx[dir];
                    int nz = cz + NeighborDz[dir];
                    if (nx < 0 || nx >= _width || nz < 0 || nz >= _height)
                        continue;

                    int nIdx = nz * _width + nx;
                    if (!_walkable[nIdx] || _closedStamp[nIdx] == _searchStamp)
                        continue;

                    // Prevent corner cutting diagonally across water or cliff cells
                    if (dir >= 4)
                    {
                        int adj1 = cz * _width + nx;
                        int adj2 = nz * _width + cx;
                        if (!_walkable[adj1] || !_walkable[adj2])
                            continue;
                    }

                    float stepCost = NeighborCost[dir] * _cellCost[nIdx];
                    float tentativeG = currentG + stepCost;

                    bool inOpen = _openStamp[nIdx] == _searchStamp;
                    if (!inOpen || tentativeG < _gScore[nIdx])
                    {
                        _cameFrom[nIdx] = current;
                        _gScore[nIdx] = tentativeG;
                        float h = Heuristic(nx, nz, gx, gz);
                        float f = tentativeG + h;

                        if (h < bestFallbackH)
                        {
                            bestFallbackH = h;
                            bestFallbackIdx = nIdx;
                        }

                        if (!inOpen)
                        {
                            _openStamp[nIdx] = _searchStamp;
                            HeapPush(nIdx, f);
                        }
                        else
                        {
                            HeapDecreaseKey(nIdx, f);
                        }
                    }
                }
            }

            int endIdx = found ? goalIdx : bestFallbackIdx;
            if (endIdx == startIdx)
                return false;

            // Reconstruct cell path from endIdx back to startIdx
            _rawPoints.Clear();
            int trace = endIdx;
            int safety = _totalCells;
            while (trace != -1 && safety-- > 0)
            {
                int tx = trace % _width;
                int tz = trace / _width;
                _rawPoints.Add(new Vector3(CellToWorldX(tx), 0f, CellToWorldZ(tz)));
                if (trace == startIdx)
                    break;
                trace = _cameFrom[trace];
            }

            _rawPoints.Reverse();

            // Replace first/last points with exact world coords when walkable
            if (_rawPoints.Count > 0)
                _rawPoints[0] = startFlat;

            if (found && IsWalkableExact(goalFlat.x, goalFlat.z))
            {
                Vector3 lastCell = _rawPoints[_rawPoints.Count - 1];
                if (HasWalkableLineOfSight(lastCell, goalFlat))
                    _rawPoints[_rawPoints.Count - 1] = goalFlat;
                else
                    _rawPoints.Add(goalFlat);
            }

            // Greedy Line-of-Sight string-pulling to turn jagged 8-way grid steps into sleek Warband routes
            SmoothPathLineOfSight(_rawPoints, outWaypoints);
            return outWaypoints.Count > 0;
        }

        private void SmoothPathLineOfSight(List<Vector3> raw, List<Vector3> smoothed)
        {
            smoothed.Clear();
            if (raw.Count == 0)
                return;

            int anchor = 0;
            int count = raw.Count;

            while (anchor < count - 1)
            {
                int farthest = anchor + 1;
                // Scan backward from the end so straight stretches collapse immediately
                for (int probe = count - 1; probe > anchor + 1; probe--)
                {
                    if (HasWalkableLineOfSight(raw[anchor], raw[probe]))
                    {
                        farthest = probe;
                        break;
                    }
                }

                smoothed.Add(raw[farthest]);
                anchor = farthest;
            }
        }

        public bool HasWalkableLineOfSight(Vector3 from, Vector3 to)
        {
            Vector3 delta = to - from;
            delta.y = 0f;
            float dist = delta.magnitude;
            if (dist < 0.1f)
                return true;

            Vector2 dir = new Vector2(delta.x / dist, delta.z / dist);
            Vector2 perp = new Vector2(-dir.y, dir.x);

            const float stepMeters = 1.65f;
            int steps = Mathf.CeilToInt(dist / stepMeters);
            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)steps;
                float x = from.x + delta.x * t;
                float z = from.z + delta.z * t;

                // Inside a narrow bridge roadway, only require the centerline to stay inside the inner bridge deck
                if (WorldMapBridgeZone.IsInsideAnyBridgeRoadway(x, z, wallMargin: 4.3f))
                    continue;

                if (!IsWalkableExact(x, z))
                    return false;

                // On open terrain, require a small shoulder clearance (+/-2.0m) so string-pulling never clips hill crags or shorelines!
                if (!IsWalkableExact(x + perp.x * 2.0f, z + perp.y * 2.0f) ||
                    !IsWalkableExact(x - perp.x * 2.0f, z - perp.y * 2.0f))
                    return false;
            }

            return true;
        }

        public bool IsWalkableExact(float x, float z)
        {
            if (WorldMapBridgeZone.IsInsideAnyBridgeRoadway(x, z, wallMargin: 4.3f))
                return true;

            if (WorldMapBridgeZone.IsBlockedByAnyBridgeParapet(x, z, wallMargin: 4.3f))
                return false;

            Terrain terrain = Terrain.activeTerrain;
            if (terrain == null || terrain.terrainData == null)
                return true;

            TerrainData td = terrain.terrainData;
            Vector3 tPos = terrain.transform.position;
            float nx = Mathf.Clamp01((x - tPos.x) / td.size.x);
            float nz = Mathf.Clamp01((z - tPos.z) / td.size.z);

            float y = td.GetInterpolatedHeight(nx, nz) + tPos.y;
            if (y < _seaLevelY + 0.60f)
                return false;

            return td.GetSteepness(nx, nz) <= _maxPlanSlopeDeg;
        }

        private bool FindNearestWalkableCell(ref int cx, ref int cz, int maxRadius)
        {
            if (_walkable[cz * _width + cx])
                return true;

            for (int r = 1; r <= maxRadius; r++)
            {
                int bestIdx = -1;
                float bestDistSq = float.MaxValue;

                for (int dz = -r; dz <= r; dz++)
                {
                    int nz = cz + dz;
                    if (nz < 0 || nz >= _height)
                        continue;

                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Mathf.Abs(dx) != r && Mathf.Abs(dz) != r)
                            continue;

                        int nx = cx + dx;
                        if (nx < 0 || nx >= _width)
                            continue;

                        int idx = nz * _width + nx;
                        if (!_walkable[idx])
                            continue;

                        float dSq = dx * dx + dz * dz;
                        if (dSq < bestDistSq)
                        {
                            bestDistSq = dSq;
                            bestIdx = idx;
                        }
                    }
                }

                if (bestIdx != -1)
                {
                    cx = bestIdx % _width;
                    cz = bestIdx / _width;
                    return true;
                }
            }

            return false;
        }

        private static float Heuristic(int x0, int z0, int x1, int z1)
        {
            int dx = Mathf.Abs(x1 - x0);
            int dz = Mathf.Abs(z1 - z0);
            int min = dx < dz ? dx : dz;
            int max = dx > dz ? dx : dz;
            return (max - min) + DiagCost * min;
        }

        private bool WorldToCell(float wx, float wz, out int cx, out int cz)
        {
            cx = Mathf.FloorToInt((wx - _minX) / CellSize);
            cz = Mathf.FloorToInt((wz - _minZ) / CellSize);
            return cx >= 0 && cx < _width && cz >= 0 && cz < _height;
        }

        private bool WorldToCellClamped(float wx, float wz, out int cx, out int cz)
        {
            cx = Mathf.Clamp(Mathf.FloorToInt((wx - _minX) / CellSize), 0, _width - 1);
            cz = Mathf.Clamp(Mathf.FloorToInt((wz - _minZ) / CellSize), 0, _height - 1);
            return true;
        }

        private float CellToWorldX(int cx) => _minX + (cx + 0.5f) * CellSize;
        private float CellToWorldZ(int cz) => _minZ + (cz + 0.5f) * CellSize;

        // Compact binary min-heap for A* open set
        private void HeapPush(int node, float fScore)
        {
            int i = _heapCount++;
            _heapNodes[i] = node;
            _heapF[i] = fScore;
            _heapIndex[node] = i;
            SiftUp(i);
        }

        private int HeapPop()
        {
            int root = _heapNodes[0];
            _heapCount--;
            if (_heapCount > 0)
            {
                int last = _heapNodes[_heapCount];
                _heapNodes[0] = last;
                _heapF[0] = _heapF[_heapCount];
                _heapIndex[last] = 0;
                SiftDown(0);
            }
            return root;
        }

        private void HeapDecreaseKey(int node, float newF)
        {
            int i = _heapIndex[node];
            if (i < _heapCount && _heapNodes[i] == node && newF < _heapF[i])
            {
                _heapF[i] = newF;
                SiftUp(i);
            }
        }

        private void SiftUp(int i)
        {
            int node = _heapNodes[i];
            float f = _heapF[i];
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (_heapF[parent] <= f)
                    break;

                int pNode = _heapNodes[parent];
                _heapNodes[i] = pNode;
                _heapF[i] = _heapF[parent];
                _heapIndex[pNode] = i;
                i = parent;
            }
            _heapNodes[i] = node;
            _heapF[i] = f;
            _heapIndex[node] = i;
        }

        private void SiftDown(int i)
        {
            int node = _heapNodes[i];
            float f = _heapF[i];
            int half = _heapCount >> 1;
            while (i < half)
            {
                int child = (i << 1) + 1;
                int right = child + 1;
                if (right < _heapCount && _heapF[right] < _heapF[child])
                    child = right;

                if (f <= _heapF[child])
                    break;

                int cNode = _heapNodes[child];
                _heapNodes[i] = cNode;
                _heapF[i] = _heapF[child];
                _heapIndex[cNode] = i;
                i = child;
            }
            _heapNodes[i] = node;
            _heapF[i] = f;
            _heapIndex[node] = i;
        }
    }
}
