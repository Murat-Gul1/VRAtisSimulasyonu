using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using VRAtis.Targets;

namespace VRAtis.EditorTools
{
    [InitializeOnLoad]
    public static class SteelGongSetupTool
    {
        private const string FbxPath = "Assets/Models/Targets/SteelGongTargetStand.fbx";
        private const string SilhouetteFbxPath = "Assets/Models/Targets/SteelSilhouetteTargetStand.fbx";
        private const string TexDir = "Assets/Models/Targets/Textures";
        private const string MatDir = "Assets/Materials/Targets";
        private const string AudioDir = "Assets/Audio/Targets";
        private const string PrefabPath = "Assets/Prefabs/SteelGongTargetStand.prefab";
        private const string SilhouettePrefabPath = "Assets/Prefabs/SteelSilhouetteTargetStand.prefab";
        private const string AutoSetupVersionKey = "VRAtis_SteelGongs_V6_Done";

        private static readonly string[] LegacyTargetNames =
        {
            "TestTarget_25m",
            "TestTarget_100m",
            "TestTarget_300m",
            "TestTarget_500m",
            "SteelGongTargetStand"
        };

        private static readonly string[] GongStandNames =
        {
            "SteelGong_25m",
            "SteelGong_100m",
            "SteelGong_300m",
            "SteelGong_500m"
        };

        static SteelGongSetupTool()
        {
            EditorApplication.delayCall += TryAutoSetupOnce;
        }

        private static void TryAutoSetupOnce()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || Application.isBatchMode)
                return;

            if (GameObject.Find("SteelGong_100m") != null && EditorPrefs.GetBool(AutoSetupVersionKey, false))
                return;

