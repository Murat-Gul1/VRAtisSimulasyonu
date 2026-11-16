using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace VRAtis.EditorTools
{
    [InitializeOnLoad]
    public static class VegetationSetupTool
    {
        private const string VegTexDir = "Assets/Models/Environment/Vegetation/Textures";
        private const string VegMeshDir = "Assets/Models/Environment/Vegetation/Meshes";
        private const string PineFbxPath = "Assets/Models/Environment/Vegetation/AlpinePine.fbx";
        private const string BoulderFbxPath = "Assets/Models/Environment/Vegetation/ValleyBoulder.fbx";
        private const string EnvMatDir = "Assets/Materials/Environment";
        private const string RootObjectName = "Environment_Vegetation";
        private const string AutoSetupVersionKey = "VRAtis_Vegetation_V4_Done";
        private const int MaxTuftsPerUInt16Chunk = 3200; // <= 51,200 verts max per chunk

        static VegetationSetupTool()
        {
            EditorApplication.delayCall += TryAutoSetupOnce;
        }

        private static void TryAutoSetupOnce()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || Application.isBatchMode)
                return;

            if (GameObject.Find(RootObjectName) != null && EditorPrefs.GetBool(AutoSetupVersionKey, false))
                return;

            if (AssetDatabase.LoadAssetAtPath<GameObject>(PineFbxPath) == null ||
                AssetDatabase.LoadAssetAtPath<Texture2D>($"{VegTexDir}/AlpineGrass_Tile.png") == null)
                return;

            SetupValleyVegetation();
        }

        [MenuItem("VRAtis/Bitki Örtüsü Kur")]
        public static void SetupValleyVegetation()
        {
            GameObject pinePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PineFbxPath);
            GameObject boulderPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(BoulderFbxPath);
            if (pinePrefab == null || boulderPrefab == null)
            {
                Debug.LogError("[VegetationSetupTool] Vegetation FBX modelleri bulunamadı.");
                return;
            }

            EnsureFolder("Assets", "Materials");
            EnsureFolder("Assets/Materials", "Environment");
            EnsureFolder("Assets/Models/Environment/Vegetation", "Meshes");

            UpgradeGroundMaterial();

            // 1) Near player: lush wind-swaying grass (0.85..33m)
            Material grassSwayMat = BuildFoliageMaterial(
                "AlpineGrass_Mat", $"{VegTexDir}/AlpineGrass_Tile.png",
                tint: new Color(0.96f, 0.99f, 0.88f, 1f),
                cutoff: 0.32f, windStrength: 0.092f, windSpeed: 1.75f,
                fadeStart: 28f, fadeEnd: 40f, translucency: 0.28f);

            // 2) Near player understory: short completely static ground cover filling bare spots (0.65..31.5m)
            Material grassUnderstoryMat = BuildFoliageMaterial(
                "AlpineGrass_StaticUnderstory_Mat", $"{VegTexDir}/AlpineGrass_Tile.png",
                tint: new Color(0.92f, 0.96f, 0.82f, 1f),
                cutoff: 0.34f, windStrength: 0.0f, windSpeed: 0.1f,
                fadeStart: 28f, fadeEnd: 41f, translucency: 0.0f);

            // 3) Mid visible valley static meadow (26..160m, windStrength = 0, zero shadow cast)
            Material grassMidStaticMat = BuildFoliageMaterial(
                "AlpineGrass_MidStatic_Mat", $"{VegTexDir}/AlpineGrass_Tile.png",
                tint: new Color(0.88f, 0.94f, 0.78f, 1f),
                cutoff: 0.34f, windStrength: 0.0f, windSpeed: 0.1f,
                fadeStart: 145f, fadeEnd: 205f, translucency: 0.0f);

            // 4) Far valley & foothills horizon static meadow (135..440m, windStrength = 0, zero shadow cast)
            Material grassFarStaticMat = BuildFoliageMaterial(
                "AlpineGrass_FarStatic_Mat", $"{VegTexDir}/AlpineGrass_Tile.png",
                tint: new Color(0.84f, 0.91f, 0.75f, 1f),
                cutoff: 0.36f, windStrength: 0.0f, windSpeed: 0.1f,
                fadeStart: 400f, fadeEnd: 530f, translucency: 0.0f);

            // 5) Shrubs: near swaying + mid/far static
            Material shrubSwayMat = BuildFoliageMaterial(
                "AlpineShrub_Mat", $"{VegTexDir}/AlpineShrub_Tile.png",
                tint: new Color(0.95f, 0.97f, 0.88f, 1f),
                cutoff: 0.34f, windStrength: 0.055f, windSpeed: 1.45f,
                fadeStart: 32f, fadeEnd: 48f, translucency: 0.20f);

            Material shrubStaticMat = BuildFoliageMaterial(
                "AlpineShrub_Static_Mat", $"{VegTexDir}/AlpineShrub_Tile.png",
                tint: new Color(0.88f, 0.93f, 0.79f, 1f),
                cutoff: 0.35f, windStrength: 0.0f, windSpeed: 0.1f,
                fadeStart: 390f, fadeEnd: 520f, translucency: 0.0f);

            Material pineBranchMat = BuildFoliageMaterial(
                "AlpinePine_Mat", $"{VegTexDir}/AlpinePine_Branch.png",
                tint: new Color(0.96f, 0.98f, 0.96f, 1f),
                cutoff: 0.32f, windStrength: 0.040f, windSpeed: 1.20f,
                fadeStart: 2400f, fadeEnd: 3000f, translucency: 0.18f);

            Material barkMat = BuildBarkMaterial();
            Material boulderMat = BuildBoulderMaterial();

            GameObject existing = GameObject.Find(RootObjectName);
            if (existing != null)
                Object.DestroyImmediate(existing);

            DeleteOldGeneratedMeshes();

            GameObject root = new GameObject(RootObjectName);
            Random.State prevState = Random.state;
            Random.InitState(20261119);

            Vector3 shooterAnchor = ResolveShooterAnchor();

            // Layer 1 & 2: Near-player rich swaying meadow + static gap-filler understory
            BuildSwayingShooterMeadow(root.transform, shooterAnchor, grassSwayMat, shrubSwayMat);
            BuildStaticShortUnderstory(root.transform, shooterAnchor, grassUnderstoryMat);

            // Layer 3 & 4: Optimize visible mid & far range greenery across the entire valley
            BuildMidValleyStaticMeadow(root.transform, shooterAnchor, grassMidStaticMat, shrubStaticMat);
            BuildFarValleyStaticMeadow(root.transform, shooterAnchor, grassFarStaticMat, shrubStaticMat);

            // Layer 5: Pine groves & granite boulders framing foothills
            SpawnPineGroves(root.transform, shooterAnchor, pinePrefab, barkMat, pineBranchMat);
            SpawnBoulders(root.transform, shooterAnchor, boulderPrefab, boulderMat);

            Random.state = prevState;

            EditorSceneManager.MarkSceneDirty(root.scene);
            EditorSceneManager.SaveOpenScenes();
            AssetDatabase.SaveAssets();
            EditorPrefs.SetBool(AutoSetupVersionKey, true);
            Debug.Log("[VegetationSetupTool] V4 Kuruldu: Yakın rüzgarlı kır + yakın sabit zemin + orta/uzak statik vadi yeşilliği!");
        }

        private static void UpgradeGroundMaterial()
        {
            Material groundMat = AssetDatabase.LoadAssetAtPath<Material>($"{EnvMatDir}/RangeGround_Mat.mat");
            if (groundMat == null)
                return;

            Shader groundShader = Shader.Find("VRAtis/AlpineGroundURP");
            if (groundShader == null)
                groundShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Shaders/AlpineGroundURP.shader");

            if (groundShader != null)
            {
                Texture mainTex = groundMat.GetTexture("_BaseMap");
                Texture bumpTex = groundMat.GetTexture("_BumpMap");
                Vector2 mainScale = groundMat.GetTextureScale("_BaseMap");

                groundMat.shader = groundShader;
                if (mainTex != null)
                {
                    groundMat.SetTexture("_BaseMap", mainTex);
                    groundMat.SetTextureScale("_BaseMap", mainScale.sqrMagnitude > 0.1f ? mainScale : new Vector2(220f, 220f));
                }
                if (bumpTex != null)
                {
                    groundMat.SetTexture("_BumpMap", bumpTex);
                    groundMat.SetTextureScale("_BumpMap", mainScale.sqrMagnitude > 0.1f ? mainScale : new Vector2(220f, 220f));
                }

                groundMat.SetFloat("_BumpScale", 0.85f);
                groundMat.SetColor("_BaseColor", new Color(0.68f, 0.84f, 0.56f, 1f));
                groundMat.SetColor("_DarkPatchColor", new Color(0.46f, 0.66f, 0.40f, 1f));
                groundMat.SetColor("_LightPatchColor", new Color(0.80f, 0.92f, 0.58f, 1f));
                groundMat.SetFloat("_MacroScale", 0.024f);
                groundMat.SetFloat("_DetailScale", 0.11f);
            }
            else
            {
                groundMat.SetColor("_BaseColor", new Color(0.68f, 0.84f, 0.56f, 1f));
            }

            EditorUtility.SetDirty(groundMat);
        }

        private static Vector3 ResolveShooterAnchor()
        {
            GameObject rifle = GameObject.Find("PlayerRifle");
            if (rifle != null)
                return new Vector3(rifle.transform.position.x, 0f, rifle.transform.position.z);

            if (Camera.main != null)
                return new Vector3(Camera.main.transform.position.x, 0f, Camera.main.transform.position.z);

            return new Vector3(2.01f, 0f, -3.783f);
        }

        private static void BuildSwayingShooterMeadow(Transform parent, Vector3 anchor, Material grassMat, Material shrubMat)
        {
            GameObject swayRoot = new GameObject("Player_SwayingMeadow");
            swayRoot.transform.SetParent(parent, false);

            Mesh tuftTemplate = CreateCrossedTuftMesh(3, 0.72f, 0.36f);
            List<CombineInstance>[] quadrantBuckets = CreateQuadrantBuckets();

            for (int i = 0; i < 4200; i++)
            {
                float rNorm = Mathf.Pow(Random.value, 0.58f);
                float radius = Mathf.Lerp(0.85f, 33.0f, rNorm);
                float angle = Random.Range(0f, Mathf.PI * 2f);
                float x = anchor.x + Mathf.Cos(angle) * radius;
                float z = anchor.z + Mathf.Sin(angle) * radius;

                float patch = Mathf.PerlinNoise((x + 320f) * 0.18f, (z + 320f) * 0.18f);
                if (patch < 0.18f && radius > 6.5f)
                    continue;

                float distTaper = Mathf.InverseLerp(34.0f, 18.0f, radius);
                float scaleXZ = Random.Range(0.75f, 1.32f) * Mathf.Lerp(0.82f, 1.18f, patch);
                float scaleY = Random.Range(0.72f, 1.28f) * Mathf.Lerp(0.72f, 1.05f, distTaper);

                if (radius < 2.2f)
                    scaleY *= 0.72f;

                Quaternion rot = Quaternion.Euler(Random.Range(-5f, 5f), Random.Range(0f, 360f), Random.Range(-5f, 5f));
                Matrix4x4 mtx = Matrix4x4.TRS(new Vector3(x, -0.01f, z), rot, new Vector3(scaleXZ, scaleY, scaleXZ));

                int q = GetQuadrantIndex(x - anchor.x, z - anchor.z);
                quadrantBuckets[q].Add(new CombineInstance { mesh = tuftTemplate, transform = mtx });
            }

            string[] qNames = { "NE", "NW", "SE", "SW" };
            for (int q = 0; q < 4; q++)
                CreateChunkObjects($"MeadowSway_{qNames[q]}", quadrantBuckets[q], swayRoot.transform, grassMat, castShadows: false, markStatic: false);

            Object.DestroyImmediate(tuftTemplate);

            Mesh shrubTemplate = CreateCrossedTuftMesh(3, 0.90f, 0.62f);
            List<CombineInstance> shrubCombines = new List<CombineInstance>(160);
            for (int i = 0; i < 160; i++)
            {
                float radius = Random.Range(4.5f, 32.0f);
                float angle = Random.Range(0f, Mathf.PI * 2f);
                float x = anchor.x + Mathf.Cos(angle) * radius;
                float z = anchor.z + Mathf.Sin(angle) * radius;

                float scale = Random.Range(0.50f, 0.90f);
                Quaternion rot = Quaternion.Euler(Random.Range(-4f, 4f), Random.Range(0f, 360f), Random.Range(-4f, 4f));
                Matrix4x4 mtx = Matrix4x4.TRS(new Vector3(x, -0.02f, z), rot, new Vector3(scale, scale, scale));
                shrubCombines.Add(new CombineInstance { mesh = shrubTemplate, transform = mtx });
            }

            CreateChunkObjects("MeadowSway_Shrubs", shrubCombines, swayRoot.transform, shrubMat, castShadows: true, markStatic: false);
            Object.DestroyImmediate(shrubTemplate);
        }

        private static void BuildStaticShortUnderstory(Transform parent, Vector3 anchor, Material understoryMat)
        {
            GameObject staticRoot = new GameObject("Player_StaticUnderstory");
            staticRoot.transform.SetParent(parent, false);

            Mesh understoryTemplate = CreateShortUnderstoryTuftMesh();
            List<CombineInstance>[] quadrantBuckets = CreateQuadrantBuckets();

            for (int i = 0; i < 7600; i++)
            {
                float rNorm = Mathf.Pow(Random.value, 0.56f);
                float radius = Mathf.Lerp(0.65f, 32.0f, rNorm);
                float angle = Random.Range(0f, Mathf.PI * 2f);
                float x = anchor.x + Mathf.Cos(angle) * radius;
                float z = anchor.z + Mathf.Sin(angle) * radius;

                float sXZ = Random.Range(0.82f, 1.38f);
                float sY = Random.Range(0.70f, 1.18f);

                Quaternion rot = Quaternion.Euler(Random.Range(-4f, 4f), Random.Range(0f, 360f), Random.Range(-4f, 4f));
                Matrix4x4 mtx = Matrix4x4.TRS(new Vector3(x, -0.005f, z), rot, new Vector3(sXZ, sY, sXZ));

                int q = GetQuadrantIndex(x - anchor.x, z - anchor.z);
                quadrantBuckets[q].Add(new CombineInstance { mesh = understoryTemplate, transform = mtx });
            }

            string[] qNames = { "NE", "NW", "SE", "SW" };
            for (int q = 0; q < 4; q++)
                CreateChunkObjects($"StaticUnderstory_{qNames[q]}", quadrantBuckets[q], staticRoot.transform, understoryMat, castShadows: false, markStatic: true);

            Object.DestroyImmediate(understoryTemplate);
        }

        private static void BuildMidValleyStaticMeadow(Transform parent, Vector3 anchor, Material midGrassMat, Material shrubStaticMat)
        {
            GameObject midRoot = new GameObject("MidValley_StaticMeadow");
            midRoot.transform.SetParent(parent, false);

            // Wide 2-plane static clumps cover 4x area per vertex across 28..155m
            Mesh wideClumpTemplate = CreateCrossedTuftMesh(2, 1.55f, 0.38f);
            Dictionary<Vector2Int, List<CombineInstance>> buckets = new Dictionary<Vector2Int, List<CombineInstance>>();
            const float chunkSize = 85f;

            for (int i = 0; i < 5400; i++)
            {
                float radius = Mathf.Lerp(28f, 160f, Mathf.Pow(Random.value, 0.68f));
                float angle = Random.Range(-Mathf.PI * 0.65f, Mathf.PI * 1.65f);
                // Bias slightly toward forward firing valley hemisphere (+Z) while still framing sides
                if (Random.value < 0.70f)
                    angle = Random.Range(0.05f, Mathf.PI - 0.05f);

                float x = anchor.x + Mathf.Cos(angle) * radius;
                float z = anchor.z + Mathf.Sin(angle) * radius;

                float patch = Mathf.PerlinNoise((x + 510f) * 0.055f, (z + 510f) * 0.055f);
                if (patch < 0.20f)
                    continue;

                float sXZ = Random.Range(0.85f, 1.45f) * Mathf.Lerp(0.85f, 1.25f, patch);
                // Keep height <= 0.44m near target line so 25m/100m targets stay 100% clear
                float sY = Random.Range(0.80f, 1.12f);

                Quaternion rot = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
                Matrix4x4 mtx = Matrix4x4.TRS(new Vector3(x, -0.01f, z), rot, new Vector3(sXZ, sY, sXZ));

                Vector2Int key = new Vector2Int(Mathf.FloorToInt(x / chunkSize), Mathf.FloorToInt(z / chunkSize));
                if (!buckets.TryGetValue(key, out List<CombineInstance> list))
                {
                    list = new List<CombineInstance>();
                    buckets[key] = list;
                }
                list.Add(new CombineInstance { mesh = wideClumpTemplate, transform = mtx });
            }

            foreach (var kv in buckets)
                CreateChunkObjects($"MidGrass_{kv.Key.x}_{kv.Key.y}", kv.Value, midRoot.transform, midGrassMat, castShadows: false, markStatic: true);

            Object.DestroyImmediate(wideClumpTemplate);

            // Low static wild shrubs framing mid-valley (staying clear of central targets)
            Mesh shrubTemplate = CreateCrossedTuftMesh(2, 1.50f, 0.88f);
            Dictionary<Vector2Int, List<CombineInstance>> shrubBuckets = new Dictionary<Vector2Int, List<CombineInstance>>();
            for (int i = 0; i < 520; i++)
            {
                float x = Random.Range(-155f, 155f);
                float z = Random.Range(26f, 165f);
                if (Mathf.Abs(x) < 6.5f)
                    continue;

                float patch = Mathf.PerlinNoise((x + 810f) * 0.045f, (z + 810f) * 0.045f);
                if (patch < 0.34f)
                    continue;

                float s = Random.Range(0.75f, 1.35f);
                Quaternion rot = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
                Matrix4x4 mtx = Matrix4x4.TRS(new Vector3(x, -0.02f, z), rot, new Vector3(s, s, s));

                Vector2Int key = new Vector2Int(Mathf.FloorToInt(x / 110f), Mathf.FloorToInt(z / 110f));
                if (!shrubBuckets.TryGetValue(key, out List<CombineInstance> list))
                {
                    list = new List<CombineInstance>();
                    shrubBuckets[key] = list;
                }
                list.Add(new CombineInstance { mesh = shrubTemplate, transform = mtx });
            }

            foreach (var kv in shrubBuckets)
                CreateChunkObjects($"MidShrub_{kv.Key.x}_{kv.Key.y}", kv.Value, midRoot.transform, shrubStaticMat, castShadows: false, markStatic: true);

            Object.DestroyImmediate(shrubTemplate);
        }

        private static void BuildFarValleyStaticMeadow(Transform parent, Vector3 anchor, Material farGrassMat, Material shrubStaticMat)
        {
            GameObject farRoot = new GameObject("FarValley_StaticMeadow");
            farRoot.transform.SetParent(parent, false);

            // Wide far-meadow horizon clumps covering 130..440m out toward mountain foothills
            Mesh farClumpTemplate = CreateCrossedTuftMesh(2, 3.10f, 0.48f);
            Dictionary<Vector2Int, List<CombineInstance>> buckets = new Dictionary<Vector2Int, List<CombineInstance>>();
            const float chunkSize = 140f;

            for (int i = 0; i < 4800; i++)
            {
                float x = Random.Range(-290f, 290f);
                float z = Random.Range(115f, 455f);

                // Keep central target sightlines under target bottom height (0.50m)
                float patch = Mathf.PerlinNoise((x + 1120f) * 0.024f, (z + 1120f) * 0.024f);
                if (patch < 0.22f)
                    continue;

                float sXZ = Random.Range(0.85f, 1.55f);
                float sY = (Mathf.Abs(x) < 7.0f) ? Random.Range(0.65f, 0.85f) : Random.Range(0.85f, 1.35f);

                Quaternion rot = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
                Matrix4x4 mtx = Matrix4x4.TRS(new Vector3(x, -0.02f, z), rot, new Vector3(sXZ, sY, sXZ));

                Vector2Int key = new Vector2Int(Mathf.FloorToInt(x / chunkSize), Mathf.FloorToInt(z / chunkSize));
                if (!buckets.TryGetValue(key, out List<CombineInstance> list))
                {
                    list = new List<CombineInstance>();
                    buckets[key] = list;
                }
                list.Add(new CombineInstance { mesh = farClumpTemplate, transform = mtx });
            }

            foreach (var kv in buckets)
                CreateChunkObjects($"FarGrass_{kv.Key.x}_{kv.Key.y}", kv.Value, farRoot.transform, farGrassMat, castShadows: false, markStatic: true);

            Object.DestroyImmediate(farClumpTemplate);

            // Broad static bushes dotting distant plains & foothills (never blocking targets)
            Mesh farShrubTemplate = CreateCrossedTuftMesh(2, 2.30f, 1.15f);
            Dictionary<Vector2Int, List<CombineInstance>> shrubBuckets = new Dictionary<Vector2Int, List<CombineInstance>>();
            for (int i = 0; i < 1050; i++)
            {
                float x = Random.Range(-310f, 310f);
                float z = Random.Range(125f, 480f);
                if (Mathf.Abs(x) < 8.0f)
                    continue;

                float patch = Mathf.PerlinNoise((x + 1650f) * 0.022f, (z + 1650f) * 0.022f);
                if (patch < 0.30f)
                    continue;

                float s = Random.Range(0.80f, 1.65f);
                Quaternion rot = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
                Matrix4x4 mtx = Matrix4x4.TRS(new Vector3(x, -0.03f, z), rot, new Vector3(s, s, s));

                Vector2Int key = new Vector2Int(Mathf.FloorToInt(x / 160f), Mathf.FloorToInt(z / 160f));
                if (!shrubBuckets.TryGetValue(key, out List<CombineInstance> list))
                {
                    list = new List<CombineInstance>();
                    shrubBuckets[key] = list;
                }
                list.Add(new CombineInstance { mesh = farShrubTemplate, transform = mtx });
            }

            foreach (var kv in shrubBuckets)
                CreateChunkObjects($"FarShrub_{kv.Key.x}_{kv.Key.y}", kv.Value, farRoot.transform, shrubStaticMat, castShadows: false, markStatic: true);

            Object.DestroyImmediate(farShrubTemplate);
        }

        private static List<CombineInstance>[] CreateQuadrantBuckets()
        {
            return new[]
            {
                new List<CombineInstance>(2100),
                new List<CombineInstance>(2100),
                new List<CombineInstance>(2100),
                new List<CombineInstance>(2100)
            };
        }

        private static int GetQuadrantIndex(float dx, float dz)
        {
            if (dz >= 0f)
                return dx >= 0f ? 0 : 1;
            return dx >= 0f ? 2 : 3;
        }

        private static void DeleteOldGeneratedMeshes()
        {
            string[] oldAssets = AssetDatabase.FindAssets("t:Mesh", new[] { VegMeshDir });
            foreach (string guid in oldAssets)
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(p))
                    AssetDatabase.DeleteAsset(p);
            }
        }

        private static void CreateChunkObjects(
            string baseName,
            List<CombineInstance> combines,
            Transform parent,
            Material mat,
            bool castShadows,
            bool markStatic)
        {
            if (combines == null || combines.Count == 0)
                return;

            int part = 0;
            for (int offset = 0; offset < combines.Count; offset += MaxTuftsPerUInt16Chunk)
            {
                int count = Mathf.Min(MaxTuftsPerUInt16Chunk, combines.Count - offset);
                CombineInstance[] slice = new CombineInstance[count];
                combines.CopyTo(offset, slice, 0, count);

                string chunkName = part == 0 ? baseName : $"{baseName}_p{part}";
                part++;

                Mesh combined = new Mesh { name = chunkName, indexFormat = IndexFormat.UInt16 };
                combined.CombineMeshes(slice, true, true, false);
                combined.RecalculateBounds();

                string assetPath = $"{VegMeshDir}/{chunkName}.asset";
                AssetDatabase.CreateAsset(combined, assetPath);
                Mesh savedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);

                GameObject go = new GameObject(chunkName);
                go.transform.SetParent(parent, false);
                go.AddComponent<MeshFilter>().sharedMesh = savedMesh;
                MeshRenderer mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
                mr.receiveShadows = true;

                if (markStatic)
                    GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
            }
        }

        private static Mesh CreateCrossedTuftMesh(int planes, float width, float height)
        {
            Mesh mesh = new Mesh();
            List<Vector3> verts = new List<Vector3>();
            List<Vector3> normals = new List<Vector3>();
            List<Vector2> uvs = new List<Vector2>();
            List<Color> colors = new List<Color>();
            List<int> tris = new List<int>();

            float halfW = width * 0.5f;
            Color rootTint = new Color(0.80f, 0.85f, 0.74f, 1f);
            Color tipTint = Color.white;

            for (int i = 0; i < planes; i++)
            {
                float ang = (180f / planes) * i;
                Quaternion r = Quaternion.Euler(0f, ang, 0f);
                int baseIdx = verts.Count;

                verts.Add(r * new Vector3(-halfW, 0f, 0f));
                verts.Add(r * new Vector3(+halfW, 0f, 0f));
                verts.Add(r * new Vector3(+halfW, height, 0f));
                verts.Add(r * new Vector3(-halfW, height, 0f));

                for (int k = 0; k < 4; k++)
                    normals.Add(Vector3.up);

                uvs.Add(new Vector2(0f, 0f));
                uvs.Add(new Vector2(1f, 0f));
                uvs.Add(new Vector2(1f, 1f));
                uvs.Add(new Vector2(0f, 1f));

                colors.Add(rootTint);
                colors.Add(rootTint);
                colors.Add(tipTint);
                colors.Add(tipTint);

                tris.Add(baseIdx + 0);
                tris.Add(baseIdx + 2);
                tris.Add(baseIdx + 1);
                tris.Add(baseIdx + 0);
                tris.Add(baseIdx + 3);
                tris.Add(baseIdx + 2);
            }

            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(colors);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh CreateShortUnderstoryTuftMesh()
        {
            Mesh mesh = new Mesh();
            List<Vector3> verts = new List<Vector3>();
            List<Vector3> normals = new List<Vector3>();
            List<Vector2> uvs = new List<Vector2>();
            List<Color> colors = new List<Color>();
            List<int> tris = new List<int>();

            Color rootTint = new Color(0.82f, 0.88f, 0.75f, 1f);
            Color tipTint = new Color(0.98f, 1.0f, 0.94f, 1f);

            AddQuad(verts, normals, uvs, colors, tris, width: 0.68f, height: 0.16f, tiltDeg: 0f, yawDeg: 0f, rootTint, tipTint);
            AddQuad(verts, normals, uvs, colors, tris, width: 0.68f, height: 0.16f, tiltDeg: 0f, yawDeg: 90f, rootTint, tipTint);
            AddQuad(verts, normals, uvs, colors, tris, width: 0.80f, height: 0.22f, tiltDeg: 56f, yawDeg: 45f, rootTint, tipTint);
            AddQuad(verts, normals, uvs, colors, tris, width: 0.80f, height: 0.22f, tiltDeg: -56f, yawDeg: 135f, rootTint, tipTint);

            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(colors);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void AddQuad(
            List<Vector3> verts,
            List<Vector3> normals,
            List<Vector2> uvs,
            List<Color> colors,
            List<int> tris,
            float width,
            float height,
            float tiltDeg,
            float yawDeg,
            Color rootTint,
            Color tipTint)
        {
            float halfW = width * 0.5f;
            Quaternion r = Quaternion.Euler(0f, yawDeg, 0f) * Quaternion.Euler(tiltDeg, 0f, 0f);
            int baseIdx = verts.Count;

            verts.Add(r * new Vector3(-halfW, 0f, 0f));
            verts.Add(r * new Vector3(+halfW, 0f, 0f));
            verts.Add(r * new Vector3(+halfW, height, 0f));
            verts.Add(r * new Vector3(-halfW, height, 0f));

            for (int k = 0; k < 4; k++)
                normals.Add(Vector3.up);

            uvs.Add(new Vector2(0f, 0f));
            uvs.Add(new Vector2(1f, 0f));
            uvs.Add(new Vector2(1f, 1f));
            uvs.Add(new Vector2(0f, 1f));

            colors.Add(rootTint);
            colors.Add(rootTint);
            colors.Add(tipTint);
            colors.Add(tipTint);

            tris.Add(baseIdx + 0);
            tris.Add(baseIdx + 2);
            tris.Add(baseIdx + 1);
            tris.Add(baseIdx + 0);
            tris.Add(baseIdx + 3);
            tris.Add(baseIdx + 2);
        }

        private static void SpawnPineGroves(Transform parent, Vector3 anchor, GameObject pinePrefab, Material barkMat, Material foliageMat)
        {
            GameObject groveRoot = new GameObject("Alpine_PineGroves");
            groveRoot.transform.SetParent(parent, false);

            int placed = 0;
            for (int attempt = 0; attempt < 1100 && placed < 185; attempt++)
            {
                float x = Random.Range(-390f, 390f);
                float z = Random.Range(-95f, 740f);

                if (Mathf.Abs(x - anchor.x) < 20f && z > -25f && z < 560f)
                    continue;
                if (Vector2.Distance(new Vector2(x, z), new Vector2(anchor.x, anchor.z)) < 28f)
                    continue;

                float cluster = Mathf.PerlinNoise((x + 1400f) * 0.014f, (z + 1400f) * 0.014f);
                if (cluster < 0.36f)
                    continue;

                GameObject tree = (GameObject)PrefabUtility.InstantiatePrefab(pinePrefab, groveRoot.transform);
                tree.name = $"AlpinePine_{placed:D3}";
                tree.transform.position = new Vector3(x, 0f, z);
                tree.transform.rotation = Quaternion.Euler(Random.Range(-2.5f, 2.5f), Random.Range(0f, 360f), Random.Range(-2.5f, 2.5f));
                float s = Random.Range(0.90f, 2.05f);
                tree.transform.localScale = new Vector3(s * Random.Range(0.92f, 1.08f), s, s * Random.Range(0.92f, 1.08f));

                foreach (MeshRenderer mr in tree.GetComponentsInChildren<MeshRenderer>(true))
                {
                    bool isTrunk = mr.gameObject.name.Contains("Trunk");
                    mr.sharedMaterial = isTrunk ? barkMat : foliageMat;
                    mr.shadowCastingMode = ShadowCastingMode.On;
                    mr.receiveShadows = true;
                    Collider c = mr.GetComponent<Collider>();
                    if (c != null) Object.DestroyImmediate(c);
                }

                placed++;
            }
        }

        private static void SpawnBoulders(Transform parent, Vector3 anchor, GameObject boulderPrefab, Material boulderMat)
        {
            GameObject rockRoot = new GameObject("Valley_Boulders");
            rockRoot.transform.SetParent(parent, false);

            int placed = 0;
            for (int attempt = 0; attempt < 600 && placed < 95; attempt++)
            {
                float x = Random.Range(-280f, 280f);
                float z = Random.Range(-60f, 600f);

                if (Mathf.Abs(x - anchor.x) < 16f && z > -10f && z < 530f)
                    continue;
                if (Vector2.Distance(new Vector2(x, z), new Vector2(anchor.x, anchor.z)) < 18f)
                    continue;

                float rockNoise = Mathf.PerlinNoise((x + 2100f) * 0.022f, (z + 2100f) * 0.022f);
                if (rockNoise < 0.38f)
                    continue;

                GameObject rock = (GameObject)PrefabUtility.InstantiatePrefab(boulderPrefab, rockRoot.transform);
                rock.name = $"ValleyBoulder_{placed:D2}";
                float sx = Random.Range(0.65f, 2.4f);
                float sy = sx * Random.Range(0.55f, 0.95f);
                float sz = sx * Random.Range(0.75f, 1.25f);
                rock.transform.position = new Vector3(x, sy * 0.05f, z);
                rock.transform.rotation = Quaternion.Euler(Random.Range(-10f, 10f), Random.Range(0f, 360f), Random.Range(-10f, 10f));
                rock.transform.localScale = new Vector3(sx, sy, sz);

                foreach (MeshRenderer mr in rock.GetComponentsInChildren<MeshRenderer>(true))
                {
                    mr.sharedMaterial = boulderMat;
                    mr.shadowCastingMode = ShadowCastingMode.On;
                    mr.receiveShadows = true;
                    Collider c = mr.GetComponent<Collider>();
                    if (c != null) Object.DestroyImmediate(c);
                }

                placed++;
            }
        }

        private static Material BuildFoliageMaterial(
            string matName,
            string texPath,
            Color tint,
            float cutoff,
            float windStrength,
            float windSpeed,
            float fadeStart,
            float fadeEnd,
            float translucency)
        {
            string matPath = $"{EnvMatDir}/{matName}.mat";
            Shader shader = Shader.Find("VRAtis/AlpineFoliageURP");
            if (shader == null)
                shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Shaders/AlpineFoliageURP.shader");

            Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, matPath);
            }
            else if (shader != null)
            {
                mat.shader = shader;
            }

            TextureImporter importer = AssetImporter.GetAtPath(texPath) as TextureImporter;
            if (importer != null)
            {
                bool dirty = false;
                if (!importer.alphaIsTransparency)
                {
                    importer.alphaIsTransparency = true;
                    dirty = true;
                }
                if (importer.wrapMode != TextureWrapMode.Clamp)
                {
                    importer.wrapMode = TextureWrapMode.Clamp;
                    dirty = true;
                }
                if (importer.maxTextureSize > 512)
                {
                    importer.maxTextureSize = 512;
                    dirty = true;
                }
                if (dirty)
                    importer.SaveAndReimport();
            }

            Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            if (tex != null)
                mat.SetTexture("_BaseMap", tex);

            mat.SetColor("_BaseColor", tint);
            mat.SetFloat("_Cutoff", cutoff);
            mat.SetFloat("_WindStrength", windStrength);
            mat.SetFloat("_WindSpeed", windSpeed);
            mat.SetFloat("_FadeStart", fadeStart);
            mat.SetFloat("_FadeEnd", fadeEnd);
            mat.SetFloat("_Translucency", translucency);
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Material BuildBarkMaterial()
        {
            string matPath = $"{EnvMatDir}/AlpineBark_Mat.mat";
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, matPath);
            }
            else if (shader != null)
            {
                mat.shader = shader;
            }

            Texture2D barkTex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{VegTexDir}/AlpineBark.png");
            if (barkTex != null)
                mat.SetTexture("_BaseMap", barkTex);

            mat.SetColor("_BaseColor", new Color(0.88f, 0.84f, 0.80f, 1f));
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Smoothness", 0.08f);
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Material BuildBoulderMaterial()
        {
            string matPath = $"{EnvMatDir}/ValleyBoulder_Mat.mat";
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, matPath);
            }
            else if (shader != null)
            {
                mat.shader = shader;
            }

            mat.SetColor("_BaseColor", new Color(0.36f, 0.37f, 0.35f, 1f));
            mat.SetFloat("_Metallic", 0.02f);
            mat.SetFloat("_Smoothness", 0.14f);
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static void EnsureFolder(string parent, string folderName)
        {
            string full = $"{parent}/{folderName}";
            if (!AssetDatabase.IsValidFolder(full))
                AssetDatabase.CreateFolder(parent, folderName);
        }
    }
}
