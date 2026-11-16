using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using VRAtis.Weapons;

namespace VRAtis.EditorTools
{
    [InitializeOnLoad]
    public static class AR15SetupTool
    {
        private const string FbxAssetPath = "Assets/Models/AR15/AR15_WithRedDot.fbx";
        private const string CasingFbxPath = "Assets/Models/AR15/BrassCasing556.fbx";
        private const string MuzzleFlashFbxPath = "Assets/Models/AR15/MuzzleFlash3D.fbx";
        private const string TexFolderPath = "Assets/Models/AR15/Textures";
        private const string MatFolderPath = "Assets/Materials/AR15";
        private const string PrefabFolderPath = "Assets/Prefabs";
        private const string CasingPrefabPath = "Assets/Prefabs/BrassCasing556.prefab";
        private const string SetupInstanceName = "AR15_WithRedDot";
        private const string AutoSetupVersionKey = "VRAtis_AR15_BakedV7_Done";

        static AR15SetupTool()
        {
            EditorApplication.delayCall += TryAutoSetupOnce;
        }

        private static void TryAutoSetupOnce()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || Application.isBatchMode)
                return;

            GameObject recoilPivot = GameObject.Find("PlayerRifle/RecoilPivot");
            if (recoilPivot == null)
                return;

            bool alreadyHasInstance = recoilPivot.transform.Find(SetupInstanceName) != null;
            if (alreadyHasInstance && EditorPrefs.GetBool(AutoSetupVersionKey, false))
                return;

            if (AssetDatabase.LoadAssetAtPath<GameObject>(FbxAssetPath) == null)
                return;

