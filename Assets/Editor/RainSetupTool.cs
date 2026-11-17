using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using VRAtis.Ballistics;
using VRAtis.Environment;

namespace VRAtis.Editor
{
    [InitializeOnLoad]
    public static class RainSetupTool
    {
        private const string AutoSetupDoneKey = "VRAtis_RainWeather_V5_Done";
        private const string RootObjectName = "Environment_RainWeather";
        private const string WeatherDir = "Assets/Models/Environment/Weather";
        private const string MeshDir = "Assets/Models/Environment/Weather/Meshes";
        private const string EnvMatDir = "Assets/Materials/Environment";
        private const string RainAudioPath = "Assets/Audio/Environment/Rain_Steady_01.wav";

        private static readonly Vector3 NearBoxSize = new Vector3(16f, 13f, 16f);
        private static readonly Vector3 MidBoxSize = new Vector3(40f, 18f, 40f);

        static RainSetupTool()
        {
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode || Application.isPlaying)
                    return;

                if (!EditorPrefs.GetBool(AutoSetupDoneKey, false))
                {
                    if (TrySetupRainWeather())
                        EditorPrefs.SetBool(AutoSetupDoneKey, true);
                }
            };
        }

        [MenuItem("VRAtis/Yağmur Sistemi Kur", false, 35)]
        public static void SetupRainWeatherMenu()
        {
            bool ok = TrySetupRainWeather();
            EditorPrefs.SetBool(AutoSetupDoneKey, ok);
        }

        private static bool TrySetupRainWeather()
        {
            Shader rainShader = Shader.Find("VRAtis/AlpineRainURP");
            if (rainShader == null)
            {
                Debug.LogWarning("[RainSetupTool] VRAtis/AlpineRainURP shader henüz hazır değil; yeniden denenecek.");
                return false;
            }

            EnvironmentManager envMgr = Object.FindAnyObjectByType<EnvironmentManager>();
            if (envMgr == null)
            {
                Debug.LogError("[RainSetupTool] Sahnede EnvironmentManager bulunamadı.");
                return false;
            }

            EnsureFolder(MeshDir);
            EnsureFolder(EnvMatDir);

            CleanupObsoleteClusterAssets();

            string streakTexPath = $"{WeatherDir}/RainStreak.png";
            string splashTexPath = $"{WeatherDir}/RainSplash.png";
            ConfigureTextureImporter(streakTexPath, maxSize: 256, enableMipMaps: true);
            ConfigureTextureImporter(splashTexPath, maxSize: 128, enableMipMaps: true);

            Mesh nearMesh = BuildOrUpdateRainDropMesh(
                $"{MeshDir}/NearRainDropsMesh.asset",
                "NearRainDropsMesh",
                dropCount: 2200,
                boxSize: NearBoxSize,
                seed: 19487);

            Mesh midMesh = BuildOrUpdateRainDropMesh(
                $"{MeshDir}/MidRainDropsMesh.asset",
                "MidRainDropsMesh",
                dropCount: 2400,
                boxSize: MidBoxSize,
                seed: 63211);

            Material nearMat = BuildGpuRainMaterial(
                "RainNearGPU_Mat",
                rainShader,
                streakTexPath,
                new Color(0.84f, 0.90f, 0.96f, 0.72f),
                NearBoxSize,
                baseFallSpeed: 19.5f,
                minLen: 0.65f,
                maxLen: 1.20f,
                minWidth: 0.055f,
                maxWidth: 0.115f,
                nearFadeStart: 0.65f,
                nearFadeEnd: 1.65f);

            Material midMat = BuildGpuRainMaterial(
                "RainMidGPU_Mat",
                rainShader,
                streakTexPath,
                new Color(0.80f, 0.86f, 0.93f, 0.48f),
                MidBoxSize,
                baseFallSpeed: 16.5f,
                minLen: 0.90f,
                maxLen: 1.65f,
                minWidth: 0.13f,
                maxWidth: 0.25f,
                nearFadeStart: 4.5f,
                nearFadeEnd: 9.0f);

            Material splashMat = BuildRainParticleMaterial(
                "RainSplash_Mat",
                splashTexPath,
                new Color(0.82f, 0.89f, 0.95f, 0.75f),
                nearFadeMeters: 0.4f,
                farFadeMeters: 1.0f);

            AudioClip rainClip = LoadAndOptimizeRainClip(RainAudioPath);
            if (rainClip == null)
            {
                Debug.LogWarning($"[RainSetupTool] Yağmur ses klibi henüz import edilmedi: {RainAudioPath}");
                return false;
            }

            GameObject existing = GameObject.Find(RootObjectName);
            if (existing != null)
                Object.DestroyImmediate(existing);

            GameObject root = new GameObject(RootObjectName);
            RainWeatherController controller = root.AddComponent<RainWeatherController>();

            MeshRenderer nearRenderer = CreateGpuRainLayerObject(root.transform, "GPU_NearRain", nearMesh, nearMat);
            MeshRenderer midRenderer = CreateGpuRainLayerObject(root.transform, "GPU_MidRain", midMesh, midMat);
            ParticleSystem splashPs = CreateGroundSplashEmitter(root.transform, splashMat);
            CreateRainAudioObject(root.transform, rainClip, out AudioSource rainAudioSource, out AudioLowPassFilter rainLowPass);

            WireControllerReferences(controller, envMgr, nearRenderer, midRenderer, splashPs, rainClip, rainAudioSource, rainLowPass);

            EditorUtility.SetDirty(controller);
            EditorUtility.SetDirty(root);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            AssetDatabase.SaveAssets();
            EditorSceneManager.SaveOpenScenes();

            Debug.Log("[RainSetupTool] V5 Kuruldu: 4.600 bağımsız rastgele GPU yağmur damlası + rüzgara bağlı açı + zemin sıçramaları + stereo yağmur sesi!");
            return true;
        }

        private static void CleanupObsoleteClusterAssets()
        {
            string[] obsoleteAssets =
            {
                $"{WeatherDir}/RainStreakCluster.png",
                $"{WeatherDir}/RainSheetCluster.png",
                $"{EnvMatDir}/RainStreakNear_Mat.mat",
                $"{EnvMatDir}/RainSheetFar_Mat.mat",
            };

            foreach (string path in obsoleteAssets)
            {
                if (AssetDatabase.LoadAssetAtPath<Object>(path) != null)
                    AssetDatabase.DeleteAsset(path);
            }
        }

        private static void EnsureFolder(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath))
                return;

            string[] parts = folderPath.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private static void ConfigureTextureImporter(string texPath, int maxSize, bool enableMipMaps)
        {
            TextureImporter importer = AssetImporter.GetAtPath(texPath) as TextureImporter;
            if (importer == null)
                return;

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
            if (importer.filterMode != FilterMode.Bilinear)
            {
                importer.filterMode = FilterMode.Bilinear;
                dirty = true;
            }
            if (importer.mipmapEnabled != enableMipMaps)
            {
                importer.mipmapEnabled = enableMipMaps;
                dirty = true;
            }
            if (importer.maxTextureSize != maxSize)
            {
                importer.maxTextureSize = maxSize;
                dirty = true;
            }

            if (dirty)
                importer.SaveAndReimport();
        }

        private static Mesh BuildOrUpdateRainDropMesh(
            string assetPath,
            string meshName,
            int dropCount,
            Vector3 boxSize,
            int seed)
        {
            Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
            if (mesh == null)
            {
                mesh = new Mesh { name = meshName };
                PopulateRainMesh(mesh, dropCount, boxSize, seed);
                AssetDatabase.CreateAsset(mesh, assetPath);
            }
            else
            {
                mesh.name = meshName;
                PopulateRainMesh(mesh, dropCount, boxSize, seed);
            }

            EditorUtility.SetDirty(mesh);
            return mesh;
        }

        private static void PopulateRainMesh(Mesh mesh, int dropCount, Vector3 boxSize, int seed)
        {
            mesh.Clear();
            mesh.indexFormat = IndexFormat.UInt16;

            int vertCount = dropCount * 4;
            int idxCount = dropCount * 6;
            Vector3[] positions = new Vector3[vertCount];
            Vector2[] uvs = new Vector2[vertCount];
            Color[] colors = new Color[vertCount];
            int[] triangles = new int[idxCount];

            System.Random rng = new System.Random(seed);

            for (int i = 0; i < dropCount; i++)
            {
                float cx = ((float)rng.NextDouble() - 0.5f) * boxSize.x;
                float cy = ((float)rng.NextDouble() - 0.5f) * boxSize.y;
                float cz = ((float)rng.NextDouble() - 0.5f) * boxSize.z;
                Vector3 center = new Vector3(cx, cy, cz);

                Color randProps = new Color(
                    (float)rng.NextDouble(),
                    (float)rng.NextDouble(),
                    (float)rng.NextDouble(),
                    (float)rng.NextDouble());

                int v = i * 4;
                positions[v + 0] = center;
                positions[v + 1] = center;
                positions[v + 2] = center;
                positions[v + 3] = center;

                uvs[v + 0] = new Vector2(0f, 0f);
                uvs[v + 1] = new Vector2(1f, 0f);
                uvs[v + 2] = new Vector2(1f, 1f);
                uvs[v + 3] = new Vector2(0f, 1f);

                colors[v + 0] = randProps;
                colors[v + 1] = randProps;
                colors[v + 2] = randProps;
                colors[v + 3] = randProps;

                int t = i * 6;
                triangles[t + 0] = v + 0;
                triangles[t + 1] = v + 1;
                triangles[t + 2] = v + 2;
                triangles[t + 3] = v + 0;
                triangles[t + 4] = v + 2;
                triangles[t + 5] = v + 3;
            }

            mesh.vertices = positions;
            mesh.uv = uvs;
            mesh.colors = colors;
            mesh.SetTriangles(triangles, 0, calculateBounds: false);
            mesh.bounds = new Bounds(Vector3.zero, boxSize + new Vector3(3f, 3f, 3f));
        }

        private static Material BuildGpuRainMaterial(
            string matName,
            Shader shader,
            string texPath,
            Color baseColor,
            Vector3 boxSize,
            float baseFallSpeed,
            float minLen,
            float maxLen,
            float minWidth,
            float maxWidth,
            float nearFadeStart,
            float nearFadeEnd)
        {
            string matPath = $"{EnvMatDir}/{matName}.mat";
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, matPath);
            }
            else
            {
                mat.shader = shader;
            }

            Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            if (tex != null)
                mat.SetTexture("_BaseMap", tex);

            mat.SetColor("_BaseColor", baseColor);
            mat.SetVector("_BoxSize", new Vector4(boxSize.x, boxSize.y, boxSize.z, 0f));
            mat.SetFloat("_BaseFallSpeed", baseFallSpeed);
            mat.SetFloat("_MinLen", minLen);
            mat.SetFloat("_MaxLen", maxLen);
            mat.SetFloat("_MinWidth", minWidth);
            mat.SetFloat("_MaxWidth", maxWidth);
            mat.SetFloat("_NearFadeStart", nearFadeStart);
            mat.SetFloat("_NearFadeEnd", nearFadeEnd);
            mat.renderQueue = (int)RenderQueue.Transparent + 10;

            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static MeshRenderer CreateGpuRainLayerObject(
            Transform parent,
            string objName,
            Mesh mesh,
            Material mat)
        {
            GameObject go = new GameObject(objName);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;

            MeshFilter mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;

            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            mr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            mr.enabled = false;
            return mr;
        }

        private static ParticleSystem CreateGroundSplashEmitter(Transform parent, Material mat)
        {
            GameObject go = new GameObject("Rain_GroundSplashes");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0.03f, 0f);

            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.loop = true;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.20f, 0.32f);
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(0.16f, 0.28f);
            main.startColor = new Color(0.84f, 0.90f, 0.96f, 0.62f);
            main.maxParticles = 320;

            var emission = ps.emission;
            emission.rateOverTime = 0f;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(16f, 0.04f, 16f);

            var sizeLife = ps.sizeOverLifetime;
            sizeLife.enabled = true;
            sizeLife.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                new Keyframe(0f, 0.25f),
                new Keyframe(1f, 1.0f)));

            var col = ps.colorOverLifetime;
            col.enabled = true;
            Gradient grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0.85f, 0f), new GradientAlphaKey(0f, 1f) });
            col.color = grad;

            ParticleSystemRenderer renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = mat;
            renderer.renderMode = ParticleSystemRenderMode.HorizontalBillboard;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return ps;
        }

        private static Material BuildRainParticleMaterial(
            string matName,
            string texPath,
            Color baseColor,
            float nearFadeMeters,
            float farFadeMeters)
        {
            string matPath = $"{EnvMatDir}/{matName}.mat";
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

            Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            if (tex != null)
                mat.SetTexture("_BaseMap", tex);

            mat.SetColor("_BaseColor", baseColor);
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");

            mat.SetFloat("_CameraFadingEnabled", 1f);
            float invDist = 1f / Mathf.Max(0.1f, farFadeMeters - nearFadeMeters);
            mat.SetVector("_CameraFadeParams", new Vector4(nearFadeMeters, invDist, 0f, 0f));
            mat.EnableKeyword("_FADING_ON");

            mat.renderQueue = (int)RenderQueue.Transparent;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static AudioClip LoadAndOptimizeRainClip(string path)
        {
            if (AssetDatabase.LoadAssetAtPath<AudioClip>(path) == null && System.IO.File.Exists(path))
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

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
                if (settings.quality < 0.72f || settings.quality > 0.78f)
                {
                    settings.quality = 0.75f;
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

            return AssetDatabase.LoadAssetAtPath<AudioClip>(path);
        }

        private static void CreateRainAudioObject(
            Transform parent,
            AudioClip clip,
            out AudioSource audioSource,
            out AudioLowPassFilter lowPass)
        {
            GameObject go = new GameObject("Rain_AmbientAudio");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;

            audioSource = go.AddComponent<AudioSource>();
            audioSource.clip = clip;
            audioSource.loop = true;
            audioSource.playOnAwake = false;
            audioSource.spatialBlend = 0f;
            audioSource.dopplerLevel = 0f;
            audioSource.volume = 0f;
            audioSource.priority = 195;

            lowPass = go.AddComponent<AudioLowPassFilter>();
            lowPass.cutoffFrequency = 9200f;
            lowPass.lowpassResonanceQ = 0.75f;
        }

        private static void WireControllerReferences(
            RainWeatherController controller,
            EnvironmentManager envMgr,
            MeshRenderer nearRenderer,
            MeshRenderer midRenderer,
            ParticleSystem splashPs,
            AudioClip rainClip,
            AudioSource rainAudioSource,
            AudioLowPassFilter rainLowPass)
        {
            SerializedObject so = new SerializedObject(controller);
            so.FindProperty("environmentManager").objectReferenceValue = envMgr;
            so.FindProperty("timeOfDayController").objectReferenceValue = Object.FindAnyObjectByType<DynamicTimeOfDay>();
            so.FindProperty("cameraTransform").objectReferenceValue = Camera.main != null ? Camera.main.transform : null;
            so.FindProperty("nearRainRenderer").objectReferenceValue = nearRenderer;
            so.FindProperty("midRainRenderer").objectReferenceValue = midRenderer;
            so.FindProperty("groundSplashParticles").objectReferenceValue = splashPs;
            so.FindProperty("rainAudioClip").objectReferenceValue = rainClip;
            so.FindProperty("rainAudioSource").objectReferenceValue = rainAudioSource;
            so.FindProperty("rainLowPassFilter").objectReferenceValue = rainLowPass;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
