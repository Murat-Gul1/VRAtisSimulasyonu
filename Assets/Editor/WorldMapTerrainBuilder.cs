using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using VRAtis.WorldMap;

namespace VRAtis.EditorTools
{
    /// <summary>
    /// Generates a Mount & Blade: Warband / Bannerlord-inspired 1600m x 1600m continent Terrain:
    /// - 1025x1025 heightmap & 1024x1024 splatmap for silky-smooth rivers & coastlines (zero jagged teeth)
    /// - Catmull-Rom spline rivers + shoreline Gaussian smoothing + baked Water Depth texture
    /// - Custom animated URP Water Shader (shallow turquoise -> deep navy + animated foam + wave normals)
    /// - High-detail procedural normal-mapped alpine rock, snow, and sand biome textures
    /// - 4 arched 3D stone & timber bridges with WorldMapBridgeZone so the commander crosses every river smoothly
    /// </summary>
    public static class WorldMapTerrainBuilder
    {
        public const float MapSizeMeters = 2400f;
        public const float MapScale = 1.5f;
        public const float MaxTerrainHeight = 155f;
        public const float SeaLevelY = 18.0f;

        private const string TerrainDataPath = "Assets/Materials/WorldMap/WorldMapTerrainData.asset";
        private const string TerrainMatPath = "Assets/Materials/WorldMap/WorldMapTerrain_Mat.mat";
        private const string WaterMatPath = "Assets/Materials/WorldMap/WorldMapWater_Mat.mat";
        private const string WaterNorPath = "Assets/Materials/WorldMap/WorldMapWater_Nor.png";
        private const string WaterDepthPath = "Assets/Materials/WorldMap/WorldMapWaterDepth_Tex.png";

        private const string GrassLayerPath = "Assets/Materials/WorldMap/WorldMapGrass_Lush.terrainlayer";
        private const string SandLayerPath = "Assets/Materials/WorldMap/WorldMapSand_BeachDesert.terrainlayer";
        private const string RockLayerPath = "Assets/Materials/WorldMap/WorldMapMountain_Rock.terrainlayer";
        private const string SnowLayerPath = "Assets/Materials/WorldMap/WorldMapMountain_Snow.terrainlayer";

        private const string ProjectGrassDiffPath = "Assets/Models/Environment/Ground/Textures/grass_ground_diff.jpg";
        private const string ProjectGrassNorPath = "Assets/Models/Environment/Ground/Textures/grass_ground_nor.png";
        private const string SandDiffPath = "Assets/Materials/WorldMap/WorldMapSand_Tex.png";
        private const string SandNorPath = "Assets/Materials/WorldMap/WorldMapSand_Nor.png";
        private const string RockDiffPath = "Assets/Materials/WorldMap/WorldMapRock_Tex.png";
        private const string RockNorPath = "Assets/Materials/WorldMap/WorldMapRock_Nor.png";
        private const string SnowDiffPath = "Assets/Materials/WorldMap/WorldMapSnow_Tex.png";
        private const string SnowNorPath = "Assets/Materials/WorldMap/WorldMapSnow_Nor.png";

        private const string BridgeStoneMatPath = "Assets/Materials/WorldMap/WorldMapBridge_Stone_Mat.mat";
        private const string BridgeWoodMatPath = "Assets/Materials/WorldMap/WorldMapBridge_Wood_Mat.mat";
        private const string EdgeMistMatPath = "Assets/Materials/WorldMap/WorldMapEdgeMist_Mat.mat";
        private const string EdgeMistNoisePath = "Assets/Materials/WorldMap/WorldMapEdgeMist_Noise.png";
        private const string EdgeMistMeshPath = "Assets/Materials/WorldMap/WorldMapEdgeMist_Mesh.asset";
        private const string ForestBarkMatPath = "Assets/Materials/WorldMap/WorldMapForest_Bark_Mat.mat";
        private const string ForestPineMatPath = "Assets/Materials/WorldMap/WorldMapForest_Pine_Mat.mat";
        private const string ForestOakMatPath = "Assets/Materials/WorldMap/WorldMapForest_Oak_Mat.mat";
        private const string ForestOakTexPath = "Assets/Materials/WorldMap/WorldMapOak_Branch.png";
        private const string ProjectAlpinePineFbxPath = "Assets/Models/Environment/Vegetation/AlpinePine.fbx";
        private const string ProjectAlpinePineBranchPath = "Assets/Models/Environment/Vegetation/Textures/AlpinePine_Branch.png";
        private const string ProjectAlpineBarkPath = "Assets/Models/Environment/Vegetation/Textures/AlpineBark.png";
        private const string ForestChunksFolder = "Assets/Materials/WorldMap/ForestChunks";

        private static readonly Vector2[] MainRiverControlPts =
        {
            new Vector2(270f, 892f),
            new Vector2(195f, 645f),
            new Vector2(68f, 412f),
            new Vector2(-142f, 248f),
            new Vector2(-382f, 142f),
            new Vector2(-638f, 52f),
            new Vector2(-878f, -27f),
            new Vector2(-1118f, -82f)
        };

        private static readonly Vector2[] CreekControlPts =
        {
            new Vector2(780f, -345f),
            new Vector2(540f, -282f),
            new Vector2(285f, -237f),
            new Vector2(22f, -202f),
            new Vector2(-232f, -123f),
            new Vector2(-442f, -12f),
            new Vector2(-592f, 69f)
        };

        public static Terrain BuildCalradiaWorldMap(Transform parent)
        {
            EnsureFolder("Assets", "Materials");
            EnsureFolder("Assets/Materials", "WorldMap");

            EnsureBiomeAndWaterTextures();

            // Pre-sample smooth Catmull-Rom spline polylines for razor-smooth, non-jagged rivers
            Vector2[] mainRiverSpline = SampleCatmullRomSpline(MainRiverControlPts, samplesPerSegment: 14);
            Vector2[] creekSpline = SampleCatmullRomSpline(CreekControlPts, samplesPerSegment: 14);

            Texture2D grassDiff = AssetDatabase.LoadAssetAtPath<Texture2D>(ProjectGrassDiffPath);
            Texture2D grassNor = AssetDatabase.LoadAssetAtPath<Texture2D>(ProjectGrassNorPath);
            Texture2D sandDiff = AssetDatabase.LoadAssetAtPath<Texture2D>(SandDiffPath);
            Texture2D sandNor = AssetDatabase.LoadAssetAtPath<Texture2D>(SandNorPath);
            Texture2D rockDiff = AssetDatabase.LoadAssetAtPath<Texture2D>(RockDiffPath);
            Texture2D rockNor = AssetDatabase.LoadAssetAtPath<Texture2D>(RockNorPath);
            Texture2D snowDiff = AssetDatabase.LoadAssetAtPath<Texture2D>(SnowDiffPath);
            Texture2D snowNor = AssetDatabase.LoadAssetAtPath<Texture2D>(SnowNorPath);

            // 4 crisp normal-mapped biome layers
            TerrainLayer grassLayer = LoadOrCreateTerrainLayer(
                GrassLayerPath, grassDiff, grassNor, new Vector2(30f, 30f),
                new Color(0.58f, 0.94f, 0.48f, 1f), normalScale: 0.48f, smoothness: 0.03f);

            TerrainLayer sandLayer = LoadOrCreateTerrainLayer(
                SandLayerPath, sandDiff, sandNor, new Vector2(56f, 56f),
                Color.white, normalScale: 0.42f, smoothness: 0.04f);

            TerrainLayer rockLayer = LoadOrCreateTerrainLayer(
                RockLayerPath, rockDiff, rockNor, new Vector2(54f, 54f),
                Color.white, normalScale: 0.65f, smoothness: 0.02f);

            TerrainLayer snowLayer = LoadOrCreateTerrainLayer(
                SnowLayerPath, snowDiff, snowNor, new Vector2(46f, 46f),
                Color.white, normalScale: 0.38f, smoothness: 0.12f);

            TerrainData terrainData = AssetDatabase.LoadAssetAtPath<TerrainData>(TerrainDataPath);
            if (terrainData == null)
            {
                terrainData = new TerrainData();
                AssetDatabase.CreateAsset(terrainData, TerrainDataPath);
            }

            // 1025x1025 heightmap (~1.56m vertex spacing) for ultra-smooth rivers, beaches & crisp rocky peaks
            const int hmRes = 1025;
            const int alphaRes = 512;
            terrainData.heightmapResolution = hmRes;
            terrainData.alphamapResolution = alphaRes;
            terrainData.baseMapResolution = 1024;
            terrainData.size = new Vector3(MapSizeMeters, MaxTerrainHeight, MapSizeMeters);

            // 1. Sculpt High-Resolution Continent Heightmap
            float[,] worldHeights = new float[hmRes, hmRes];
            float half = MapSizeMeters * 0.5f;

            for (int iz = 0; iz < hmRes; iz++)
            {
                float vz = iz / (float)(hmRes - 1);
                float wz = (vz - 0.5f) * MapSizeMeters;

                for (int ix = 0; ix < hmRes; ix++)
                {
                    float vx = ix / (float)(hmRes - 1);
                    float wx = (vx - 0.5f) * MapSizeMeters;

                    worldHeights[iz, ix] = EvaluateContinentHeight(wx, wz, mainRiverSpline, creekSpline);
                }
            }

            // 2. Smooth riverbanks & coastal transitions (2 passes) to eliminate every trace of diagonal staircase jitter
            SmoothShorelinesAndRiverbanks(worldHeights, hmRes, passes: 2);

            float[,] normHeights = new float[hmRes, hmRes];
            for (int iz = 0; iz < hmRes; iz++)
                for (int ix = 0; ix < hmRes; ix++)
                    normHeights[iz, ix] = Mathf.Clamp01(worldHeights[iz, ix] / MaxTerrainHeight);

            terrainData.SetHeights(0, 0, normHeights);
            terrainData.terrainLayers = new[] { grassLayer, sandLayer, rockLayer, snowLayer };

            // 3. Bake Water Depth Texture directly from smoothed heightmap (zero runtime GPU depth-pass overhead!)
            BakeWaterDepthTexture(worldHeights, hmRes);

            // 4. Paint High-Detail Biome Splatmaps
            float[,,] alphaMap = new float[alphaRes, alphaRes, 4];
            for (int iz = 0; iz < alphaRes; iz++)
            {
                float vz = iz / (float)(alphaRes - 1);
                float wz = (vz - 0.5f) * MapSizeMeters;

                for (int ix = 0; ix < alphaRes; ix++)
                {
                    float vx = ix / (float)(alphaRes - 1);
                    float wx = (vx - 0.5f) * MapSizeMeters;

                    float worldY = terrainData.GetInterpolatedHeight(vx, vz);
                    float steepnessDeg = terrainData.GetSteepness(vx, vz);

                    ComputeBiomeWeights(wx, wz, worldY, steepnessDeg,
                        out float wGrass, out float wSand, out float wRock, out float wSnow);

                    alphaMap[iz, ix, 0] = wGrass;
                    alphaMap[iz, ix, 1] = wSand;
                    alphaMap[iz, ix, 2] = wRock;
                    alphaMap[iz, ix, 3] = wSnow;
                }
            }

            terrainData.SetAlphamaps(0, 0, alphaMap);
            EditorUtility.SetDirty(terrainData);

            // 5. Instantiate Terrain centered at (0, 0, 0)
            Shader terrainShader = Shader.Find("Universal Render Pipeline/Terrain/Lit") ?? Shader.Find("Nature/Terrain/Standard");
            Material terrainMat = LoadOrCreateMaterial(TerrainMatPath, terrainShader);
            EditorUtility.SetDirty(terrainMat);

            GameObject terrainGo = Terrain.CreateTerrainGameObject(terrainData);
            terrainGo.name = "WorldMap_Terrain";
            terrainGo.transform.SetParent(parent, false);
            terrainGo.transform.position = new Vector3(-half, 0f, -half);
            terrainGo.isStatic = true;

            Terrain terrain = terrainGo.GetComponent<Terrain>();
            if (terrain != null)
            {
                terrain.materialTemplate = terrainMat;
                terrain.heightmapPixelError = 4f;
                terrain.basemapDistance = 850f;
                terrain.drawTreesAndFoliage = false;
                terrain.shadowCastingMode = ShadowCastingMode.On;
            }

            // 6. Create Custom Animated URP Sea & River Water Plane
            CreateWorldMapWaterPlane(parent);

            // 7. Build 6 Strategic 3D Arched Bridges across the 2400m continent's rivers!
            List<Vector2> bridgeCenters = BuildRiverBridges(parent, terrain, mainRiverSpline, creekSpline);

            // 8. Build Soft Drifting Alpine Mist Veil conforming smoothly over North & East mountain slopes
            CreateMountainEdgeMist(parent, terrainData);

            // 9. Generate Strategic 5-Faction Settlement Blueprint (5 Capitals + 19 Cities + 29 Military Bases reserved + 154 Linked Villages)
            List<WorldMapSettlementRecord> settlements = WorldMapSettlementBuilder.GenerateStrategicBlueprint(
                parent, terrain, bridgeCenters, mainRiverSpline, creekSpline, SeaLevelY);

            // 10. Build Realistic Biome Forests (10x10 mobile chunks, clearing glades around all 154 villages & 53 reserved hub sites!)
            BuildWarbandForests(parent, terrain, bridgeCenters, settlements);

            // 11. Build ALL 4 Settlement Tiers (5 Monumental Capitals, 14 Cities, 24 Military Bases, 124 Compact Villages)
            WorldMapSettlementBuilder.BuildAllSettlementVisuals(parent, terrain, settlements);

            AssetDatabase.SaveAssets();
            return terrain;
        }

