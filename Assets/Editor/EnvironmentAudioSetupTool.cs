using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRAtis.Ballistics;
using VRAtis.Environment;

namespace VRAtis.EditorTools
{
    [InitializeOnLoad]
    public static class EnvironmentAudioSetupTool
    {
        private const string EnvAudioDir = "Assets/Audio/Environment";
        private const string AutoSetupVersionKey = "VRAtis_WindAudio_V2_Done";

        private static readonly string[] CalmClipPaths =
        {
            $"{EnvAudioDir}/ValleyWind_Calm_01.wav",
            $"{EnvAudioDir}/ValleyWind_Calm_02.wav"
        };

        private static readonly string[] GustClipPaths =
        {
            $"{EnvAudioDir}/ValleyWind_Gust_01.wav",
            $"{EnvAudioDir}/ValleyWind_Gust_02.wav"
        };

        static EnvironmentAudioSetupTool()
        {
            EditorApplication.delayCall += TryAutoSetupOnce;
        }

        private static void TryAutoSetupOnce()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || Application.isBatchMode)
                return;

            EnvironmentManager env = Object.FindAnyObjectByType<EnvironmentManager>();
            if (env == null)
                return;

            if (env.GetComponent<WindAudioController>() != null &&
                EditorPrefs.GetBool(AutoSetupVersionKey, false))
                return;

            if (AssetDatabase.LoadAssetAtPath<AudioClip>(CalmClipPaths[0]) == null)
                return;

            SetupValleyWindAudio();
        }

        [MenuItem("VRAtis/Ortam Rüzgar Sesi Kur")]
        public static void SetupValleyWindAudio()
        {
            EnvironmentManager env = Object.FindAnyObjectByType<EnvironmentManager>();
            if (env == null)
            {
                Debug.LogError("[EnvironmentAudioSetupTool] Sahnede EnvironmentManager bulunamadı.");
                return;
            }

            AudioClip[] calmClips = LoadAndOptimizeWindClips(CalmClipPaths);
            AudioClip[] gustClips = LoadAndOptimizeWindClips(GustClipPaths);
            if (calmClips.Length == 0 && gustClips.Length == 0)
            {
                Debug.LogError($"[EnvironmentAudioSetupTool] Rüzgar WAV dosyaları yüklenemedi: {EnvAudioDir}");
                return;
            }

            WindAudioController windAudio = env.GetComponent<WindAudioController>();
            if (windAudio == null)
                windAudio = Undo.AddComponent<WindAudioController>(env.gameObject);

            SerializedObject so = new SerializedObject(windAudio);
            so.FindProperty("environmentManager").objectReferenceValue = env;
            so.FindProperty("headCameraTransform").objectReferenceValue = Camera.main != null ? Camera.main.transform : null;
            so.FindProperty("masterWindVolume").floatValue = 0.22f;
            so.FindProperty("calmLayerGain").floatValue = 0.72f;
            so.FindProperty("gustLayerGain").floatValue = 0.95f;
            so.FindProperty("calmFullSpeedMS").floatValue = 6.0f;
            so.FindProperty("gustStartSpeedMS").floatValue = 4.2f;
            so.FindProperty("gustFullSpeedMS").floatValue = 13.0f;

            AssignClipArray(so.FindProperty("calmWindClips"), calmClips);
            AssignClipArray(so.FindProperty("gustWindClips"), gustClips);
            so.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(windAudio);
            EditorUtility.SetDirty(env.gameObject);
            EditorSceneManager.MarkSceneDirty(env.gameObject.scene);
            EditorSceneManager.SaveOpenScenes();
            EditorPrefs.SetBool(AutoSetupVersionKey, true);
            Debug.Log("[EnvironmentAudioSetupTool] Çift katmanlı doğal vadi rüzgâr sesi (Vorbis optimize + kafa yönüne duyarlı) kuruldu!");
        }

        private static AudioClip[] LoadAndOptimizeWindClips(string[] paths)
        {
            var list = new System.Collections.Generic.List<AudioClip>(paths.Length);
            foreach (string path in paths)
            {
                AudioImporter importer = AssetImporter.GetAtPath(path) as AudioImporter;
                if (importer != null)
                {
                    AudioImporterSampleSettings settings = importer.defaultSampleSettings;
                    bool dirty = false;

                    if (settings.loadType != AudioClipLoadType.CompressedInMemory)
                    {
                        settings.loadType = AudioClipLoadType.CompressedInMemory;
                        dirty = true;
                    }
                    if (settings.compressionFormat != AudioCompressionFormat.Vorbis)
                    {
                        settings.compressionFormat = AudioCompressionFormat.Vorbis;
                        dirty = true;
                    }
                    if (settings.quality < 0.70f || settings.quality > 0.78f)
                    {
                        settings.quality = 0.72f;
                        dirty = true;
                    }
                    if (importer.forceToMono)
                    {
                        importer.forceToMono = false;
                        dirty = true;
                    }

                    if (dirty)
                    {
                        importer.defaultSampleSettings = settings;
                        importer.SaveAndReimport();
                    }
                }

                AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                if (clip != null)
                    list.Add(clip);
            }
            return list.ToArray();
        }

        private static void AssignClipArray(SerializedProperty arrayProp, AudioClip[] clips)
        {
            arrayProp.ClearArray();
            for (int i = 0; i < clips.Length; i++)
            {
                arrayProp.InsertArrayElementAtIndex(i);
                arrayProp.GetArrayElementAtIndex(i).objectReferenceValue = clips[i];
            }
        }
    }
}
