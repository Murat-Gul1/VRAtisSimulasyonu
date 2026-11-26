using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRAtis.Soldiers;
using VRAtis.WorldMap;

namespace VRAtis.EditorTools
{
    /// <summary>
    /// Configures the unified 5-Nation Modular Soldier FBX (Humanoid rig, 26 distinct regional face
    /// BlendShapes, 3 body builds, 8 hair/beard parts, Idle_AtEase/Walk clips) and generates 5
    /// per-nation base prefabs (USA, Turkey, Soviet, China, Rebels) ready for recruitment & dynamic gear!
    /// </summary>
    public static class UsSoldierImportSetupTool
    {
        public const string AllNationsFbxPath = "Assets/Models/Soldiers/Soldier_AllNations_ModularBase.fbx";
        public const string SoldierAnimCtrlPath = "Assets/Animations/ModularSoldier.controller";
        private const string SoldierMatDir = "Assets/Materials/Soldiers";

        [MenuItem("VRAtis/Asker Sistemi/1. Tüm 5 Ülke Modüler Asker Prefablarını Kur")]
        public static void SetupAllFiveNationSoldiers()
        {
            AssetDatabase.ImportAsset(AllNationsFbxPath, ImportAssetOptions.ForceUpdate);

            ModelImporter importer = AssetImporter.GetAtPath(AllNationsFbxPath) as ModelImporter;
            if (importer == null)
            {
                Debug.LogError($"[UsSoldierImportSetupTool] FBX bulunamadı: {AllNationsFbxPath}");
                return;
            }

            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.importBlendShapes = true;
            importer.importBlendShapeNormals = ModelImporterNormals.Calculate;
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            importer.SaveAndReimport();

            // Configure looping on Idle_AtEase and Walk clips
            ModelImporterClipAnimation[] clips = importer.defaultClipAnimations;
            if (clips != null && clips.Length > 0)
            {
                for (int i = 0; i < clips.Length; i++)
                {
                    if (clips[i].name.Contains("Idle"))
                    {
                        clips[i].name = "Idle_AtEase";
                        clips[i].loopTime = true;
                    }
                    else if (clips[i].name.Contains("Walk"))
                    {
                        clips[i].name = "Walk";
                        clips[i].loopTime = true;
                    }
                }
                importer.clipAnimations = clips;
                importer.SaveAndReimport();
            }

            EnsureFolder("Assets", "Materials");
            EnsureFolder("Assets/Materials", "Soldiers");
            EnsureFolder("Assets", "Prefabs");
            EnsureFolder("Assets/Prefabs", "Soldiers");
            EnsureFolder("Assets", "Animations");

            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            Material skinMat = LoadOrCreateUrpMat($"{SoldierMatDir}/Mat_SoldierSkin_URP.mat", urpLit, new Color(0.78f, 0.60f, 0.48f), smoothness: 0.22f);
            Material shortsMat = LoadOrCreateUrpMat($"{SoldierMatDir}/Mat_SoldierPTShorts_URP.mat", urpLit, new Color(0.20f, 0.24f, 0.17f), smoothness: 0.08f);
            Material hairMat = LoadOrCreateUrpMat($"{SoldierMatDir}/Mat_SoldierHair_URP.mat", urpLit, new Color(0.11f, 0.08f, 0.06f), smoothness: 0.14f);
            Material eyeWhiteMat = LoadOrCreateUrpMat($"{SoldierMatDir}/Mat_EyeSclera_URP.mat", urpLit, new Color(0.92f, 0.91f, 0.89f), smoothness: 0.72f);
            Material eyeIrisMat = LoadOrCreateUrpMat($"{SoldierMatDir}/Mat_EyeIris_URP.mat", urpLit, new Color(0.14f, 0.09f, 0.06f), smoothness: 0.86f);

            AnimatorController animCtrl = BuildOrUpdateAnimatorController();

            // Build all 5 per-nation base prefabs (each sharing the 1 lightweight Humanoid FBX & URP materials!)
            CreateFactionPrefab(WorldMapFaction.USA,    "USA",    "Soldier_USA_Base",    101, skinMat, shortsMat, hairMat, eyeWhiteMat, eyeIrisMat, animCtrl);
            CreateFactionPrefab(WorldMapFaction.Turkey, "Turkey", "Soldier_Turkey_Base", 202, skinMat, shortsMat, hairMat, eyeWhiteMat, eyeIrisMat, animCtrl);
            CreateFactionPrefab(WorldMapFaction.Soviet, "Soviet", "Soldier_Soviet_Base", 303, skinMat, shortsMat, hairMat, eyeWhiteMat, eyeIrisMat, animCtrl);
            CreateFactionPrefab(WorldMapFaction.China,  "China",  "Soldier_China_Base",  404, skinMat, shortsMat, hairMat, eyeWhiteMat, eyeIrisMat, animCtrl);
            CreateFactionPrefab(WorldMapFaction.Rebels, "Rebels", "Soldier_Rebels_Base", 505, skinMat, shortsMat, hairMat, eyeWhiteMat, eyeIrisMat, animCtrl);

            AssetDatabase.SaveAssets();
            Debug.Log("[UsSoldierImportSetupTool] Tüm 5 ülke modüler asker prefabları kuruldu (USA, Turkey, Soviet, China, Rebels)!");
        }