        private static float EvaluateContinentHeight(
            float worldX,
            float worldZ,
            Vector2[] mainRiverSpline,
            Vector2[] creekSpline)
        {
            // Normalize macro continent coordinates by MapScale (1.5x) so all coastlines, mountains, hills & basins scale proportionally to 2400m!
            float wx = worldX / MapScale;
            float wz = worldZ / MapScale;

            // Keep ~85-90% of playable landmass open & conquerable; sea, beaches & mountains frame ONLY the outer edges!
            float coastNoise = (Fbm(wx * 0.0020f + 3.1f, wz * 0.0020f + 1.7f, 3) - 0.5f) * 125f;
            float fineCoast = (Fbm(wx * 0.0052f + 9.2f, wz * 0.0052f + 4.4f, 2) - 0.5f) * 32f;

            // Western coastline pushed out near outer edge with gentle coastal coves & beaches
            float westBay = Mathf.Exp(-Mathf.Pow((wz + 40f) / 220f, 2f)) * 55f;
            float westShoreX = -675f + westBay + coastNoise * 0.65f + fineCoast * 0.45f;
            float distFromWestSea = wx - westShoreX;

            // Southern coastline pushed out near outer edge
            float southCove = Mathf.Exp(-Mathf.Pow((wx + 90f) / 310f, 2f)) * 52f;
            float southShoreZ = -685f + southCove + (Fbm(wx * 0.0022f + 14.1f, wz * 0.0022f + 2.3f, 3) - 0.5f) * 95f;
            float distFromSouthSea = wz - southShoreZ;

            float seaSignedDist = Mathf.Min(distFromWestSea, distFromSouthSea);

            // Smooth golden beach slope from shallow sea (7m) up onto expansive plains (26.5m so basins stay dry above sea!)
            float beachSlopeT = SmoothStep(-65f, 95f, seaSignedDist);
            float baseElevation = Mathf.Lerp(7.0f, 26.5f, beachSlopeT);

            // Compute spline river distances in true 2400m world meters so rivers & bridges keep natural width!
            Vector2 pWorld = new Vector2(worldX, worldZ);
            Vector2 p = new Vector2(wx, wz);
            float mainRiverDist = DistanceToPolyline(pWorld, mainRiverSpline);
            float creekDist = DistanceToPolyline(pWorld, creekSpline);
            float minRiverDist = Mathf.Min(mainRiverDist, creekDist);
            float riverValleyClear = SmoothStep(32f, 92f, minRiverDist);

            // Expansive Southern Desert Plains (Z < -240m — stays wide, open & much flatter than the northern highlands!)
            float desertMask = SmoothStep(-205f, -365f, wz) * SmoothStep(-560f, -320f, wx) * beachSlopeT;
            float duneWaves = (Mathf.Sin((wx * 0.020f + wz * 0.014f) + Fbm(wx * 0.007f, wz * 0.007f, 2) * 2.6f) * 0.5f + 0.5f) * 3.4f;
            float desertFlatDelta = -2.2f * desertMask + duneWaves * desertMask;

            // Organic rolling hills + shallow dry grassy basins (çukurlar & tümsekler) outside the desert
            float reliefAllowance = (1f - desertMask * 0.88f) * beachSlopeT * riverValleyClear;
            float centerFlatten = Mathf.Exp(-(wx * wx + wz * wz) / (115f * 115f));

            float macroRolling = (Fbm(wx * 0.0036f + 5.7f, wz * 0.0036f + 8.3f, 3) - 0.48f) * 18.5f;
            float midHills = (Fbm(wx * 0.0082f + 12.1f, wz * 0.0082f + 3.4f, 3) - 0.50f) * 7.5f;
            float microUndulation = (Fbm(wx * 0.016f + 1.2f, wz * 0.016f + 6.9f, 2) - 0.50f) * 1.8f;

            // Shallow dry grassy basins / valleys (çukur alanlar) creating natural elevation contrast
            float basin1 = GaussianHill(wx, wz, -135f, -55f, 105f, 75f, 0.35f) * -5.8f;
            float basin2 = GaussianHill(wx, wz,  275f, 165f, 115f, 85f, -0.40f) * -6.2f;
            float basin3 = GaussianHill(wx, wz,  -90f, 365f, 100f, 78f, 0.20f) * -5.5f;
            float basin4 = GaussianHill(wx, wz, -435f, -185f, 95f, 75f, -0.25f) * -4.8f;
            float basins = (basin1 + basin2 + basin3 + basin4) * reliefAllowance;

            // 4 Iconic Bannerlord-style "route-around" craggy hills & rolling ridges (domain-warped for organic natural silhouettes!)
            float hWarpX = (Fbm(wx * 0.0085f + 3.1f, wz * 0.0085f + 7.7f, 3) - 0.5f) * 42f;
            float hWarpZ = (Fbm(wx * 0.0085f + 15.4f, wz * 0.0085f + 2.3f, 3) - 0.5f) * 42f;
            float hwx = wx + hWarpX;
            float hwz = wz + hWarpZ;
            float cragNoise = RidgedNoise(wx * 0.011f + 4.4f, wz * 0.011f + 9.8f);
            float hillNoiseMod = 0.72f + cragNoise * 0.44f;

            // 1) Western Watch Hill (elongated organic rocky ridge overlooking west plains)
            float hillWestCrag = Mathf.Pow(GaussianHill(hwx, hwz, -315f, -135f, 88f, 48f, 0.48f), 1.20f) * 33f * hillNoiseMod;
            // 2) Heartland Twin Ridge (broad scenic ridge east of center crossroads)
            float hillHeartland = Mathf.Pow(GaussianHill(hwx, hwz, 175f, -40f, 95f, 52f, -0.52f), 1.15f) * 25f * hillNoiseMod;
            // 3) Northern Crag Butte (forested rocky outcrop north of main river)
            float hillNorthButte = Mathf.Pow(GaussianHill(hwx, hwz, -235f, 305f, 86f, 52f, 0.65f), 1.20f) * 34f * hillNoiseMod;
            // 4) Northwest Saddle Knoll (gentle rolling hill above the western bridge)
            float hillWestSaddle = Mathf.Pow(GaussianHill(hwx, hwz, -285f, 200f, 78f, 48f, -0.35f), 1.15f) * 22f * hillNoiseMod;
            // 5) Central-East Rolling Mound (organic knoll between main river & east hills)
            float hillCentralEast = Mathf.Pow(GaussianHill(hwx, hwz, 290f, 40f, 76f, 48f, 0.30f), 1.15f) * 21f * hillNoiseMod;

            float landmarkHills = (hillWestCrag + hillHeartland + hillNorthButte + hillWestSaddle + hillCentralEast) * reliefAllowance;

            // Compact Natural Mountain Rim ONLY along North & East outer edges (fading into alpine mist!)
            float mtnWarp = (Fbm(wx * 0.0028f + 21.4f, wz * 0.0028f + 11.8f, 3) - 0.5f) * 115f;
            float northWallMask = SmoothStep(545f + mtnWarp * 0.65f, 755f + mtnWarp * 0.30f, wz);
            float eastWallMask = SmoothStep(555f + mtnWarp * 0.65f, 760f + mtnWarp * 0.30f, wx);

            // 3 Natural Mountain Foothill Spurs merging smoothly from the North & East mountains into the plains (never blocking!)
            float spurNorth = SegmentRidgeMask(p, new Vector2(10f, 610f), new Vector2(-15f, 425f), halfWidth: 82f) * 0.62f;
            float spurNE = SegmentRidgeMask(p, new Vector2(580f, 470f), new Vector2(365f, 295f), halfWidth: 92f) * 0.64f;
            float spurEast = SegmentRidgeMask(p, new Vector2(590f, -75f), new Vector2(415f, -110f), halfWidth: 78f) * 0.56f;
            float foothillSpurs = Mathf.Clamp01(Mathf.Max(spurNorth, Mathf.Max(spurNE, spurEast))) * riverValleyClear * (1f - desertMask * 0.75f);

            // Small natural rocky headland on far Northwest corner where mountains meet the sea
            float nwCornerMask = SmoothStep(460f, 720f, wz) * SmoothStep(-430f, -690f, wx) * 0.72f;

            float combinedMtnMask = Mathf.Clamp01(Mathf.Max(Mathf.Max(northWallMask, eastWallMask), Mathf.Max(nwCornerMask, foothillSpurs)));

            // 4-octave craggy alpine peaks
            float ridge1 = RidgedNoise(wx * 0.0062f + 2.1f, wz * 0.0062f + 7.4f);
            float ridge2 = RidgedNoise(wx * 0.0140f + 13.3f, wz * 0.0140f + 3.9f);
            float ridge3 = RidgedNoise(wx * 0.0320f + 6.8f, wz * 0.0320f + 19.2f);
            float ridge4 = RidgedNoise(wx * 0.0650f + 11.4f, wz * 0.0650f + 4.7f);
            float alpineShape = ridge1 * 0.50f + ridge2 * 0.28f + ridge3 * 0.14f + ridge4 * 0.08f;

            float mountainElevation = Mathf.Pow(combinedMtnMask, 1.25f) * (30f + alpineShape * 96f);
            float outerRimWall = Mathf.Max(SmoothStep(705f, 798f, wz), SmoothStep(705f, 798f, wx)) * 46f;

            float landDetail = (macroRolling + midHills + microUndulation) * reliefAllowance * (1f - centerFlatten * 0.72f);
            float height = baseElevation + landDetail + basins + landmarkHills + desertFlatDelta + mountainElevation + outerRimWall;

            // Keep dry inland basins safely above sea level so no accidental puddles form away from rivers/sea
            if (seaSignedDist > 42f && minRiverDist > 36f)
                height = Mathf.Max(height, SeaLevelY + 3.2f);

            // Silky-smooth spline river carving
            float mainRiverHalfWidth = Mathf.Lerp(10f, 22f, SmoothStep(540f, -660f, wx));
            float mainRiverCut = 1f - SmoothStep(mainRiverHalfWidth * 0.55f, mainRiverHalfWidth * 2.50f, mainRiverDist);

            float creekHalfWidth = Mathf.Lerp(8f, 12.5f, SmoothStep(480f, -380f, wx));
            float creekCut = 1f - SmoothStep(creekHalfWidth * 0.55f, creekHalfWidth * 2.50f, creekDist);

            float riverFactor = Mathf.Clamp01(Mathf.Max(mainRiverCut, creekCut));
            if (riverFactor > 0.001f && combinedMtnMask < 0.55f)
            {
                float riverBedY = SeaLevelY - 3.0f;
                float riverBlend = riverFactor * (1f - SmoothStep(0.28f, 0.55f, combinedMtnMask));
                height = Mathf.Lerp(height, riverBedY, riverBlend);
            }

            return Mathf.Clamp(height, 3.0f, MaxTerrainHeight - 2f);
        }

