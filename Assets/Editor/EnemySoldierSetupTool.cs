using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRAtis.Targets;

namespace VRAtis.EditorTools
{
    [InitializeOnLoad]
    public static class EnemySoldierSetupTool
    {
        public const string FbxPath = "Assets/Models/Enemies/EnemySoldier_KSK.fbx";
        private const string TexDir = "Assets/Models/Enemies/Textures";
        private const string MatDir = "Assets/Materials/Enemies";
        private const string AudioDir = "Assets/Audio/Targets";
        public const string PrefabPath = "Assets/Prefabs/EnemySoldier_KSK.prefab";
        private const string AutoSetupVersionKey = "VRAtis_EnemySoldier_V6_Done";

        static EnemySoldierSetupTool()
        {
            EditorApplication.delayCall += TryAutoSetupOnce;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode)
                EditorApplication.delayCall += TryAutoSetupOnce;
        }

        private static void TryAutoSetupOnce()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || Application.isBatchMode)
                return;

            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null && EditorPrefs.GetBool(AutoSetupVersionKey, false))
                return;

            if (AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath) == null)
                return;

            SetupEnemySoldierPrefab();
        }

        [MenuItem("VRAtis/Hareketli Düşman Asker (KSK) Kur")]
        public static GameObject SetupEnemySoldierPrefab()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("[EnemySoldierSetupTool] Play modu aktifken kurulum çalıştırılamaz; lütfen Play modundan çıkıp deneyin.");
                return null;
            }

            ConfigureFbxImporter();

            GameObject fbxAsset = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
            if (fbxAsset == null)
            {
                Debug.LogError($"[EnemySoldierSetupTool] FBX bulunamadı: {FbxPath}");
                return null;
            }

            EnsureFolder("Assets", "Materials");
            EnsureFolder("Assets/Materials", "Enemies");
            EnsureFolder("Assets", "Prefabs");

            // Force Unity to pick up updated calibrated camo/rifle textures & generated normal maps
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            Material matHelmet = BuildTacticalMaterial(
                "KSK_Helmet",
                $"{TexDir}/KSK_Helmet_BaseColor.png",
                $"{TexDir}/KSK_Helmet_Normal.png",
                metallic: 0.04f,
                smoothness: 0.16f,
                bumpScale: 0.50f,
                matteClothNoSkyGlare: true);
            Material matHead = BuildTacticalMaterial(
                "KSK_Head",
                $"{TexDir}/KSK_Head_BaseColor.png",
                normalPath: null,
                metallic: 0.0f,
                smoothness: 0.12f,
                bumpScale: 0.0f,
                matteClothNoSkyGlare: true);
            Material matBody = BuildTacticalMaterial(
                "KSK_Body",
                $"{TexDir}/KSK_Body_BaseColor.png",
                $"{TexDir}/KSK_Body_Normal.png",
                metallic: 0.0f,
                smoothness: 0.04f,
                bumpScale: 0.65f,
                matteClothNoSkyGlare: true);
            Material matRifle = BuildTacticalMaterial(
                "KSK_Rifle",
                $"{TexDir}/KSK_Rifle_BaseColor.png",
                normalPath: null,
                metallic: 0.30f,
                smoothness: 0.36f,
                bumpScale: 0.0f,
                matteClothNoSkyGlare: false);

            ExtractAnimationClips(out AnimationClip idleClip, out AnimationClip walkClip, out AnimationClip runClip, out AnimationClip deathClip);

            AudioClip bodyHit01 = LoadConfiguredAudioClip($"{AudioDir}/EnemyHit_Body_01.wav");
            AudioClip headHit01 = LoadConfiguredAudioClip($"{AudioDir}/EnemyHit_Helmet_01.wav");
            AudioClip deathFall01 = LoadConfiguredAudioClip($"{AudioDir}/EnemyDeath_Fall_01.wav");
            AudioClip runFootstep01 = LoadConfiguredAudioClip($"{AudioDir}/EnemySoldier_Run_01.wav");

            GameObject tempRoot = new GameObject("EnemySoldier_KSK");
            try
            {
                GameObject modelInst = (GameObject)PrefabUtility.InstantiatePrefab(fbxAsset);
                modelInst.name = "KSK_Model";
                modelInst.transform.SetParent(tempRoot.transform, false);
                modelInst.transform.localPosition = Vector3.zero;
                modelInst.transform.localRotation = Quaternion.identity;
                modelInst.transform.localScale = Vector3.one;
                PrefabUtility.UnpackPrefabInstance(modelInst, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

                // Assign clean URP Lit PBR camouflage, helmet, skin & HK416A5 rifle materials
                SkinnedMeshRenderer[] renderers = modelInst.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    SkinnedMeshRenderer smr = renderers[i];
                    smr.updateWhenOffscreen = true;
                    string n = smr.gameObject.name;
                    string nl = n.ToLowerInvariant();
                    Material targetMat = matBody;
                    bool isWeapon = nl.Contains("rifle") || nl.Contains("gun") || nl.Contains("hk416");
                    if (isWeapon)
                        targetMat = matRifle;
                    else if (n == "Object_7" || nl.Contains("helmet"))
                        targetMat = matHelmet;
                    else if (n == "Object_9" || nl.Contains("head"))
                        targetMat = matHead;

                    // Disable skybox reflection probe glaze on matte uniform/helmet/skin so Flecktarn camo never washes out grey
                    smr.reflectionProbeUsage = isWeapon
                        ? UnityEngine.Rendering.ReflectionProbeUsage.BlendProbes
                        : UnityEngine.Rendering.ReflectionProbeUsage.Off;

                    Material[] mats = new Material[Mathf.Max(1, smr.sharedMaterials.Length)];
                    for (int m = 0; m < mats.Length; m++)
                        mats[m] = targetMat;
                    smr.sharedMaterials = mats;
                }

                // Ensure Animator exists on the model root (where FBX bone hierarchy starts)
                Animator anim = modelInst.GetComponent<Animator>();
                if (anim == null)
                    anim = modelInst.GetComponentInChildren<Animator>(true);
                if (anim == null)
                    anim = modelInst.AddComponent<Animator>();
                anim.applyRootMotion = false;
                anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                // Sample Idle frame 0 BEFORE measuring height/grounding so we measure the real standing pose
                // (not the rest T-pose whose hips sit near local Y = 0 and boots near Y = -0.96m!)
                if (idleClip != null)
                    idleClip.SampleAnimation(modelInst, 0f);

                // Measure exact skinned visual height (BakeMesh) and normalize cleanly to 1.82m standing on Y = 0
                NormalizeSoldierHeightAndGround(modelInst, idleClip, renderers, desiredHeightMeters: 1.82f);

                EnemySoldierTarget soldier = tempRoot.AddComponent<EnemySoldierTarget>();

                // Attach scale-compensated anatomical bone hitboxes for accurate Head / Torso / Limb ballistics
                AttachBoneHitboxes(modelInst.transform, soldier);

                SerializedObject so = new SerializedObject(soldier);
                so.FindProperty("soldierAnimator").objectReferenceValue = anim;
                so.FindProperty("idleClip").objectReferenceValue = idleClip;
                so.FindProperty("walkClip").objectReferenceValue = walkClip;
                so.FindProperty("runClip").objectReferenceValue = runClip;
                so.FindProperty("deathClip").objectReferenceValue = deathClip;
                SetClipArray(so.FindProperty("bodyHitClips"), bodyHit01);
                SetClipArray(so.FindProperty("headHitClips"), headHit01);
                SetClipArray(so.FindProperty("deathFallClips"), deathFall01);
                so.FindProperty("runningFootstepLoopClip").objectReferenceValue = runFootstep01;
                so.ApplyModifiedPropertiesWithoutUndo();

                GameObject savedPrefab = PrefabUtility.SaveAsPrefabAsset(tempRoot, PrefabPath);
                WireToTargetRangeManager(savedPrefab);

                AssetDatabase.SaveAssets();
                EditorSceneManager.MarkAllScenesDirty();
                EditorSceneManager.SaveOpenScenes();
                EditorPrefs.SetBool(AutoSetupVersionKey, true);

                Debug.Log($"[EnemySoldierSetupTool] Hareketli KSK Düşman Asker prefabı kuruldu -> Idle:{idleClip?.name} Walk:{walkClip?.name} Run:{runClip?.name} Death:{deathClip?.name}");
                return savedPrefab;
            }
            finally
            {
                if (tempRoot != null)
                    Object.DestroyImmediate(tempRoot);
            }
        }

        private static void ConfigureFbxImporter()
        {
            ModelImporter importer = AssetImporter.GetAtPath(FbxPath) as ModelImporter;
            if (importer == null)
                return;

            bool dirty = false;
            if (importer.animationType != ModelImporterAnimationType.Generic)
            {
                importer.animationType = ModelImporterAnimationType.Generic;
                dirty = true;
            }
            if (!importer.importAnimation)
            {
                importer.importAnimation = true;
                dirty = true;
            }

            ModelImporterClipAnimation[] clips = importer.clipAnimations;
            if (clips == null || clips.Length == 0)
                clips = importer.defaultClipAnimations;

            if (clips != null && clips.Length > 0)
            {
                bool clipDirty = false;
                for (int i = 0; i < clips.Length; i++)
                {
                    string lower = clips[i].name.ToLowerInvariant();
                    bool shouldLoop = !lower.Contains("death");
                    if (clips[i].loopTime != shouldLoop)
                    {
                        clips[i].loopTime = shouldLoop;
                        clipDirty = true;
                    }
                }
                if (clipDirty || importer.clipAnimations == null || importer.clipAnimations.Length == 0)
                {
                    importer.clipAnimations = clips;
                    dirty = true;
                }
            }

            if (dirty)
                importer.SaveAndReimport();
        }

        private static void ExtractAnimationClips(
            out AnimationClip idleClip,
            out AnimationClip walkClip,
            out AnimationClip runClip,
            out AnimationClip deathClip)
        {
            idleClip = null;
            walkClip = null;
            runClip = null;
            deathClip = null;

            Object[] subAssets = AssetDatabase.LoadAllAssetsAtPath(FbxPath);
            List<AnimationClip> validClips = new List<AnimationClip>();
            for (int i = 0; i < subAssets.Length; i++)
            {
                if (subAssets[i] is AnimationClip clip && !clip.name.StartsWith("__preview__"))
                    validClips.Add(clip);
            }

            for (int i = 0; i < validClips.Count; i++)
            {
                string n = validClips[i].name.ToLowerInvariant();
                if (n.Contains("run")) runClip = validClips[i];
                else if (n.Contains("walk")) walkClip = validClips[i];
                else if (n.Contains("death")) deathClip = validClips[i];
                else if (n.Contains("idle") || n.Contains("mixamo")) idleClip = validClips[i];
            }

            if (idleClip == null && validClips.Count > 0) idleClip = validClips[0];
            if (runClip == null) runClip = walkClip ?? idleClip;
            if (walkClip == null) walkClip = runClip ?? idleClip;
            if (deathClip == null) deathClip = idleClip;
        }

        private static void NormalizeSoldierHeightAndGround(
            GameObject modelInst,
            AnimationClip sampleClip,
            SkinnedMeshRenderer[] renderers,
            float desiredHeightMeters)
        {
            if (modelInst == null)
                return;

            Transform modelTr = modelInst.transform;

            // Measure height & foot grounding directly from animated world bone transforms!
            // (Avoids SkinnedMeshRenderer.BakeMesh unscaled-parent trap that can misreport 19x scale on nested FBX hierarchies.)
            Transform headBone = FindBoneContaining(modelTr, "Head_06", "Head");
            Transform lToeBone = FindBoneContaining(modelTr, "LeftToeBase_058", "LeftToeBase", "LeftFoot_00", "LeftFoot");
            Transform rToeBone = FindBoneContaining(modelTr, "RightToeBase_063", "RightToeBase", "RightFoot_062", "RightFoot");

            if (headBone != null && (lToeBone != null || rToeBone != null))
            {
                float minFootY = float.PositiveInfinity;
                if (lToeBone != null) minFootY = Mathf.Min(minFootY, lToeBone.position.y);
                if (rToeBone != null) minFootY = Mathf.Min(minFootY, rToeBone.position.y);

                // In Mixamo standing pose, Head_06 is at neck (~1.556m) and top of helmet is ~1.17x higher (~1.82m).
                float headToFootSpan = headBone.position.y - minFootY;
                float estimatedFullHeight = headToFootSpan * 1.175f;

                if (estimatedFullHeight > 0.05f && (estimatedFullHeight < 1.35f || estimatedFullHeight > 2.25f))
                {
                    float scaleFix = desiredHeightMeters / estimatedFullHeight;
                    modelTr.localScale *= scaleFix;
                    if (sampleClip != null)
                        sampleClip.SampleAnimation(modelInst, 0f);

                    minFootY = float.PositiveInfinity;
                    if (lToeBone != null) minFootY = Mathf.Min(minFootY, lToeBone.position.y);
                    if (rToeBone != null) minFootY = Mathf.Min(minFootY, rToeBone.position.y);
                    headToFootSpan = headBone.position.y - minFootY;
                    estimatedFullHeight = headToFootSpan * 1.175f;
                }

                // Boot sole sits ~8mm below ToeBase bone. Only shift Y if meaningfully off ground.
                float soleY = minFootY - 0.008f;
                if (Mathf.Abs(soleY) > 0.04f && Mathf.Abs(soleY) < 2.0f)
                {
                    modelTr.localPosition += new Vector3(0f, -soleY, 0f);
                }

                Debug.Log($"[EnemySoldierSetupTool] Bone Idle ölçümü -> HeadY={headBone.position.y:F3}m | FootY={minFootY:F3}m | TahminiBoy={estimatedFullHeight:F3}m | ModelScale={modelTr.localScale.x:F3}");
                return;
            }

            // Fallback only if skeleton bone names were completely unknown
            if (renderers == null || renderers.Length == 0)
                return;

            Bounds total = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                total.Encapsulate(renderers[i].bounds);
            Debug.Log($"[EnemySoldierSetupTool] Fallback bounds -> SizeY={total.size.y:F3}m | MinY={total.min.y:F3}m");
        }

        private static void AttachBoneHitboxes(Transform modelRoot, EnemySoldierTarget soldier)
        {
            Transform headBone   = FindBoneContaining(modelRoot, "Head_06", "Head");
            Transform chestBone  = FindBoneContaining(modelRoot, "Spine2_04", "Spine1_03", "Spine2");
            Transform hipsBone   = FindBoneContaining(modelRoot, "Hips_01", "Hips");
            Transform lUpLegBone = FindBoneContaining(modelRoot, "LeftUpLeg_056", "LeftUpLeg");
            Transform rUpLegBone = FindBoneContaining(modelRoot, "RightUpLeg_060", "RightUpLeg");
            Transform lLegBone   = FindBoneContaining(modelRoot, "LeftLeg_057", "LeftLeg");
            Transform rLegBone   = FindBoneContaining(modelRoot, "RightLeg_061", "RightLeg");
            Transform lArmBone   = FindBoneContaining(modelRoot, "LeftArm_09", "LeftArm");
            Transform rArmBone   = FindBoneContaining(modelRoot, "RightArm_033", "RightArm");

            bool anyAttached = false;
            if (headBone != null)
            {
                // Mixamo Head_06 local +Y points straight up from neck toward top of helmet
                AddSphereHitbox(headBone, "Hitbox_Head", soldier, SoldierHitZone.Head, worldRadius: 0.145f, localAlongBoneY: 0.11f);
                anyAttached = true;
            }
            if (chestBone != null)
            {
                AddCapsuleHitbox(chestBone, "Hitbox_Torso", soldier, SoldierHitZone.Torso, worldRadius: 0.22f, worldHeight: 0.54f, localAlongBoneY: 0.05f);
                anyAttached = true;
            }
            if (hipsBone != null)
            {
                AddCapsuleHitbox(hipsBone, "Hitbox_Pelvis", soldier, SoldierHitZone.Torso, worldRadius: 0.19f, worldHeight: 0.36f, localAlongBoneY: 0.04f);
                anyAttached = true;
            }
            if (lUpLegBone != null)
                // Mixamo UpLeg & Leg local +Y points straight down along the limb (hip->knee, knee->ankle)
                AddCapsuleHitbox(lUpLegBone, "Hitbox_LeftThigh", soldier, SoldierHitZone.Limb, worldRadius: 0.11f, worldHeight: 0.44f, localAlongBoneY: 0.21f);
            if (rUpLegBone != null)
                AddCapsuleHitbox(rUpLegBone, "Hitbox_RightThigh", soldier, SoldierHitZone.Limb, worldRadius: 0.11f, worldHeight: 0.44f, localAlongBoneY: 0.21f);
            if (lLegBone != null)
                AddCapsuleHitbox(lLegBone, "Hitbox_LeftShin", soldier, SoldierHitZone.Limb, worldRadius: 0.095f, worldHeight: 0.44f, localAlongBoneY: 0.19f);
            if (rLegBone != null)
                AddCapsuleHitbox(rLegBone, "Hitbox_RightShin", soldier, SoldierHitZone.Limb, worldRadius: 0.095f, worldHeight: 0.44f, localAlongBoneY: 0.19f);
            if (lArmBone != null)
                AddCapsuleHitbox(lArmBone, "Hitbox_LeftArm", soldier, SoldierHitZone.Limb, worldRadius: 0.085f, worldHeight: 0.40f, localAlongBoneY: 0.18f);
            if (rArmBone != null)
                AddCapsuleHitbox(rArmBone, "Hitbox_RightArm", soldier, SoldierHitZone.Limb, worldRadius: 0.085f, worldHeight: 0.40f, localAlongBoneY: 0.18f);

            // Fallback root torso/head colliders if bone naming ever differed
            if (!anyAttached)
            {
                GameObject fallbackTorso = new GameObject("Hitbox_TorsoFallback");
                fallbackTorso.transform.SetParent(soldier.transform, false);
                CapsuleCollider cc = fallbackTorso.AddComponent<CapsuleCollider>();
                cc.center = new Vector3(0f, 0.90f, 0f);
                cc.radius = 0.24f;
                cc.height = 1.68f;
                fallbackTorso.AddComponent<EnemySoldierHitbox>().Configure(soldier, SoldierHitZone.Torso);
            }
        }

        private static void AddSphereHitbox(Transform bone, string goName, EnemySoldierTarget soldier, SoldierHitZone zone, float worldRadius, float localAlongBoneY)
        {
            GameObject go = new GameObject(goName);
            go.transform.SetParent(bone, false);
            float invScale = ComputeInverseAvgScale(bone);
            SphereCollider sc = go.AddComponent<SphereCollider>();
            sc.radius = worldRadius * invScale;
            sc.center = new Vector3(0f, localAlongBoneY * invScale, 0f);
            go.AddComponent<EnemySoldierHitbox>().Configure(soldier, zone);
        }

        private static void AddCapsuleHitbox(Transform bone, string goName, EnemySoldierTarget soldier, SoldierHitZone zone, float worldRadius, float worldHeight, float localAlongBoneY)
        {
            GameObject go = new GameObject(goName);
            go.transform.SetParent(bone, false);
            float invScale = ComputeInverseAvgScale(bone);
            CapsuleCollider cc = go.AddComponent<CapsuleCollider>();
            cc.direction = 1; // Y axis along Mixamo bone length
            cc.radius = worldRadius * invScale;
            cc.height = worldHeight * invScale;
            cc.center = new Vector3(0f, localAlongBoneY * invScale, 0f);
            go.AddComponent<EnemySoldierHitbox>().Configure(soldier, zone);
        }

        private static float ComputeInverseAvgScale(Transform tr)
        {
            Vector3 ls = tr.lossyScale;
            float avg = (Mathf.Abs(ls.x) + Mathf.Abs(ls.y) + Mathf.Abs(ls.z)) / 3f;
            return 1f / Mathf.Max(1e-5f, avg);
        }

        private static Transform FindBoneContaining(Transform root, params string[] tokens)
        {
            for (int t = 0; t < tokens.Length; t++)
            {
                Transform exact = FindRecursive(root, tokens[t], exactMatch: true);
                if (exact != null) return exact;
                Transform partial = FindRecursive(root, tokens[t], exactMatch: false);
                if (partial != null) return partial;
            }
            return null;
        }

        private static Transform FindRecursive(Transform cur, string token, bool exactMatch)
        {
            if (exactMatch ? cur.name == token : cur.name.Contains(token))
                return cur;
            for (int i = 0; i < cur.childCount; i++)
            {
                Transform hit = FindRecursive(cur.GetChild(i), token, exactMatch);
                if (hit != null) return hit;
            }
            return null;
        }

        public static void WireToTargetRangeManager(GameObject soldierPrefab)
        {
            if (soldierPrefab == null)
                soldierPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (soldierPrefab == null)
                return;

            TargetRangeManager mgr = Object.FindAnyObjectByType<TargetRangeManager>();
            if (mgr == null)
                return;

            SerializedObject so = new SerializedObject(mgr);
            SerializedProperty prop = so.FindProperty("enemySoldierPrefab");
            if (prop != null)
            {
                prop.objectReferenceValue = soldierPrefab;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(mgr.gameObject);
            }
        }

        private static Material BuildTacticalMaterial(
            string matName,
            string texPath,
            string normalPath,
            float metallic,
            float smoothness,
            float bumpScale,
            bool matteClothNoSkyGlare)
        {
            string matPath = $"{MatDir}/{matName}.mat";
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, matPath);
            }

            if (!string.IsNullOrEmpty(texPath) && File.Exists(texPath))
            {
                TextureImporter ti = AssetImporter.GetAtPath(texPath) as TextureImporter;
                if (ti != null)
                {
                    bool dirty = false;
                    if (ti.textureType != TextureImporterType.Default) { ti.textureType = TextureImporterType.Default; dirty = true; }
                    if (!ti.sRGBTexture) { ti.sRGBTexture = true; dirty = true; }
                    if (ti.maxTextureSize > 1024) { ti.maxTextureSize = 1024; dirty = true; }
                    if (dirty) ti.SaveAndReimport();
                }
                Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
                if (tex != null)
                {
                    mat.SetTexture("_BaseMap", tex);
                    mat.SetTexture("_MainTex", tex);
                }
            }

            if (!string.IsNullOrEmpty(normalPath) && File.Exists(normalPath))
            {
                TextureImporter nti = AssetImporter.GetAtPath(normalPath) as TextureImporter;
                if (nti != null)
                {
                    bool dirty = false;
                    if (nti.textureType != TextureImporterType.NormalMap) { nti.textureType = TextureImporterType.NormalMap; dirty = true; }
                    if (nti.maxTextureSize > 1024) { nti.maxTextureSize = 1024; dirty = true; }
                    if (dirty) nti.SaveAndReimport();
                }
                Texture2D nrm = AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath);
                if (nrm != null)
                {
                    mat.SetTexture("_BumpMap", nrm);
                    mat.SetFloat("_BumpScale", bumpScale);
                    mat.EnableKeyword("_NORMALMAP");
                }
            }
            else
            {
                mat.SetTexture("_BumpMap", null);
                mat.DisableKeyword("_NORMALMAP");
            }

            mat.SetColor("_BaseColor", Color.white);
            mat.SetColor("_Color", Color.white);
            mat.SetFloat("_Metallic", metallic);
            mat.SetFloat("_Smoothness", smoothness);

            if (matteClothNoSkyGlare)
            {
                mat.SetFloat("_EnvironmentReflections", 0f);
                mat.SetFloat("_SpecularHighlights", 0f);
                mat.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
                mat.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
            }
            else
            {
                mat.SetFloat("_EnvironmentReflections", 1f);
                mat.SetFloat("_SpecularHighlights", 1f);
                mat.DisableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
                mat.DisableKeyword("_SPECULARHIGHLIGHTS_OFF");
            }

            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static AudioClip LoadConfiguredAudioClip(string assetPath)
        {
            AudioImporter importer = AssetImporter.GetAtPath(assetPath) as AudioImporter;
            if (importer != null)
            {
                bool dirty = false;
                if (!importer.forceToMono) { importer.forceToMono = true; dirty = true; }
                if (importer.loadInBackground) { importer.loadInBackground = false; dirty = true; }
                AudioImporterSampleSettings s = importer.defaultSampleSettings;
                if (s.loadType != AudioClipLoadType.DecompressOnLoad || s.compressionFormat != AudioCompressionFormat.Vorbis)
                {
                    s.loadType = AudioClipLoadType.DecompressOnLoad;
                    s.compressionFormat = AudioCompressionFormat.Vorbis;
                    s.quality = 0.85f;
                    importer.defaultSampleSettings = s;
                    dirty = true;
                }
                if (dirty) importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
        }

        private static void SetClipArray(SerializedProperty prop, params AudioClip[] clips)
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

        private static void EnsureFolder(string parent, string folderName)
        {
            string full = $"{parent}/{folderName}";
            if (!AssetDatabase.IsValidFolder(full))
                AssetDatabase.CreateFolder(parent, folderName);
        }
    }
}
