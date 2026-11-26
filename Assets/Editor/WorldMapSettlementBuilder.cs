using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using VRAtis.WorldMap;

namespace VRAtis.EditorTools
{
    /// <summary>
    /// Generates the Bannerlord-scale 5-Faction Continent Settlement Network across 2400x2400m:
    /// - 5 Monumental Capitals (largest, double-walled citadels at each nation's strategic core)
    /// - 14 Major Walled Cities (large fortified regional hubs)
    /// - 24 Military Bases (fortified border/bridge/pass garrisons for reinforcement dispatches)
    /// - 124 Compact Linked Villages (small hamlets widely spaced across the entire continent!)
    /// Every tier has an unmistakable visual scale progression (Village < Military Base < City << Capital)
    /// and unique cultural architecture for Turkey, Soviet, USA, China, and Rebels.
    /// Baked into 10x10 frustum-culled static spatial chunks with 1 shared mobile URP material.
    /// </summary>
    public static class WorldMapSettlementBuilder
    {
        private const string SettlementMatPath = "Assets/Materials/WorldMap/WorldMapSettlement_Mat.mat";
        private const string SettlementChunksFolder = "Assets/Materials/WorldMap/VillageChunks";

        private struct HubBlueprint
        {
            public string Id;
            public string Name;
            public WorldMapFaction Faction;
            public WorldMapSettlementKind Kind;
            public Vector2 IdealPos;
            public int VillageCount;

            public HubBlueprint(string id, string name, WorldMapFaction faction, WorldMapSettlementKind kind, float x, float z, int villages)
            {
                Id = id;
                Name = name;
                Faction = faction;
                Kind = kind;
                IdealPos = new Vector2(x, z);
                VillageCount = villages;
            }
        }

        private sealed class SettlementChunkBuilder
        {
            public readonly List<Vector3> Verts = new List<Vector3>(4096);
            public readonly List<Vector3> Normals = new List<Vector3>(4096);
            public readonly List<Color> Colors = new List<Color>(4096);
            public readonly List<int> Tris = new List<int>(8192);
        }

        // Authentic regional village names for each of the 5 nations
        private static readonly string[] TurkeyVillageNames =
        {
            "Göreme", "Şirince", "Cumalıkızık", "Safranbolu", "Beypazarı", "Birgi", "Akyaka", "Amasra",
            "Mudurnu", "Taraklı", "Uçhisar", "Çavuşin", "Mustafapaşa", "Doğanbey", "Eskikaraağaç", "Kaleköy",
            "İznik", "Göynük", "Sille", "Eğirdir", "Halfeti", "Harran", "Hasankeyf", "Kemaliye",
            "Ayder", "Uzungöl", "Boğazkale", "Ağlasun"
        };

        private static readonly string[] SovietVillageNames =
        {
            "Suzdal", "Kiji", "Paleh", "Myshkin", "Plyos", "Pereslavl", "Uglich", "Borodino",
            "Kaluga", "Vologda", "Kostroma", "Ryazan", "Tula", "Smolensk", "Pskov", "Novgorod",
            "Orel", "Bryansk", "Voronej", "Tambov", "Penza", "Samara", "Saratov", "Omsk",
            "Tomsk", "Tobolsk", "Irkutsk", "Perm"
        };

        private static readonly string[] UsaVillageNames =
        {
            "Gettysburg", "Salem", "Tombstone", "Deadwood", "Sedona", "Aspen", "Jackson Hole", "Mystic",
            "Bar Harbor", "Taos", "Fredericksburg", "Galena", "Woodstock", "Stowe", "Carmel", "Solvang",
            "Leavenworth", "Bisbee", "Telluride", "Bozeman", "Cody", "Moab", "Marfa", "Natchez",
            "Beaufort", "Savannah", "Abilene", "Dodge City"
        };

        private static readonly string[] ChinaVillageNames =
        {
            "Hongcun", "Xidi", "Wuzhen", "Zhouzhuang", "Fenghuang", "Lijiang", "Pingyao", "Tongli",
            "Xitang", "Nanxun", "Likeng", "Wuyuan", "Heshun", "Dali", "Yangshuo", "Furong",
            "Zhenyuan", "Langzhong", "Qikou", "Cuandixia", "Shaxi", "Danba", "Jiaju", "Basha",
            "Zhaoxing", "Chengkan", "Hekeng", "Tashkurgan"
        };

        private static readonly string[] RebelVillageNames =
        {
            "Kül Kampı", "Paslı Koy", "Akrep Çukuru", "Kanlı Kanyon", "Kuru Kuyu", "Kara Çadır",
            "Gölge Sığınağı", "Serseri İskelesi", "Kırık Gemi", "Barut Deposu", "Yağmacı Tepesi",
            "Fırtına Burnu", "Zehirli Vaha", "Dikenli Geçit", "Çakal İni", "Harabe Kule",
            "Yanık Kumul", "Kaçakçı Koyu", "Kırık Pusula", "Demir İskele", "Kızıl Kum", "Tuzlu Kuyu"
        };

        /// <summary>
        /// Computes generously spaced, terrain-validated sites for all 43 Strategic Hubs (5 Capitals + 14 Cities + 24 Military Bases)
        /// and 124 Linked Villages across the full 2400x2400m continent.
        /// </summary>
        public static List<WorldMapSettlementRecord> GenerateStrategicBlueprint(
            Transform environmentRoot,
            Terrain terrain,
            List<Vector2> bridgeCenters,
            Vector2[] mainRiverSpline,
            Vector2[] creekSpline,
            float seaLevelY)
        {
            HubBlueprint[] hubBlueprints = BuildContinentHubBlueprints();
            var records = new List<WorldMapSettlementRecord>(180);
            var occupied = new List<Vector3>(180); // (x, z, radius)

            // Keep player spawn point (0,0) clean so commander starts on open ground near Ankara
            occupied.Add(new Vector3(0f, 0f, 36f));

            System.Random rng = new System.Random(20261127);

            // 1. Snap all 43 Hubs (5 Capitals + 14 Cities + 24 Military Bases) widely across the entire continent
            var placedHubs = new List<WorldMapSettlementRecord>(hubBlueprints.Length);
            for (int i = 0; i < hubBlueprints.Length; i++)
            {
                HubBlueprint hb = hubBlueprints[i];
                float hubClearance = hb.Kind == WorldMapSettlementKind.Capital ? 48f
                    : (hb.Kind == WorldMapSettlementKind.City ? 36f : 28f);

                Vector2 snappedXZ = FindCleanSettlementSite(
                    hb.IdealPos, terrain, bridgeCenters, mainRiverSpline, creekSpline,
                    occupied, minSpacing: 185f, maxSlopeDeg: 16.5f, seaLevelY, maxSearchRadius: 210f);

                float y = SampleTerrainY(terrain, snappedXZ);
                var rec = new WorldMapSettlementRecord
                {
                    id = hb.Id,
                    displayName = hb.Name,
                    faction = hb.Faction,
                    kind = hb.Kind,
                    parentHubId = string.Empty,
                    worldPosition = new Vector3(snappedXZ.x, y, snappedXZ.y),
                    yawDeg = (float)rng.NextDouble() * 360f,
                    clearanceRadius = hubClearance,
                    isVisuallyBuilt = true
                };

                placedHubs.Add(rec);
                records.Add(rec);
                occupied.Add(new Vector3(snappedXZ.x, snappedXZ.y, hubClearance));
            }

            // 2. Place 124 Linked Villages at generous Bannerlord distances (115m-185m from parent hub, >=105m apart!)
            int[] factionVillageIdx = new int[5];
            for (int h = 0; h < hubBlueprints.Length; h++)
            {
                HubBlueprint hb = hubBlueprints[h];
                WorldMapSettlementRecord parentHub = placedHubs[h];
                int vCount = hb.VillageCount;
                float baseAngle = (float)(rng.NextDouble() * Math.PI * 2.0);

                for (int v = 0; v < vCount; v++)
                {
                    float sectorAngle = baseAngle + (v / (float)vCount) * Mathf.PI * 2f + ((float)rng.NextDouble() - 0.5f) * 0.45f;
                    float orbitDist = Mathf.Lerp(118f, 178f, (float)rng.NextDouble());
                    Vector2 idealVillageXZ = new Vector2(
                        parentHub.worldPosition.x + Mathf.Cos(sectorAngle) * orbitDist,
                        parentHub.worldPosition.z + Mathf.Sin(sectorAngle) * orbitDist);

                    idealVillageXZ.x = Mathf.Clamp(idealVillageXZ.x, -940f, 835f);
                    idealVillageXZ.y = Mathf.Clamp(idealVillageXZ.y, -940f, 795f);

                    Vector2 snappedXZ = FindCleanSettlementSite(
                        idealVillageXZ, terrain, bridgeCenters, mainRiverSpline, creekSpline,
                        occupied, minSpacing: 105f, maxSlopeDeg: 18f, seaLevelY, maxSearchRadius: 210f);

                    float y = SampleTerrainY(terrain, snappedXZ);
                    int fIdx = (int)hb.Faction;
                    string vId = $"{hb.Id}_V{v + 1}";
                    string vName = GetAuthenticVillageName(hb.Faction, factionVillageIdx[fIdx]++);

                    var vRec = new WorldMapSettlementRecord
                    {
                        id = vId,
                        displayName = vName,
                        faction = hb.Faction,
                        kind = WorldMapSettlementKind.Village,
                        parentHubId = hb.Id,
                        worldPosition = new Vector3(snappedXZ.x, y, snappedXZ.y),
                        yawDeg = (float)rng.NextDouble() * 360f,
                        clearanceRadius = 22f,
                        isVisuallyBuilt = true
                    };

                    records.Add(vRec);
                    occupied.Add(new Vector3(snappedXZ.x, snappedXZ.y, 24f));
                }
            }

            WorldMapSettlementRegistry registry = environmentRoot.GetComponent<WorldMapSettlementRegistry>();
            if (registry == null)
                registry = environmentRoot.gameObject.AddComponent<WorldMapSettlementRegistry>();
            registry.SetRecords(records);
            EditorUtility.SetDirty(registry);

            return records;
        }