        private static float GaussianHill(float wx, float wz, float cx, float cz, float rx, float rz, float angleRad)
        {
            float dx = wx - cx;
            float dz = wz - cz;
            float cos = Mathf.Cos(angleRad);
            float sin = Mathf.Sin(angleRad);
            float lx = (dx * cos + dz * sin) / Mathf.Max(1f, rx);
            float lz = (-dx * sin + dz * cos) / Mathf.Max(1f, rz);
            return Mathf.Exp(-(lx * lx + lz * lz));
        }

        private static float SegmentRidgeMask(Vector2 p, Vector2 a, Vector2 b, float halfWidth)
        {
            Vector2 ab = b - a;
            float lenSq = Mathf.Max(1f, ab.sqrMagnitude);
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq);
            Vector2 closest = a + ab * t;
            float dist = Vector2.Distance(p, closest);

            // Taper ridge gently from mountain root (t=0) down into plains (t=1)
            float widthAtT = Mathf.Lerp(halfWidth * 1.15f, halfWidth * 0.62f, t);
            float profile = 1f - SmoothStep(widthAtT * 0.18f, widthAtT, dist);
            float lengthwiseTaper = Mathf.Lerp(1.0f, 0.22f, t);
            return profile * lengthwiseTaper;
        }

        private static void SmoothShorelinesAndRiverbanks(float[,] heights, int res, int passes)
        {
            float[,] temp = new float[res, res];

            for (int pass = 0; pass < passes; pass++)
            {
                for (int z = 1; z < res - 1; z++)
                {
                    for (int x = 1; x < res - 1; x++)
                    {
                        float h = heights[z, x];
                        // Smooth strongly around shoreline/riverbanks (Y in 12..27m) while keeping high mountain peaks sharp!
                        float distFromWater = Mathf.Abs(h - SeaLevelY);
                        float smoothWeight = 1f - SmoothStep(4.5f, 10.5f, distFromWater);
                        if (smoothWeight <= 0.001f)
                        {
                            temp[z, x] = h;
                            continue;
                        }

                        // 3x3 Gaussian kernel
                        float avg = (
                            heights[z - 1, x - 1] + 2f * heights[z - 1, x] + heights[z - 1, x + 1] +
                            2f * heights[z, x - 1] + 4f * h + 2f * heights[z, x + 1] +
                            heights[z + 1, x - 1] + 2f * heights[z + 1, x] + heights[z + 1, x + 1]
                        ) * 0.0625f;

                        temp[z, x] = Mathf.Lerp(h, avg, smoothWeight * 0.85f);
                    }
                }

                for (int z = 1; z < res - 1; z++)
                    for (int x = 1; x < res - 1; x++)
                        heights[z, x] = temp[z, x];
            }
        }

        private static void BakeWaterDepthTexture(float[,] worldHeights, int hmRes)
        {
            const int texSize = 512;
            Texture2D depthTex = new Texture2D(texSize, texSize, TextureFormat.RGBA32, false);
            Color[] pixels = new Color[texSize * texSize];

            for (int y = 0; y < texSize; y++)
            {
                float fy = (y / (float)(texSize - 1)) * (hmRes - 1);
                int iz = Mathf.Clamp(Mathf.RoundToInt(fy), 0, hmRes - 1);
                for (int x = 0; x < texSize; x++)
                {
                    float fx = (x / (float)(texSize - 1)) * (hmRes - 1);
                    int ix = Mathf.Clamp(Mathf.RoundToInt(fx), 0, hmRes - 1);

                    float worldY = worldHeights[iz, ix];
                    float waterDepthMeters = Mathf.Max(0f, SeaLevelY - worldY);
                    float dNorm = Mathf.Clamp01(waterDepthMeters / 9.5f);
                    pixels[y * texSize + x] = new Color(dNorm, dNorm, dNorm, 1f);
                }
            }

            depthTex.SetPixels(pixels);
            depthTex.Apply();
            File.WriteAllBytes(WaterDepthPath, depthTex.EncodeToPNG());
            Object.DestroyImmediate(depthTex);
            AssetDatabase.ImportAsset(WaterDepthPath, ImportAssetOptions.ForceUpdate);

            TextureImporter imp = AssetImporter.GetAtPath(WaterDepthPath) as TextureImporter;
            if (imp != null)
            {
                imp.sRGBTexture = false;
                imp.wrapMode = TextureWrapMode.Clamp;
                imp.filterMode = FilterMode.Bilinear;
                imp.mipmapEnabled = false;
                imp.SaveAndReimport();
            }
        }

        private static List<Vector2> BuildRiverBridges(
            Transform parent,
            Terrain terrain,
            Vector2[] mainRiverSpline,
            Vector2[] creekSpline)
        {
            GameObject bridgesRoot = new GameObject("WorldMap_Bridges");
            bridgesRoot.transform.SetParent(parent, false);

            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            Material stoneMat = LoadOrCreateMaterial(BridgeStoneMatPath, urpLit);
            stoneMat.SetColor("_BaseColor", new Color(0.54f, 0.52f, 0.48f, 1f));
            stoneMat.SetFloat("_Smoothness", 0.08f);
            stoneMat.SetFloat("_Metallic", 0.02f);
            EditorUtility.SetDirty(stoneMat);

            Material woodMat = LoadOrCreateMaterial(BridgeWoodMatPath, urpLit);
            woodMat.SetColor("_BaseColor", new Color(0.42f, 0.30f, 0.19f, 1f));
            woodMat.SetFloat("_Smoothness", 0.10f);
            woodMat.SetFloat("_Metallic", 0f);
            EditorUtility.SetDirty(woodMat);

            // 6 strategic crossings across the 2400m continent so armies & reinforcements reach every frontier smoothly!
            var centers = new List<Vector2>(6);
            centers.Add(CreateSingleArchedBridge(bridgesRoot.transform, "Bridge_NorthEast",     mainRiverSpline, 0.22f, width: 12.0f, terrain, stoneMat, woodMat));
            centers.Add(CreateSingleArchedBridge(bridgesRoot.transform, "Bridge_NorthCentral",  mainRiverSpline, 0.40f, width: 12.5f, terrain, stoneMat, woodMat));
            centers.Add(CreateSingleArchedBridge(bridgesRoot.transform, "Bridge_WestHeartland", mainRiverSpline, 0.60f, width: 12.5f, terrain, stoneMat, woodMat));
            centers.Add(CreateSingleArchedBridge(bridgesRoot.transform, "Bridge_WestDelta",     mainRiverSpline, 0.78f, width: 12.5f, terrain, stoneMat, woodMat));
            centers.Add(CreateSingleArchedBridge(bridgesRoot.transform, "Bridge_SouthDesert",   creekSpline,     0.52f, width: 12.0f, terrain, stoneMat, woodMat));
            centers.Add(CreateSingleArchedBridge(bridgesRoot.transform, "Bridge_EastFoothills", creekSpline,     0.22f, width: 12.0f, terrain, stoneMat, woodMat));
            return centers;
        }

        private static Vector2 CreateSingleArchedBridge(
            Transform parent,
            string bridgeName,
            Vector2[] spline,
            float tNormalized,
            float width,
            Terrain terrain,
            Material stoneMat,
            Material woodMat)
        {
            int idx = Mathf.Clamp(Mathf.RoundToInt(tNormalized * (spline.Length - 1)), 1, spline.Length - 2);
            Vector2 center2D = spline[idx];
            Vector2 riverDir = (spline[idx + 1] - spline[idx - 1]).normalized;
            Vector2 crossDir = new Vector2(-riverDir.y, riverDir.x); // perpendicular across the river!

            // Measure exact distance along crossDir on both sides until terrain rises cleanly onto walkable land (SeaLevelY + 1.4m)
            float posExtent = MeasureDistanceToSolidBank(terrain, center2D, crossDir, maxSearch: 55f);
            float negExtent = MeasureDistanceToSolidBank(terrain, center2D, -crossDir, maxSearch: 55f);

            // Recenter bridge midpoint between the two solid banks + add generous approach overlap on each bank
            Vector2 adjustedCenter = center2D + crossDir * ((posExtent - negExtent) * 0.5f);
            float totalLength = Mathf.Clamp(posExtent + negExtent + 14f, 34f, 96f);

            float bankPosY = SampleTerrainY(terrain, adjustedCenter + crossDir * (totalLength * 0.48f));
            float bankNegY = SampleTerrainY(terrain, adjustedCenter - crossDir * (totalLength * 0.48f));
            float deckBaseY = Mathf.Max(SeaLevelY + 1.25f, Mathf.Min(bankPosY, bankNegY) - 0.15f);
            float archHeight = 1.9f;

            GameObject bridgeGo = new GameObject(bridgeName);
            bridgeGo.transform.SetParent(parent, false);
            bridgeGo.transform.position = new Vector3(adjustedCenter.x, deckBaseY, adjustedCenter.y);
            bridgeGo.transform.rotation = Quaternion.LookRotation(new Vector3(crossDir.x, 0f, crossDir.y), Vector3.up);

            // Walkable runtime bridge zone (slightly wider than deck for effortless navigation)
            WorldMapBridgeZone zone = bridgeGo.AddComponent<WorldMapBridgeZone>();
            zone.Configure(totalLength + 6f, width + 3f, deckBaseY + 0.35f, archHeight);

            // Build arched segmented deck + stone parapets + pillars into riverbed
            const int segments = 9;
            float segLen = totalLength / segments;
            float halfLen = totalLength * 0.5f;

            for (int s = 0; s < segments; s++)
            {
                float z0 = -halfLen + s * segLen;
                float z1 = z0 + segLen;
                float zMid = (z0 + z1) * 0.5f;

                float u0 = Mathf.Clamp01(Mathf.Abs(z0) / halfLen);
                float u1 = Mathf.Clamp01(Mathf.Abs(z1) / halfLen);
                float y0 = archHeight * (1f - u0 * u0);
                float y1 = archHeight * (1f - u1 * u1);
                float yMid = (y0 + y1) * 0.5f;
                float slopeDeg = -Mathf.Atan2(y1 - y0, segLen) * Mathf.Rad2Deg;

                // Timber plank roadway segment
                GameObject deckSeg = CreateBridgePart($"Deck_{s}", bridgeGo.transform, woodMat,
                    new Vector3(0f, yMid, zMid),
                    Quaternion.Euler(slopeDeg, 0f, 0f),
                    new Vector3(width - 1.6f, 0.7f, segLen + 0.35f));

                // Left & Right Stone Parapet walls
                float wallX = (width * 0.5f) - 0.55f;
                CreateBridgePart($"WallL_{s}", bridgeGo.transform, stoneMat,
                    new Vector3(-wallX, yMid + 0.75f, zMid),
                    Quaternion.Euler(slopeDeg, 0f, 0f),
                    new Vector3(1.1f, 1.55f, segLen + 0.30f));

                CreateBridgePart($"WallR_{s}", bridgeGo.transform, stoneMat,
                    new Vector3(wallX, yMid + 0.75f, zMid),
                    Quaternion.Euler(slopeDeg, 0f, 0f),
                    new Vector3(1.1f, 1.55f, segLen + 0.30f));

                // Stone support piers standing into riverbed every 2nd segment
                if (s == 2 || s == 4 || s == 6)
                {
                    CreateBridgePart($"Pier_{s}", bridgeGo.transform, stoneMat,
                        new Vector3(0f, yMid - 3.6f, zMid),
                        Quaternion.identity,
                        new Vector3(width + 0.4f, 7.2f, 2.4f));
                }
            }

            // 4 Corner Entrance Stone Bollards (classic Warband bridge silhouette from above!)
            float postX = (width * 0.5f) - 0.35f;
            float postZ = halfLen - 0.5f;
            CreateBridgePart("Post_FL", bridgeGo.transform, stoneMat, new Vector3(-postX, 1.3f, postZ), Quaternion.identity, new Vector3(1.8f, 2.8f, 1.8f));
            CreateBridgePart("Post_FR", bridgeGo.transform, stoneMat, new Vector3(postX, 1.3f, postZ), Quaternion.identity, new Vector3(1.8f, 2.8f, 1.8f));
            CreateBridgePart("Post_BL", bridgeGo.transform, stoneMat, new Vector3(-postX, 1.3f, -postZ), Quaternion.identity, new Vector3(1.8f, 2.8f, 1.8f));
            CreateBridgePart("Post_BR", bridgeGo.transform, stoneMat, new Vector3(postX, 1.3f, -postZ), Quaternion.identity, new Vector3(1.8f, 2.8f, 1.8f));
            return adjustedCenter;
        }

