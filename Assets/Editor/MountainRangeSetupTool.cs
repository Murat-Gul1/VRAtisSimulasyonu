using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using VRAtis.Environment;
using VRAtis.Targets;

namespace VRAtis.EditorTools
{
    [InitializeOnLoad]
    public static class MountainRangeSetupTool
    {
        private const string EverestFbxPath = "Assets/Models/Environment/Mountains/Mountain_EverestRidge.fbx";
        private const string AraratFbxPath = "Assets/Models/Environment/Mountains/Mountain_AraratPeak.fbx";
        private const string MountainTexDir = "Assets/Models/Environment/Mountains/Textures";
        private const string GroundTexDir = "Assets/Models/Environment/Ground/Textures";
        private const string EnvMatDir = "Assets/Materials/Environment";
        private const string AutoSetupVersionKey = "VRAtis_Mountains_V2_Done";

        static MountainRangeSetupTool()
        {
            EditorApplication.delayCall += TryAutoSetupOnce;
        }

        private static void TryAutoSetupOnce()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || Application.isBatchMode)
                return;

            if (GameObject.Find("Environment_Mountains") != null && EditorPrefs.GetBool(AutoSetupVersionKey, false))
                return;

            if (AssetDatabase.LoadAssetAtPath<GameObject>(EverestFbxPath) == null ||
                AssetDatabase.LoadAssetAtPath<GameObject>(AraratFbxPath) == null)
                return;