        public static string GetFactionPrefabPath(WorldMapFaction faction)
        {
            switch (faction)
            {
                case WorldMapFaction.USA:    return "Assets/Prefabs/Soldiers/USA/Soldier_USA_Base.prefab";
                case WorldMapFaction.Turkey: return "Assets/Prefabs/Soldiers/Turkey/Soldier_Turkey_Base.prefab";
                case WorldMapFaction.Soviet: return "Assets/Prefabs/Soldiers/Soviet/Soldier_Soviet_Base.prefab";
                case WorldMapFaction.China:  return "Assets/Prefabs/Soldiers/China/Soldier_China_Base.prefab";
                default:                     return "Assets/Prefabs/Soldiers/Rebels/Soldier_Rebels_Base.prefab";
            }
        }

        private static GameObject CreateFactionPrefab(
            WorldMapFaction faction,
            string subFolder,
            string prefabName,
            int defaultSeed,
            Material skinMat,
            Material shortsMat,
            Material hairMat,
            Material eyeWhiteMat,
            Material eyeIrisMat,
            AnimatorController animCtrl)
        {
            EnsureFolder("Assets/Prefabs/Soldiers", subFolder);
            GameObject fbxAsset = AssetDatabase.LoadAssetAtPath<GameObject>(AllNationsFbxPath);
            if (fbxAsset == null)
                return null;

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(fbxAsset);
            instance.name = prefabName;

            foreach (SkinnedMeshRenderer smr in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                switch (smr.name)
                {
                    case "Soldier_Body":
                        smr.sharedMaterial = skinMat;
                        break;
                    case "Base_PTShorts":
                        smr.sharedMaterial = shortsMat;
                        break;
                    case "Soldier_Eyes":
                        smr.sharedMaterials = new[] { eyeWhiteMat, eyeIrisMat };
                        break;
                    default:
                        smr.sharedMaterial = hairMat;
                        break;
                }
            }

            Animator anim = instance.GetComponent<Animator>();
            if (anim == null) anim = instance.AddComponent<Animator>();
            anim.runtimeAnimatorController = animCtrl;
            anim.applyRootMotion = false;

            ModularSoldierAssembler assembler = instance.AddComponent<ModularSoldierAssembler>();
            assembler.AutoResolveReferences();
            assembler.ApplyProfile(SoldierAppearanceProfile.GenerateForFaction(faction, defaultSeed, SoldierTier.Tier1_Recruit));

            string path = $"Assets/Prefabs/Soldiers/{subFolder}/{prefabName}.prefab";
            GameObject saved = PrefabUtility.SaveAsPrefabAsset(instance, path);
            Object.DestroyImmediate(instance);
            return saved;
        }

        private static AnimatorController BuildOrUpdateAnimatorController()
        {
            AnimationClip idleClip = null;
            AnimationClip walkClip = null;

            foreach (Object sub in AssetDatabase.LoadAllAssetsAtPath(AllNationsFbxPath))
            {
                if (sub is AnimationClip clip && !clip.name.StartsWith("__preview__"))
                {
                    if (clip.name.Contains("Idle")) idleClip = clip;
                    else if (clip.name.Contains("Walk")) walkClip = clip;
                }
            }

            AnimatorController ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(SoldierAnimCtrlPath);
            if (ctrl != null)
                AssetDatabase.DeleteAsset(SoldierAnimCtrlPath);

            ctrl = AnimatorController.CreateAnimatorControllerAtPath(SoldierAnimCtrlPath);
            ctrl.AddParameter("IsMoving", AnimatorControllerParameterType.Bool);

            AnimatorStateMachine sm = ctrl.layers[0].stateMachine;
            AnimatorState idleState = sm.AddState("Idle_AtEase");
            idleState.motion = idleClip;
            sm.defaultState = idleState;

            if (walkClip != null)
            {
                AnimatorState walkState = sm.AddState("Walk");
                walkState.motion = walkClip;

                var toWalk = idleState.AddTransition(walkState);
                toWalk.hasExitTime = false;
                toWalk.duration = 0.15f;
                toWalk.AddCondition(AnimatorConditionMode.If, 0f, "IsMoving");

                var toIdle = walkState.AddTransition(idleState);
                toIdle.hasExitTime = false;
                toIdle.duration = 0.15f;
                toIdle.AddCondition(AnimatorConditionMode.IfNot, 0f, "IsMoving");
            }

            EditorUtility.SetDirty(ctrl);
            return ctrl;
        }

        private static Material LoadOrCreateUrpMat(string path, Shader shader, Color baseColor, float smoothness)
        {
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            else
            {
                mat.shader = shader;
            }

            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", baseColor);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", baseColor);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static void EnsureFolder(string parent, string child)
        {
            if (!AssetDatabase.IsValidFolder($"{parent}/{child}"))
                AssetDatabase.CreateFolder(parent, child);
        }
    }
}