        /// <summary>
        /// Bakes ALL 167 Settlements (5 Monumental Capitals + 14 Walled Cities + 24 Military Bases + 124 Compact Villages)
        /// into frustum-culled 10x10 static spatial chunks using 1 shared matte URP material.
        /// </summary>
        public static void BuildAllSettlementVisuals(Transform environmentRoot, Terrain terrain, List<WorldMapSettlementRecord> records)
        {
            if (terrain == null || records == null)
                return;

            EnsureFolder("Assets/Materials/WorldMap", "VillageChunks");

            Shader settleShader = Shader.Find("VRAtis/WorldMapSettlementURP")
                ?? Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Standard");

            Material settleMat = LoadOrCreateMaterial(SettlementMatPath, settleShader);
            EditorUtility.SetDirty(settleMat);

            GameObject settlementsRoot = new GameObject("WorldMap_Settlements");
            settlementsRoot.transform.SetParent(environmentRoot, false);
            settlementsRoot.isStatic = true;

            const int chunkGrid = 10;
            const float minCoord = -1020f;
            const float maxCoord = 980f;
            const float span = maxCoord - minCoord;

            SettlementChunkBuilder[,] chunks = new SettlementChunkBuilder[chunkGrid, chunkGrid];

            for (int i = 0; i < records.Count; i++)
            {
                WorldMapSettlementRecord rec = records[i];
                Vector3 wp = rec.worldPosition;
                int cx = Mathf.Clamp(Mathf.FloorToInt(((wp.x - minCoord) / span) * chunkGrid), 0, chunkGrid - 1);
                int cz = Mathf.Clamp(Mathf.FloorToInt(((wp.z - minCoord) / span) * chunkGrid), 0, chunkGrid - 1);

                SettlementChunkBuilder cb = chunks[cz, cx];
                if (cb == null)
                {
                    cb = new SettlementChunkBuilder();
                    chunks[cz, cx] = cb;
                }

                int seed = rec.id.GetHashCode();
                switch (rec.kind)
                {
                    case WorldMapSettlementKind.Village:
                        BuildCompactVillage(cb, terrain, rec.faction, wp, rec.yawDeg, seed);
                        break;
                    case WorldMapSettlementKind.MilitaryBase:
                        BuildMilitaryBase(cb, terrain, rec.faction, wp, rec.yawDeg, seed);
                        break;
                    case WorldMapSettlementKind.City:
                        BuildWalledCity(cb, terrain, rec.faction, wp, rec.yawDeg, seed, isCapital: false);
                        break;
                    case WorldMapSettlementKind.Capital:
                        BuildWalledCity(cb, terrain, rec.faction, wp, rec.yawDeg, seed, isCapital: true);
                        break;
                }
            }

            int activeChunks = 0;
            int totalTris = 0;
            for (int cz = 0; cz < chunkGrid; cz++)
            {
                for (int cx = 0; cx < chunkGrid; cx++)
                {
                    string meshPath = $"{SettlementChunksFolder}/WorldMapVillages_Chunk_{cx}_{cz}.asset";
                    SettlementChunkBuilder cb = chunks[cz, cx];
                    if (cb == null || cb.Verts.Count == 0)
                    {
                        if (AssetDatabase.LoadAssetAtPath<Mesh>(meshPath) != null)
                            AssetDatabase.DeleteAsset(meshPath);
                        continue;
                    }

                    Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
                    if (mesh == null)
                    {
                        mesh = new Mesh { name = $"WorldMapSettlements_Chunk_{cx}_{cz}" };
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
                    mesh.SetColors(cb.Colors);
                    mesh.SetTriangles(cb.Tris, 0);
                    mesh.RecalculateBounds();
                    EditorUtility.SetDirty(mesh);

                    GameObject chunkGo = new GameObject($"SettlementChunk_{cx}_{cz}");
                    chunkGo.transform.SetParent(settlementsRoot.transform, false);
                    chunkGo.isStatic = true;

                    MeshFilter mf = chunkGo.AddComponent<MeshFilter>();
                    mf.sharedMesh = mesh;

                    MeshRenderer mr = chunkGo.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = settleMat;
                    mr.shadowCastingMode = ShadowCastingMode.On;
                    mr.receiveShadows = true;

                    activeChunks++;
                    totalTris += cb.Tris.Count / 3;
                }
            }

            Debug.Log($"[WorldMapSettlementBuilder] 4-katmanlı yerleşkeler kuruldu: {records.Count} toplam yerleşke ({activeChunks} chunk, {totalTris} toplam üçgen).");
        }

        // =========================================================================
        // FACTION COLOR PALETTES (Instant cultural recognition across all 4 tiers!)
        // =========================================================================

        private struct FactionPalette
        {
            public Color WallPrimary;
            public Color WallSecondary;
            public Color RoofPrimary;
            public Color RoofAccent;
            public Color FortressWall;
            public Color BannerColor;
        }

        private static FactionPalette GetPalette(WorldMapFaction faction)
        {
            switch (faction)
            {
                case WorldMapFaction.Turkey:
                    return new FactionPalette
                    {
                        WallPrimary   = LinCol(new Color(0.87f, 0.83f, 0.75f), 0.94f),
                        WallSecondary = LinCol(new Color(0.75f, 0.70f, 0.61f), 0.90f),
                        RoofPrimary   = LinCol(new Color(0.70f, 0.27f, 0.16f), 0.96f),
                        RoofAccent    = LinCol(new Color(0.42f, 0.46f, 0.50f), 0.98f), // lead dome
                        FortressWall  = LinCol(new Color(0.70f, 0.65f, 0.57f), 0.90f),
                        BannerColor   = LinCol(new Color(0.82f, 0.11f, 0.14f), 1.00f)
                    };
                case WorldMapFaction.Soviet:
                    return new FactionPalette
                    {
                        WallPrimary   = LinCol(new Color(0.42f, 0.30f, 0.20f), 0.88f),
                        WallSecondary = LinCol(new Color(0.56f, 0.25f, 0.21f), 0.92f), // Soviet brick
                        RoofPrimary   = LinCol(new Color(0.24f, 0.27f, 0.30f), 0.95f),
                        RoofAccent    = LinCol(new Color(0.76f, 0.62f, 0.22f), 1.00f), // golden dome/star
                        FortressWall  = LinCol(new Color(0.50f, 0.24f, 0.20f), 0.90f), // Kremlin red brick
                        BannerColor   = LinCol(new Color(0.78f, 0.08f, 0.08f), 1.00f)
                    };
                case WorldMapFaction.USA:
                    return new FactionPalette
                    {
                        WallPrimary   = LinCol(new Color(0.88f, 0.88f, 0.85f), 0.96f),
                        WallSecondary = LinCol(new Color(0.66f, 0.71f, 0.76f), 0.93f),
                        RoofPrimary   = LinCol(new Color(0.28f, 0.30f, 0.33f), 0.95f),
                        RoofAccent    = LinCol(new Color(0.64f, 0.16f, 0.14f), 0.95f), // classic red barn
                        FortressWall  = LinCol(new Color(0.54f, 0.58f, 0.62f), 0.92f), // reinforced concrete/steel
                        BannerColor   = LinCol(new Color(0.14f, 0.28f, 0.72f), 1.00f)
                    };
                case WorldMapFaction.China:
                    return new FactionPalette
                    {
                        WallPrimary   = LinCol(new Color(0.87f, 0.83f, 0.72f), 0.94f),
                        WallSecondary = LinCol(new Color(0.68f, 0.14f, 0.12f), 0.95f), // imperial crimson
                        RoofPrimary   = LinCol(new Color(0.20f, 0.25f, 0.31f), 0.96f), // slate pagoda tiles
                        RoofAccent    = LinCol(new Color(0.88f, 0.66f, 0.14f), 1.00f), // imperial gold roof
                        FortressWall  = LinCol(new Color(0.52f, 0.50f, 0.47f), 0.90f), // Great Wall stone
                        BannerColor   = LinCol(new Color(0.90f, 0.68f, 0.14f), 1.00f)
                    };
                default: // Rebels / Pirates
                    return new FactionPalette
                    {
                        WallPrimary   = LinCol(new Color(0.66f, 0.55f, 0.40f), 0.90f),
                        WallSecondary = LinCol(new Color(0.42f, 0.28f, 0.22f), 0.88f), // rusty scrap
                        RoofPrimary   = LinCol(new Color(0.54f, 0.46f, 0.34f), 0.92f), // canvas tent
                        RoofAccent    = LinCol(new Color(0.22f, 0.20f, 0.19f), 0.88f), // charcoal tarp
                        FortressWall  = LinCol(new Color(0.40f, 0.32f, 0.24f), 0.88f), // timber/scrap rampart
                        BannerColor   = LinCol(new Color(0.08f, 0.08f, 0.09f), 1.00f)  // black pirate flag
                    };
            }
        }

        // =========================================================================
        // TIER 1: COMPACT VILLAGE (~13m footprint — CLEARLY SMALLEST!)
        // =========================================================================

        private static void BuildCompactVillage(SettlementChunkBuilder cb, Terrain terrain, WorldMapFaction faction, Vector3 center, float yawDeg, int seed)
        {
            Quaternion rot = Quaternion.Euler(0f, yawDeg, 0f);
            FactionPalette pal = GetPalette(faction);

            // Compact 3-4 tiny village cottages + 1 modest cultural centerpiece
            Vector4[] huts =
            {
                new Vector4(-4.8f,  3.2f, 3.0f, 2.3f),
                new Vector4( 4.9f, -2.5f, 3.1f, 2.4f),
                new Vector4(-3.8f, -4.4f, 2.8f, 2.2f),
                new Vector4( 3.6f,  4.4f, 2.7f, 2.1f)
            };

            for (int i = 0; i < huts.Length; i++)
            {
                float hYaw = yawDeg + ((seed + i * 53) % 70) - 35f;
                Quaternion hRot = Quaternion.Euler(0f, hYaw, 0f);
                Vector3 hp = GroundedPoint(terrain, center, rot, new Vector2(huts[i].x, huts[i].y));
                float w = huts[i].z;
                float d = huts[i].w;
                float h = 1.9f;

                if (faction == WorldMapFaction.China)
                {
                    AppendBox(cb, hp, hRot, new Vector3(w, h, d), -1.2f, pal.WallPrimary);
                    AppendPagodaRoof(cb, hp + new Vector3(0f, h - 1.2f, 0f), hRot, w + 0.9f, d + 0.9f, 1.15f, pal.RoofPrimary);
                }
                else if (faction == WorldMapFaction.Rebels)
                {
                    if (i % 2 == 0)
                    {
                        AppendBox(cb, hp, hRot, new Vector3(w, h, d), -1.2f, pal.WallPrimary);
                        AppendBox(cb, hp + new Vector3(0f, h - 1.2f, 0f), hRot, new Vector3(w + 0.25f, 0.28f, d + 0.25f), 0f, pal.WallSecondary);
                    }
                    else
                    {
                        AppendPitchedRoof(cb, hp + new Vector3(0f, -0.35f, 0f), hRot, w + 0.4f, d + 0.4f, 1.95f, pal.RoofPrimary);
                    }
                }
                else
                {
                    AppendBox(cb, hp, hRot, new Vector3(w, h, d), -1.2f, (i % 2 == 0) ? pal.WallPrimary : pal.WallSecondary);
                    float roofH = faction == WorldMapFaction.Soviet ? 1.65f : 1.15f;
                    AppendPitchedRoof(cb, hp + new Vector3(0f, h - 1.2f, 0f), hRot, w + 0.45f, d + 0.45f, roofH, pal.RoofPrimary);
                }
            }

            // Modest cultural centerpiece (clearly small compared to cities/capitals!)
            Vector3 cPos = GroundedPoint(terrain, center, rot, Vector2.zero);
            switch (faction)
            {
                case WorldMapFaction.Turkey:
                    AppendBox(cb, cPos, rot, new Vector3(3.2f, 2.5f, 3.2f), -1.2f, pal.WallPrimary);
                    AppendDome(cb, cPos + new Vector3(0f, 1.3f, 0f), 1.45f, 1.35f, 7, pal.RoofAccent);
                    Vector3 minP = GroundedPoint(terrain, center, rot, new Vector2(1.8f, 1.6f));
                    AppendCylinder(cb, minP, 0.38f, 0.30f, -1.0f, 5.2f, 6, pal.WallPrimary);
                    AppendCone(cb, minP, 0.36f, 5.15f, 1.4f, 6, pal.RoofAccent);
                    break;

                case WorldMapFaction.Soviet:
                    AppendCylinder(cb, cPos, 1.45f, 1.35f, -1.2f, 4.9f, 7, pal.WallSecondary);
                    AppendCone(cb, cPos, 1.75f, 4.8f, 1.6f, 7, pal.RoofPrimary);
                    break;

                case WorldMapFaction.USA:
                    AppendBox(cb, cPos, rot, new Vector3(4.2f, 2.8f, 3.0f), -1.2f, pal.RoofAccent); // small red barn
                    AppendPitchedRoof(cb, cPos + new Vector3(0f, 1.6f, 0f), rot, 4.55f, 3.35f, 1.6f, pal.RoofPrimary);
                    Vector3 wtP = GroundedPoint(terrain, center, rot, new Vector2(3.1f, 2.0f));
                    AppendCylinder(cb, wtP, 0.32f, 0.25f, -1.0f, 3.4f, 5, pal.FortressWall);
                    AppendCylinder(cb, wtP, 1.15f, 1.15f, 3.3f, 5.1f, 7, pal.WallSecondary);
                    AppendCone(cb, wtP, 1.35f, 5.05f, 1.0f, 7, pal.FortressWall);
                    break;

                case WorldMapFaction.China:
                    AppendBox(cb, cPos, rot, new Vector3(3.3f, 2.3f, 3.3f), -1.2f, pal.WallSecondary);
                    AppendPagodaRoof(cb, cPos + new Vector3(0f, 1.05f, 0f), rot, 4.8f, 4.8f, 1.55f, pal.RoofPrimary);
                    break;

                default: // Rebels
                    AppendBox(cb, cPos, rot, new Vector3(2.2f, 4.2f, 2.2f), -1.2f, pal.FortressWall);
                    AppendBox(cb, cPos + new Vector3(0f, 2.8f, 0f), rot, new Vector3(2.8f, 0.7f, 2.8f), 0f, pal.WallSecondary);
                    AppendPitchedRoof(cb, cPos + new Vector3(0f, 3.6f, 0f), rot, 3.0f, 3.0f, 0.7f, pal.RoofAccent);
                    break;
            }

            AppendBanner(cb, GroundedPoint(terrain, center, rot, new Vector2(-2.0f, 2.8f)), rot, pal.BannerColor, poleHeight: 6.2f, flagScale: 0.78f);
        }

        // =========================================================================
        // TIER 2: MILITARY BASE / GARRISON (~24m fortified square compound!)
        // =========================================================================

        private static void BuildMilitaryBase(SettlementChunkBuilder cb, Terrain terrain, WorldMapFaction faction, Vector3 center, float yawDeg, int seed)
        {
            Quaternion rot = Quaternion.Euler(0f, yawDeg, 0f);
            FactionPalette pal = GetPalette(faction);
            Color darkMetal = LinCol(new Color(0.24f, 0.26f, 0.28f), 0.92f);

            const float half = 10.5f;

            // 4 Fortified Perimeter Walls (thick garrison ramparts!)
            AppendBox(cb, GroundedPoint(terrain, center, rot, new Vector2(0f,  half)), rot, new Vector3(half * 2f, 4.4f, 2.1f), -1.8f, pal.FortressWall);
            AppendBox(cb, GroundedPoint(terrain, center, rot, new Vector2(-half, 0f)), rot, new Vector3(2.1f, 4.4f, half * 2f), -1.8f, pal.FortressWall);
            AppendBox(cb, GroundedPoint(terrain, center, rot, new Vector2( half, 0f)), rot, new Vector3(2.1f, 4.4f, half * 2f), -1.8f, pal.FortressWall);
            // Front gatehouse wall with central entrance gap
            AppendBox(cb, GroundedPoint(terrain, center, rot, new Vector2(-6.2f, -half)), rot, new Vector3(8.5f, 4.4f, 2.1f), -1.8f, pal.FortressWall);
            AppendBox(cb, GroundedPoint(terrain, center, rot, new Vector2( 6.2f, -half)), rot, new Vector3(8.5f, 4.4f, 2.1f), -1.8f, pal.FortressWall);
            AppendBox(cb, GroundedPoint(terrain, center, rot, new Vector2(0f, -half)) + new Vector3(0f, 3.2f, 0f), rot, new Vector3(5.6f, 2.2f, 2.6f), 0f, pal.FortressWall);

            // 4 Tall Corner Watchtowers / Bastions (distinctive military base silhouette!)
            Vector2[] corners =
            {
                new Vector2(-half, -half), new Vector2(half, -half),
                new Vector2(-half,  half), new Vector2(half,  half)
            };
            for (int c = 0; c < 4; c++)
            {
                Vector3 tp = GroundedPoint(terrain, center, rot, corners[c]);
                AppendBox(cb, tp, rot, new Vector3(3.8f, 7.6f, 3.8f), -1.8f, pal.FortressWall);
                if (faction == WorldMapFaction.China)
                    AppendPagodaRoof(cb, tp + new Vector3(0f, 5.7f, 0f), rot, 5.4f, 5.4f, 1.9f, pal.RoofPrimary);
                else if (faction == WorldMapFaction.USA)
                    AppendBox(cb, tp + new Vector3(0f, 5.7f, 0f), rot, new Vector3(4.3f, 0.9f, 4.3f), 0f, darkMetal);
                else
                    AppendPyramidRoof(cb, tp + new Vector3(0f, 5.7f, 0f), rot, 4.4f, 4.4f, 2.0f, pal.RoofPrimary);
            }

            // Central Command Bunker / Barracks + Tall Comms / Radar Mast!
            Vector3 hqPos = GroundedPoint(terrain, center, rot, new Vector2(0f, 1.2f));
            AppendBox(cb, hqPos, rot, new Vector3(8.6f, 5.4f, 6.2f), -1.8f, pal.WallSecondary);
            if (faction == WorldMapFaction.China)
                AppendPagodaRoof(cb, hqPos + new Vector3(0f, 3.5f, 0f), rot, 10.6f, 8.0f, 2.3f, pal.RoofPrimary);
            else if (faction == WorldMapFaction.USA || faction == WorldMapFaction.Soviet)
                AppendBox(cb, hqPos + new Vector3(0f, 3.5f, 0f), rot, new Vector3(9.0f, 0.8f, 6.6f), 0f, darkMetal);
            else
                AppendPitchedRoof(cb, hqPos + new Vector3(0f, 3.5f, 0f), rot, 9.2f, 6.8f, 2.1f, pal.RoofPrimary);

            // Tall Military Comms / Radar Tower rising above HQ
            Vector3 mastPos = GroundedPoint(terrain, center, rot, new Vector2(3.6f, 2.4f));
            AppendCylinder(cb, mastPos, 0.55f, 0.25f, -1.0f, 12.2f, 6, darkMetal);
            AppendBox(cb, mastPos + new Vector3(0f, 10.6f, 0f), rot, new Vector3(2.8f, 0.7f, 0.7f), 0f, pal.WallPrimary);

            // Secondary Barracks Wing
            Vector3 barPos = GroundedPoint(terrain, center, rot, new Vector2(-4.4f, -4.2f));
            AppendBox(cb, barPos, rot, new Vector3(5.4f, 3.4f, 3.4f), -1.5f, pal.WallPrimary);
            AppendPitchedRoof(cb, barPos + new Vector3(0f, 1.9f, 0f), rot, 5.8f, 3.8f, 1.3f, pal.RoofPrimary);

            AppendBanner(cb, GroundedPoint(terrain, center, rot, new Vector2(-3.8f, 5.2f)), rot, pal.BannerColor, poleHeight: 11.5f, flagScale: 1.25f);
        }

        // =========================================================================
        // TIER 3 & TIER 4: WALLED CITY (~36m) & MONUMENTAL CAPITAL (~54m!)
        // =========================================================================

        private static void BuildWalledCity(
            SettlementChunkBuilder cb,
            Terrain terrain,
            WorldMapFaction faction,
            Vector3 center,
            float yawDeg,
            int seed,
            bool isCapital)
        {
            Quaternion rot = Quaternion.Euler(0f, yawDeg, 0f);
            FactionPalette pal = GetPalette(faction);
            Color goldAccent = LinCol(new Color(0.90f, 0.72f, 0.20f), 1.0f);

            float wallRadius = isCapital ? 24.5f : 16.0f;
            float wallHeight = isCapital ? 6.8f : 5.0f;
            int sides = 8;

            // 1. Grand Octagonal Fortified City Walls + Corner Bastion Towers
            for (int s = 0; s < sides; s++)
            {
                float a0 = (s / (float)sides) * Mathf.PI * 2f;
                float a1 = ((s + 1) / (float)sides) * Mathf.PI * 2f;
                float aMid = (a0 + a1) * 0.5f;

                Vector2 v0 = new Vector2(Mathf.Cos(a0) * wallRadius, Mathf.Sin(a0) * wallRadius);
                Vector2 v1 = new Vector2(Mathf.Cos(a1) * wallRadius, Mathf.Sin(a1) * wallRadius);
                Vector2 mid = (v0 + v1) * 0.5f;
                float segLen = Vector2.Distance(v0, v1) + 1.2f;
                Quaternion wallRot = rot * Quaternion.Euler(0f, -Mathf.Atan2(v1.y - v0.y, v1.x - v0.x) * Mathf.Rad2Deg, 0f);

                // Curtain Wall Segment (leave South gate s==6 slightly taller as Grand Gatehouse!)
                Vector3 wp = GroundedPoint(terrain, center, rot, mid);
                AppendBox(cb, wp, wallRot, new Vector3(segLen, wallHeight, isCapital ? 2.8f : 2.1f), -2.2f, pal.FortressWall);
                if (s == 6)
                {
                    AppendBox(cb, wp + new Vector3(0f, wallHeight - 2.2f, 0f), wallRot, new Vector3(segLen * 0.55f, isCapital ? 4.2f : 2.8f, 3.6f), 0f, pal.FortressWall);
                    if (faction == WorldMapFaction.China)
                        AppendPagodaRoof(cb, wp + new Vector3(0f, wallHeight + (isCapital ? 1.9f : 0.5f), 0f), wallRot, segLen * 0.72f, 5.2f, 2.4f, pal.RoofAccent);
                }

                // Corner Bastion Tower
                Vector3 tp = GroundedPoint(terrain, center, rot, v0);
                float tW = isCapital ? 4.8f : 3.5f;
                float tH = wallHeight + (isCapital ? 4.4f : 3.0f);
                Quaternion tRot = rot * Quaternion.Euler(0f, -a0 * Mathf.Rad2Deg, 0f);
                AppendBox(cb, tp, tRot, new Vector3(tW, tH, tW), -2.2f, pal.FortressWall);
                if (faction == WorldMapFaction.China)
                    AppendPagodaRoof(cb, tp + new Vector3(0f, tH - 2.2f, 0f), tRot, tW + 2.2f, tW + 2.2f, 2.4f, isCapital ? pal.RoofAccent : pal.RoofPrimary);
                else if (faction == WorldMapFaction.Soviet)
                    AppendCone(cb, tp, tW * 0.65f, tH - 2.3f, isCapital ? 3.8f : 2.6f, 6, pal.RoofPrimary);
                else
                    AppendPyramidRoof(cb, tp + new Vector3(0f, tH - 2.2f, 0f), tRot, tW + 0.9f, tW + 0.9f, 2.2f, pal.RoofPrimary);
            }

            // 2. Capital-Only Inner Citadel Ring (makes Capitals UNMISTAKABLY HUGE!)
            if (isCapital)
            {
                const float innerR = 13.5f;
                for (int s = 0; s < 6; s++)
                {
                    float a0 = (s / 6f) * Mathf.PI * 2f;
                    float a1 = ((s + 1) / 6f) * Mathf.PI * 2f;
                    Vector2 v0 = new Vector2(Mathf.Cos(a0) * innerR, Mathf.Sin(a0) * innerR);
                    Vector2 v1 = new Vector2(Mathf.Cos(a1) * innerR, Mathf.Sin(a1) * innerR);
                    Vector2 mid = (v0 + v1) * 0.5f;
                    float segLen = Vector2.Distance(v0, v1) + 0.8f;
                    Quaternion wRot = rot * Quaternion.Euler(0f, -Mathf.Atan2(v1.y - v0.y, v1.x - v0.x) * Mathf.Rad2Deg, 0f);
                    AppendBox(cb, GroundedPoint(terrain, center, rot, mid), wRot, new Vector3(segLen, 8.2f, 2.2f), -2.0f, pal.WallSecondary);
                }
            }

            // 3. Dense Inner Urban District Ring (houses & civic blocks inside the walls)
            int houseRingCount = isCapital ? 12 : 8;
            float ringR = isCapital ? 18.2f : 10.4f;
            for (int i = 0; i < houseRingCount; i++)
            {
                float ang = (i / (float)houseRingCount) * Mathf.PI * 2f + 0.2f;
                Vector2 offset = new Vector2(Mathf.Cos(ang) * ringR, Mathf.Sin(ang) * ringR);
                Vector3 hp = GroundedPoint(terrain, center, rot, offset);
                Quaternion hRot = rot * Quaternion.Euler(0f, -ang * Mathf.Rad2Deg + 90f, 0f);
                float w = isCapital ? 5.2f : 4.2f;
                float d = isCapital ? 3.8f : 3.2f;
                float h = (isCapital ? 4.4f : 3.2f) + (i % 3) * 0.85f;

                Color wCol = (i % 2 == 0) ? pal.WallPrimary : pal.WallSecondary;
                AppendBox(cb, hp, hRot, new Vector3(w, h, d), -1.8f, wCol);
                if (faction == WorldMapFaction.China)
                    AppendPagodaRoof(cb, hp + new Vector3(0f, h - 1.8f, 0f), hRot, w + 1.5f, d + 1.5f, 1.8f, pal.RoofPrimary);
                else if (faction == WorldMapFaction.USA && isCapital && i % 2 == 0)
                    AppendBox(cb, hp + new Vector3(0f, h - 1.8f, 0f), hRot, new Vector3(w * 0.85f, 2.6f, d * 0.85f), 0f, pal.WallSecondary); // modern city mid-rise!
                else
                    AppendPitchedRoof(cb, hp + new Vector3(0f, h - 1.8f, 0f), hRot, w + 0.6f, d + 0.6f, 1.8f, pal.RoofPrimary);
            }

            // 4. Towering Cultural Centerpiece (Grand Mosque / Kremlin / Capitol / Forbidden Palace / Pirate Citadel!)
            Vector3 cPos = GroundedPoint(terrain, center, rot, Vector2.zero);
            float scale = isCapital ? 1.50f : 1.00f;

            switch (faction)
            {
                case WorldMapFaction.Turkey:
                    // Grand Domed Complex + Twin (City) or Quad (Capital) Tall Minarets!
                    AppendBox(cb, cPos, rot, new Vector3(8.5f, 6.4f, 8.5f) * scale, -2.0f, pal.WallPrimary);
                    AppendDome(cb, cPos + new Vector3(0f, 4.4f * scale, 0f), 4.2f * scale, 4.2f * scale, 10, pal.RoofAccent);
                    AppendCone(cb, cPos, 0.35f * scale, 8.5f * scale, 1.6f * scale, 6, goldAccent);
                    int minarets = isCapital ? 4 : 2;
                    for (int m = 0; m < minarets; m++)
                    {
                        float ma = (m / (float)minarets) * Mathf.PI * 2f + 0.78f;
                        Vector2 mo = new Vector2(Mathf.Cos(ma), Mathf.Sin(ma)) * (6.2f * scale);
                        Vector3 mp = GroundedPoint(terrain, center, rot, mo);
                        AppendCylinder(cb, mp, 0.75f * scale, 0.55f * scale, -1.6f, 11.5f * scale, 7, pal.WallPrimary);
                        AppendCone(cb, mp, 0.65f * scale, 11.4f * scale, 3.0f * scale, 7, pal.RoofAccent);
                    }
                    break;

                case WorldMapFaction.Soviet:
                    // Grand Kremlin Red-Brick Citadel + Towering Spasskaya Clock/Star Spire + Golden Domes!
                    AppendBox(cb, cPos, rot, new Vector3(9.0f, 7.6f, 8.2f) * scale, -2.0f, pal.WallSecondary);
                    AppendBox(cb, cPos + new Vector3(0f, 5.6f * scale, 0f), rot, new Vector3(4.6f, 5.5f, 4.6f) * scale, 0f, pal.WallSecondary);
                    AppendCone(cb, cPos, 2.8f * scale, 11.0f * scale, 5.4f * scale, 6, pal.RoofPrimary);
                    AppendCone(cb, cPos, 0.75f * scale, 16.0f * scale, 1.8f * scale, 5, pal.BannerColor); // red star spire!
                    AppendDome(cb, cPos + rot * new Vector3(-4.2f * scale, 5.6f * scale, 0f), 2.1f * scale, 2.8f * scale, 8, pal.RoofAccent);
                    AppendDome(cb, cPos + rot * new Vector3( 4.2f * scale, 5.6f * scale, 0f), 2.1f * scale, 2.8f * scale, 8, pal.RoofAccent);
                    break;

                case WorldMapFaction.USA:
                    // Grand White Capitol Rotunda + Modern Skyline Towers (City) / Twin Skyscrapers (Capital)!
                    AppendBox(cb, cPos, rot, new Vector3(10.5f, 5.8f, 6.8f) * scale, -2.0f, pal.WallPrimary);
                    AppendCylinder(cb, cPos, 3.2f * scale, 3.0f * scale, 3.8f * scale, 7.2f * scale, 10, pal.WallPrimary);
                    AppendDome(cb, cPos + new Vector3(0f, 7.1f * scale, 0f), 3.1f * scale, 3.8f * scale, 10, pal.WallPrimary);
                    AppendCone(cb, cPos, 0.45f * scale, 10.8f * scale, 2.4f * scale, 6, goldAccent);
                    // Tall modern glass-steel towers flanking the capitol
                    Vector3 sky1 = GroundedPoint(terrain, center, rot, new Vector2(-6.5f * scale, 5.2f * scale));
                    AppendBox(cb, sky1, rot, new Vector3(3.8f, 11.5f, 3.8f) * scale, -1.8f, pal.WallSecondary);
                    if (isCapital)
                    {
                        Vector3 sky2 = GroundedPoint(terrain, center, rot, new Vector2(6.5f * scale, 5.2f * scale));
                        AppendBox(cb, sky2, rot, new Vector3(4.0f, 13.8f, 4.0f) * scale, -1.8f, pal.WallSecondary);
                    }
                    break;

                case WorldMapFaction.China:
                    // Monumental 3-Tier Imperial Forbidden Palace Pagoda with Golden Roofs!
                    AppendBox(cb, cPos, rot, new Vector3(9.4f, 5.2f, 9.4f) * scale, -2.0f, pal.WallSecondary);
                    AppendPagodaRoof(cb, cPos + new Vector3(0f, 3.1f * scale, 0f), rot, 13.2f * scale, 13.2f * scale, 2.8f * scale, isCapital ? pal.RoofAccent : pal.RoofPrimary);
                    AppendBox(cb, cPos + new Vector3(0f, 5.6f * scale, 0f), rot, new Vector3(6.4f, 3.6f, 6.4f) * scale, 0f, pal.WallSecondary);
                    AppendPagodaRoof(cb, cPos + new Vector3(0f, 9.0f * scale, 0f), rot, 9.8f * scale, 9.8f * scale, 2.6f * scale, isCapital ? pal.RoofAccent : pal.RoofPrimary);
                    AppendBox(cb, cPos + new Vector3(0f, 11.3f * scale, 0f), rot, new Vector3(3.8f, 2.6f, 3.8f) * scale, 0f, pal.WallPrimary);
                    AppendPagodaRoof(cb, cPos + new Vector3(0f, 13.7f * scale, 0f), rot, 6.2f * scale, 6.2f * scale, 2.6f * scale, pal.RoofAccent);
                    break;

                default: // Rebels / Pirates
                    // Massive Rugged Desert Pirate Citadel / Fortress Keep with Jagged Spires!
                    AppendBox(cb, cPos, rot, new Vector3(9.2f, 7.0f, 8.6f) * scale, -2.0f, pal.WallPrimary);
                    AppendBox(cb, cPos + new Vector3(0f, 5.0f * scale, 0f), rot, new Vector3(5.6f, 4.8f, 5.6f) * scale, 0f, pal.WallSecondary);
                    AppendPitchedRoof(cb, cPos + new Vector3(0f, 9.8f * scale, 0f), rot, 6.2f * scale, 6.2f * scale, 2.6f * scale, pal.RoofAccent);
                    Vector3 mastP = GroundedPoint(terrain, center, rot, new Vector2(4.4f * scale, -4.0f * scale));
                    AppendCylinder(cb, mastP, 0.8f * scale, 0.5f * scale, -1.6f, 12.0f * scale, 6, pal.FortressWall);
                    break;
            }

            // Grand Faction Banner(s) flying high above the City / Capital
            AppendBanner(cb, GroundedPoint(terrain, center, rot, new Vector2(0f, -wallRadius + 2.5f)), rot, pal.BannerColor,
                poleHeight: isCapital ? 20.5f : 14.5f, flagScale: isCapital ? 2.15f : 1.55f);

            if (isCapital)
            {
                AppendBanner(cb, GroundedPoint(terrain, center, rot, new Vector2(8.5f, 8.5f)), rot, goldAccent,
                    poleHeight: 18.5f, flagScale: 1.75f);
            }
        }

        private static void AppendBanner(SettlementChunkBuilder cb, Vector3 basePos, Quaternion rot, Color flagColor, float poleHeight, float flagScale)
        {
            Color poleCol = LinCol(new Color(0.28f, 0.22f, 0.16f), 0.90f);
            float rBot = 0.22f * Mathf.Sqrt(flagScale);
            AppendCylinder(cb, basePos, rBot, rBot * 0.65f, -1.4f, poleHeight, 5, poleCol);
            float fw = 3.2f * flagScale;
            float fh = 1.8f * flagScale;
            AppendBox(cb, basePos + rot * new Vector3(fw * 0.52f, poleHeight - fh * 0.55f, 0f), rot, new Vector3(fw, fh, 0.20f), 0f, flagColor);
        }

        // =========================================================================
        // WIDELY SPACED 5-FACTION CONTINENT BLUEPRINT (2400m x 2400m)
        // 5 Capitals + 14 Cities + 24 Military Bases = 43 Hubs, 124 Linked Villages
        // =========================================================================

        private static HubBlueprint[] BuildContinentHubBlueprints()
        {
            return new[]
            {
                // 1) TÜRKİYE — Central Heartland & Great Bridge Crossroads (1 Başkent + 3 Şehir + 5 Askeri Üs = 26 Köy)
                new HubBlueprint("TR_CAP", "Ankara",               WorldMapFaction.Turkey, WorldMapSettlementKind.Capital,       45f,   45f, 4),
                new HubBlueprint("TR_C1",  "İstanbul",             WorldMapFaction.Turkey, WorldMapSettlementKind.City,        -245f,  155f, 4),
                new HubBlueprint("TR_C2",  "İzmir",                WorldMapFaction.Turkey, WorldMapSettlementKind.City,        -285f, -165f, 4),
                new HubBlueprint("TR_C3",  "Konya",                WorldMapFaction.Turkey, WorldMapSettlementKind.City,         195f, -175f, 4),
                new HubBlueprint("TR_B1",  "Çanakkale Garnizonu",  WorldMapFaction.Turkey, WorldMapSettlementKind.MilitaryBase,-415f,   25f, 2),
                new HubBlueprint("TR_B2",  "Erzurum Üssü",         WorldMapFaction.Turkey, WorldMapSettlementKind.MilitaryBase, 335f,  135f, 2),
                new HubBlueprint("TR_B3",  "Gölcük Köprü Üssü",    WorldMapFaction.Turkey, WorldMapSettlementKind.MilitaryBase,  85f,  325f, 2),
                new HubBlueprint("TR_B4",  "İncirlik Üssü",        WorldMapFaction.Turkey, WorldMapSettlementKind.MilitaryBase, -45f, -295f, 2),
                new HubBlueprint("TR_B5",  "Sarıkamış Karakolu",   WorldMapFaction.Turkey, WorldMapSettlementKind.MilitaryBase, 355f,  -75f, 2),

                // 2) SOVYETLER (RUSYA) — Northern & Northeastern Pine Highlands (1 Başkent + 3 Şehir + 5 Askeri Üs = 26 Köy)
                new HubBlueprint("SU_CAP", "Moskova",              WorldMapFaction.Soviet, WorldMapSettlementKind.Capital,      165f,  665f, 4),
                new HubBlueprint("SU_C1",  "Leningrad",            WorldMapFaction.Soviet, WorldMapSettlementKind.City,        -315f,  660f, 4),
                new HubBlueprint("SU_C2",  "Stalingrad",           WorldMapFaction.Soviet, WorldMapSettlementKind.City,         -85f,  495f, 4),
                new HubBlueprint("SU_C3",  "Kazan",                WorldMapFaction.Soviet, WorldMapSettlementKind.City,         555f,  625f, 4),
                new HubBlueprint("SU_B1",  "Murmansk Üssü",        WorldMapFaction.Soviet, WorldMapSettlementKind.MilitaryBase,-495f,  535f, 2),
                new HubBlueprint("SU_B2",  "Kursk Garnizonu",      WorldMapFaction.Soviet, WorldMapSettlementKind.MilitaryBase, -20f,  745f, 2),
                new HubBlueprint("SU_B3",  "Ural Tahkimatı",       WorldMapFaction.Soviet, WorldMapSettlementKind.MilitaryBase, 375f,  485f, 2),
                new HubBlueprint("SU_B4",  "Baykal Üssü",          WorldMapFaction.Soviet, WorldMapSettlementKind.MilitaryBase, 725f,  525f, 2),
                new HubBlueprint("SU_B5",  "Sevastopol Köprü Üssü",WorldMapFaction.Soviet, WorldMapSettlementKind.MilitaryBase, 255f,  365f, 2),

                // 3) ABD — Western Coastal Plains & Northwest Coast (1 Başkent + 3 Şehir + 5 Askeri Üs = 26 Köy)
                new HubBlueprint("US_CAP", "Washington",           WorldMapFaction.USA,    WorldMapSettlementKind.Capital,     -735f,  195f, 4),
                new HubBlueprint("US_C1",  "New York",             WorldMapFaction.USA,    WorldMapSettlementKind.City,        -795f,  515f, 4),
                new HubBlueprint("US_C2",  "Chicago",              WorldMapFaction.USA,    WorldMapSettlementKind.City,        -505f,  325f, 4),
                new HubBlueprint("US_C3",  "Houston",              WorldMapFaction.USA,    WorldMapSettlementKind.City,        -775f, -195f, 4),
                new HubBlueprint("US_B1",  "Norfolk Kıyı Üssü",    WorldMapFaction.USA,    WorldMapSettlementKind.MilitaryBase,-915f,  -15f, 2),
                new HubBlueprint("US_B2",  "West Point Üssü",      WorldMapFaction.USA,    WorldMapSettlementKind.MilitaryBase,-645f,  715f, 2),
                new HubBlueprint("US_B3",  "Fort Bragg",           WorldMapFaction.USA,    WorldMapSettlementKind.MilitaryBase,-545f,   75f, 2),
                new HubBlueprint("US_B4",  "Fort Knox",            WorldMapFaction.USA,    WorldMapSettlementKind.MilitaryBase,-525f, -215f, 2),
                new HubBlueprint("US_B5",  "Area 51 Sınır Üssü",   WorldMapFaction.USA,    WorldMapSettlementKind.MilitaryBase,-745f, -435f, 2),

                // 4) ÇİN — Eastern & Southeastern Highlands and Foothills (1 Başkent + 3 Şehir + 5 Askeri Üs = 26 Köy)
                new HubBlueprint("CN_CAP", "Pekin",                WorldMapFaction.China,  WorldMapSettlementKind.Capital,      655f,   65f, 4),
                new HubBlueprint("CN_C1",  "Şanghay",              WorldMapFaction.China,  WorldMapSettlementKind.City,         645f,  315f, 4),
                new HubBlueprint("CN_C2",  "Nanjing",              WorldMapFaction.China,  WorldMapSettlementKind.City,         455f,  195f, 4),
                new HubBlueprint("CN_C3",  "Chengdu",              WorldMapFaction.China,  WorldMapSettlementKind.City,         655f, -265f, 4),
                new HubBlueprint("CN_B1",  "Çin Seddi Garnizonu",  WorldMapFaction.China,  WorldMapSettlementKind.MilitaryBase, 755f,  195f, 2),
                new HubBlueprint("CN_B2",  "Harbin Sınır Üssü",    WorldMapFaction.China,  WorldMapSettlementKind.MilitaryBase, 475f,  355f, 2),
                new HubBlueprint("CN_B3",  "Xian Yayla Üssü",      WorldMapFaction.China,  WorldMapSettlementKind.MilitaryBase, 485f,  -85f, 2),
                new HubBlueprint("CN_B4",  "Lanzhou Üssü",         WorldMapFaction.China,  WorldMapSettlementKind.MilitaryBase, 765f, -115f, 2),
                new HubBlueprint("CN_B5",  "Dalian Geçit Üssü",    WorldMapFaction.China,  WorldMapSettlementKind.MilitaryBase, 465f, -365f, 2),

                // 5) İSYANCILAR / KORSANLAR — Southern Desert & Southern Coast (1 Ana Karargâh + 2 Şehir + 4 Üs = 20 Köy/Kamp)
                new HubBlueprint("RB_CAP", "Korsan Koyu",          WorldMapFaction.Rebels, WorldMapSettlementKind.Capital,     -155f, -815f, 4),
                new HubBlueprint("RB_C1",  "Kara Liman",           WorldMapFaction.Rebels, WorldMapSettlementKind.City,        -615f, -795f, 4),
                new HubBlueprint("RB_C2",  "Kızıl Vaha",           WorldMapFaction.Rebels, WorldMapSettlementKind.City,         345f, -735f, 4),
                new HubBlueprint("RB_B1",  "Kafatası Kayası",      WorldMapFaction.Rebels, WorldMapSettlementKind.MilitaryBase,-415f, -545f, 2),
                new HubBlueprint("RB_B2",  "Kum Taburu",           WorldMapFaction.Rebels, WorldMapSettlementKind.MilitaryBase,  35f, -525f, 2),
                new HubBlueprint("RB_B3",  "Demir Barikat",        WorldMapFaction.Rebels, WorldMapSettlementKind.MilitaryBase, 615f, -565f, 2),
                new HubBlueprint("RB_B4",  "Sürgün Kalesi",        WorldMapFaction.Rebels, WorldMapSettlementKind.MilitaryBase, 145f, -905f, 2)
            };
        }

        private static string GetAuthenticVillageName(WorldMapFaction faction, int index)
        {
            string[] pool;
            switch (faction)
            {
                case WorldMapFaction.Turkey: pool = TurkeyVillageNames; break;
                case WorldMapFaction.Soviet: pool = SovietVillageNames; break;
                case WorldMapFaction.USA:    pool = UsaVillageNames; break;
                case WorldMapFaction.China:  pool = ChinaVillageNames; break;
                default:                     pool = RebelVillageNames; break;
            }
            return index < pool.Length ? pool[index] : $"{pool[index % pool.Length]} {(index / pool.Length) + 1}";
        }

        // =========================================================================
        // TERRAIN-AWARE SPIRAL SITE VALIDATOR
        // =========================================================================

        private static Vector2 FindCleanSettlementSite(
            Vector2 idealXZ,
            Terrain terrain,
            List<Vector2> bridgeCenters,
            Vector2[] mainRiverSpline,
            Vector2[] creekSpline,
            List<Vector3> occupied,
            float minSpacing,
            float maxSlopeDeg,
            float seaLevelY,
            float maxSearchRadius)
        {
            if (IsValidSite(idealXZ, terrain, bridgeCenters, mainRiverSpline, creekSpline, occupied, minSpacing, maxSlopeDeg, seaLevelY))
                return idealXZ;

            for (float r = 16f; r <= maxSearchRadius; r += 16f)
            {
                int steps = Mathf.Clamp(Mathf.RoundToInt(r * 0.45f), 12, 36);
                for (int s = 0; s < steps; s++)
                {
                    float ang = (s / (float)steps) * Mathf.PI * 2f;
                    Vector2 cand = new Vector2(
                        Mathf.Clamp(idealXZ.x + Mathf.Cos(ang) * r, -945f, 840f),
                        Mathf.Clamp(idealXZ.y + Mathf.Sin(ang) * r, -945f, 800f));

                    if (IsValidSite(cand, terrain, bridgeCenters, mainRiverSpline, creekSpline, occupied, minSpacing, maxSlopeDeg, seaLevelY))
                        return cand;
                }
            }

            // Soft fallback keeping terrain strictly dry & walkable
            for (float r = 16f; r <= maxSearchRadius * 1.4f; r += 18f)
            {
                for (int s = 0; s < 24; s++)
                {
                    float ang = (s / 24f) * Mathf.PI * 2f;
                    Vector2 cand = new Vector2(
                        Mathf.Clamp(idealXZ.x + Mathf.Cos(ang) * r, -945f, 840f),
                        Mathf.Clamp(idealXZ.y + Mathf.Sin(ang) * r, -945f, 800f));

                    if (IsValidSite(cand, terrain, bridgeCenters, mainRiverSpline, creekSpline, occupied, minSpacing * 0.72f, maxSlopeDeg + 4f, seaLevelY))
                        return cand;
                }
            }

            return idealXZ;
        }

        private static bool IsValidSite(
            Vector2 p,
            Terrain terrain,
            List<Vector2> bridgeCenters,
            Vector2[] mainRiverSpline,
            Vector2[] creekSpline,
            List<Vector3> occupied,
            float minSpacing,
            float maxSlopeDeg,
            float seaLevelY)
        {
            if (terrain == null || terrain.terrainData == null)
                return true;

            TerrainData td = terrain.terrainData;
            Vector3 tPos = terrain.transform.position;

            for (int k = 0; k < 5; k++)
            {
                float ox = k == 1 ? -12f : (k == 2 ? 12f : 0f);
                float oz = k == 3 ? -12f : (k == 4 ? 12f : 0f);
                float nx = Mathf.Clamp01((p.x + ox - tPos.x) / td.size.x);
                float nz = Mathf.Clamp01((p.y + oz - tPos.z) / td.size.z);
                float h = td.GetInterpolatedHeight(nx, nz) + tPos.y;
                float sl = td.GetSteepness(nx, nz);

                if (h < seaLevelY + 3.2f || h > 72f || sl > maxSlopeDeg)
                    return false;
            }

            if (bridgeCenters != null)
            {
                for (int b = 0; b < bridgeCenters.Count; b++)
                {
                    if ((p - bridgeCenters[b]).sqrMagnitude < 52f * 52f)
                        return false;
                }
            }

            if (DistanceToPolyline(p, mainRiverSpline) < 38f || DistanceToPolyline(p, creekSpline) < 32f)
                return false;

            for (int i = 0; i < occupied.Count; i++)
            {
                Vector3 occ = occupied[i];
                float req = Mathf.Max(minSpacing, occ.z + 42f);
                float dx = p.x - occ.x;
                float dz = p.y - occ.y;
                if (dx * dx + dz * dz < req * req)
                    return false;
            }

            return true;
        }

        // =========================================================================
        // ARCHITECTURAL GEOMETRY PRIMITIVES (100% outward CW winding)
        // =========================================================================

        private static Vector3 GroundedPoint(Terrain terrain, Vector3 center, Quaternion rot, Vector2 localXZ)
        {
            Vector3 wp = center + rot * new Vector3(localXZ.x, 0f, localXZ.y);
            wp.y = SampleTerrainY(terrain, new Vector2(wp.x, wp.z));
            return wp;
        }

        private static float SampleTerrainY(Terrain terrain, Vector2 xz)
        {
            if (terrain == null)
                return 24f;
            return terrain.SampleHeight(new Vector3(xz.x, 0f, xz.y)) + terrain.transform.position.y;
        }

        private static Color LinCol(Color srgb, float ao)
        {
            Color c = srgb.linear;
            c.a = ao;
            return c;
        }

        private static void AppendQuad(SettlementChunkBuilder cb, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Color col)
        {
            Vector3 n = Vector3.Cross(p1 - p0, p2 - p0).normalized;
            int vi = cb.Verts.Count;
            cb.Verts.Add(p0); cb.Verts.Add(p1); cb.Verts.Add(p2); cb.Verts.Add(p3);
            cb.Normals.Add(n); cb.Normals.Add(n); cb.Normals.Add(n); cb.Normals.Add(n);
            cb.Colors.Add(col); cb.Colors.Add(col); cb.Colors.Add(col); cb.Colors.Add(col);
            cb.Tris.Add(vi); cb.Tris.Add(vi + 1); cb.Tris.Add(vi + 2);
            cb.Tris.Add(vi); cb.Tris.Add(vi + 2); cb.Tris.Add(vi + 3);
        }

        private static void AppendTri(SettlementChunkBuilder cb, Vector3 p0, Vector3 p1, Vector3 p2, Color col)
        {
            Vector3 n = Vector3.Cross(p1 - p0, p2 - p0).normalized;
            int vi = cb.Verts.Count;
            cb.Verts.Add(p0); cb.Verts.Add(p1); cb.Verts.Add(p2);
            cb.Normals.Add(n); cb.Normals.Add(n); cb.Normals.Add(n);
            cb.Colors.Add(col); cb.Colors.Add(col); cb.Colors.Add(col);
            cb.Tris.Add(vi); cb.Tris.Add(vi + 1); cb.Tris.Add(vi + 2);
        }

        private static void AppendBox(SettlementChunkBuilder cb, Vector3 pos, Quaternion rot, Vector3 size, float yBase, Color col)
        {
            float hx = size.x * 0.5f;
            float hz = size.z * 0.5f;
            float y0 = yBase;
            float y1 = yBase + size.y;

            Vector3 b0 = pos + rot * new Vector3(-hx, y0, -hz);
            Vector3 b1 = pos + rot * new Vector3(-hx, y0,  hz);
            Vector3 b2 = pos + rot * new Vector3( hx, y0,  hz);
            Vector3 b3 = pos + rot * new Vector3( hx, y0, -hz);

            Vector3 t0 = pos + rot * new Vector3(-hx, y1, -hz);
            Vector3 t1 = pos + rot * new Vector3(-hx, y1,  hz);
            Vector3 t2 = pos + rot * new Vector3( hx, y1,  hz);
            Vector3 t3 = pos + rot * new Vector3( hx, y1, -hz);

            Color shadeWall = new Color(col.r, col.g, col.b, col.a * 0.90f);
            AppendQuad(cb, t0, t1, t2, t3, col);
            AppendQuad(cb, b0, t0, t3, b3, shadeWall);
            AppendQuad(cb, b2, t2, t1, b1, shadeWall);
            AppendQuad(cb, b1, t1, t0, b0, shadeWall);
            AppendQuad(cb, b3, t3, t2, b2, shadeWall);
        }

        private static void AppendPitchedRoof(SettlementChunkBuilder cb, Vector3 basePos, Quaternion rot, float widthX, float depthZ, float height, Color col)
        {
            float hx = widthX * 0.5f;
            float hz = depthZ * 0.5f;

            Vector3 c0 = basePos + rot * new Vector3(-hx, 0f, -hz);
            Vector3 c1 = basePos + rot * new Vector3(-hx, 0f,  hz);
            Vector3 c2 = basePos + rot * new Vector3( hx, 0f,  hz);
            Vector3 c3 = basePos + rot * new Vector3( hx, 0f, -hz);
            Vector3 rL = basePos + rot * new Vector3(-hx, height, 0f);
            Vector3 rR = basePos + rot * new Vector3( hx, height, 0f);

            AppendQuad(cb, c0, rL, rR, c3, col);
            AppendQuad(cb, c2, rR, rL, c1, col);
            AppendTri(cb, c1, rL, c0, col);
            AppendTri(cb, c3, rR, c2, col);
        }

        private static void AppendPyramidRoof(SettlementChunkBuilder cb, Vector3 basePos, Quaternion rot, float widthX, float depthZ, float height, Color col)
        {
            float hx = widthX * 0.5f;
            float hz = depthZ * 0.5f;
            Vector3 c0 = basePos + rot * new Vector3(-hx, 0f, -hz);
            Vector3 c1 = basePos + rot * new Vector3(-hx, 0f,  hz);
            Vector3 c2 = basePos + rot * new Vector3( hx, 0f,  hz);
            Vector3 c3 = basePos + rot * new Vector3( hx, 0f, -hz);
            Vector3 apex = basePos + rot * new Vector3(0f, height, 0f);

            AppendTri(cb, c0, apex, c3, col);
            AppendTri(cb, c3, apex, c2, col);
            AppendTri(cb, c2, apex, c1, col);
            AppendTri(cb, c1, apex, c0, col);
        }

        private static void AppendPagodaRoof(SettlementChunkBuilder cb, Vector3 basePos, Quaternion rot, float widthX, float depthZ, float height, Color col)
        {
            float hx0 = widthX * 0.5f;
            float hz0 = depthZ * 0.5f;
            float hx1 = widthX * 0.28f;
            float hz1 = depthZ * 0.28f;
            float midH = height * 0.38f;

            Vector3 b0 = basePos + rot * new Vector3(-hx0, 0f, -hz0);
            Vector3 b1 = basePos + rot * new Vector3(-hx0, 0f,  hz0);
            Vector3 b2 = basePos + rot * new Vector3( hx0, 0f,  hz0);
            Vector3 b3 = basePos + rot * new Vector3( hx0, 0f, -hz0);

            Vector3 m0 = basePos + rot * new Vector3(-hx1, midH, -hz1);
            Vector3 m1 = basePos + rot * new Vector3(-hx1, midH,  hz1);
            Vector3 m2 = basePos + rot * new Vector3( hx1, midH,  hz1);
            Vector3 m3 = basePos + rot * new Vector3( hx1, midH, -hz1);

            Vector3 apex = basePos + rot * new Vector3(0f, height, 0f);

            Color eaveCol = new Color(col.r, col.g, col.b, col.a * 0.90f);
            AppendQuad(cb, b0, m0, m3, b3, eaveCol);
            AppendQuad(cb, b3, m3, m2, b2, eaveCol);
            AppendQuad(cb, b2, m2, m1, b1, eaveCol);
            AppendQuad(cb, b1, m1, m0, b0, eaveCol);

            AppendTri(cb, m0, apex, m3, col);
            AppendTri(cb, m3, apex, m2, col);
            AppendTri(cb, m2, apex, m1, col);
            AppendTri(cb, m1, apex, m0, col);
        }

        private static void AppendCylinder(SettlementChunkBuilder cb, Vector3 pos, float rBot, float rTop, float y0, float y1, int sides, Color col)
        {
            for (int i = 0; i < sides; i++)
            {
                float a0 = (i / (float)sides) * Mathf.PI * 2f;
                float a1 = ((i + 1) / (float)sides) * Mathf.PI * 2f;
                Vector3 b0 = pos + new Vector3(Mathf.Cos(a0) * rBot, y0, Mathf.Sin(a0) * rBot);
                Vector3 b1 = pos + new Vector3(Mathf.Cos(a1) * rBot, y0, Mathf.Sin(a1) * rBot);
                Vector3 t0 = pos + new Vector3(Mathf.Cos(a0) * rTop, y1, Mathf.Sin(a0) * rTop);
                Vector3 t1 = pos + new Vector3(Mathf.Cos(a1) * rTop, y1, Mathf.Sin(a1) * rTop);

                AppendQuad(cb, b0, t0, t1, b1, col);
            }
        }

        private static void AppendCone(SettlementChunkBuilder cb, Vector3 pos, float radius, float yBase, float height, int sides, Color col)
        {
            Vector3 apex = pos + new Vector3(0f, yBase + height, 0f);
            for (int i = 0; i < sides; i++)
            {
                float a0 = (i / (float)sides) * Mathf.PI * 2f;
                float a1 = ((i + 1) / (float)sides) * Mathf.PI * 2f;
                Vector3 p0 = pos + new Vector3(Mathf.Cos(a0) * radius, yBase, Mathf.Sin(a0) * radius);
                Vector3 p1 = pos + new Vector3(Mathf.Cos(a1) * radius, yBase, Mathf.Sin(a1) * radius);
                AppendTri(cb, p0, apex, p1, col);
            }
        }

        private static void AppendDome(SettlementChunkBuilder cb, Vector3 basePos, float radius, float height, int sides, Color col)
        {
            float rMid = radius * 0.72f;
            float yMid = height * 0.62f;
            Vector3 apex = basePos + new Vector3(0f, height, 0f);
            for (int i = 0; i < sides; i++)
            {
                float a0 = (i / (float)sides) * Mathf.PI * 2f;
                float a1 = ((i + 1) / (float)sides) * Mathf.PI * 2f;
                Vector3 b0 = basePos + new Vector3(Mathf.Cos(a0) * radius, 0f, Mathf.Sin(a0) * radius);
                Vector3 b1 = basePos + new Vector3(Mathf.Cos(a1) * radius, 0f, Mathf.Sin(a1) * radius);
                Vector3 m0 = basePos + new Vector3(Mathf.Cos(a0) * rMid, yMid, Mathf.Sin(a0) * rMid);
                Vector3 m1 = basePos + new Vector3(Mathf.Cos(a1) * rMid, yMid, Mathf.Sin(a1) * rMid);

                AppendQuad(cb, b0, m0, m1, b1, col);
                AppendTri(cb, m0, apex, m1, col);
            }
        }

        private static float DistanceToPolyline(Vector2 p, Vector2[] pts)
        {
            if (pts == null || pts.Length < 2)
                return 9999f;

            float minDistSq = float.MaxValue;
            for (int i = 0; i < pts.Length - 1; i++)
            {
                Vector2 a = pts[i];
                Vector2 b = pts[i + 1];
                Vector2 ab = b - a;
                float lenSq = ab.sqrMagnitude;
                float t = lenSq > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq) : 0f;
                float dSq = (p - (a + ab * t)).sqrMagnitude;
                if (dSq < minDistSq) minDistSq = dSq;
            }
            return Mathf.Sqrt(minDistSq);
        }

        private static Material LoadOrCreateMaterial(string path, Shader shader)
        {
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            else if (shader != null)
            {
                mat.shader = shader;
            }
            return mat;
        }

        private static void EnsureFolder(string parent, string child)
        {
            if (!AssetDatabase.IsValidFolder($"{parent}/{child}"))
                AssetDatabase.CreateFolder(parent, child);
        }
    }
}