            SetupMountainsAndRange();
        }

        [MenuItem("VRAtis/Dağlık 500m Poligon Kur")]
        public static void SetupMountainsAndRange()
        {
            GameObject everestPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(EverestFbxPath);
            GameObject araratPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(AraratFbxPath);
            if (everestPrefab == null || araratPrefab == null)
            {
                Debug.LogError("[MountainRangeSetupTool] Dağ FBX modelleri bulunamadı.");
                return;
            }

            EnsureFolder("Assets/Materials", "Environment");

            Material matEverest = BuildMountainMaterial("Mountain_Everest_Mat", $"{MountainTexDir}/Everest_baseColor.png");
            Material matArarat = BuildMountainMaterial("Mountain_Ararat_Mat", $"{MountainTexDir}/Ararat_baseColor.png");
            Material matGround = BuildGroundMaterial();

            // 1. Setup 3000x3000m valley floor (with 500x500m unobstructed shooting range directly ahead of player at Z=0..500)
            GameObject ground = GameObject.Find("Ground");
            if (ground == null)
            {
                ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
                ground.name = "Ground";
            }
            ground.transform.position = new Vector3(0f, 0f, 250f);
            ground.transform.rotation = Quaternion.identity;
            ground.transform.localScale = new Vector3(300f, 1f, 300f); // 3000m x 3000m continuous valley
            ground.isStatic = false; // Dynamic shadows move across the ground with sun angle
            MeshRenderer groundMr = ground.GetComponent<MeshRenderer>();
            if (groundMr != null && matGround != null)
                groundMr.sharedMaterial = matGround;

            // 2. Extend Camera far clip plane so distant alpine peaks up to 3.5 km never clip
            Camera mainCam = Camera.main;
            if (mainCam != null)
            {
                mainCam.farClipPlane = 4500f;
                EditorUtility.SetDirty(mainCam);
            }

            // 3. Ensure 300m and 500m targets exist along the 500m range so depth is immediately readable
            EnsureRangeTarget("TestTarget_300m", new Vector3(-2.5f, 1.5f, 300f), new Vector3(1.8f, 2.0f, 0.25f));
            EnsureRangeTarget("TestTarget_500m", new Vector3(0.15f, 1.8f, 500f), new Vector3(2.4f, 2.6f, 0.3f));

            // 4. Rebuild Environment_Mountains ring + Rear Shadow Mountain
            GameObject existingRoot = GameObject.Find("Environment_Mountains");
            if (existingRoot != null)
                Object.DestroyImmediate(existingRoot);

            GameObject mountainRoot = new GameObject("Environment_Mountains");

            // A) Rear Shadow Mountain right behind & slightly left of player so its dynamic shadow falls directly onto player (0,0,0)
            //    Width ~420m, peak height ~135m, centered at (-45, 0, -215). Foothills taper naturally underground well behind Z=0!
            SpawnMountain(
                "Mountain_RearShadow_Ararat",
                araratPrefab,
                matArarat,
                mountainRoot.transform,
                new Vector3(-45f, 0f, -215f),
                new Vector3(0f, 22f, 0f),
                new Vector3(4.2f, 4.4f, 4.2f),
                castShadows: true);

            // B) Continuous Distant Hero Snowy Backdrop beyond the 500m range (Z = +920m..+1150m)
            SpawnMountain(
                "Mountain_NorthHero_Everest",
                everestPrefab,
                matEverest,
                mountainRoot.transform,
                new Vector3(20f, 0f, 980f),
                new Vector3(0f, 185f, 0f),
                new Vector3(13.5f, 13.0f, 13.5f),
                castShadows: true);

            SpawnMountain(
                "Mountain_NorthWest_Ararat",
                araratPrefab,
                matArarat,
                mountainRoot.transform,
                new Vector3(-620f, 0f, 880f),
                new Vector3(0f, 38f, 0f),
                new Vector3(11.5f, 11.5f, 11.5f),
                castShadows: true);

            SpawnMountain(
                "Mountain_NorthEast_Ararat",
                araratPrefab,
                matArarat,
                mountainRoot.transform,
                new Vector3(680f, 0f, 900f),
                new Vector3(0f, -32f, 0f),
                new Vector3(11.8f, 12.0f, 11.8f),
                castShadows: true);

            // C) Left (West) Continuous Valley Wall framing the 500x500m range
            SpawnMountain(
                "Mountain_WestFlank_Everest",
                everestPrefab,
                matEverest,
                mountainRoot.transform,
                new Vector3(-760f, 0f, 340f),
                new Vector3(0f, 92f, 0f),
                new Vector3(11.0f, 10.5f, 11.0f),
                castShadows: true);

            SpawnMountain(
                "Mountain_SouthWest_Ararat",
                araratPrefab,
                matArarat,
                mountainRoot.transform,
                new Vector3(-640f, 0f, -220f),
                new Vector3(0f, 125f, 0f),
                new Vector3(9.5f, 9.5f, 9.5f),
                castShadows: true);

            // D) Right (East) Continuous Valley Wall framing the 500x500m range
            SpawnMountain(
                "Mountain_EastFlank_Everest",
                everestPrefab,
                matEverest,
                mountainRoot.transform,
                new Vector3(780f, 0f, 360f),
                new Vector3(0f, -88f, 0f),
                new Vector3(11.2f, 10.8f, 11.2f),
                castShadows: true);

            SpawnMountain(
                "Mountain_SouthEast_Ararat",
                araratPrefab,
                matArarat,
                mountainRoot.transform,
                new Vector3(630f, 0f, -240f),
                new Vector3(0f, -135f, 0f),
                new Vector3(9.5f, 9.5f, 9.5f),
                castShadows: true);

            // E) Far South Horizon Ridge behind Rear Shadow Mountain
            SpawnMountain(
                "Mountain_SouthBackdrop_Everest",
                everestPrefab,
                matEverest,
                mountainRoot.transform,
                new Vector3(80f, 0f, -760f),
                new Vector3(0f, 8f, 0f),
                new Vector3(12.5f, 12.0f, 12.5f),
                castShadows: true);

            // 5. Attach & configure DynamicTimeOfDay on EnvironmentManager
            GameObject envMgr = GameObject.Find("EnvironmentManager");
            if (envMgr != null)
            {
                DynamicTimeOfDay tod = envMgr.GetComponent<DynamicTimeOfDay>();
                if (tod == null)
                    tod = envMgr.AddComponent<DynamicTimeOfDay>();

                tod.SetTimeOfDay(16.45f);
                EditorUtility.SetDirty(tod);
            }

            EditorSceneManager.MarkSceneDirty(mountainRoot.scene);
            EditorSceneManager.SaveOpenScenes();
            AssetDatabase.SaveAssets();
            EditorPrefs.SetBool(AutoSetupVersionKey, true);
            Debug.Log("[MountainRangeSetupTool] 500x500m poligon vadisi, 360 derece karlı dağ halkası ve dinamik arka dağ gölgesi başarıyla kuruldu!");
        }

        private static void SpawnMountain(
            string name,
            GameObject prefab,
            Material mat,
            Transform parent,
            Vector3 position,
            Vector3 eulerRot,
            Vector3 scale,
            bool castShadows)
        {
            GameObject go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            go.name = name;
            go.transform.position = position;
            go.transform.rotation = Quaternion.Euler(eulerRot);
            go.transform.localScale = scale;
            go.isStatic = false;

            foreach (MeshRenderer mr in go.GetComponentsInChildren<MeshRenderer>(true))
            {
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
                mr.receiveShadows = true;

                // Remove unnecessary colliders on distant mountains for mobile VR performance;
                // keep collider on the close rear shadow mountain if needed
                if (!name.Contains("RearShadow"))
                {
                    Collider col = mr.GetComponent<Collider>();
                    if (col != null)
                        Object.DestroyImmediate(col);
                }
            }
        }

        private static void EnsureRangeTarget(string targetName, Vector3 pos, Vector3 scale)
        {
            GameObject existing = GameObject.Find(targetName);
            if (existing != null)
                return;

            GameObject t = GameObject.CreatePrimitive(PrimitiveType.Cube);
            t.name = targetName;
            t.transform.position = pos;
            t.transform.localScale = scale;
            t.AddComponent<TestTargetFeedback>();
        }

        private static Material BuildMountainMaterial(string matName, string baseColorPath)
        {
            string matPath = $"{EnvMatDir}/{matName}.mat";
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, matPath);
            }

            Texture2D tex = LoadTexture(baseColorPath, false, 2048);
            if (tex != null)
                mat.SetTexture("_BaseMap", tex);

            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_Metallic", 0.0f);
            mat.SetFloat("_Smoothness", 0.18f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Material BuildGroundMaterial()
        {
            string matPath = $"{EnvMatDir}/RangeGround_Mat.mat";
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, matPath);
            }

            Texture2D diff = LoadTexture($"{GroundTexDir}/grass_ground_diff.jpg", false, 1024);
            Texture2D nor = LoadTexture($"{GroundTexDir}/grass_ground_nor.png", true, 1024);

            if (diff != null)
                mat.SetTexture("_BaseMap", diff);
            if (nor != null)
            {
                mat.SetTexture("_BumpMap", nor);
                mat.SetFloat("_BumpScale", 0.85f);
                mat.EnableKeyword("_NORMALMAP");
            }

            // 3000m plane tiled cleanly without dark moire
            mat.SetTextureScale("_BaseMap", new Vector2(220f, 220f));
            mat.SetTextureScale("_BumpMap", new Vector2(220f, 220f));
            mat.SetColor("_BaseColor", new Color(1.22f, 1.24f, 1.10f, 1f));
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Smoothness", 0.04f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Texture2D LoadTexture(string path, bool isNormal, int maxSize)
        {
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                bool dirty = false;
                if (isNormal && importer.textureType != TextureImporterType.NormalMap)
                {
                    importer.textureType = TextureImporterType.NormalMap;
                    dirty = true;
                }
                if (importer.maxTextureSize > maxSize)
                {
                    importer.maxTextureSize = maxSize;
                    dirty = true;
                }
                if (dirty)
                    importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static void EnsureFolder(string parent, string folderName)
        {
            string full = $"{parent}/{folderName}";
            if (!AssetDatabase.IsValidFolder(full))
                AssetDatabase.CreateFolder(parent, folderName);
        }
    }
}