            if (AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath) == null)
                return;

            SetupSteelGongTargets();
        }

        [MenuItem("VRAtis/Sallanan Metal Hedefleri (Gong) Kur")]
        public static void SetupSteelGongTargets()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("[SteelGongSetupTool] Play modu aktifken sahne kurulumu çalıştırılamaz; lütfen Play modundan çıkıp tekrar deneyin.");
                return;
            }

            GameObject fbxPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
            if (fbxPrefab == null)
            {
                Debug.LogError($"[SteelGongSetupTool] FBX bulunamadı: {FbxPath}");
                return;
            }

            EnsureFolder("Assets", "Materials");
            EnsureFolder("Assets/Materials", "Targets");
            EnsureFolder("Assets", "Prefabs");

            EnsureRequiredTags("Ground", "SteelArmor");
            TagGroundIfPresent();

            Material matGongFace = BuildGongFaceMaterial();
            Material matGongEdge = BuildMetalMaterial(
                "GongPlate_EdgeSteel",
                $"{TexDir}/metal_plate_02_Diffuse.png",
                $"{TexDir}/metal_plate_02_nor_gl.png",
                $"{TexDir}/metal_plate_02_Metal.png",
                $"{TexDir}/metal_plate_02_Rough.png",
                new Color(0.55f, 0.56f, 0.58f, 1f),
                metallicFallback: 0.88f,
                smoothnessFallback: 0.48f);

            Material matChain = BuildMetalMaterial(
                "GongChain_Steel",
                $"{TexDir}/metal_plate_02_Diffuse.png",
                $"{TexDir}/metal_plate_02_nor_gl.png",
                null,
                $"{TexDir}/metal_plate_02_Rough.png",
                new Color(0.76f, 0.78f, 0.80f, 1f),
                metallicFallback: 0.90f,
                smoothnessFallback: 0.62f);

            Material matStandFrame = BuildMetalMaterial(
                "GongStand_Frame",
                $"{TexDir}/rusty_metal_02_Diffuse.png",
                $"{TexDir}/rusty_metal_02_nor_gl.png",
                null,
                $"{TexDir}/rusty_metal_02_Rough.png",
                new Color(0.28f, 0.29f, 0.31f, 1f),
                metallicFallback: 0.62f,
                smoothnessFallback: 0.36f);

            AudioClip[] gongHitClips =
            {
                LoadConfiguredTargetClip($"{AudioDir}/SteelGong_Hit_01.wav"),
                LoadConfiguredTargetClip($"{AudioDir}/SteelGong_Hit_02.wav"),
                LoadConfiguredTargetClip($"{AudioDir}/SteelGong_Hit_03.wav")
            };
            AudioClip[] frameClankClips =
            {
                LoadConfiguredTargetClip($"{AudioDir}/SteelFrame_Clank_01.wav")
            };

            GameObject prefabAsset = BuildOrUpdateGongPrefab(
                fbxPrefab,
                matGongFace,
                matGongEdge,
                matChain,
                matStandFrame,
                gongHitClips,
                frameClankClips);

            GameObject silhouettePrefabAsset = null;
            GameObject silhouetteFbx = AssetDatabase.LoadAssetAtPath<GameObject>(SilhouetteFbxPath);
            if (silhouetteFbx != null)
            {
                Material matSilhouetteFace = BuildSilhouetteFaceMaterial();
                silhouettePrefabAsset = BuildOrUpdateSilhouettePrefab(
                    silhouetteFbx,
                    matSilhouetteFace,
                    matGongEdge,
                    matStandFrame,
                    gongHitClips,
                    frameClankClips);
            }

            RemoveObjectsByName(LegacyTargetNames);
            RemoveObjectsByName(GongStandNames);

            Vector3 shooterPos = new Vector3(2.01f, 0f, -3.783f);
            GameObject playerRifle = GameObject.Find("PlayerRifle");
            if (playerRifle != null)
                shooterPos = new Vector3(playerRifle.transform.position.x, 0f, playerRifle.transform.position.z);

            // Closest gong aligned dead ahead of shooter's startup barrel height (~1.42m); distant gongs staggered for clean sightlines
            PlaceGongStation(prefabAsset, "SteelGong_25m",  new Vector3(shooterPos.x, 0f,  25f), 1.24f, 12f, shooterPos);
            PlaceGongStation(prefabAsset, "SteelGong_100m", new Vector3(0.15f,        0f, 100f), 1.20f, 14f, shooterPos);
            PlaceGongStation(prefabAsset, "SteelGong_300m", new Vector3(-2.50f,       0f, 300f), 1.50f, 16f, shooterPos);
            PlaceGongStation(prefabAsset, "SteelGong_500m", new Vector3(4.20f,        0f, 500f), 2.00f, 18f, shooterPos);

            EnsureTargetRangeManager(prefabAsset, silhouettePrefabAsset, playerRifle != null ? playerRifle.transform : null);

            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkAllScenesDirty();
            EditorSceneManager.SaveOpenScenes();
            EditorPrefs.SetBool(AutoSetupVersionKey, true);

            Debug.Log("[SteelGongSetupTool] AR500 zincirli Gong + yere çakılı Metal İnsan Silueti prefabları ve TargetRangeManager kuruldu.");
        }

        public static TargetRangeManager EnsureTargetRangeManager(GameObject gongPrefab, Transform shooterOrigin)
        {
            return EnsureTargetRangeManager(gongPrefab, null, shooterOrigin);
        }

        public static TargetRangeManager EnsureTargetRangeManager(GameObject gongPrefab, GameObject silhouettePrefab, Transform shooterOrigin)
        {
            TargetRangeManager mgr = Object.FindAnyObjectByType<TargetRangeManager>();
            if (mgr == null)
            {
                GameObject mgrGo = GameObject.Find("TargetRangeManager") ?? new GameObject("TargetRangeManager");
                mgr = GetOrAddComponent<TargetRangeManager>(mgrGo);
            }

            if (gongPrefab == null)
                gongPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (silhouettePrefab == null)
                silhouettePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(SilhouettePrefabPath);

            if (shooterOrigin == null)
            {
                GameObject rifle = GameObject.Find("PlayerRifle");
                if (rifle != null) shooterOrigin = rifle.transform;
            }

            SerializedObject so = new SerializedObject(mgr);
            if (gongPrefab != null)
                so.FindProperty("steelGongPrefab").objectReferenceValue = gongPrefab;
            if (silhouettePrefab != null)
                so.FindProperty("metalSilhouettePrefab").objectReferenceValue = silhouettePrefab;
            GameObject soldierPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(EnemySoldierSetupTool.PrefabPath);
            if (soldierPrefab != null && so.FindProperty("enemySoldierPrefab") != null)
                so.FindProperty("enemySoldierPrefab").objectReferenceValue = soldierPrefab;
            if (shooterOrigin != null)
                so.FindProperty("shooterOriginTransform").objectReferenceValue = shooterOrigin;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(mgr.gameObject);
            return mgr;
        }

        private static GameObject BuildOrUpdateGongPrefab(
            GameObject fbxPrefab,
            Material matGongFace,
            Material matGongEdge,
            Material matChain,
            Material matStandFrame,
            AudioClip[] gongHitClips,
            AudioClip[] frameClankClips)
        {
            GameObject tempRoot = (GameObject)PrefabUtility.InstantiatePrefab(fbxPrefab);
            tempRoot.name = "__SteelGongPrefabTemp";
            tempRoot.transform.position = Vector3.zero;
            tempRoot.transform.rotation = Quaternion.identity;
            tempRoot.transform.localScale = Vector3.one;

            // Unpack FBX instance so we can save a clean self-contained prefab
            PrefabUtility.UnpackPrefabInstance(tempRoot, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

            try
            {
                Transform standTr = FindChildByName(tempRoot.transform, "Stand_Frame");
                Transform pivotTr = FindChildByName(tempRoot.transform, "Gong_Pivot");
                Transform chainsTr = FindChildByName(tempRoot.transform, "Gong_Chains");
                Transform plateTr = FindChildByName(tempRoot.transform, "AR500_GongPlate");
                int paintedSubmeshIdx = 0;

                if (plateTr != null)
                {
                    MeshRenderer plateMr = plateTr.GetComponent<MeshRenderer>();
                    if (plateMr != null && plateMr.sharedMaterials != null)
                    {
                        Material[] mats = plateMr.sharedMaterials;
                        if (mats.Length >= 2)
                        {
                            bool anyNamedMatch = false;
                            for (int i = 0; i < mats.Length; i++)
                            {
                                string mn = mats[i] != null ? mats[i].name : "";
                                if (mn.Contains("PaintedFace"))
                                {
                                    mats[i] = matGongFace;
                                    paintedSubmeshIdx = i;
                                    anyNamedMatch = true;
                                }
                                else if (mn.Contains("EdgeSteel") || mn.Contains("Steel"))
                                {
                                    mats[i] = matGongEdge;
                                    anyNamedMatch = true;
                                }
                            }
                            if (!anyNamedMatch)
                            {
                                mats[0] = matGongFace;
                                mats[1] = matGongEdge;
                                paintedSubmeshIdx = 0;
                            }
                            plateMr.sharedMaterials = mats;
                        }
                        else
                        {
                            plateMr.sharedMaterial = matGongFace;
                        }
                    }
                }

                // Guarantee the stand is upright AND chains + gong plate hang directly underneath the crossbar pivot
                EnsureStandUprightAndGrounded(standTr, pivotTr, chainsTr, plateTr);

                // Normalize front orientation so the painted bullseye face points strictly along -Z (towards shooter when LookRotation faces +Z downrange)
                if (plateTr != null && DetectPaintedFacePlusZ(plateTr, paintedSubmeshIdx))
                {
                    if (standTr != null) standTr.RotateAround(Vector3.zero, Vector3.up, 180f);
                    if (pivotTr != null) pivotTr.RotateAround(Vector3.zero, Vector3.up, 180f);
                }

                if (standTr != null)
                {
                    AssignRendererMaterial(standTr.gameObject, matStandFrame);
                    standTr.gameObject.tag = "SteelArmor";
                    MeshCollider standCol = GetOrAddComponent<MeshCollider>(standTr.gameObject);
                    standCol.sharedMesh = standTr.GetComponent<MeshFilter>() != null ? standTr.GetComponent<MeshFilter>().sharedMesh : null;
                    standCol.convex = false;
                }

                if (chainsTr != null)
                {
                    AssignRendererMaterial(chainsTr.gameObject, matChain);
                }

                if (plateTr != null)
                {
                    plateTr.gameObject.tag = "SteelArmor";

                    Rigidbody rb = GetOrAddComponent<Rigidbody>(plateTr.gameObject);
                    rb.isKinematic = true;
                    rb.useGravity = false;

                    MeshCollider plateCol = GetOrAddComponent<MeshCollider>(plateTr.gameObject);
                    plateCol.sharedMesh = plateTr.GetComponent<MeshFilter>() != null ? plateTr.GetComponent<MeshFilter>().sharedMesh : null;
                    plateCol.convex = true;
                }

                SteelGongTarget gong = GetOrAddComponent<SteelGongTarget>(tempRoot);
                SerializedObject gongSo = new SerializedObject(gong);
                gongSo.FindProperty("gongPivot").objectReferenceValue = pivotTr;
                gongSo.FindProperty("gongPlateTransform").objectReferenceValue = plateTr;
                gongSo.FindProperty("standFrameTransform").objectReferenceValue = standTr;
                if (pivotTr != null && plateTr != null)
                    gongSo.FindProperty("pendulumLengthMeters").floatValue = Mathf.Max(0.25f, Vector3.Distance(pivotTr.position, plateTr.position));
                SetClipArray(gongSo.FindProperty("gongHitClips"), gongHitClips);
                SetClipArray(gongSo.FindProperty("frameClankClips"), frameClankClips);
                gongSo.ApplyModifiedPropertiesWithoutUndo();

                if (plateTr != null)
                {
                    SteelGongHitReceiver plateRecv = GetOrAddComponent<SteelGongHitReceiver>(plateTr.gameObject);
                    plateRecv.Configure(gong, standFrame: false);
                }
                if (standTr != null)
                {
                    SteelGongHitReceiver frameRecv = GetOrAddComponent<SteelGongHitReceiver>(standTr.gameObject);
                    frameRecv.Configure(gong, standFrame: true);
                }

                tempRoot.name = "SteelGongTargetStand";
                return PrefabUtility.SaveAsPrefabAsset(tempRoot, PrefabPath);
            }
            finally
            {
                if (tempRoot != null)
                    Object.DestroyImmediate(tempRoot);
            }
        }

        private static GameObject BuildOrUpdateSilhouettePrefab(
            GameObject fbxPrefab,
            Material matSilhouetteFace,
            Material matEdgeSteel,
            Material matStandFrame,
            AudioClip[] gongHitClips,
            AudioClip[] frameClankClips)
        {
            GameObject tempRoot = (GameObject)PrefabUtility.InstantiatePrefab(fbxPrefab);
            tempRoot.name = "__SteelSilhouettePrefabTemp";
            tempRoot.transform.position = Vector3.zero;
            tempRoot.transform.rotation = Quaternion.identity;
            tempRoot.transform.localScale = Vector3.one;

            PrefabUtility.UnpackPrefabInstance(tempRoot, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

            try
            {
                Transform standTr = FindChildByName(tempRoot.transform, "Stand_Frame");
                Transform pivotTr = FindChildByName(tempRoot.transform, "Gong_Pivot");
                Transform plateTr = FindChildByName(tempRoot.transform, "AR500_GongPlate");
                int paintedSubmeshIdx = 0;

                if (plateTr != null)
                {
                    MeshRenderer plateMr = plateTr.GetComponent<MeshRenderer>();
                    if (plateMr != null && plateMr.sharedMaterials != null)
                    {
                        Material[] mats = plateMr.sharedMaterials;
                        if (mats.Length >= 2)
                        {
                            bool anyNamedMatch = false;
                            for (int i = 0; i < mats.Length; i++)
                            {
                                string mn = mats[i] != null ? mats[i].name : "";
                                if (mn.Contains("PaintedFace"))
                                {
                                    mats[i] = matSilhouetteFace;
                                    paintedSubmeshIdx = i;
                                    anyNamedMatch = true;
                                }
                                else if (mn.Contains("EdgeSteel") || mn.Contains("Steel"))
                                {
                                    mats[i] = matEdgeSteel;
                                    anyNamedMatch = true;
                                }
                            }
                            if (!anyNamedMatch)
                            {
                                mats[0] = matSilhouetteFace;
                                mats[1] = matEdgeSteel;
                                paintedSubmeshIdx = 0;
                            }
                            plateMr.sharedMaterials = mats;
                        }
                        else
                        {
                            plateMr.sharedMaterial = matSilhouetteFace;
                        }
                    }
                }

                // Guarantee upright (+Y) standing orientation from Blender Z-up FBX, grounded at Y = 0, facing -Z toward shooter
                EnsureSilhouetteUprightAndGrounded(standTr, pivotTr, plateTr);

                if (plateTr != null && DetectPaintedFacePlusZ(plateTr, paintedSubmeshIdx))
                {
                    if (standTr != null) standTr.RotateAround(Vector3.zero, Vector3.up, 180f);
                    if (pivotTr != null) pivotTr.RotateAround(Vector3.zero, Vector3.up, 180f);
                }

                if (standTr != null)
                {
                    AssignRendererMaterial(standTr.gameObject, matStandFrame);
                    standTr.gameObject.tag = "SteelArmor";
                    MeshCollider standCol = GetOrAddComponent<MeshCollider>(standTr.gameObject);
                    standCol.sharedMesh = standTr.GetComponent<MeshFilter>() != null ? standTr.GetComponent<MeshFilter>().sharedMesh : null;
                    standCol.convex = false;
                }

                if (plateTr != null)
                {
                    plateTr.gameObject.tag = "SteelArmor";
                    // Non-convex MeshCollider preserves exact head/neck/shoulder silhouette hitbox for raycast ballistics
                    MeshCollider plateCol = GetOrAddComponent<MeshCollider>(plateTr.gameObject);
                    plateCol.sharedMesh = plateTr.GetComponent<MeshFilter>() != null ? plateTr.GetComponent<MeshFilter>().sharedMesh : null;
                    plateCol.convex = false;
                }

                SteelGongTarget target = GetOrAddComponent<SteelGongTarget>(tempRoot);
                SerializedObject targetSo = new SerializedObject(target);
                targetSo.FindProperty("gongPivot").objectReferenceValue = pivotTr;
                targetSo.FindProperty("gongPlateTransform").objectReferenceValue = plateTr;
                targetSo.FindProperty("standFrameTransform").objectReferenceValue = standTr;
                float armLen = (pivotTr != null && plateTr != null)
                    ? Mathf.Max(0.28f, Vector3.Distance(pivotTr.position, plateTr.position))
                    : 0.39f;
                targetSo.FindProperty("pendulumLengthMeters").floatValue = armLen;
                targetSo.FindProperty("plateMassKg").floatValue = 14f;
                targetSo.FindProperty("angularDamping").floatValue = 2.4f;
                targetSo.FindProperty("maxSwingAngleDeg").floatValue = 38f;
                targetSo.FindProperty("lateralTwistStiffness").floatValue = 46f;
                targetSo.FindProperty("lateralTwistDamping").floatValue = 5.2f;
                SetClipArray(targetSo.FindProperty("gongHitClips"), gongHitClips);
                SetClipArray(targetSo.FindProperty("frameClankClips"), frameClankClips);
                targetSo.ApplyModifiedPropertiesWithoutUndo();

                if (plateTr != null)
                {
                    SteelGongHitReceiver plateRecv = GetOrAddComponent<SteelGongHitReceiver>(plateTr.gameObject);
                    plateRecv.Configure(target, standFrame: false);
                }
                if (standTr != null)
                {
                    SteelGongHitReceiver frameRecv = GetOrAddComponent<SteelGongHitReceiver>(standTr.gameObject);
                    frameRecv.Configure(target, standFrame: true);
                }

                tempRoot.name = "SteelSilhouetteTargetStand";
                return PrefabUtility.SaveAsPrefabAsset(tempRoot, SilhouettePrefabPath);
            }
            finally
            {
                if (tempRoot != null)
                    Object.DestroyImmediate(tempRoot);
            }
        }

        private static T GetOrAddComponent<T>(GameObject go) where T : Component
        {
            T existing = go.GetComponent<T>();
            if (existing != null)
                return existing;
            return go.AddComponent<T>();
        }

        private static void EnsureSilhouetteUprightAndGrounded(Transform standTr, Transform pivotTr, Transform plateTr)
        {
            if (standTr == null)
                return;

            MeshRenderer standMr = standTr.GetComponent<MeshRenderer>();
            if (standMr == null)
                return;

            // 1. If Blender Z-up FBX imported lying along +Z (size.z > size.y), stand both frame & pivot upright along +Y
            Bounds sb = standMr.bounds;
            if (sb.size.y < sb.size.z * 0.90f)
            {
                standTr.RotateAround(Vector3.zero, Vector3.right, -90f);
                if (pivotTr != null)
                    pivotTr.RotateAround(Vector3.zero, Vector3.right, -90f);
                sb = standMr.bounds;
            }

            // 2. If plate ended up hanging below pivot instead of standing above the ground stake, flip upright
            if (pivotTr != null && plateTr != null && plateTr.position.y < pivotTr.position.y)
            {
                standTr.RotateAround(Vector3.zero, Vector3.right, 180f);
                pivotTr.RotateAround(Vector3.zero, Vector3.right, 180f);
                sb = standMr.bounds;
            }

            // 3. Ground the anchor plate cleanly at Y = 0 and shift pivot by the exact same offset
            float minY = sb.min.y;
            if (Mathf.Abs(minY) > 0.001f)
            {
                Vector3 shiftY = new Vector3(0f, -minY, 0f);
                standTr.position += shiftY;
                if (pivotTr != null)
                    pivotTr.position += shiftY;
            }

            Bounds finalStand = standMr.bounds;
            Bounds finalPlate = plateTr != null && plateTr.GetComponent<MeshRenderer>() != null
                ? plateTr.GetComponent<MeshRenderer>().bounds
                : new Bounds();
            Debug.Log($"[SteelGongSetupTool] Metal İnsan Silueti hizalandı -> Stand boyut={finalStand.size:F2} tabanY={finalStand.min.y:F3} | PivotY={(pivotTr != null ? pivotTr.position.y : 0f):F2} | Siluet={finalPlate.min.y:F2}..{finalPlate.max.y:F2}");
        }

        private static void EnsureStandUprightAndGrounded(Transform standTr, Transform pivotTr, Transform chainsTr, Transform plateTr)
        {
            if (standTr == null)
                return;

            MeshRenderer standMr = standTr.GetComponent<MeshRenderer>();
            if (standMr == null)
                return;

            Bounds sb = standMr.bounds;
            float maxDim = Mathf.Max(sb.size.x, Mathf.Max(sb.size.y, sb.size.z));
            if (maxDim > 0.0001f && maxDim < 0.25f)
            {
                float fixScale = 1.85f / maxDim;
                standTr.localScale *= fixScale;
                standTr.position *= fixScale;
                if (pivotTr != null)
                {
                    pivotTr.localScale *= fixScale;
                    pivotTr.position *= fixScale;
                }
                sb = standMr.bounds;
            }

            // 1. Orient stand frame upright along +Y (width along X, splayed footprint along Z)
            if (sb.size.y < Mathf.Max(sb.size.x, sb.size.z) * 0.95f)
            {
                Vector3 rotAxis = sb.size.z >= sb.size.x ? Vector3.right : Vector3.forward;
                standTr.RotateAround(Vector3.zero, rotAxis, -90f);
            }

            float minY = standMr.bounds.min.y;
            if (Mathf.Abs(minY) > 0.001f)
            {
                standTr.position += new Vector3(0f, -minY, 0f);
            }

            Bounds uprightStand = standMr.bounds;
            float crossbarY = uprightStand.min.y + uprightStand.size.y * 0.957f; // ~1.77m on 1.85m stand

            if (pivotTr == null)
                return;

            // 2. Anchor Gong_Pivot right on the crossbar centerline
            pivotTr.position = new Vector3(uprightStand.center.x, crossbarY, uprightStand.center.z);
            pivotTr.rotation = Quaternion.identity;

            // 3. Orient and hang Gong_Chains directly from crossbar hanger collars
            float chainsBottomY = crossbarY - 0.40f;
            if (chainsTr != null)
            {
                chainsTr.localRotation = Quaternion.identity;
                chainsTr.localPosition = Vector3.zero;
                MeshRenderer chainsMr = chainsTr.GetComponent<MeshRenderer>();
                if (chainsMr != null)
                {
                    if (chainsMr.bounds.size.z > chainsMr.bounds.size.y)
                        chainsTr.localRotation = Quaternion.Euler(-90f, 0f, 0f);

                    // Top chain lugs touch crossbarY + 0.015m; centered in X & Z
                    Bounds cb = chainsMr.bounds;
                    Vector3 shift = new Vector3(
                        pivotTr.position.x - cb.center.x,
                        (crossbarY + 0.015f) - cb.max.y,
                        pivotTr.position.z - cb.center.z);
                    chainsTr.position += shift;
                    chainsBottomY = chainsMr.bounds.min.y;
                }
            }

            // 4. Orient and hang AR500_GongPlate cleanly inside the bottom chain shackles
            if (plateTr != null)
            {
                plateTr.localRotation = Quaternion.identity;
                plateTr.localPosition = new Vector3(0f, -0.62f, 0f);
                MeshRenderer plateMr = plateTr.GetComponent<MeshRenderer>();
                if (plateMr != null)
                {
                    if (plateMr.bounds.size.z > plateMr.bounds.size.y * 0.5f)
                        plateTr.localRotation = Quaternion.Euler(-90f, 0f, 0f);

                    // Check if mounting ears (at |x| ~ 0.165) are on top (+Y) rather than upside down
                    if (AreMountingEarsBelowCenter(plateTr))
                        plateTr.Rotate(Vector3.right, 180f, Space.World);

                    Bounds pb = plateMr.bounds;
                    Vector3 shift = new Vector3(
                        pivotTr.position.x - pb.center.x,
                        (chainsBottomY + 0.065f) - pb.max.y,
                        pivotTr.position.z - pb.center.z);
                    plateTr.position += shift;
                }
            }

            Bounds finalPlate = plateTr != null ? plateTr.GetComponent<MeshRenderer>().bounds : new Bounds();
            Debug.Log($"[SteelGongSetupTool] Kurulum hizalandı -> Stand boyut={uprightStand.size:F2} tabanY={uprightStand.min.y:F3} | PivotY={pivotTr.position.y:F2} | ZincirAltY={chainsBottomY:F2} | Plaka={finalPlate.min.y:F2}..{finalPlate.max.y:F2}");
        }

        private static bool AreMountingEarsBelowCenter(Transform plateTr)
        {
            MeshFilter mf = plateTr.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null)
                return false;

            Vector3[] verts = mf.sharedMesh.vertices;
            float centerY = plateTr.position.y;
            float maxAbove = 0f;
            float maxBelow = 0f;

            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 wp = plateTr.TransformPoint(verts[i]);
                float dx = Mathf.Abs(wp.x - plateTr.position.x);
                if (dx >= 0.13f && dx <= 0.21f)
                {
                    float dy = wp.y - centerY;
                    if (dy > maxAbove) maxAbove = dy;
                    if (-dy > maxBelow) maxBelow = -dy;
                }
            }

            return maxBelow > maxAbove + 0.01f;
        }

        private static bool DetectPaintedFacePlusZ(Transform plateTr, int submeshIdx)
        {
            return TryGetSubmeshWorldNormal(plateTr, submeshIdx, out Vector3 n) && n.z > 0f;
        }

        private static bool TryGetSubmeshWorldNormal(Transform plateTr, int submeshIdx, out Vector3 worldNormal)
        {
            worldNormal = Vector3.zero;
            MeshFilter mf = plateTr.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null || mf.sharedMesh.subMeshCount == 0)
                return false;

            Mesh mesh = mf.sharedMesh;
            int safeSubmesh = Mathf.Clamp(submeshIdx, 0, mesh.subMeshCount - 1);
            int[] tris = mesh.GetTriangles(safeSubmesh);
            Vector3[] verts = mesh.vertices;
            if (tris == null || tris.Length < 3 || verts == null)
                return false;

            Vector3 v0 = plateTr.TransformPoint(verts[tris[0]]);
            Vector3 v1 = plateTr.TransformPoint(verts[tris[1]]);
            Vector3 v2 = plateTr.TransformPoint(verts[tris[2]]);
            worldNormal = Vector3.Cross(v1 - v0, v2 - v0).normalized;
            return worldNormal.sqrMagnitude > 1e-4f;
        }

        private static int FindPaintedSubmeshIndex(Transform plateTr)
        {
            MeshRenderer mr = plateTr != null ? plateTr.GetComponent<MeshRenderer>() : null;
            if (mr != null && mr.sharedMaterials != null)
            {
                for (int i = 0; i < mr.sharedMaterials.Length; i++)
                {
                    if (mr.sharedMaterials[i] != null && mr.sharedMaterials[i].name.Contains("PaintedFace"))
                        return i;
                }
            }
            return 0;
        }

        private static void PlaceGongStation(GameObject prefabAsset, string objName, Vector3 worldPos, float scale, float plateMassKg, Vector3 shooterPos)
        {
            if (prefabAsset == null)
                return;

            GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset);
            inst.name = objName;
            inst.transform.position = worldPos;
            inst.transform.localScale = Vector3.one * scale;

            // Face painted front (-Z local) towards shooter
            Vector3 awayFromShooter = worldPos - new Vector3(shooterPos.x, worldPos.y, shooterPos.z);
            awayFromShooter.y = 0f;
            if (awayFromShooter.sqrMagnitude > 1e-4f)
                inst.transform.rotation = Quaternion.LookRotation(awayFromShooter.normalized, Vector3.up);

            // Double-check painted front face normal points towards shooter; correct 180 deg if opposite
            Transform plateTr = FindChildByName(inst.transform, "AR500_GongPlate");
            if (plateTr != null)
            {
                int paintedIdx = FindPaintedSubmeshIndex(plateTr);
                if (TryGetSubmeshWorldNormal(plateTr, paintedIdx, out Vector3 faceNormal))
                {
                    Vector3 toShooter = (shooterPos - worldPos).normalized;
                    if (Vector3.Dot(faceNormal, toShooter) < 0f)
                        inst.transform.Rotate(Vector3.up, 180f, Space.World);
                }
            }

            SteelGongTarget target = inst.GetComponent<SteelGongTarget>();
            if (target != null)
            {
                SerializedObject so = new SerializedObject(target);
                so.FindProperty("plateMassKg").floatValue = plateMassKg;
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            EditorUtility.SetDirty(inst);
        }

        private static Material BuildGongFaceMaterial()
        {
            string matPath = $"{MatDir}/GongPlate_PaintedFace.mat";
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, matPath);
            }

            Texture2D faceTex = LoadTexture($"{TexDir}/GongFace_Painted_BaseColor.png", isNormal: false, maxSize: 1024);
            if (faceTex != null)
                mat.SetTexture("_BaseMap", faceTex);

            Texture2D steelNorm = LoadTexture($"{TexDir}/metal_plate_02_nor_gl.png", isNormal: true, maxSize: 1024);
            if (steelNorm != null)
            {
                mat.SetTexture("_BumpMap", steelNorm);
                mat.SetFloat("_BumpScale", 0.35f);
                mat.EnableKeyword("_NORMALMAP");
            }

            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_Metallic", 0.14f);
            mat.SetFloat("_Smoothness", 0.45f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Material BuildSilhouetteFaceMaterial()
        {
            string matPath = $"{MatDir}/SilhouettePlate_PaintedFace.mat";
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, matPath);
            }

            Texture2D faceTex = LoadTexture($"{TexDir}/SilhouetteFace_Painted_BaseColor.png", isNormal: false, maxSize: 1024);
            if (faceTex != null)
                mat.SetTexture("_BaseMap", faceTex);

            Texture2D steelNorm = LoadTexture($"{TexDir}/metal_plate_02_nor_gl.png", isNormal: true, maxSize: 1024);
            if (steelNorm != null)
            {
                mat.SetTexture("_BumpMap", steelNorm);
                mat.SetFloat("_BumpScale", 0.35f);
                mat.EnableKeyword("_NORMALMAP");
            }

            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_Metallic", 0.14f);
            mat.SetFloat("_Smoothness", 0.45f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Material BuildMetalMaterial(
            string matName,
            string diffusePath,
            string normalPath,
            string metalPath,
            string roughPath,
            Color tintColor,
            float metallicFallback,
            float smoothnessFallback)
        {
            string matPath = $"{MatDir}/{matName}.mat";
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, matPath);
            }

            Texture2D baseTex = LoadTexture(diffusePath, isNormal: false, maxSize: 1024);
            if (baseTex != null)
                mat.SetTexture("_BaseMap", baseTex);

            Texture2D normTex = LoadTexture(normalPath, isNormal: true, maxSize: 1024);
            if (normTex != null)
            {
                mat.SetTexture("_BumpMap", normTex);
                mat.SetFloat("_BumpScale", 0.75f);
                mat.EnableKeyword("_NORMALMAP");
            }

            Texture2D packedMs = BuildPackedMetallicSmoothness(matName, metalPath, roughPath, metallicFallback);
            if (packedMs != null)
            {
                mat.SetTexture("_MetallicGlossMap", packedMs);
                mat.SetFloat("_Metallic", 1f);
                mat.SetFloat("_Smoothness", 0.85f);
                mat.EnableKeyword("_METALLICSPECGLOSSMAP");
            }
            else
            {
                mat.SetFloat("_Metallic", metallicFallback);
                mat.SetFloat("_Smoothness", smoothnessFallback);
            }

            mat.SetColor("_BaseColor", tintColor);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Texture2D BuildPackedMetallicSmoothness(string matName, string metalPath, string roughPath, float metallicFallback)
        {
            if (string.IsNullOrEmpty(roughPath) || !File.Exists(roughPath))
                return null;

            string outPath = $"{TexDir}/{matName}_URP_MetallicSmoothness.png";
            Texture2D existing = AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
            if (existing != null)
                return existing;

            TextureImporter rImp = AssetImporter.GetAtPath(roughPath) as TextureImporter;
            if (rImp == null)
                return null;

            bool prevRReadable = rImp.isReadable;
            TextureImporterCompression prevRComp = rImp.textureCompression;
            rImp.isReadable = true;
            rImp.sRGBTexture = false;
            rImp.textureCompression = TextureImporterCompression.Uncompressed;
            rImp.SaveAndReimport();

            Texture2D roughTex = AssetDatabase.LoadAssetAtPath<Texture2D>(roughPath);
            Texture2D metalTex = null;
            TextureImporter mImp = !string.IsNullOrEmpty(metalPath) ? AssetImporter.GetAtPath(metalPath) as TextureImporter : null;
            if (mImp != null)
            {
                mImp.isReadable = true;
                mImp.sRGBTexture = false;
                mImp.textureCompression = TextureImporterCompression.Uncompressed;
                mImp.SaveAndReimport();
                metalTex = AssetDatabase.LoadAssetAtPath<Texture2D>(metalPath);
            }

            if (roughTex == null)
                return null;

            int w = 512;
            int h = 512;
            Texture2D dst = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            Color32[] rPixels = roughTex.GetPixels32();
            Color32[] mPixels = metalTex != null ? metalTex.GetPixels32() : null;
            byte fallbackM = (byte)Mathf.RoundToInt(Mathf.Clamp01(metallicFallback) * 255f);

            int rStepX = Mathf.Max(1, roughTex.width / w);
            int rStepY = Mathf.Max(1, roughTex.height / h);
            Color32[] dstPixels = new Color32[w * h];

            for (int y = 0; y < h; y++)
            {
                int ry = Mathf.Min(roughTex.height - 1, y * rStepY);
                int my = metalTex != null ? Mathf.Min(metalTex.height - 1, (y * metalTex.height) / h) : 0;
                for (int x = 0; x < w; x++)
                {
                    int rx = Mathf.Min(roughTex.width - 1, x * rStepX);
                    int mx = metalTex != null ? Mathf.Min(metalTex.width - 1, (x * metalTex.width) / w) : 0;

                    byte rough = rPixels[ry * roughTex.width + rx].r;
                    byte smooth = (byte)(255 - rough);
                    byte metal = mPixels != null ? mPixels[my * metalTex.width + mx].r : fallbackM;
                    dstPixels[y * w + x] = new Color32(metal, metal, metal, smooth);
                }
            }

            dst.SetPixels32(dstPixels);
            dst.Apply();
            File.WriteAllBytes(outPath, dst.EncodeToPNG());
            Object.DestroyImmediate(dst);

            rImp.isReadable = prevRReadable;
            rImp.textureCompression = prevRComp;
            rImp.maxTextureSize = 1024;
            rImp.SaveAndReimport();

            if (mImp != null)
            {
                mImp.isReadable = false;
                mImp.textureCompression = TextureImporterCompression.Compressed;
                mImp.maxTextureSize = 1024;
                mImp.SaveAndReimport();
            }

            AssetDatabase.ImportAsset(outPath, ImportAssetOptions.ForceUpdate);
            TextureImporter outImp = AssetImporter.GetAtPath(outPath) as TextureImporter;
            if (outImp != null)
            {
                outImp.sRGBTexture = false;
                outImp.maxTextureSize = 512;
                outImp.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
        }

        private static Texture2D LoadTexture(string path, bool isNormal, int maxSize)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;

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

        private static AudioClip LoadConfiguredTargetClip(string assetPath)
        {
            AudioImporter importer = AssetImporter.GetAtPath(assetPath) as AudioImporter;
            if (importer != null)
            {
                bool dirty = false;
                if (!importer.forceToMono)
                {
                    importer.forceToMono = true;
                    dirty = true;
                }
                if (importer.loadInBackground)
                {
                    importer.loadInBackground = false;
                    dirty = true;
                }

                AudioImporterSampleSettings s = importer.defaultSampleSettings;
                if (s.loadType != AudioClipLoadType.DecompressOnLoad || s.compressionFormat != AudioCompressionFormat.Vorbis)
                {
                    s.loadType = AudioClipLoadType.DecompressOnLoad;
                    s.compressionFormat = AudioCompressionFormat.Vorbis;
                    s.quality = 0.85f;
                    importer.defaultSampleSettings = s;
                    dirty = true;
                }

                if (dirty)
                    importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
        }

        private static void EnsureRequiredTags(params string[] requiredTags)
        {
            Object[] tagManagerAssets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (tagManagerAssets == null || tagManagerAssets.Length == 0)
                return;

            SerializedObject tagManager = new SerializedObject(tagManagerAssets[0]);
            SerializedProperty tagsProp = tagManager.FindProperty("tags");
            bool changed = false;

            foreach (string tag in requiredTags)
            {
                bool exists = false;
                for (int i = 0; i < tagsProp.arraySize; i++)
                {
                    if (tagsProp.GetArrayElementAtIndex(i).stringValue == tag)
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists)
                {
                    tagsProp.InsertArrayElementAtIndex(tagsProp.arraySize);
                    tagsProp.GetArrayElementAtIndex(tagsProp.arraySize - 1).stringValue = tag;
                    changed = true;
                }
            }

            if (changed)
                tagManager.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void TagGroundIfPresent()
        {
            GameObject ground = GameObject.Find("Ground");
            if (ground != null && ground.tag != "Ground")
            {
                ground.tag = "Ground";
                EditorUtility.SetDirty(ground);
            }
        }

        private static void RemoveObjectsByName(string[] names)
        {
            foreach (string n in names)
            {
                GameObject existing = GameObject.Find(n);
                while (existing != null)
                {
                    Object.DestroyImmediate(existing);
                    existing = GameObject.Find(n);
                }
            }
        }

        private static void AssignRendererMaterial(GameObject go, Material mat)
        {
            if (go == null || mat == null) return;
            MeshRenderer mr = go.GetComponent<MeshRenderer>();
            if (mr != null)
                mr.sharedMaterial = mat;
        }

        private static void SetClipArray(SerializedProperty prop, AudioClip[] clips)
        {
            prop.ClearArray();
            if (clips == null) return;
            foreach (AudioClip c in clips)
            {
                if (c == null) continue;
                prop.InsertArrayElementAtIndex(prop.arraySize);
                prop.GetArrayElementAtIndex(prop.arraySize - 1).objectReferenceValue = c;
            }
        }

        private static Transform FindChildByName(Transform parent, string targetName)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.name == targetName)
                    return child;
                Transform nested = FindChildByName(child, targetName);
                if (nested != null)
                    return nested;
            }
            return null;
        }

        private static void EnsureFolder(string parent, string folderName)
        {
            string full = $"{parent}/{folderName}";
            if (!AssetDatabase.IsValidFolder(full))
                AssetDatabase.CreateFolder(parent, folderName);
        }
    }
}