        private static GameObject CreateBridgePart(string name, Transform parent, Material mat, Vector3 localPos, Quaternion localRot, Vector3 localScale)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = localRot;
            go.transform.localScale = localScale;
            go.isStatic = true;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            MeshRenderer mr = go.GetComponent<MeshRenderer>();
            if (mr != null && mat != null)
            {
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = ShadowCastingMode.On;
            }
            return go;
        }

        private static float MeasureDistanceToSolidBank(Terrain terrain, Vector2 origin, Vector2 dir, float maxSearch)
        {
            for (float d = 4f; d <= maxSearch; d += 2f)
            {
                Vector2 p = origin + dir * d;
                float y = SampleTerrainY(terrain, p);
                if (y >= SeaLevelY + 1.6f)
                    return d;
            }
            return 22f;
        }

        private static float SampleTerrainY(Terrain terrain, Vector2 worldXZ)
        {
            if (terrain == null)
                return SeaLevelY + 2f;

            return terrain.SampleHeight(new Vector3(worldXZ.x, 0f, worldXZ.y)) + terrain.transform.position.y;
        }

        private static void ComputeBiomeWeights(
            float worldX,
            float worldZ,
            float worldY,
            float steepnessDeg,
            out float wGrass,
            out float wSand,
            out float wRock,
            out float wSnow)
        {
            float wx = worldX / MapScale;
            float wz = worldZ / MapScale;
            float biomeNoise = (Fbm(wx * 0.0045f + 1.9f, wz * 0.0045f + 5.2f, 3) - 0.5f) * 130f;

            // 1. Beach & Riverbank Sand
            float beachSand = 1f - SmoothStep(SeaLevelY + 0.7f, SeaLevelY + 3.4f, worldY);

            // 2. Southern Warm Desert Region (starts around Z < -235m)
            float desertRegion = SmoothStep(-215f + biomeNoise, -365f + biomeNoise, wz) *
                                 SmoothStep(-580f, -340f, wx);

            float sandRaw = Mathf.Clamp01(Mathf.Max(beachSand, desertRegion));

            // 3. Steep Mountain Rock (rock shows cleanly on steep cliff faces & high alpine ridges while gentle hilltops stay grassy!)
            float highlandMask = SmoothStep(29f, 40f, worldY);
            float slopeRock = SmoothStep(18f, 31f, steepnessDeg) * highlandMask;
            float highRock = SmoothStep(56f, 76f, worldY);
            float rockRaw = Mathf.Clamp01(Mathf.Max(slopeRock, highRock));

            // 4. High Alpine Snow Peaks
            float snowRaw = SmoothStep(72f + biomeNoise * 0.08f, 94f + biomeNoise * 0.08f, worldY) *
                            (1f - SmoothStep(38f, 56f, steepnessDeg) * 0.58f);

            wSnow = Mathf.Clamp01(snowRaw);
            float rem = 1f - wSnow;

            wRock = Mathf.Clamp01(rockRaw * rem);
            rem -= wRock;

            wSand = Mathf.Clamp01(sandRaw * rem);
            rem -= wSand;

            wGrass = Mathf.Max(0f, rem);

            float sum = wGrass + wSand + wRock + wSnow;
            if (sum > 0.0001f)
            {
                wGrass /= sum;
                wSand /= sum;
                wRock /= sum;
                wSnow /= sum;
            }
            else
            {
                wGrass = 1f;
                wSand = wRock = wSnow = 0f;
            }
        }

        private static void CreateWorldMapWaterPlane(Transform parent)
        {
            Texture2D waveNor = AssetDatabase.LoadAssetAtPath<Texture2D>(WaterNorPath);
            Texture2D depthTex = AssetDatabase.LoadAssetAtPath<Texture2D>(WaterDepthPath);

            Shader waterShader = Shader.Find("VRAtis/WorldMapWaterURP")
                ?? Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Standard");

            Material waterMat = LoadOrCreateMaterial(WaterMatPath, waterShader);
            if (waterMat.HasProperty("_WaveNormalMap"))
                waterMat.SetTexture("_WaveNormalMap", waveNor);
            if (waterMat.HasProperty("_DepthMap"))
                waterMat.SetTexture("_DepthMap", depthTex);
            if (waterMat.HasProperty("_MapOriginXZ"))
                waterMat.SetVector("_MapOriginXZ", new Vector4(-MapSizeMeters * 0.5f, -MapSizeMeters * 0.5f, 0f, 0f));
            if (waterMat.HasProperty("_MapSize"))
                waterMat.SetFloat("_MapSize", MapSizeMeters);
            EditorUtility.SetDirty(waterMat);

            GameObject waterGo = GameObject.CreatePrimitive(PrimitiveType.Plane);
            waterGo.name = "WorldMap_SeaAndRivers";
            waterGo.transform.SetParent(parent, false);
            waterGo.transform.position = new Vector3(0f, SeaLevelY, 0f);
            waterGo.transform.localScale = new Vector3(540f, 1f, 540f);
            waterGo.isStatic = true;
            Object.DestroyImmediate(waterGo.GetComponent<Collider>());

            MeshRenderer mr = waterGo.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                mr.sharedMaterial = waterMat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = true;
            }
        }

        private static void CreateMountainEdgeMist(Transform parent, TerrainData terrainData)
        {
            Texture2D mistNoise = AssetDatabase.LoadAssetAtPath<Texture2D>(EdgeMistNoisePath);
            Shader mistShader = Shader.Find("VRAtis/WorldMapEdgeMistURP");
            if (mistShader == null)
            {
                Debug.LogWarning("[WorldMapTerrainBuilder] VRAtis/WorldMapEdgeMistURP shader bulunamadı.");
                return;
            }

            Material mistMat = LoadOrCreateMaterial(EdgeMistMatPath, mistShader);
            mistMat.SetTexture("_NoiseTex", mistNoise);
            mistMat.SetFloat("_NorthStartZ", 700f);
            mistMat.SetFloat("_NorthFullZ", 1005f);
            mistMat.SetFloat("_EastStartX", 715f);
            mistMat.SetFloat("_EastFullX", 1020f);
            mistMat.SetFloat("_MaxOpacity", 0.96f);
            EditorUtility.SetDirty(mistMat);

            Mesh mistMesh = BuildTerrainConformingMistMesh(terrainData);

            // Two softly offset terrain-conforming cloud blankets (zero mountain peak clipping seams, only 2 draw calls!)
            float[] yOffsets = { 0f, 18f };
            for (int i = 0; i < yOffsets.Length; i++)
            {
                GameObject mistLayer = new GameObject($"WorldMap_MountainMist_{i + 1}");
                mistLayer.transform.SetParent(parent, false);
                mistLayer.transform.localPosition = new Vector3(0f, yOffsets[i], 0f);
                mistLayer.isStatic = true;

                MeshFilter mf = mistLayer.AddComponent<MeshFilter>();
                mf.sharedMesh = mistMesh;

                MeshRenderer mr = mistLayer.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mistMat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }
        }

        private static Mesh BuildTerrainConformingMistMesh(TerrainData terrainData)
        {
            const int grid = 64;
            const float minCoord = -1260f;
            const float maxCoord = 2150f;
            const float halfMap = MapSizeMeters * 0.5f;

            int vPerSide = grid + 1;
            float[,] minClearY = new float[vPerSide, vPerSide];
            float[,] smoothY = new float[vPerSide, vPerSide];

            for (int iz = 0; iz < vPerSide; iz++)
            {
                float wz = Mathf.Lerp(minCoord, maxCoord, iz / (float)grid);
                for (int ix = 0; ix < vPerSide; ix++)
                {
                    float wx = Mathf.Lerp(minCoord, maxCoord, ix / (float)grid);

                    // Sample max terrain height in a 3x3 neighborhood so sharp peaks never poke through between vertices
                    float peakY = 26f;
                    for (int oz = -1; oz <= 1; oz++)
                    {
                        for (int ox = -1; ox <= 1; ox++)
                        {
                            float sx = wx + ox * 24f;
                            float sz = wz + oz * 24f;
                            float u = Mathf.Clamp01((sx + halfMap) / MapSizeMeters);
                            float v = Mathf.Clamp01((sz + halfMap) / MapSizeMeters);
                            float h = terrainData.GetInterpolatedHeight(u, v);
                            if (h > peakY) peakY = h;
                        }
                    }

                    float y = Mathf.Max(peakY + 14f, 38f);
                    minClearY[iz, ix] = y;
                    smoothY[iz, ix] = y;
                }
            }

            // Smooth canopy slightly while preserving minimum peak clearance
            float[,] tmp = new float[vPerSide, vPerSide];
            for (int pass = 0; pass < 2; pass++)
            {
                for (int iz = 0; iz < vPerSide; iz++)
                {
                    for (int ix = 0; ix < vPerSide; ix++)
                    {
                        if (iz == 0 || iz == vPerSide - 1 || ix == 0 || ix == vPerSide - 1)
                        {
                            tmp[iz, ix] = smoothY[iz, ix];
                            continue;
                        }

                        float avg = (smoothY[iz, ix] * 4f +
                                     smoothY[iz - 1, ix] + smoothY[iz + 1, ix] +
                                     smoothY[iz, ix - 1] + smoothY[iz, ix + 1]) * 0.125f;
                        tmp[iz, ix] = Mathf.Max(minClearY[iz, ix], avg);
                    }
                }

                for (int iz = 0; iz < vPerSide; iz++)
                    for (int ix = 0; ix < vPerSide; ix++)
                        smoothY[iz, ix] = tmp[iz, ix];
            }

            Vector3[] verts = new Vector3[vPerSide * vPerSide];
            Vector2[] uvs = new Vector2[vPerSide * vPerSide];
            int[] tris = new int[grid * grid * 6];

            int vi = 0;
            for (int iz = 0; iz < vPerSide; iz++)
            {
                float tz = iz / (float)grid;
                float wz = Mathf.Lerp(minCoord, maxCoord, tz);
                for (int ix = 0; ix < vPerSide; ix++)
                {
                    float tx = ix / (float)grid;
                    float wx = Mathf.Lerp(minCoord, maxCoord, tx);
                    verts[vi] = new Vector3(wx, smoothY[iz, ix], wz);
                    uvs[vi] = new Vector2(tx, tz);
                    vi++;
                }
            }

            int ti = 0;
            for (int iz = 0; iz < grid; iz++)
            {
                for (int ix = 0; ix < grid; ix++)
                {
                    int i0 = iz * vPerSide + ix;
                    int i1 = i0 + 1;
                    int i2 = i0 + vPerSide;
                    int i3 = i2 + 1;

                    tris[ti++] = i0;
                    tris[ti++] = i2;
                    tris[ti++] = i1;
                    tris[ti++] = i1;
                    tris[ti++] = i2;
                    tris[ti++] = i3;
                }
            }

            Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(EdgeMistMeshPath);
            if (mesh == null)
            {
                mesh = new Mesh { name = "WorldMap_MountainMistMesh" };
                AssetDatabase.CreateAsset(mesh, EdgeMistMeshPath);
            }
            else
            {
                mesh.Clear();
            }

            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            EditorUtility.SetDirty(mesh);
            return mesh;
        }

        private sealed class ForestChunkBuilder
        {
            public readonly List<Vector3> Verts = new List<Vector3>(8192);
            public readonly List<Vector3> Normals = new List<Vector3>(8192);
            public readonly List<Vector2> Uvs = new List<Vector2>(8192);
            public readonly List<Color> Colors = new List<Color>(8192);
            public readonly List<int> BarkTris = new List<int>(4096);
            public readonly List<int> PineTris = new List<int>(8192);
            public readonly List<int> OakTris = new List<int>(8192);
        }

        private static void BuildWarbandForests(
            Transform parent,
            Terrain terrain,
            List<Vector2> bridgeCenters,
            List<WorldMapSettlementRecord> settlements)
        {
            if (terrain == null || terrain.terrainData == null)
                return;

            EnsureFolder("Assets/Materials/WorldMap", "ForestChunks");

            Shader forestShader = Shader.Find("VRAtis/WorldMapForestURP")
                ?? Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Standard");

            Texture2D barkTex = AssetDatabase.LoadAssetAtPath<Texture2D>(ProjectAlpineBarkPath);
            Texture2D pineBranchTex = AssetDatabase.LoadAssetAtPath<Texture2D>(ProjectAlpinePineBranchPath);
            Texture2D oakBranchTex = AssetDatabase.LoadAssetAtPath<Texture2D>(ForestOakTexPath);

            // 3 shared mobile URP materials across the ENTIRE continent (low draw calls, zero plastic sheen!)
            Material barkMat = LoadOrCreateMaterial(ForestBarkMatPath, forestShader);
            barkMat.SetTexture("_BaseMap", barkTex);
            barkMat.SetColor("_BaseColor", new Color(0.88f, 0.84f, 0.80f, 1f));
            barkMat.SetFloat("_Cutoff", 0f);
            barkMat.SetFloat("_SunBoost", 0.92f);
            barkMat.SetFloat("_AmbientStrength", 0.34f);
            barkMat.SetFloat("_CullMode", 2f); // Back
            EditorUtility.SetDirty(barkMat);

            Material pineMat = LoadOrCreateMaterial(ForestPineMatPath, forestShader);
            pineMat.SetTexture("_BaseMap", pineBranchTex);
            pineMat.SetColor("_BaseColor", new Color(0.92f, 0.96f, 0.90f, 1f));
            pineMat.SetFloat("_Cutoff", 0.34f);
            pineMat.SetFloat("_SunBoost", 0.96f);
            pineMat.SetFloat("_AmbientStrength", 0.38f);
            pineMat.SetFloat("_CullMode", 0f); // Double-sided branches
            EditorUtility.SetDirty(pineMat);

            Material oakMat = LoadOrCreateMaterial(ForestOakMatPath, forestShader);
            oakMat.SetTexture("_BaseMap", oakBranchTex);
            oakMat.SetColor("_BaseColor", new Color(0.96f, 0.98f, 0.92f, 1f));
            oakMat.SetFloat("_Cutoff", 0.36f);
            oakMat.SetFloat("_SunBoost", 0.98f);
            oakMat.SetFloat("_AmbientStrength", 0.38f);
            oakMat.SetFloat("_CullMode", 0f); // Double-sided leaf boughs
            EditorUtility.SetDirty(oakMat);

            // Load real AlpinePine.fbx meshes (only 124 tris per realistic 3D pine tree!)
            Mesh pineTrunkMesh = null;
            Mesh pineBoughsMesh = null;
            GameObject pinePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ProjectAlpinePineFbxPath);
            if (pinePrefab != null)
            {
                foreach (MeshFilter mf in pinePrefab.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf.sharedMesh == null) continue;
                    if (mf.name.IndexOf("Trunk", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        pineTrunkMesh = mf.sharedMesh;
                    else
                        pineBoughsMesh = mf.sharedMesh;
                }
            }

            // Cache AlpinePine arrays once so baking thousands of trees takes < 0.3s!
            Vector3[] ptVerts = pineTrunkMesh != null ? pineTrunkMesh.vertices : null;
            Vector3[] ptNorms = pineTrunkMesh != null ? pineTrunkMesh.normals : null;
            Vector2[] ptUvs = pineTrunkMesh != null ? pineTrunkMesh.uv : null;
            int[] ptTris = pineTrunkMesh != null ? pineTrunkMesh.triangles : null;

            Vector3[] pbVerts = pineBoughsMesh != null ? pineBoughsMesh.vertices : null;
            Vector3[] pbNorms = pineBoughsMesh != null ? pineBoughsMesh.normals : null;
            Vector2[] pbUvs = pineBoughsMesh != null ? pineBoughsMesh.uv : null;
            int[] pbTris = pineBoughsMesh != null ? pineBoughsMesh.triangles : null;

            GameObject forestRoot = new GameObject("WorldMap_Forests");
            forestRoot.transform.SetParent(parent, false);
            forestRoot.isStatic = true;

            TerrainData td = terrain.terrainData;
            Vector3 tPos = terrain.transform.position;
            const int chunkGrid = 10; // 10x10 spatial chunks (~200m each) for automatic mobile frustum culling across 2400m!
            const float minCoord = -1020f;
            const float maxCoord = 980f;
            const float span = maxCoord - minCoord;

            ForestChunkBuilder[,] chunks = new ForestChunkBuilder[chunkGrid, chunkGrid];

            // Dense Bannerlord forest centers scaled for the 2400m continent (x, z, radius, peakDensity)
            Vector4[] denseForests =
            {
                new Vector4(-245f,  590f, 265f, 0.94f), // Northern Timberwoods (Soviet)
                new Vector4( 480f,  495f, 250f, 0.92f), // Northeast Alpine Pinewoods (Soviet/China border)
                new Vector4(-665f, -200f, 235f, 0.90f), // Western Emerald Woods (USA)
                new Vector4( 365f,  110f, 220f, 0.88f), // Central Riverwood (Turkey/China)
                new Vector4(-680f,  395f, 210f, 0.86f), // Northwest Coastal Pines (USA)
                new Vector4( 640f, -160f, 195f, 0.84f)  // Eastern Foothill Grove (China)
            };

            // Strategic open meadow clearings (x, z, radius)
            Vector3[] openClearings =
            {
                new Vector3(   0f,    0f, 110f), // Heartland starting glade
                new Vector3(-320f, -230f, 115f), // Southwest meadow plain
                new Vector3( -15f,  485f, 105f), // North central plain
                new Vector3( 230f, -165f, 100f)  // South-central crossroads plain
            };

            System.Random rng = new System.Random(20261126);
            int totalTrees = 0;

            void PlaceSingleTree(ForestChunkBuilder targetChunk, float x, float y, float z, bool pine, float scl, float yaw, float tiltX, float tiltZ, float cVar)
            {
                Quaternion rot = Quaternion.Euler(tiltX, yaw, tiltZ);
                Vector3 pos = new Vector3(x, y - 0.35f, z);
                if (pine && ptVerts != null && pbVerts != null)
                {
                    // Realistic AlpinePine.fbx (height ~6.2m * scl => 8m - 12.5m)
                    float pineScl = scl * 1.45f;
                    Color trunkTint = ToLinearForestColor(new Color(0.95f, 0.92f, 0.88f), 0.82f);
                    Color foliageTint = ToLinearForestColor(
                        Color.Lerp(new Color(0.76f, 0.86f, 0.72f), new Color(0.94f, 1.00f, 0.86f), cVar), 1.0f);

                    AppendMeshSubmesh(targetChunk, targetChunk.BarkTris, ptVerts, ptNorms, ptUvs, ptTris, pos, rot, pineScl, trunkTint, 0.72f, 0.95f, 5.6f);
                    AppendMeshSubmesh(targetChunk, targetChunk.PineTris, pbVerts, pbNorms, pbUvs, pbTris, pos, rot, pineScl, foliageTint, 0.68f, 1.02f, 6.2f);
                }
                else
                {
                    AppendRealisticTexturedOak(targetChunk, pos, rot, scl * 1.25f, cVar);
                }
                totalTrees++;
            }

            // Jittered grid sampling (~13.5m spacing across 2400m) keeps lush overlapping canopies while staying mobile-friendly!
            const float sampleStep = 13.5f;
            for (float gz = minCoord + 12f; gz <= maxCoord - 12f; gz += sampleStep)
            {
                for (float gx = minCoord + 12f; gx <= maxCoord - 12f; gx += sampleStep)
                {
                    float wx = gx + ((float)rng.NextDouble() - 0.5f) * sampleStep * 0.90f;
                    float wz = gz + ((float)rng.NextDouble() - 0.5f) * sampleStep * 0.90f;

                    // Keep commander start glade, all 6 bridges, AND all 207 settlement/village pads clear of trees!
                    if (wx * wx + wz * wz < 28f * 28f)
                        continue;

                    bool nearBlocked = false;
                    if (bridgeCenters != null)
                    {
                        for (int b = 0; b < bridgeCenters.Count; b++)
                        {
                            float dx = wx - bridgeCenters[b].x;
                            float dz = wz - bridgeCenters[b].y;
                            if (dx * dx + dz * dz < 36f * 36f)
                            {
                                nearBlocked = true;
                                break;
                            }
                        }
                    }
                    if (!nearBlocked && settlements != null)
                    {
                        for (int s = 0; s < settlements.Count; s++)
                        {
                            Vector3 sp = settlements[s].worldPosition;
                            float clr = settlements[s].clearanceRadius;
                            float dx = wx - sp.x;
                            float dz = wz - sp.z;
                            if (dx * dx + dz * dz < clr * clr)
                            {
                                nearBlocked = true;
                                break;
                            }
                        }
                    }
                    if (nearBlocked)
                        continue;

                    float nx = Mathf.Clamp01((wx - tPos.x) / td.size.x);
                    float nz = Mathf.Clamp01((wz - tPos.z) / td.size.z);
                    float worldY = td.GetInterpolatedHeight(nx, nz) + tPos.y;
                    float steepness = td.GetSteepness(nx, nz);

                    // Strictly exclude water, beaches, desert sand, steep rock, and snowy/icy mountain zones!
                    if (worldY < SeaLevelY + 3.0f || worldY > 72f || steepness > 23f)
                        continue;

                    ComputeBiomeWeights(wx, wz, worldY, steepness,
                        out float wGrass, out float wSand, out float wRock, out float wSnow);

                    if (wSand > 0.10f || wSnow > 0.01f || wRock > 0.38f || wGrass < 0.60f)
                        continue;

                    // Evaluate organic Warband forest density: dense forest cores + gentle continent-wide groves
                    float warp = (Fbm(wx * 0.0065f + 4.3f, wz * 0.0065f + 9.1f, 3) - 0.5f) * 75f;
                    float denseFactor = 0f;
                    for (int f = 0; f < denseForests.Length; f++)
                    {
                        Vector4 df = denseForests[f];
                        float dist = Vector2.Distance(new Vector2(wx, wz), new Vector2(df.x, df.y)) + warp * 0.45f;
                        float mask = 1f - SmoothStep(df.z * 0.25f, df.z, dist);
                        denseFactor = Mathf.Max(denseFactor, mask * df.w);
                    }

                    // Natural continent-wide Warband woodland scatter (clumpy small groves across all green plains)
                    float groveNoise = Fbm(wx * 0.0095f + 17.2f, wz * 0.0095f + 3.8f, 3);
                    float scatteredDensity = SmoothStep(0.46f, 0.72f, groveNoise) * 0.42f + 0.055f;

                    float density = Mathf.Clamp01(Mathf.Max(denseFactor, scatteredDensity));

                    // Soft open clearings so strategic plains stay clean for cities/battles
                    for (int c = 0; c < openClearings.Length; c++)
                    {
                        Vector3 cl = openClearings[c];
                        float d = Vector2.Distance(new Vector2(wx, wz), new Vector2(cl.x, cl.y)) + warp * 0.30f;
                        float clearSuppress = SmoothStep(cl.z * 0.45f, cl.z, d);
                        density *= Mathf.Lerp(0.08f, 1.0f, clearSuppress);
                    }

                    if ((float)rng.NextDouble() > density)
                        continue;

                    int cx = Mathf.Clamp(Mathf.FloorToInt(((wx - minCoord) / span) * chunkGrid), 0, chunkGrid - 1);
                    int cz = Mathf.Clamp(Mathf.FloorToInt(((wz - minCoord) / span) * chunkGrid), 0, chunkGrid - 1);
                    ForestChunkBuilder chunk = chunks[cz, cx];
                    if (chunk == null)
                    {
                        chunk = new ForestChunkBuilder();
                        chunks[cz, cx] = chunk;
                    }

                    // Pines dominate northern/eastern highlands & higher elevations; realistic leafy Oaks dominate temperate plains
                    float swx = wx / MapScale;
                    float swz = wz / MapScale;
                    float pineBias = Mathf.Clamp01(
                        SmoothStep(100f, 410f, swz) * 0.58f +
                        SmoothStep(160f, 450f, swx) * 0.38f +
                        SmoothStep(27f, 46f, worldY) * 0.45f +
                        (Fbm(wx * 0.011f, wz * 0.011f, 2) - 0.5f) * 0.38f);

                    bool isPine = (float)rng.NextDouble() < pineBias;
                    float baseScale = Mathf.Lerp(0.88f, 1.24f, denseFactor) * Mathf.Lerp(0.82f, 1.22f, (float)rng.NextDouble());
                    float yawDeg = (float)rng.NextDouble() * 360f;
                    float tiltX = ((float)rng.NextDouble() - 0.5f) * 5.5f;
                    float tiltZ = ((float)rng.NextDouble() - 0.5f) * 5.5f;
                    float colorVar = (float)rng.NextDouble();

                    PlaceSingleTree(chunk, wx, worldY, wz, isPine, baseScale, yawDeg, tiltX, tiltZ, colorVar);

                    // Inside dense forest cores, add a tight companion tree so woods merge into thick dark-green canopies!
                    if (denseFactor > 0.52f && (float)rng.NextDouble() < (denseFactor - 0.32f))
                    {
                        float cx2 = wx + ((float)rng.NextDouble() - 0.5f) * 6.8f;
                        float cz2 = wz + ((float)rng.NextDouble() - 0.5f) * 6.8f;
                        float nx2 = Mathf.Clamp01((cx2 - tPos.x) / td.size.x);
                        float nz2 = Mathf.Clamp01((cz2 - tPos.z) / td.size.z);
                        float y2 = td.GetInterpolatedHeight(nx2, nz2) + tPos.y;
                        if (y2 >= SeaLevelY + 3.0f && y2 <= 64f)
                        {
                            float subScale = baseScale * Mathf.Lerp(0.74f, 1.06f, (float)rng.NextDouble());
                            PlaceSingleTree(
                                chunk, cx2, y2, cz2, isPine, subScale,
                                (float)rng.NextDouble() * 360f,
                                ((float)rng.NextDouble() - 0.5f) * 6f,
                                ((float)rng.NextDouble() - 0.5f) * 6f,
                                (float)rng.NextDouble());
                        }
                    }
                }
            }

            // Bake active spatial chunks with 3 shared materials (Bark, Pine, Oak)
            int activeChunks = 0;
            for (int cz = 0; cz < chunkGrid; cz++)
            {
                for (int cx = 0; cx < chunkGrid; cx++)
                {
                    string meshPath = $"{ForestChunksFolder}/WorldMapForest_Chunk_{cx}_{cz}.asset";
                    ForestChunkBuilder cb = chunks[cz, cx];
                    if (cb == null || cb.Verts.Count == 0)
                    {
                        if (AssetDatabase.LoadAssetAtPath<Mesh>(meshPath) != null)
                            AssetDatabase.DeleteAsset(meshPath);
                        continue;
                    }

                    Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
                    if (mesh == null)
                    {
                        mesh = new Mesh { name = $"WorldMapForest_Chunk_{cx}_{cz}" };
                        AssetDatabase.CreateAsset(mesh, meshPath);
                    }
                    else
                    {
                        mesh.Clear();
                    }

                    if (cb.Verts.Count > 65000)
                        mesh.indexFormat = IndexFormat.UInt32;

                    mesh.SetVertices(cb.Verts);
                    mesh.SetNormals(cb.Normals);
                    mesh.SetUVs(0, cb.Uvs);
                    mesh.SetColors(cb.Colors);

                    var subMats = new List<Material>(3);
                    var subTris = new List<List<int>>(3);
                    if (cb.BarkTris.Count > 0) { subMats.Add(barkMat); subTris.Add(cb.BarkTris); }
                    if (cb.PineTris.Count > 0) { subMats.Add(pineMat); subTris.Add(cb.PineTris); }
                    if (cb.OakTris.Count > 0) { subMats.Add(oakMat); subTris.Add(cb.OakTris); }

                    mesh.subMeshCount = subTris.Count;
                    for (int s = 0; s < subTris.Count; s++)
                        mesh.SetTriangles(subTris[s], s);

                    mesh.RecalculateBounds();
                    EditorUtility.SetDirty(mesh);

                    GameObject chunkGo = new GameObject($"ForestChunk_{cx}_{cz}");
                    chunkGo.transform.SetParent(forestRoot.transform, false);
                    chunkGo.isStatic = true;

                    MeshFilter mf = chunkGo.AddComponent<MeshFilter>();
                    mf.sharedMesh = mesh;

                    MeshRenderer mr = chunkGo.AddComponent<MeshRenderer>();
                    mr.sharedMaterials = subMats.ToArray();
                    mr.shadowCastingMode = ShadowCastingMode.On;
                    mr.receiveShadows = true;
                    activeChunks++;
                }
            }

            Debug.Log($"[WorldMapTerrainBuilder] Gerçekçi Warband ormanları kuruldu: {totalTrees} ağaç, {activeChunks} mekansal parça.");
        }

        private static Color ToLinearForestColor(Color srgb, float ao)
        {
            Color lin = srgb.linear;
            lin.a = ao;
            return lin;
        }

        private static void AppendMeshSubmesh(
            ForestChunkBuilder cb,
            List<int> targetTris,
            Vector3[] srcVerts,
            Vector3[] srcNorms,
            Vector2[] srcUvs,
            int[] srcTris,
            Vector3 origin,
            Quaternion rot,
            float scale,
            Color tint,
            float bottomAo,
            float topAo,
            float heightRef)
        {
            int baseVert = cb.Verts.Count;
            bool hasNorms = srcNorms != null && srcNorms.Length == srcVerts.Length;
            bool hasUvs = srcUvs != null && srcUvs.Length == srcVerts.Length;

            for (int i = 0; i < srcVerts.Length; i++)
            {
                Vector3 v = srcVerts[i];
                cb.Verts.Add(origin + rot * (v * scale));
                cb.Normals.Add(hasNorms ? (rot * srcNorms[i]).normalized : Vector3.up);
                cb.Uvs.Add(hasUvs ? srcUvs[i] : Vector2.zero);

                float hT = Mathf.Clamp01(v.y / Mathf.Max(0.1f, heightRef));
                Color c = tint;
                c.a = Mathf.Lerp(bottomAo, topAo, hT);
                cb.Colors.Add(c);
            }

            for (int i = 0; i < srcTris.Length; i++)
                targetTris.Add(baseVert + srcTris[i]);
        }

        private static void AppendRealisticTexturedOak(
            ForestChunkBuilder cb,
            Vector3 origin,
            Quaternion rot,
            float scale,
            float colorVar)
        {
            Color barkTint = ToLinearForestColor(new Color(0.95f, 0.90f, 0.85f), 0.80f);
            Color leafTint = ToLinearForestColor(
                Color.Lerp(new Color(0.76f, 0.88f, 0.72f), new Color(0.98f, 1.00f, 0.82f), colorVar), 1.0f);

            // 1. Furrowed bark trunk + 2 fork limbs
            AppendTexturedBarkCylinder(cb, origin, rot, scale, 0f, 2.75f, 0.44f, 0.24f, 5, barkTint);

            // 2. 12 overlapping arched 3D oak leaf bough cards with outward spherical canopy normals (zero mushroom look!)
            Vector3 crownCenter = new Vector3(0f, 4.15f, 0f) * scale;

            // Lower outer bough ring (6 outward-fanned cards angled ~32 deg down)
            for (int i = 0; i < 6; i++)
            {
                float ang = (i * 60f) + (colorVar * 35f);
                Quaternion cardRot = rot * Quaternion.Euler(34f, ang, ((i % 2) == 0 ? 12f : -12f));
                Vector3 anchor = new Vector3(0f, 2.65f, 0f);
                AppendOakFoliageCard(cb, origin, rot, cardRot, anchor, crownCenter, scale, width: 3.15f, length: 3.35f, leafTint, baseAo: 0.72f, tipAo: 0.94f);
            }

            // Mid fuller bough ring (4 cards rotated 45 deg offset, angled gently upward)
            for (int i = 0; i < 4; i++)
            {
                float ang = (i * 90f) + 30f + (colorVar * 40f);
                Quaternion cardRot = rot * Quaternion.Euler(18f, ang, ((i % 2) == 0 ? -16f : 16f));
                Vector3 anchor = new Vector3(0f, 3.60f, 0f);
                AppendOakFoliageCard(cb, origin, rot, cardRot, anchor, crownCenter, scale, width: 3.05f, length: 3.15f, leafTint, baseAo: 0.82f, tipAo: 1.00f);
            }

            // Top crossed sunlit crown cards (2 intersecting upright-angled cards so top-down view is lush & layered!)
            for (int i = 0; i < 2; i++)
            {
                float ang = i * 90f + 15f;
                Quaternion cardRot = rot * Quaternion.Euler(52f, ang, 0f);
                Vector3 anchor = new Vector3(0f, 4.25f, 0f);
                AppendOakFoliageCard(cb, origin, rot, cardRot, anchor, crownCenter, scale, width: 3.10f, length: 2.85f, leafTint, baseAo: 0.90f, tipAo: 1.04f);
            }
        }

        private static void AppendOakFoliageCard(
            ForestChunkBuilder cb,
            Vector3 origin,
            Quaternion treeRot,
            Quaternion cardRot,
            Vector3 anchorLocal,
            Vector3 crownCenterLocal,
            float scale,
            float width,
            float length,
            Color tint,
            float baseAo,
            float tipAo)
        {
            // Gently bent 2-segment leaf bough card (4 tris) so it curves organically like a real branch!
            float hw = width * 0.5f;
            float midLen = length * 0.52f;
            float archLift = length * 0.14f;
            float tipDrop = -length * 0.08f;

            Vector3[] localPts =
            {
                new Vector3(-hw * 0.72f, 0f,        0f),
                new Vector3( hw * 0.72f, 0f,        0f),
                new Vector3(-hw,         archLift,  midLen),
                new Vector3( hw,         archLift,  midLen),
                new Vector3(-hw * 0.78f, tipDrop,   length),
                new Vector3( hw * 0.78f, tipDrop,   length)
            };

            Vector2[] uvs =
            {
                new Vector2(0.08f, 0.02f),
                new Vector2(0.92f, 0.02f),
                new Vector2(0.00f, 0.52f),
                new Vector2(1.00f, 0.52f),
                new Vector2(0.10f, 0.98f),
                new Vector2(0.90f, 0.98f)
            };

            float[] aos = { baseAo, baseAo, Mathf.Lerp(baseAo, tipAo, 0.55f), Mathf.Lerp(baseAo, tipAo, 0.55f), tipAo, tipAo };

            Vector3 anchorWorld = origin + treeRot * (anchorLocal * scale);
            Vector3 crownCenterWorld = origin + treeRot * crownCenterLocal;
            int vi = cb.Verts.Count;

            for (int i = 0; i < 6; i++)
            {
                Vector3 wp = anchorWorld + cardRot * (localPts[i] * scale);
                cb.Verts.Add(wp);

                // Radial canopy normal from crown center (mixed with +Y) makes crossed leaf cards light like a lush 3D tree!
                Vector3 radial = (wp - crownCenterWorld);
                radial.y = Mathf.Max(radial.y, 0.65f * scale);
                cb.Normals.Add((radial.normalized * 0.78f + Vector3.up * 0.22f).normalized);
                cb.Uvs.Add(uvs[i]);

                Color c = tint;
                c.a = aos[i];
                cb.Colors.Add(c);
            }

            // Segment 1
            cb.OakTris.Add(vi + 0); cb.OakTris.Add(vi + 2); cb.OakTris.Add(vi + 1);
            cb.OakTris.Add(vi + 1); cb.OakTris.Add(vi + 2); cb.OakTris.Add(vi + 3);
            // Segment 2
            cb.OakTris.Add(vi + 2); cb.OakTris.Add(vi + 4); cb.OakTris.Add(vi + 3);
            cb.OakTris.Add(vi + 3); cb.OakTris.Add(vi + 4); cb.OakTris.Add(vi + 5);
        }

        private static void AppendTexturedBarkCylinder(
            ForestChunkBuilder cb, Vector3 origin, Quaternion rot, float scale,
            float y0, float y1, float r0, float r1, int sides, Color col)
        {
            for (int i = 0; i < sides; i++)
            {
                float u0 = i / (float)sides;
                float u1 = (i + 1) / (float)sides;
                float a0 = u0 * Mathf.PI * 2f;
                float a1 = u1 * Mathf.PI * 2f;

                Vector3 p00 = new Vector3(Mathf.Cos(a0) * r0, y0, Mathf.Sin(a0) * r0) * scale;
                Vector3 p10 = new Vector3(Mathf.Cos(a1) * r0, y0, Mathf.Sin(a1) * r0) * scale;
                Vector3 p01 = new Vector3(Mathf.Cos(a0) * r1, y1, Mathf.Sin(a0) * r1) * scale;
                Vector3 p11 = new Vector3(Mathf.Cos(a1) * r1, y1, Mathf.Sin(a1) * r1) * scale;

                Vector3 wn = rot * Vector3.Cross(p01 - p00, p10 - p00).normalized;

                int vi = cb.Verts.Count;
                cb.Verts.Add(origin + rot * p00);
                cb.Verts.Add(origin + rot * p10);
                cb.Verts.Add(origin + rot * p11);
                cb.Verts.Add(origin + rot * p01);

                cb.Normals.Add(wn); cb.Normals.Add(wn); cb.Normals.Add(wn); cb.Normals.Add(wn);
                cb.Uvs.Add(new Vector2(u0, 0f));
                cb.Uvs.Add(new Vector2(u1, 0f));
                cb.Uvs.Add(new Vector2(u1, 1.5f));
                cb.Uvs.Add(new Vector2(u0, 1.5f));
                cb.Colors.Add(col); cb.Colors.Add(col); cb.Colors.Add(col); cb.Colors.Add(col);

                cb.BarkTris.Add(vi); cb.BarkTris.Add(vi + 3); cb.BarkTris.Add(vi + 1);
                cb.BarkTris.Add(vi + 1); cb.BarkTris.Add(vi + 3); cb.BarkTris.Add(vi + 2);
            }
        }

        private static void GenerateOakBranchTexture(string path, int size)
        {
            // Fill transparent pixels with deep leaf green RGB so mipmaps never bleed white halos!
            Color bgBleed = new Color(0.14f, 0.24f, 0.10f, 0f);
            Color[] pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = bgBleed;

            void PaintSegment(Vector2 a, Vector2 b, float thickPx, Color col)
            {
                int minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, b.x) - thickPx - 1));
                int maxX = Mathf.Min(size - 1, Mathf.CeilToInt(Mathf.Max(a.x, b.x) + thickPx + 1));
                int minY = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, b.y) - thickPx - 1));
                int maxY = Mathf.Min(size - 1, Mathf.CeilToInt(Mathf.Max(a.y, b.y) + thickPx + 1));
                Vector2 ab = b - a;
                float lenSq = Mathf.Max(0.001f, ab.sqrMagnitude);

                for (int y = minY; y <= maxY; y++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        float t = Mathf.Clamp01(Vector2.Dot(new Vector2(x, y) - a, ab) / lenSq);
                        float d = Vector2.Distance(new Vector2(x, y), a + ab * t);
                        if (d <= thickPx)
                            pixels[y * size + x] = col;
                    }
                }
            }

            void PaintOakLeaf(Vector2 center, float angleRad, float lenPx, float widPx, Color baseCol, Color tipCol)
            {
                float cos = Mathf.Cos(angleRad);
                float sin = Mathf.Sin(angleRad);
                float bound = Mathf.Max(lenPx, widPx) + 4f;
                int minX = Mathf.Max(0, Mathf.FloorToInt(center.x - bound));
                int maxX = Mathf.Min(size - 1, Mathf.CeilToInt(center.x + bound));
                int minY = Mathf.Max(0, Mathf.FloorToInt(center.y - bound));
                int maxY = Mathf.Min(size - 1, Mathf.CeilToInt(center.y + bound));

                for (int y = minY; y <= maxY; y++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        float dx = x - center.x;
                        float dy = y - center.y;
                        // Transform into leaf local space: u along leaf length [-0.5..+0.5], v across width
                        float u = (dx * cos + dy * sin) / lenPx;
                        float v = (-dx * sin + dy * cos) / widPx;
                        if (u < -0.52f || u > 0.52f)
                            continue;

                        // Lobed oak leaf silhouette (gentle 3-lobe contour!)
                        float tNorm = u + 0.5f; // 0 at stem, 1 at tip
                        float envelope = Mathf.Sin(tNorm * Mathf.PI);
                        float lobe = 0.78f + 0.24f * Mathf.Sin(tNorm * Mathf.PI * 5.0f);
                        float halfW = envelope * lobe * 0.52f;
                        float absV = Mathf.Abs(v);
                        if (absV > halfW)
                            continue;

                        // Subtle midrib vein + leaf tip sunlight gradient
                        float vein = Mathf.Exp(-absV * absV * 95f) * 0.14f;
                        Color c = Color.Lerp(baseCol, tipCol, tNorm * 0.75f + vein);
                        c.a = 1f;
                        pixels[y * size + x] = c;
                    }
                }
            }

            // 1. Woody central stem & branching twigs
            Color twigCol = new Color(0.24f, 0.16f, 0.10f, 1f);
            Vector2 root = new Vector2(size * 0.50f, size * 0.04f);
            Vector2 mid = new Vector2(size * 0.50f, size * 0.54f);
            Vector2 tip = new Vector2(size * 0.50f, size * 0.86f);
            PaintSegment(root, mid, size * 0.014f, twigCol);
            PaintSegment(mid, tip, size * 0.009f, twigCol);

            Vector2[] branchEnds =
            {
                new Vector2(size * 0.20f, size * 0.34f),
                new Vector2(size * 0.80f, size * 0.34f),
                new Vector2(size * 0.15f, size * 0.62f),
                new Vector2(size * 0.85f, size * 0.62f),
                new Vector2(size * 0.28f, size * 0.82f),
                new Vector2(size * 0.72f, size * 0.82f)
            };
            Vector2[] branchStarts =
            {
                new Vector2(size * 0.50f, size * 0.22f),
                new Vector2(size * 0.50f, size * 0.24f),
                new Vector2(size * 0.50f, size * 0.44f),
                new Vector2(size * 0.50f, size * 0.46f),
                new Vector2(size * 0.50f, size * 0.66f),
                new Vector2(size * 0.50f, size * 0.68f)
            };
            for (int b = 0; b < branchEnds.Length; b++)
                PaintSegment(branchStarts[b], branchEnds[b], size * 0.008f, twigCol);

            // 2. Paint 170 layered realistic oak leaves (dark shaded back layer -> sunlit emerald/olive top layer)
            System.Random rng = new System.Random(908172);
            for (int layer = 0; layer < 2; layer++)
            {
                int leafCount = layer == 0 ? 95 : 85;
                for (int i = 0; i < leafCount; i++)
                {
                    float r = Mathf.Pow((float)rng.NextDouble(), 0.65f);
                    float ang = (float)rng.NextDouble() * Mathf.PI * 2f;
                    float cx = size * (0.50f + Mathf.Cos(ang) * r * 0.37f);
                    float cy = size * (0.52f + Mathf.Sin(ang) * r * 0.39f);

                    // Slight teardrop cluster shape (narrower near stem at bottom, fuller across crown)
                    if (cy < size * 0.16f && Mathf.Abs(cx - size * 0.5f) > size * 0.22f)
                        continue;

                    float leafAng = Mathf.Atan2(cy - size * 0.22f, cx - size * 0.50f) + ((float)rng.NextDouble() - 0.5f) * 1.15f;
                    float len = size * Mathf.Lerp(0.11f, 0.17f, (float)rng.NextDouble());
                    float wid = len * Mathf.Lerp(0.54f, 0.72f, (float)rng.NextDouble());

                    float tone = (float)rng.NextDouble();
                    Color cDark = layer == 0
                        ? Color.Lerp(new Color(0.11f, 0.22f, 0.09f), new Color(0.16f, 0.29f, 0.11f), tone)
                        : Color.Lerp(new Color(0.18f, 0.34f, 0.12f), new Color(0.26f, 0.40f, 0.14f), tone);
                    Color cTip = layer == 0
                        ? Color.Lerp(new Color(0.18f, 0.33f, 0.12f), new Color(0.24f, 0.38f, 0.14f), tone)
                        : Color.Lerp(new Color(0.28f, 0.46f, 0.16f), new Color(0.38f, 0.51f, 0.18f), tone);

                    PaintOakLeaf(new Vector2(cx, cy), leafAng, len, wid, cDark, cTip);
                }
            }

            SaveTexture2D(pixels, size, path, isNormalMap: false);
            TextureImporter imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp != null)
            {
                imp.alphaIsTransparency = true;
                imp.wrapMode = TextureWrapMode.Clamp;
                imp.mipmapEnabled = true;
                imp.mipMapsPreserveCoverage = true;
                imp.alphaTestReferenceValue = 0.36f;
                imp.SaveAndReimport();
            }
        }

        private static void EnsureBiomeAndWaterTextures()
        {
            GenerateWindSandWithNormal(SandDiffPath, SandNorPath, 512);
            GenerateGraniteRockWithNormal(RockDiffPath, RockNorPath, 512);
            GenerateAlpineSnowWithNormal(SnowDiffPath, SnowNorPath, 512);
            GenerateWaterNormalTexture(WaterNorPath, 256);
            GenerateMistNoiseTexture(EdgeMistNoisePath, 256);
            GenerateOakBranchTexture(ForestOakTexPath, 512);
        }

        private static void GenerateMistNoiseTexture(string path, int size)
        {
            Color[] pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                float ny = y / (float)size;
                for (int x = 0; x < size; x++)
                {
                    float nx = x / (float)size;
                    float n1 = TileableValueNoise(nx, ny, 4, 131);
                    float n2 = TileableValueNoise(nx, ny, 8, 197);
                    float n3 = TileableValueNoise(nx, ny, 16, 251);
                    float v = Mathf.Clamp01(n1 * 0.55f + n2 * 0.30f + n3 * 0.15f);
                    pixels[y * size + x] = new Color(v, v, v, 1f);
                }
            }
            SaveTexture2D(pixels, size, path, isNormalMap: false);
        }

        private static void GenerateWindSandWithNormal(string diffPath, string norPath, int size)
        {
            float[,] h = new float[size, size];
            Color[] diff = new Color[size * size];

            Color sandBase = new Color(0.83f, 0.74f, 0.54f, 1f);
            Color sandLight = new Color(0.89f, 0.81f, 0.62f, 1f);
            Color sandWarm = new Color(0.75f, 0.65f, 0.45f, 1f);

            for (int y = 0; y < size; y++)
            {
                float ny = y / (float)size;
                for (int x = 0; x < size; x++)
                {
                    float nx = x / (float)size;
                    float nWave = TileableValueNoise(nx, ny, 8, 29);
                    float ripple = Mathf.Sin((ny * 8f + nx * 2f + nWave * 0.42f) * Mathf.PI * 2f) * 0.5f + 0.5f;
                    float fine = TileableValueNoise(nx, ny, 64, 83);

                    h[y, x] = ripple * 0.7f + fine * 0.3f;
                    Color c = Color.Lerp(sandWarm, sandBase, ripple * 0.65f + 0.35f);
                    c = Color.Lerp(c, sandLight, fine * 0.25f);
                    c.a = 0.04f;
                    diff[y * size + x] = c;
                }
            }

            SaveTexture2D(diff, size, diffPath, isNormalMap: false);
            SaveSobelNormalMap(h, size, strength: 1.6f, norPath);
        }

        private static void GenerateGraniteRockWithNormal(string diffPath, string norPath, int size)
        {
            float[,] h = new float[size, size];
            Color[] diff = new Color[size * size];

            Color rockDark = new Color(0.28f, 0.27f, 0.26f, 1f);
            Color rockMid = new Color(0.43f, 0.42f, 0.40f, 1f);
            Color rockWarm = new Color(0.51f, 0.47f, 0.42f, 1f);
            Color rockLight = new Color(0.58f, 0.56f, 0.53f, 1f);

            for (int y = 0; y < size; y++)
            {
                float ny = y / (float)size;
                for (int x = 0; x < size; x++)
                {
                    float nx = x / (float)size;

                    // Natural layered alpine granite & scree grain (clean matte stone, zero noisy worm-line pattern!)
                    float warp = TileableValueNoise(nx, ny, 4, 11) * 0.16f;
                    float strata = TileableValueNoise(nx + warp, ny * 2.5f + warp * 0.4f, 6, 73);
                    float slab1 = TileableValueNoise(nx, ny, 8, 37);
                    float slab2 = TileableValueNoise(nx, ny, 20, 91);
                    float grit = TileableValueNoise(nx, ny, 64, 211);

                    float elev = Mathf.Clamp01(strata * 0.36f + slab1 * 0.32f + slab2 * 0.20f + grit * 0.12f);
                    h[y, x] = elev;

                    Color c = Color.Lerp(rockDark, rockMid, SmoothStep(0.18f, 0.58f, elev));
                    c = Color.Lerp(c, rockWarm, strata * 0.32f);
                    c = Color.Lerp(c, rockLight, SmoothStep(0.64f, 0.92f, elev) * 0.55f);
                    c.a = 0.03f; // Dry matte rock in URP Terrain/Lit
                    diff[y * size + x] = c;
                }
            }

            SaveTexture2D(diff, size, diffPath, isNormalMap: false);
            SaveSobelNormalMap(h, size, strength: 4.8f, norPath);
        }

        private static void GenerateAlpineSnowWithNormal(string diffPath, string norPath, int size)
        {
            float[,] h = new float[size, size];
            Color[] diff = new Color[size * size];

            Color snowShade = new Color(0.79f, 0.85f, 0.91f, 1f);
            Color snowBase = new Color(0.92f, 0.95f, 0.98f, 1f);
            Color snowPeak = new Color(0.98f, 0.99f, 1.00f, 1f);

            for (int y = 0; y < size; y++)
            {
                float ny = y / (float)size;
                for (int x = 0; x < size; x++)
                {
                    float nx = x / (float)size;
                    float drift = TileableValueNoise(nx, ny, 6, 113);
                    float detail = TileableValueNoise(nx, ny, 24, 163);
                    float sparkle = TileableValueNoise(nx, ny, 64, 227);

                    float elev = drift * 0.65f + detail * 0.25f + sparkle * 0.10f;
                    h[y, x] = elev;

                    Color c = Color.Lerp(snowShade, snowBase, elev);
                    c = Color.Lerp(c, snowPeak, SmoothStep(0.65f, 0.95f, elev) * 0.55f);
                    c.a = 0.10f;
                    diff[y * size + x] = c;
                }
            }

            SaveTexture2D(diff, size, diffPath, isNormalMap: false);
            SaveSobelNormalMap(h, size, strength: 1.8f, norPath);
        }

        private static void GenerateWaterNormalTexture(string norPath, int size)
        {
            float[,] h = new float[size, size];

            for (int y = 0; y < size; y++)
            {
                float ny = y / (float)size;
                for (int x = 0; x < size; x++)
                {
                    float nx = x / (float)size;
                    float w1 = Mathf.Sin((nx * 3f + ny * 2f + TileableValueNoise(nx, ny, 4, 41) * 0.55f) * Mathf.PI * 2f) * 0.5f + 0.5f;
                    float w2 = Mathf.Sin((nx * -2f + ny * 5f + TileableValueNoise(nx, ny, 8, 89) * 0.45f) * Mathf.PI * 2f) * 0.5f + 0.5f;
                    float w3 = TileableValueNoise(nx, ny, 20, 173);
                    h[y, x] = w1 * 0.45f + w2 * 0.35f + w3 * 0.20f;
                }
            }

            SaveSobelNormalMap(h, size, strength: 2.8f, norPath);
        }

        private static void SaveSobelNormalMap(float[,] heightField, int size, float strength, string path)
        {
            Color[] pixels = new Color[size * size];

            for (int y = 0; y < size; y++)
            {
                int ym = (y - 1 + size) % size;
                int yp = (y + 1) % size;
                for (int x = 0; x < size; x++)
                {
                    int xm = (x - 1 + size) % size;
                    int xp = (x + 1) % size;

                    float tl = heightField[ym, xm];
                    float tc = heightField[ym, x];
                    float tr = heightField[ym, xp];
                    float ml = heightField[y, xm];
                    float mr = heightField[y, xp];
                    float bl = heightField[yp, xm];
                    float bc = heightField[yp, x];
                    float br = heightField[yp, xp];

                    float dx = (tr + 2f * mr + br) - (tl + 2f * ml + bl);
                    float dy = (bl + 2f * bc + br) - (tl + 2f * tc + tr);

                    Vector3 n = new Vector3(-dx * strength, -dy * strength, 1f).normalized;
                    pixels[y * size + x] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
                }
            }

            SaveTexture2D(pixels, size, path, isNormalMap: true);
        }

        private static void SaveTexture2D(Color[] pixels, int size, string path, bool isNormalMap)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.SetPixels(pixels);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = isNormalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.filterMode = FilterMode.Trilinear;
                importer.anisoLevel = 8;
                importer.mipmapEnabled = true;
                importer.SaveAndReimport();
            }
        }

        private static Vector2[] SampleCatmullRomSpline(Vector2[] controlPoints, int samplesPerSegment)
        {
            List<Vector2> result = new List<Vector2>((controlPoints.Length - 1) * samplesPerSegment + 1);
            int n = controlPoints.Length;

            for (int i = 0; i < n - 1; i++)
            {
                Vector2 p0 = controlPoints[Mathf.Max(0, i - 1)];
                Vector2 p1 = controlPoints[i];
                Vector2 p2 = controlPoints[i + 1];
                Vector2 p3 = controlPoints[Mathf.Min(n - 1, i + 2)];

                int steps = i == n - 2 ? samplesPerSegment : samplesPerSegment;
                for (int s = 0; s < steps; s++)
                {
                    float t = s / (float)samplesPerSegment;
                    result.Add(EvaluateCatmullRom(p0, p1, p2, p3, t));
                }
            }

            result.Add(controlPoints[n - 1]);
            return result.ToArray();
        }

        private static Vector2 EvaluateCatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            return 0.5f * (
                (2f * p1) +
                (-p0 + p2) * t +
                (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                (-p0 + 3f * p1 - 3f * p2 + p3) * t3
            );
        }

        private static float DistanceToPolyline(Vector2 p, Vector2[] pts)
        {
            float minDistSq = float.MaxValue;
            for (int i = 0; i < pts.Length - 1; i++)
            {
                Vector2 a = pts[i];
                Vector2 b = pts[i + 1];
                Vector2 ab = b - a;
                float lenSq = ab.sqrMagnitude;
                float t = lenSq > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq) : 0f;
                Vector2 proj = a + ab * t;
                float dSq = (p - proj).sqrMagnitude;
                if (dSq < minDistSq)
                    minDistSq = dSq;
            }
            return Mathf.Sqrt(minDistSq);
        }

        private static TerrainLayer LoadOrCreateTerrainLayer(
            string assetPath,
            Texture2D diffuse,
            Texture2D normalMap,
            Vector2 tileSize,
            Color diffuseTint,
            float normalScale,
            float smoothness)
        {
            TerrainLayer layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(assetPath);
            if (layer == null)
            {
                layer = new TerrainLayer();
                AssetDatabase.CreateAsset(layer, assetPath);
            }

            layer.diffuseTexture = diffuse;
            layer.normalMapTexture = normalMap;
            layer.normalScale = normalScale;
            layer.tileSize = tileSize;
            layer.tileOffset = Vector2.zero;
            layer.diffuseRemapMin = Vector4.zero;
            layer.diffuseRemapMax = new Vector4(diffuseTint.r, diffuseTint.g, diffuseTint.b, 1f);
            layer.smoothness = smoothness;
            layer.metallic = 0f;
            EditorUtility.SetDirty(layer);
            return layer;
        }

        private static Material LoadOrCreateMaterial(string assetPath, Shader shader)
        {
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, assetPath);
            }
            else
            {
                mat.shader = shader;
            }
            return mat;
        }

        private static float Fbm(float x, float y, int octaves)
        {
            float value = 0f;
            float amp = 0.5f;
            float freq = 1f;
            float maxAmp = 0f;

            for (int i = 0; i < octaves; i++)
            {
                value += Mathf.PerlinNoise(x * freq, y * freq) * amp;
                maxAmp += amp;
                freq *= 2.03f;
                amp *= 0.5f;
            }

            return value / maxAmp;
        }

        private static float RidgedNoise(float x, float y)
        {
            float n = Mathf.PerlinNoise(x, y);
            float ridge = 1f - Mathf.Abs(n * 2f - 1f);
            return ridge * ridge;
        }

        private static float SmoothStep(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float lenSq = ab.sqrMagnitude;
            if (lenSq < 1e-6f)
                return Vector2.Distance(p, a);

            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq);
            return Vector2.Distance(p, a + ab * t);
        }

        private static float TileableValueNoise(float u, float v, int period, int seed)
        {
            float fx = u * period;
            float fy = v * period;
            int x0 = Mathf.FloorToInt(fx) % period;
            int y0 = Mathf.FloorToInt(fy) % period;
            int x1 = (x0 + 1) % period;
            int y1 = (y0 + 1) % period;

            float tx = fx - Mathf.Floor(fx);
            float ty = fy - Mathf.Floor(fy);
            float sx = tx * tx * (3f - 2f * tx);
            float sy = ty * ty * (3f - 2f * ty);

            float c00 = Hash2D(x0, y0, seed);
            float c10 = Hash2D(x1, y0, seed);
            float c01 = Hash2D(x0, y1, seed);
            float c11 = Hash2D(x1, y1, seed);

            return Mathf.Lerp(Mathf.Lerp(c00, c10, sx), Mathf.Lerp(c01, c11, sx), sy);
        }

        private static float Hash2D(int x, int y, int seed)
        {
            unchecked
            {
                int n = x * 374761393 + y * 668265263 + seed * 1442695041;
                n = (n ^ (n >> 13)) * 1274126177;
                n ^= (n >> 16);
                return (n & 0x7fffffff) / (float)0x7fffffff;
            }
        }

        private static void EnsureFolder(string parent, string child)
        {
            string full = $"{parent}/{child}";
            if (!AssetDatabase.IsValidFolder(full))
                AssetDatabase.CreateFolder(parent, child);
        }
    }
}