            SetupAR15AndRedDot();
        }

        [MenuItem("VRAtis/AR15 ve Red Dot Kur")]
        public static void SetupAR15AndRedDot()
        {
            GameObject fbxPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(FbxAssetPath);
            if (fbxPrefab == null)
            {
                Debug.LogError($"[AR15SetupTool] FBX bulunamadı: {FbxAssetPath}");
                return;
            }

            GameObject playerRifle = GameObject.Find("PlayerRifle");
            if (playerRifle == null)
            {
                Debug.LogError("[AR15SetupTool] Sahnede 'PlayerRifle' bulunamadı.");
                return;
            }

            Transform recoilPivot = playerRifle.transform.Find("RecoilPivot");
            if (recoilPivot == null)
            {
                GameObject pivotGo = new GameObject("RecoilPivot");
                pivotGo.transform.SetParent(playerRifle.transform, false);
                recoilPivot = pivotGo.transform;
            }

            // Remove previous instance if re-running from menu
            Transform existing = recoilPivot.Find(SetupInstanceName);
            if (existing != null)
                Object.DestroyImmediate(existing.gameObject);

            // Ensure materials folder exists
            if (!AssetDatabase.IsValidFolder("Assets/Materials"))
                AssetDatabase.CreateFolder("Assets", "Materials");
            if (!AssetDatabase.IsValidFolder(MatFolderPath))
                AssetDatabase.CreateFolder("Assets/Materials", "AR15");

            // Prepare URP Lit PBR materials + Red Dot materials
            Material matBolt = BuildUrpPbrMaterial("AR15_BoltAndSmallParts", "material");
            Material matBarrel = BuildUrpPbrMaterial("AR15_BarrelAndStock", "material_1");
            Material matReceiver = BuildUrpPbrMaterial("AR15_Receiver", "material_2");
            Material matRedDotBody = BuildRedDotBodyMaterial();
            Material matGlass = BuildRedDotGlassMaterial();
            Material matCollimator = BuildRedDotCollimatorMaterial();

            // Instantiate FBX under RecoilPivot
            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(fbxPrefab, recoilPivot);
            instance.name = SetupInstanceName;
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;

            // Snap cardinal orientation only if FBX imported rotated by 90/180 deg, without tilting barrel pitch
            Transform muzzleMarker = FindDeepChild(instance.transform, "MuzzlePoint");
            Transform sightMarker = FindDeepChild(instance.transform, "RedDotAnchor");
            if (muzzleMarker != null && sightMarker != null)
            {
                Vector3 mPos = recoilPivot.InverseTransformPoint(muzzleMarker.position);
                Vector3 sPos = recoilPivot.InverseTransformPoint(sightMarker.position);
                // If rifle points along -Z instead of +Z, rotate 180 around Y; if along X, rotate +-90 around Y
                Vector3 flatFwd = new Vector3(mPos.x - sPos.x, 0f, mPos.z - sPos.z);
                if (flatFwd.sqrMagnitude > 1e-4f)
                {
                    float yawOffset = Vector3.SignedAngle(flatFwd.normalized, Vector3.forward, Vector3.up);
                    if (Mathf.Abs(yawOffset) > 45f)
                    {
                        float snappedYaw = Mathf.Round(yawOffset / 90f) * 90f;
                        instance.transform.localRotation = Quaternion.Euler(0f, snappedYaw, 0f);
                    }
                }

                // Check upright (+Y) after yaw snap
                Vector3 sPosAfter = recoilPivot.InverseTransformPoint(sightMarker.position);
                Vector3 mPosAfter = recoilPivot.InverseTransformPoint(muzzleMarker.position);
                if (sPosAfter.y < mPosAfter.y)
                {
                    instance.transform.localRotation *= Quaternion.Euler(0f, 0f, 180f);
                }
            }

            // Assign clean URP materials across all mesh renderers and strip any colliders on weapon
            MeshRenderer[] renderers = instance.GetComponentsInChildren<MeshRenderer>(true);
            foreach (MeshRenderer mr in renderers)
            {
                Collider col = mr.GetComponent<Collider>();
                if (col != null)
                    Object.DestroyImmediate(col);

                string objName = mr.gameObject.name;
                if (objName == "RedDot_ReticleLens")
                {
                    mr.sharedMaterial = matCollimator;
                    mr.shadowCastingMode = ShadowCastingMode.Off;
                    mr.receiveShadows = false;

                    RedDotSightController ctrl = mr.GetComponent<RedDotSightController>();
                    if (ctrl == null)
                        ctrl = mr.gameObject.AddComponent<RedDotSightController>();
                    ctrl.SetReference(recoilPivot);
                    continue;
                }

                if (objName == "RedDot_Glass")
                {
                    mr.sharedMaterial = matGlass;
                    mr.shadowCastingMode = ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                    continue;
                }

                Material[] mats = mr.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    string mName = mats[i] != null ? mats[i].name : "";
                    if (mName.StartsWith("material_1"))
                        mats[i] = matBarrel;
                    else if (mName.StartsWith("material_2"))
                        mats[i] = matReceiver;
                    else if (mName == "material" || mName.StartsWith("material "))
                        mats[i] = matBolt;
                    else if (mName.Contains("Sight_RedDot"))
                        mats[i] = matRedDotBody;
                }
                mr.sharedMaterials = mats;
            }

            // Align Muzzle transform to exact barrel tip
            Transform muzzleTransform = recoilPivot.Find("Muzzle");
            if (muzzleTransform == null)
            {
                GameObject mGo = new GameObject("Muzzle");
                mGo.transform.SetParent(recoilPivot, false);
                muzzleTransform = mGo.transform;
            }

            if (muzzleMarker != null)
            {
                muzzleTransform.position = muzzleMarker.position;
                muzzleTransform.rotation = Quaternion.LookRotation(recoilPivot.forward, recoilPivot.up);
            }

            // Setup 5.56 brass casing prefab + RifleMechanismController (bolt cycle, first-shot dust cover, casing ejection)
            EjectedCasing casingPrefab = BuildOrLoadCasingPrefab();
            Transform boltCarrier = FindDeepChild(instance.transform, "AR15_BoltCarrier");
            Transform dustCover = FindDeepChild(instance.transform, "AR15_DustCover");
            Transform ejectionPoint = FindDeepChild(instance.transform, "EjectionPoint");
            Transform magazineTransform = FindDeepChild(instance.transform, "reciver1_low.005_3_0") ??
                                          FindDeepChild(instance.transform, "reciver1_low.005");

            if (ejectionPoint != null)
            {
                ejectionPoint.rotation = recoilPivot.rotation;
            }

            // Wire RifleVisualEffects (lighting-aware 2x2 atlas muzzle flash + forward SpotLight + single smoke cloud)
            RifleVisualEffects vfx = recoilPivot.GetComponent<RifleVisualEffects>();
            if (vfx == null)
                vfx = recoilPivot.gameObject.AddComponent<RifleVisualEffects>();

            Material flashPlumeMat = BuildMuzzleFlashMaterial("MuzzleFlash_3D", "MuzzleFlash_Plume.png", 0.85f);
            Material flashCrownMat = BuildMuzzleFlashMaterial("MuzzleFlash_Crown", "MuzzleFlash_Crown.png", 1.0f);
            Material smokeMat = BuildSmokeMaterial();
            Transform flashRoot = BuildOrReplaceMuzzleFlash3D(muzzleTransform, flashPlumeMat, flashCrownMat);
            Light flashSpotLight = BuildOrReplaceMuzzleSpotLight(muzzleTransform);

            // Clean up any legacy runtime-created children left under Muzzle
            string[] legacyVfxChildren = { "MuzzleFlashVisual", "MuzzlePowderSparks", "MuzzleGasJet", "BarrelHotWisp", "MuzzleSmokeCloud" };
            foreach (string childName in legacyVfxChildren)
            {
                Transform stale = muzzleTransform.Find(childName);
                if (stale != null) Object.DestroyImmediate(stale.gameObject);
            }

            SerializedObject vfxSo = new SerializedObject(vfx);
            vfxSo.FindProperty("muzzleTransform").objectReferenceValue = muzzleTransform;
            vfxSo.FindProperty("ejectionPoint").objectReferenceValue = ejectionPoint;
            vfxSo.FindProperty("muzzlePointLight").objectReferenceValue = flashSpotLight;
            vfxSo.FindProperty("flashRootTransform").objectReferenceValue = flashRoot;
            vfxSo.FindProperty("flash3DMaterial").objectReferenceValue = flashPlumeMat;
            vfxSo.FindProperty("flashCrownMaterial").objectReferenceValue = flashCrownMat;
            vfxSo.FindProperty("smokeMaterial").objectReferenceValue = smokeMat;
            vfxSo.FindProperty("flashDurationSec").floatValue = 0.040f;
            vfxSo.FindProperty("daylightFlashIntensity").floatValue = 0.22f;
            vfxSo.FindProperty("duskFlashIntensity").floatValue = 1.05f;
            vfxSo.FindProperty("nightFlashIntensity").floatValue = 2.55f;
            vfxSo.FindProperty("daylightLightIntensity").floatValue = 0.0f;
            vfxSo.FindProperty("duskLightIntensity").floatValue = 2.8f;
            vfxSo.FindProperty("nightLightIntensity").floatValue = 12.0f;
            vfxSo.FindProperty("flashLightRange").floatValue = 7.5f;
            vfxSo.FindProperty("flashLightColor").colorValue = new Color(1f, 0.74f, 0.32f, 1f);
            vfxSo.FindProperty("flashSizeScale").floatValue = 1.0f;
            vfxSo.FindProperty("enableGunpowderSmoke").boolValue = true;
            vfxSo.FindProperty("cloudPuffsPerShot").intValue = 10;
            vfxSo.FindProperty("cloudColor").colorValue = new Color(0.76f, 0.79f, 0.83f, 0.115f);
            vfxSo.ApplyModifiedPropertiesWithoutUndo();

            RifleMechanismController mech = recoilPivot.GetComponent<RifleMechanismController>();
            if (mech == null)
                mech = recoilPivot.gameObject.AddComponent<RifleMechanismController>();

            SerializedObject mechSo = new SerializedObject(mech);
            mechSo.FindProperty("boltCarrierTransform").objectReferenceValue = boltCarrier;
            mechSo.FindProperty("dustCoverTransform").objectReferenceValue = dustCover;
            mechSo.FindProperty("ejectionPoint").objectReferenceValue = ejectionPoint;
            mechSo.FindProperty("magazineTransform").objectReferenceValue = magazineTransform;
            mechSo.FindProperty("casingPrefab").objectReferenceValue = casingPrefab;
            if (boltCarrier != null && boltCarrier.parent != null)
            {
                Vector3 boltBackAxis = boltCarrier.parent.InverseTransformDirection(-recoilPivot.forward).normalized;
                mechSo.FindProperty("boltSlideLocalAxis").vector3Value = boltBackAxis;
            }
            if (dustCover != null && dustCover.parent != null)
            {
                Vector3 dcAxis = dustCover.parent.InverseTransformDirection(-recoilPivot.forward).normalized;
                mechSo.FindProperty("dustCoverHingeAxis").vector3Value = dcAxis;
            }
            if (magazineTransform != null && magazineTransform.parent != null)
            {
                Vector3 magDownAxis = magazineTransform.parent.InverseTransformDirection(-recoilPivot.up).normalized;
                mechSo.FindProperty("magSlideLocalAxis").vector3Value = magDownAxis;
            }
            mechSo.ApplyModifiedPropertiesWithoutUndo();

            RifleWeapon weapon = playerRifle.GetComponent<RifleWeapon>();
            if (weapon != null)
            {
                SerializedObject weaponSo = new SerializedObject(weapon);
                weaponSo.FindProperty("muzzleTransform").objectReferenceValue = muzzleTransform;
                weaponSo.FindProperty("recoilPivot").objectReferenceValue = recoilPivot;
                weaponSo.FindProperty("mechanismController").objectReferenceValue = mech;
                weaponSo.FindProperty("magazineCapacity").intValue = 30;
                weaponSo.ApplyModifiedPropertiesWithoutUndo();

                EnsurePersistentListener(weapon.onWeaponFired, vfx, nameof(RifleVisualEffects.PlayMuzzleFlash));
                EnsurePersistentListener(weapon.onWeaponFired, mech, nameof(RifleMechanismController.OnWeaponFired));
                EditorUtility.SetDirty(weapon);
            }

            // Position PlayerRifle nicely relative to Main Camera (chest/shoulder right-side view)
            Camera mainCam = Camera.main;
            if (mainCam != null)
            {
                playerRifle.transform.position = mainCam.transform.position + new Vector3(0.08f, -0.14f, 0.25f);
            }

            EditorSceneManager.MarkSceneDirty(playerRifle.scene);
            EditorSceneManager.SaveOpenScenes();
            AssetDatabase.SaveAssets();
            EditorPrefs.SetBool(AutoSetupVersionKey, true);
            Debug.Log("[AR15SetupTool] Modern Taktik AR-15 + Kapalı Tüp Red Dot (URP PBR + Paralaks Collimator + Namlu Alevi) başarıyla sahneye kuruldu!");
        }

        private static Material BuildUrpPbrMaterial(string matName, string texPrefix)
        {
            string matPath = $"{MatFolderPath}/{matName}.mat";
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");

            if (mat == null)
            {
                mat = new Material(urpLit);
                AssetDatabase.CreateAsset(mat, matPath);
            }
            else if (urpLit != null && mat.shader != urpLit)
            {
                mat.shader = urpLit;
            }

            Texture2D baseColor = LoadConfiguredTexture($"{TexFolderPath}/{texPrefix}_baseColor.png", false, 2048);
            Texture2D normalMap = LoadConfiguredTexture($"{TexFolderPath}/{texPrefix}_normal.png", true, 2048);
            Texture2D metalSmooth = BuildOrLoadMetallicSmoothness(texPrefix);
            Texture2D emissiveMap = texPrefix == "material" ? LoadConfiguredTexture($"{TexFolderPath}/{texPrefix}_emissive.png", false, 1024) : null;

            if (baseColor != null)
                mat.SetTexture("_BaseMap", baseColor);

            mat.SetColor("_BaseColor", Color.white);

            if (normalMap != null)
            {
                mat.SetTexture("_BumpMap", normalMap);
                mat.SetFloat("_BumpScale", 1.0f);
                mat.EnableKeyword("_NORMALMAP");
            }

            if (metalSmooth != null)
            {
                mat.SetTexture("_MetallicGlossMap", metalSmooth);
                mat.SetFloat("_Metallic", 1.0f);
                mat.SetFloat("_Smoothness", 1.0f);
                mat.EnableKeyword("_METALLICSPECGLOSSMAP");
            }

            if (emissiveMap != null)
            {
                mat.SetTexture("_EmissionMap", emissiveMap);
                mat.SetColor("_EmissionColor", Color.white * 1.5f);
                mat.EnableKeyword("_EMISSION");
            }

            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Texture2D LoadConfiguredTexture(string assetPath, bool isNormal, int maxSize)
        {
            TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
                return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);

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

            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        /// <summary>
        /// Converts glTF metallicRoughness (G=Roughness, B=Metallic) into URP MetallicGlossMap (R=Metallic, A=1-Roughness).
        /// </summary>
        private static Texture2D BuildOrLoadMetallicSmoothness(string texPrefix)
        {
            string outAssetPath = $"{TexFolderPath}/{texPrefix}_URP_MetallicSmoothness.png";
            Texture2D existing = AssetDatabase.LoadAssetAtPath<Texture2D>(outAssetPath);
            if (existing != null)
                return existing;

            string srcAssetPath = $"{TexFolderPath}/{texPrefix}_metallicRoughness.png";
            TextureImporter importer = AssetImporter.GetAtPath(srcAssetPath) as TextureImporter;
            if (importer == null)
                return null;

            bool prevReadable = importer.isReadable;
            TextureImporterCompression prevComp = importer.textureCompression;
            if (!prevReadable || prevComp != TextureImporterCompression.Uncompressed)
            {
                importer.isReadable = true;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
            }

            Texture2D src = AssetDatabase.LoadAssetAtPath<Texture2D>(srcAssetPath);
            if (src == null)
                return null;

            int w = Mathf.Min(src.width, 2048);
            int h = Mathf.Min(src.height, 2048);
            Texture2D dst = new Texture2D(w, h, TextureFormat.RGBA32, false, true);

            Color32[] srcPixels = src.GetPixels32();
            int stepX = Mathf.Max(1, src.width / w);
            int stepY = Mathf.Max(1, src.height / h);
            Color32[] dstPixels = new Color32[w * h];

            for (int y = 0; y < h; y++)
            {
                int sy = Mathf.Min(src.height - 1, y * stepY);
                for (int x = 0; x < w; x++)
                {
                    int sx = Mathf.Min(src.width - 1, x * stepX);
                    Color32 p = srcPixels[sy * src.width + sx];
                    byte metallic = p.b;
                    byte smoothness = (byte)(255 - p.g);
                    dstPixels[y * w + x] = new Color32(metallic, metallic, metallic, smoothness);
                }
            }

            dst.SetPixels32(dstPixels);
            dst.Apply();
            File.WriteAllBytes(outAssetPath, dst.EncodeToPNG());
            Object.DestroyImmediate(dst);

            // Restore source importer to save memory
            importer.isReadable = false;
            importer.textureCompression = TextureImporterCompression.Compressed;
            importer.maxTextureSize = 2048;
            importer.SaveAndReimport();

            AssetDatabase.ImportAsset(outAssetPath, ImportAssetOptions.ForceUpdate);
            TextureImporter outImporter = AssetImporter.GetAtPath(outAssetPath) as TextureImporter;
            if (outImporter != null)
            {
                outImporter.sRGBTexture = false;
                outImporter.maxTextureSize = 2048;
                outImporter.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(outAssetPath);
        }

        private static Material BuildRedDotBodyMaterial()
        {
            string matPath = $"{MatFolderPath}/RedDot_Housing.mat";
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, matPath);
            }
            mat.SetColor("_BaseColor", new Color(0.06f, 0.06f, 0.065f, 1f));
            mat.SetFloat("_Metallic", 0.85f);
            mat.SetFloat("_Smoothness", 0.55f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Material BuildRedDotGlassMaterial()
        {
            string matPath = $"{MatFolderPath}/RedDot_Glass.mat";
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, matPath);
            }
            mat.SetFloat("_Surface", 1f); // Transparent
            mat.SetFloat("_Blend", 0f);
            mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)RenderQueue.Transparent;
            mat.SetColor("_BaseColor", new Color(0.08f, 0.22f, 0.28f, 0.12f));
            mat.SetFloat("_Metallic", 0.1f);
            mat.SetFloat("_Smoothness", 0.92f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Material BuildRedDotCollimatorMaterial()
        {
            string matPath = $"{MatFolderPath}/RedDot_Collimator.mat";
            Shader shader = Shader.Find("VRAtis/RedDotCollimator");
            if (shader == null)
                shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Shaders/RedDotCollimator.shader");

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
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static void EnsurePersistentListener(UnityEngine.Events.UnityEvent evt, Object target, string methodName)
        {
            for (int i = 0; i < evt.GetPersistentEventCount(); i++)
            {
                if (evt.GetPersistentTarget(i) == target && evt.GetPersistentMethodName(i) == methodName)
                    return;
            }

            if (target is RifleVisualEffects vfx && methodName == nameof(RifleVisualEffects.PlayMuzzleFlash))
                UnityEventTools.AddPersistentListener(evt, vfx.PlayMuzzleFlash);
            else if (target is RifleMechanismController mech && methodName == nameof(RifleMechanismController.OnWeaponFired))
                UnityEventTools.AddPersistentListener(evt, mech.OnWeaponFired);
        }

        private static Light BuildOrReplaceMuzzleSpotLight(Transform muzzleTransform)
        {
            if (muzzleTransform == null)
                return null;

            Transform old = muzzleTransform.Find("MuzzleFlashLight");
            if (old != null)
                Object.DestroyImmediate(old.gameObject);

            GameObject lightGo = new GameObject("MuzzleFlashLight");
            lightGo.transform.SetParent(muzzleTransform, false);
            lightGo.transform.localPosition = new Vector3(0f, 0f, 0.16f);
            lightGo.transform.localRotation = Quaternion.identity;

            Light spot = lightGo.AddComponent<Light>();
            spot.type = LightType.Spot;
            spot.spotAngle = 115f;
            spot.innerSpotAngle = 55f;
            spot.range = 7.5f;
            spot.color = new Color(1f, 0.74f, 0.32f, 1f);
            spot.intensity = 0f;
            spot.shadows = LightShadows.None;
            spot.enabled = false;
            return spot;
        }

        private static Transform BuildOrReplaceMuzzleFlash3D(Transform muzzleTransform, Material plumeMat, Material crownMat)
        {
            if (muzzleTransform == null)
                return null;

            Transform old = muzzleTransform.Find("MuzzleFlashVisual3D");
            if (old != null)
                Object.DestroyImmediate(old.gameObject);

            GameObject flashPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(MuzzleFlashFbxPath);
            if (flashPrefab == null)
                return null;

            GameObject go = (GameObject)PrefabUtility.InstantiatePrefab(flashPrefab, muzzleTransform);
            go.name = "MuzzleFlashVisual3D";
            go.transform.localPosition = new Vector3(0f, 0f, 0.004f);
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            foreach (MeshRenderer mr in go.GetComponentsInChildren<MeshRenderer>(true))
            {
                bool isCrown = mr.gameObject.name.Contains("Crown");
                mr.sharedMaterial = isCrown ? crownMat : plumeMat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                Collider c = mr.GetComponent<Collider>();
                if (c != null) Object.DestroyImmediate(c);
            }

            go.SetActive(false);
            return go.transform;
        }

        private static Material BuildMuzzleFlashMaterial(string matName, string texFileName, float viewFade)
        {
            string matPath = $"{MatFolderPath}/{matName}.mat";
            Shader shader = Shader.Find("VRAtis/MuzzleFlashURP");
            if (shader == null)
                shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Shaders/MuzzleFlashURP.shader");

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

            Texture2D tex = LoadConfiguredVfxTexture($"{TexFolderPath}/{texFileName}", 512);
            if (tex != null)
                mat.SetTexture("_MainTex", tex);
            mat.SetColor("_TintColor", new Color(1.45f, 1.35f, 1.15f, 1.0f));
            mat.SetFloat("_ViewFade", viewFade);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Texture2D LoadConfiguredVfxTexture(string assetPath, int maxSize)
        {
            TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer != null)
            {
                bool dirty = false;
                if (importer.wrapMode != TextureWrapMode.Clamp)
                {
                    importer.wrapMode = TextureWrapMode.Clamp;
                    dirty = true;
                }
                if (importer.alphaIsTransparency == false)
                {
                    importer.alphaIsTransparency = true;
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
            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        private static Material BuildSmokeMaterial()
        {
            string matPath = $"{MatFolderPath}/GunpowderSmoke.mat";
            Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null)
                shader = Shader.Find("Particles/Standard Unlit");

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

            Texture2D cloudTex = LoadConfiguredVfxTexture($"{TexFolderPath}/GunpowderCloud.png", 256);
            if (cloudTex != null)
                mat.SetTexture("_BaseMap", cloudTex);

            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)RenderQueue.Transparent;
            mat.SetColor("_BaseColor", Color.white);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static EjectedCasing BuildOrLoadCasingPrefab()
        {
            GameObject casingFbx = AssetDatabase.LoadAssetAtPath<GameObject>(CasingFbxPath);
            if (casingFbx == null)
                return null;

            if (!AssetDatabase.IsValidFolder(PrefabFolderPath))
                AssetDatabase.CreateFolder("Assets", "Prefabs");

            string matPath = $"{MatFolderPath}/BrassCasing.mat";
            Material brassMat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (brassMat == null)
            {
                brassMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(brassMat, matPath);
            }

            Texture2D brassTex = LoadConfiguredTexture($"{TexFolderPath}/Casing_baseColor.png", false, 1024);
            if (brassTex != null)
                brassMat.SetTexture("_BaseMap", brassTex);
            brassMat.SetColor("_BaseColor", new Color(0.95f, 0.80f, 0.42f, 1f));
            brassMat.SetFloat("_Metallic", 0.92f);
            brassMat.SetFloat("_Smoothness", 0.72f);
            EditorUtility.SetDirty(brassMat);

            GameObject temp = (GameObject)PrefabUtility.InstantiatePrefab(casingFbx);
            temp.name = "BrassCasing556";
            foreach (MeshRenderer mr in temp.GetComponentsInChildren<MeshRenderer>(true))
            {
                mr.sharedMaterial = brassMat;
                Collider col = mr.GetComponent<Collider>();
                if (col != null) Object.DestroyImmediate(col);
            }

            EjectedCasing casingComp = temp.GetComponent<EjectedCasing>();
            if (casingComp == null)
                casingComp = temp.AddComponent<EjectedCasing>();

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(temp, CasingPrefabPath);
            Object.DestroyImmediate(temp);
            return prefab != null ? prefab.GetComponent<EjectedCasing>() : null;
        }

        private static Transform FindDeepChild(Transform parent, string targetName)
        {
            Queue<Transform> queue = new Queue<Transform>();
            queue.Enqueue(parent);
            while (queue.Count > 0)
            {
                Transform cur = queue.Dequeue();
                if (cur.name == targetName)
                    return cur;
                for (int i = 0; i < cur.childCount; i++)
                    queue.Enqueue(cur.GetChild(i));
            }
            return null;
        }
    }
}
