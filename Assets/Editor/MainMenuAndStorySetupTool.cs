using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using VRAtis.Hardware;
using VRAtis.UI;
using VRAtis.WorldMap;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
#endif

namespace VRAtis.EditorTools
{
    public static class MainMenuAndStorySetupTool
    {
        public const string MainMenuScenePath = "Assets/Scenes/MainMenu.unity";
        public const string TrainingScenePath = "Assets/Scenes/SampleScene.unity";
        public const string StoryWorldMapScenePath = "Assets/Scenes/StoryWorldMap.unity";

        private const string UiTexDir = "Assets/UI/Textures";
        private const string WorldMapMatDir = "Assets/Materials/WorldMap";
        private const string BackdropTexPath = "Assets/UI/Textures/MainMenuBackdrop.png";
        private const string StoryIconTexPath = "Assets/UI/Textures/StoryMapIcon.png";
        private const string GroundTexPath = "Assets/Materials/WorldMap/WorldMapGround_Tex.png";
        private const string GroundMatPath = "Assets/Materials/WorldMap/WorldMapGround_Mat.mat";
        private const string BorderMatPath = "Assets/Materials/WorldMap/WorldMapBorder_Mat.mat";
        private const string CenterMarkerMatPath = "Assets/Materials/WorldMap/WorldMapCenter_Mat.mat";
        private const string CommanderFbxPath = "Assets/Models/WorldMap/PlayerCommander/WorldMapCommander.fbx";
        private const string CommanderTexDir = "Assets/Models/WorldMap/PlayerCommander/Textures";
        private const string CommanderMatDir = "Assets/Materials/WorldMap/PlayerCommander";
        private const string CommanderPrefabPath = "Assets/Prefabs/WorldMapPlayerCommander.prefab";

        // Tactical Military Command palette tokens
        private static readonly Color BgDeepSlate = new Color(0.039f, 0.067f, 0.102f, 1f);
        private static readonly Color CardSurface = new Color(0.055f, 0.092f, 0.141f, 0.985f);
        private static readonly Color CardSurfaceInner = new Color(0.082f, 0.133f, 0.196f, 0.98f);
        private static readonly Color HudSurface = new Color(0.050f, 0.084f, 0.128f, 0.985f);
        private static readonly Color StoryGold = new Color(0.890f, 0.663f, 0.235f, 1f);
        private static readonly Color StoryGoldBadgeBg = new Color(0.185f, 0.142f, 0.062f, 0.98f);
        private static readonly Color TrainingEmerald = new Color(0.239f, 0.761f, 0.580f, 1f);
        private static readonly Color TrainingEmeraldBadgeBg = new Color(0.062f, 0.185f, 0.148f, 0.98f);
        private static readonly Color TextPrimary = new Color(0.961f, 0.980f, 1.000f, 1f);
        private static readonly Color TextSecondary = new Color(0.710f, 0.796f, 0.878f, 1f);
        private static readonly Color TextMuted = new Color(0.555f, 0.650f, 0.745f, 1f);
        private static readonly Color TextDarkOnAccent = new Color(0.045f, 0.067f, 0.094f, 1f);
        private static readonly Color QuitBtnBg = new Color(0.135f, 0.075f, 0.095f, 0.97f);
        private static readonly Color QuitBtnBorder = new Color(0.82f, 0.34f, 0.36f, 0.65f);
        private static readonly Color DividerSubtle = new Color(1f, 1f, 1f, 0.12f);

        [MenuItem("VRAtis/Faz 1 - Menü ve Dünya Haritası/1. Tümünü Kur (Ana Menü + Dünya Haritası + Build Settings)")]
        public static void BuildAllPhase1Scenes()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("[MainMenuAndStorySetupTool] Play Mode aktifken kurulum çalıştırılamaz.");
                return;
            }

            EnsureProceduralAssets();
            BuildStoryWorldMapScene();
            BuildMainMenuScene();
            ConfigureBuildSettings();

            // Leave MainMenu open so pressing Play starts cleanly from the Main Menu
            EditorSceneManager.OpenScene(MainMenuScenePath, OpenSceneMode.Single);
            Debug.Log("[MainMenuAndStorySetupTool] Faz 1 kurulumu tamamlandı: MainMenu (0), SampleScene (1), StoryWorldMap (2).");
        }

        [MenuItem("VRAtis/Faz 1 - Menü ve Dünya Haritası/2. Sadece Ana Menü Sahnesini Kur")]
        public static void BuildMainMenuScene()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            EnsureFolder("Assets", "Scenes");

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = Color.black;
            RenderSettings.fog = false;

            // 1. Clean Black Camera
            GameObject camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            Camera cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.orthographic = true;
            cam.cullingMask = 0;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.useOcclusionCulling = false;
            camGo.AddComponent<AudioListener>();
            UniversalAdditionalCameraData urpCam = camGo.AddComponent<UniversalAdditionalCameraData>();
            urpCam.renderShadows = false;
            urpCam.renderPostProcessing = false;
            urpCam.antialiasing = AntialiasingMode.None;

            // 2. EventSystem (Input System compatible)
            EnsureEventSystem();

            // 3. Load 9-slice Sprite & Font
            Sprite roundedSprite = LoadSprite($"{UiTexDir}/RoundedPanel_9Slice.png", isNineSlice: true);
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            // Minimal palette: pure black background + matte gray panel + crisp gray buttons
            Color panelBorder = new Color(0.32f, 0.32f, 0.32f, 1f);
            Color panelGray = new Color(0.20f, 0.20f, 0.20f, 1f);
            Color buttonBorder = new Color(0.45f, 0.45f, 0.45f, 1f);
            Color buttonGray = new Color(0.29f, 0.29f, 0.29f, 1f);

            // 4. Canvas
            GameObject canvasGo = new GameObject("MainMenu_Canvas");
            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;

            UnityEngine.UI.CanvasScaler scaler = canvasGo.AddComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = UnityEngine.UI.CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            canvasGo.AddComponent<UnityEngine.UI.GraphicRaycaster>();

            // Full-bleed solid black background
            GameObject bgGo = new GameObject("BlackBackground");
            bgGo.transform.SetParent(canvasGo.transform, false);
            UnityEngine.UI.Image bgImg = bgGo.AddComponent<UnityEngine.UI.Image>();
            bgImg.color = Color.black;
            bgImg.raycastTarget = false;
            StretchFull(bgGo.GetComponent<RectTransform>());

            // Safe Area Container
            GameObject safeAreaGo = new GameObject("SafeAreaRoot");
            safeAreaGo.transform.SetParent(canvasGo.transform, false);
            StretchFull(safeAreaGo.AddComponent<RectTransform>());
            safeAreaGo.AddComponent<SafeAreaFitter>();

            // Centered Gray Panel
            GameObject menuPanel = CreateBorderedPanel("MenuPanel", safeAreaGo.transform, roundedSprite, panelBorder, panelGray, 2f);
            RectTransform panelRt = menuPanel.GetComponent<RectTransform>();
            panelRt.anchorMin = new Vector2(0.5f, 0.5f);
            panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.pivot = new Vector2(0.5f, 0.5f);
            panelRt.anchoredPosition = Vector2.zero;
            panelRt.sizeDelta = new Vector2(620f, 340f);

            GameObject buttonsContainer = new GameObject("ButtonsContainer");
            buttonsContainer.transform.SetParent(menuPanel.transform, false);
            RectTransform btnsRt = buttonsContainer.AddComponent<RectTransform>();
            btnsRt.anchorMin = Vector2.zero;
            btnsRt.anchorMax = Vector2.one;
            btnsRt.offsetMin = new Vector2(44f, 44f);
            btnsRt.offsetMax = new Vector2(-44f, -44f);

            UnityEngine.UI.VerticalLayoutGroup vLayout = buttonsContainer.AddComponent<UnityEngine.UI.VerticalLayoutGroup>();
            vLayout.spacing = 28f;
            vLayout.childAlignment = TextAnchor.MiddleCenter;
            vLayout.childControlWidth = true;
            vLayout.childControlHeight = true;
            vLayout.childForceExpandWidth = true;
            vLayout.childForceExpandHeight = true;

            // Button 1: HİKAYE
            UnityEngine.UI.Button storyBtn = CreateBorderedButton("StoryButton", buttonsContainer.transform, roundedSprite, buttonBorder, buttonGray, Vector2.zero, 2f);
            UnityEngine.UI.Text storyTxt = CreateText("Label", storyBtn.transform, font, "HİKAYE", 40, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter);
            StretchFull(storyTxt.GetComponent<RectTransform>());

            // Button 2: ANTREMAN
            UnityEngine.UI.Button trainingBtn = CreateBorderedButton("TrainingButton", buttonsContainer.transform, roundedSprite, buttonBorder, buttonGray, Vector2.zero, 2f);
            UnityEngine.UI.Text trainingTxt = CreateText("Label", trainingBtn.transform, font, "ANTREMAN", 40, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter);
            StretchFull(trainingTxt.GetComponent<RectTransform>());

            // Minimal Loading Overlay (shown only during async scene transition)
            GameObject loadingGo = new GameObject("LoadingOverlay");
            loadingGo.transform.SetParent(canvasGo.transform, false);
            UnityEngine.UI.Image loadingDim = loadingGo.AddComponent<UnityEngine.UI.Image>();
            loadingDim.color = new Color(0f, 0f, 0f, 0.88f);
            loadingDim.raycastTarget = true;
            StretchFull(loadingGo.GetComponent<RectTransform>());

            UnityEngine.UI.Text loadingText = CreateText("LoadingStatusText", loadingGo.transform, font, "Yükleniyor...", 34, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter);
            StretchFull(loadingText.GetComponent<RectTransform>());
            loadingGo.SetActive(false);

            // 5. Wire MainMenuController
            MainMenuController ctrl = canvasGo.AddComponent<MainMenuController>();
            AudioSource uiAudio = canvasGo.AddComponent<AudioSource>();
            uiAudio.playOnAwake = false;
            uiAudio.spatialBlend = 0f;

            SerializedObject so = new SerializedObject(ctrl);
            so.FindProperty("storyModeSceneName").stringValue = MainMenuController.StoryWorldMapSceneName;
            so.FindProperty("trainingModeSceneName").stringValue = MainMenuController.TrainingSceneName;
            so.FindProperty("storyModeCardButton").objectReferenceValue = null;
            so.FindProperty("storyModeButton").objectReferenceValue = storyBtn;
            so.FindProperty("trainingModeCardButton").objectReferenceValue = null;
            so.FindProperty("trainingModeButton").objectReferenceValue = trainingBtn;
            so.FindProperty("quitButton").objectReferenceValue = null;
            so.FindProperty("loadingOverlay").objectReferenceValue = loadingGo;
            so.FindProperty("loadingStatusText").objectReferenceValue = loadingText;
            so.FindProperty("uiAudioSource").objectReferenceValue = uiAudio;
            so.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, MainMenuScenePath);
            Debug.Log($"[MainMenuAndStorySetupTool] Sade Ana Menü sahnesi kaydedildi: {MainMenuScenePath}");
        }

        [MenuItem("VRAtis/Faz 1 - Menü ve Dünya Haritası/3. Sadece Hikaye Dünya Haritası Sahnesini Kur")]
        public static void BuildStoryWorldMapScene()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            EnsureProceduralAssets();
            EnsureFolder("Assets", "Scenes");

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Sky & Atmosphere
            Material skyMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/Environment/AlpineCloudsSkybox.mat");
            RenderSettings.skybox = skyMat;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.56f, 0.67f, 0.78f, 1f);
            RenderSettings.ambientEquatorColor = new Color(0.45f, 0.48f, 0.43f, 1f);
            RenderSettings.ambientGroundColor = new Color(0.24f, 0.25f, 0.21f, 1f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.67f, 0.75f, 0.80f, 1f);
            RenderSettings.fogStartDistance = 95f;
            RenderSettings.fogEndDistance = 260f;

            // 1. Directional Sun Light
            GameObject sunGo = new GameObject("Directional Light");
            Light sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1.0f, 0.965f, 0.895f, 1f);
            sun.intensity = 1.35f;
            sun.shadows = LightShadows.Soft;
            sunGo.transform.rotation = Quaternion.Euler(52f, -34f, 0f);

            // 2. Empty Warband-Style Overworld Ground (240m x 240m flat steppe canvas)
            GameObject mapRoot = new GameObject("WorldMap_Environment");

            Material groundMat = AssetDatabase.LoadAssetAtPath<Material>(GroundMatPath);
            Material borderMat = AssetDatabase.LoadAssetAtPath<Material>(BorderMatPath);
            Material centerMat = AssetDatabase.LoadAssetAtPath<Material>(CenterMarkerMatPath);

            GameObject groundGo = GameObject.CreatePrimitive(PrimitiveType.Plane);
            groundGo.name = "WorldMap_Ground";
            groundGo.transform.SetParent(mapRoot.transform, false);
            groundGo.transform.position = Vector3.zero;
            groundGo.transform.localScale = new Vector3(24f, 1f, 24f); // 240 x 240 meters
            groundGo.isStatic = true;
            MeshRenderer groundRenderer = groundGo.GetComponent<MeshRenderer>();
            if (groundRenderer != null && groundMat != null)
            {
                groundRenderer.sharedMaterial = groundMat;
                groundRenderer.shadowCastingMode = ShadowCastingMode.Off;
            }

            // Subtle perimeter boundary rails at +/- 112m so map edges read clearly from above
            CreateMapBorderRail(mapRoot.transform, "Border_North", new Vector3(0f, 0.15f, 112f), new Vector3(226f, 0.3f, 1.4f), borderMat);
            CreateMapBorderRail(mapRoot.transform, "Border_South", new Vector3(0f, 0.15f, -112f), new Vector3(226f, 0.3f, 1.4f), borderMat);
            CreateMapBorderRail(mapRoot.transform, "Border_East", new Vector3(112f, 0.15f, 0f), new Vector3(1.4f, 0.3f, 226f), borderMat);
            CreateMapBorderRail(mapRoot.transform, "Border_West", new Vector3(-112f, 0.15f, 0f), new Vector3(1.4f, 0.3f, 226f), borderMat);

            // Dynamic ground cursor ring (follows camera focus / joystick navigation on the map surface)
            GameObject cursorRing = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            cursorRing.name = "MapCursorRing";
            cursorRing.transform.SetParent(mapRoot.transform, false);
            cursorRing.transform.position = new Vector3(0f, 0.04f, 0f);
            cursorRing.transform.localScale = new Vector3(3.2f, 0.03f, 3.2f);
            Object.DestroyImmediate(cursorRing.GetComponent<Collider>());
            MeshRenderer cursorMr = cursorRing.GetComponent<MeshRenderer>();
            if (cursorMr != null && centerMat != null)
            {
                cursorMr.sharedMaterial = centerMat;
                cursorMr.shadowCastingMode = ShadowCastingMode.Off;
            }

            GameObject cursorInner = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            cursorInner.name = "MapCursorInnerCutout";
            cursorInner.transform.SetParent(cursorRing.transform, false);
            cursorInner.transform.localPosition = new Vector3(0f, 0.4f, 0f);
            cursorInner.transform.localScale = new Vector3(0.74f, 1.0f, 0.74f);
            Object.DestroyImmediate(cursorInner.GetComponent<Collider>());
            MeshRenderer cursorInnerMr = cursorInner.GetComponent<MeshRenderer>();
            if (cursorInnerMr != null && groundMat != null)
            {
                cursorInnerMr.sharedMaterial = groundMat;
                cursorInnerMr.shadowCastingMode = ShadowCastingMode.Off;
            }

            // 2B. ESP32-S3 Rifle Joystick UDP Receiver (Port 4210)
            GameObject esp32Go = new GameObject("ESP32_RifleReceiver");
            Esp32RifleReceiver esp32Receiver = esp32Go.AddComponent<Esp32RifleReceiver>();

            // 3. Warband-Style Angled Top-Down Camera
            GameObject camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            Camera cam = camGo.AddComponent<Camera>();
            cam.clearFlags = skyMat != null ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.55f, 0.67f, 0.78f, 1f);
            cam.fieldOfView = 48f;
            cam.nearClipPlane = 0.5f;
            cam.farClipPlane = 350f;
            camGo.AddComponent<AudioListener>();
            UniversalAdditionalCameraData urpCam = camGo.AddComponent<UniversalAdditionalCameraData>();
            urpCam.renderShadows = true;
            urpCam.renderPostProcessing = false;

            WorldMapCameraController mapCamCtrl = camGo.AddComponent<WorldMapCameraController>();
            SerializedObject camSo = new SerializedObject(mapCamCtrl);
            camSo.FindProperty("rifleReceiver").objectReferenceValue = esp32Receiver;
            camSo.FindProperty("cursorRingTransform").objectReferenceValue = cursorRing.transform;
            camSo.ApplyModifiedPropertiesWithoutUndo();

            // Position camera in edit mode too so Scene/Game preview immediately shows the angled top-down field
            Quaternion initialRot = Quaternion.Euler(50f, 0f, 0f);
            camGo.transform.rotation = initialRot;
            camGo.transform.position = initialRot * new Vector3(0f, 0f, -42f);

            // 3B. Spawn Armed & Flag-Bearing Player Commander on the Map
            SpawnWorldMapPlayerCommander(mapCamCtrl);

            // 4. EventSystem
            EnsureEventSystem();

            // 5. Minimal World Map HUD
            Sprite roundedSprite = LoadSprite($"{UiTexDir}/RoundedPanel_9Slice.png", isNineSlice: true);
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            GameObject canvasGo = new GameObject("StoryMapHUD_Canvas");
            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 20;

            UnityEngine.UI.CanvasScaler scaler = canvasGo.AddComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = UnityEngine.UI.CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            canvasGo.AddComponent<UnityEngine.UI.GraphicRaycaster>();

            GameObject safeAreaGo = new GameObject("SafeAreaRoot");
            safeAreaGo.transform.SetParent(canvasGo.transform, false);
            StretchFull(safeAreaGo.AddComponent<RectTransform>());
            safeAreaGo.AddComponent<SafeAreaFitter>();

            // Top-Left Actions Row (Ana Menü + Merkeze Dön)
            GameObject topLeftRow = new GameObject("TopLeftActions");
            topLeftRow.transform.SetParent(safeAreaGo.transform, false);
            RectTransform tlRt = topLeftRow.AddComponent<RectTransform>();
            tlRt.anchorMin = new Vector2(0f, 1f);
            tlRt.anchorMax = new Vector2(0f, 1f);
            tlRt.pivot = new Vector2(0f, 1f);
            tlRt.anchoredPosition = new Vector2(32f, -28f);
            tlRt.sizeDelta = new Vector2(410f, 60f);

            UnityEngine.UI.HorizontalLayoutGroup tlLayout = topLeftRow.AddComponent<UnityEngine.UI.HorizontalLayoutGroup>();
            tlLayout.spacing = 14f;
            tlLayout.childControlWidth = true;
            tlLayout.childControlHeight = true;
            tlLayout.childForceExpandWidth = false;
            tlLayout.childForceExpandHeight = true;

            UnityEngine.UI.Button backBtn = CreateBorderedButton("BackToMenuButton", topLeftRow.transform, roundedSprite, new Color(StoryGold.r, StoryGold.g, StoryGold.b, 0.72f), HudSurface, new Vector2(214f, 60f), 2f);
            UnityEngine.UI.LayoutElement backLe = backBtn.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
            backLe.minWidth = 214f;
            backLe.preferredWidth = 214f;
            UnityEngine.UI.Text backTxt = CreateText("Label", backBtn.transform, font, "←  ANA MENÜ", 21, FontStyle.Bold, TextPrimary, TextAnchor.MiddleCenter);
            StretchFull(backTxt.GetComponent<RectTransform>());

            UnityEngine.UI.Button resetBtn = CreateBorderedButton("ResetCameraButton", topLeftRow.transform, roundedSprite, new Color(0.55f, 0.66f, 0.78f, 0.40f), HudSurface, new Vector2(168f, 60f), 2f);
            UnityEngine.UI.LayoutElement resetLe = resetBtn.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
            resetLe.minWidth = 168f;
            resetLe.preferredWidth = 168f;
            UnityEngine.UI.Text resetTxt = CreateText("Label", resetBtn.transform, font, "MERKEZ", 20, FontStyle.Bold, TextSecondary, TextAnchor.MiddleCenter);
            StretchFull(resetTxt.GetComponent<RectTransform>());

            // Top-Right Map Info Pill
            GameObject topRightPill = CreateBorderedPanel("MapStatusBadge", safeAreaGo.transform, roundedSprite, new Color(StoryGold.r, StoryGold.g, StoryGold.b, 0.72f), HudSurface, 2f);
            RectTransform trRt = topRightPill.GetComponent<RectTransform>();
            trRt.anchorMin = new Vector2(1f, 1f);
            trRt.anchorMax = new Vector2(1f, 1f);
            trRt.pivot = new Vector2(1f, 1f);
            trRt.anchoredPosition = new Vector2(-32f, -28f);
            trRt.sizeDelta = new Vector2(390f, 76f);

            GameObject rightAccent = new GameObject("RightAccentBar");
            rightAccent.transform.SetParent(topRightPill.transform, false);
            UnityEngine.UI.Image raImg = rightAccent.AddComponent<UnityEngine.UI.Image>();
            raImg.color = StoryGold;
            raImg.raycastTarget = false;
            RectTransform raRt = rightAccent.GetComponent<RectTransform>();
            raRt.anchorMin = new Vector2(1f, 0.16f);
            raRt.anchorMax = new Vector2(1f, 0.84f);
            raRt.pivot = new Vector2(1f, 0.5f);
            raRt.anchoredPosition = new Vector2(-8f, 0f);
            raRt.sizeDelta = new Vector2(4f, 0f);

            UnityEngine.UI.Text modeLabel = CreateText("ModeTitle", topRightPill.transform, font, "HİKAYE MODU  •  DÜNYA HARİTASI", 17, FontStyle.Bold, StoryGold, TextAnchor.UpperRight);
            RectTransform mlRt = modeLabel.GetComponent<RectTransform>();
            mlRt.anchorMin = Vector2.zero;
            mlRt.anchorMax = Vector2.one;
            mlRt.offsetMin = new Vector2(18f, 10f);
            mlRt.offsetMax = new Vector2(-24f, -12f);

            UnityEngine.UI.Text coordText = CreateText("CoordinatesText", topRightPill.transform, font, "KONUM: 0, 0", 20, FontStyle.Bold, TextPrimary, TextAnchor.LowerRight);
            RectTransform coRt = coordText.GetComponent<RectTransform>();
            coRt.anchorMin = Vector2.zero;
            coRt.anchorMax = Vector2.one;
            coRt.offsetMin = new Vector2(18f, 12f);
            coRt.offsetMax = new Vector2(-24f, -10f);

            // Bottom-Center Control Hint Pill
            GameObject hintPill = CreateRoundedPanel("CameraHintPill", safeAreaGo.transform, roundedSprite, new Color(0.05f, 0.08f, 0.12f, 0.78f));
            hintPill.GetComponent<UnityEngine.UI.Image>().raycastTarget = false;
            RectTransform hintRt = hintPill.GetComponent<RectTransform>();
            hintRt.anchorMin = new Vector2(0.5f, 0f);
            hintRt.anchorMax = new Vector2(0.5f, 0f);
            hintRt.pivot = new Vector2(0.5f, 0f);
            hintRt.anchoredPosition = new Vector2(0f, 24f);
            hintRt.sizeDelta = new Vector2(920f, 44f);

            UnityEngine.UI.Text hintText = CreateText("HintText", hintPill.transform, font, "Joystick / WASD / Sürükle: İmleci Gezdir   •   Joystick Butonu / Space: İşaretle   •   Scroll / Çift Parmak: Yakınlaştır", 16, FontStyle.Normal, TextSecondary, TextAnchor.MiddleCenter);
            StretchFull(hintText.GetComponent<RectTransform>());

            // Wire StoryMapHudController
            StoryMapHudController hudCtrl = canvasGo.AddComponent<StoryMapHudController>();
            AudioSource hudAudio = canvasGo.AddComponent<AudioSource>();
            hudAudio.playOnAwake = false;
            hudAudio.spatialBlend = 0f;

            SerializedObject hudSo = new SerializedObject(hudCtrl);
            hudSo.FindProperty("mapCamera").objectReferenceValue = mapCamCtrl;
            hudSo.FindProperty("backToMenuButton").objectReferenceValue = backBtn;
            hudSo.FindProperty("resetCameraButton").objectReferenceValue = resetBtn;
            hudSo.FindProperty("coordinatesText").objectReferenceValue = coordText;
            hudSo.FindProperty("uiAudioSource").objectReferenceValue = hudAudio;
            hudSo.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, StoryWorldMapScenePath);
            Debug.Log($"[MainMenuAndStorySetupTool] Hikaye Dünya Haritası sahnesi kaydedildi: {StoryWorldMapScenePath}");
        }

        [MenuItem("VRAtis/Faz 1 - Menü ve Dünya Haritası/4. Build Settings Sahne Sırasını Güncelle")]
        public static void ConfigureBuildSettings()
        {
            List<EditorBuildSettingsScene> ordered = new List<EditorBuildSettingsScene>();
            AddSceneIfExists(ordered, MainMenuScenePath);
            AddSceneIfExists(ordered, TrainingScenePath);
            AddSceneIfExists(ordered, StoryWorldMapScenePath);

            // Preserve any other user scenes that may already be in Build Settings
            foreach (EditorBuildSettingsScene existing in EditorBuildSettings.scenes)
            {
                if (string.IsNullOrEmpty(existing.path))
                    continue;

                bool alreadyAdded = false;
                for (int i = 0; i < ordered.Count; i++)
                {
                    if (ordered[i].path == existing.path)
                    {
                        alreadyAdded = true;
                        break;
                    }
                }

                if (!alreadyAdded)
                    ordered.Add(existing);
            }

            EditorBuildSettings.scenes = ordered.ToArray();
            AssetDatabase.SaveAssets();
            Debug.Log($"[MainMenuAndStorySetupTool] Build Settings güncellendi ({ordered.Count} sahne). Başlangıç sahnesi: {MainMenuScenePath}");
        }

        private static void BuildModeCard(
            Transform parent,
            string cardName,
            Sprite roundedSprite,
            Sprite iconSprite,
            Font font,
            Color accentColor,
            Color badgeBgColor,
            string badgeText,
            string title,
            string description,
            string specLabel1,
            string specValue1,
            string specLabel2,
            string specValue2,
            string[] chips,
            string ctaLabel,
            out UnityEngine.UI.Button cardButton,
            out UnityEngine.UI.Button ctaButton)
        {
            // Outer accent border frame + crisp dark obsidian interior (zero outline color bleed)
            Color borderColor = new Color(accentColor.r, accentColor.g, accentColor.b, 0.55f);
            GameObject cardGo = CreateRoundedPanel(cardName, parent, roundedSprite, borderColor);
            cardButton = cardGo.AddComponent<UnityEngine.UI.Button>();
            UnityEngine.UI.ColorBlock cardColors = cardButton.colors;
            cardColors.normalColor = Color.white;
            cardColors.highlightedColor = new Color(1.10f, 1.10f, 1.10f, 1f);
            cardColors.pressedColor = new Color(0.90f, 0.92f, 0.95f, 1f);
            cardColors.fadeDuration = 0.14f;
            cardButton.colors = cardColors;

            GameObject cardFill = CreateRoundedPanel("CardSurfaceFill", cardGo.transform, roundedSprite, CardSurface);
            cardFill.GetComponent<UnityEngine.UI.Image>().raycastTarget = false;
            RectTransform fillRt = cardFill.GetComponent<RectTransform>();
            fillRt.anchorMin = Vector2.zero;
            fillRt.anchorMax = Vector2.one;
            fillRt.offsetMin = new Vector2(2.5f, 2.5f);
            fillRt.offsetMax = new Vector2(-2.5f, -2.5f);

            // Top Accent Stripe
            GameObject stripeGo = new GameObject("TopAccentStripe");
            stripeGo.transform.SetParent(cardFill.transform, false);
            UnityEngine.UI.Image stripeImg = stripeGo.AddComponent<UnityEngine.UI.Image>();
            stripeImg.color = accentColor;
            stripeImg.raycastTarget = false;
            RectTransform stripeRt = stripeGo.GetComponent<RectTransform>();
            stripeRt.anchorMin = new Vector2(0.03f, 1f);
            stripeRt.anchorMax = new Vector2(0.97f, 1f);
            stripeRt.pivot = new Vector2(0.5f, 1f);
            stripeRt.anchoredPosition = new Vector2(0f, -2f);
            stripeRt.sizeDelta = new Vector2(0f, 6f);

            // Inner padded body
            GameObject innerGo = new GameObject("CardBody");
            innerGo.transform.SetParent(cardFill.transform, false);
            RectTransform innerRt = innerGo.AddComponent<RectTransform>();
            innerRt.anchorMin = Vector2.zero;
            innerRt.anchorMax = Vector2.one;
            innerRt.offsetMin = new Vector2(36f, 32f);
            innerRt.offsetMax = new Vector2(-36f, -32f);

            // Top Row: Badge on left + Icon container on right
            GameObject badgeGo = CreateRoundedPanel("ModeBadge", innerGo.transform, roundedSprite, badgeBgColor);
            badgeGo.GetComponent<UnityEngine.UI.Image>().raycastTarget = false;
            RectTransform badgeRt = badgeGo.GetComponent<RectTransform>();
            badgeRt.anchorMin = new Vector2(0f, 1f);
            badgeRt.anchorMax = new Vector2(0f, 1f);
            badgeRt.pivot = new Vector2(0f, 1f);
            badgeRt.anchoredPosition = Vector2.zero;
            badgeRt.sizeDelta = new Vector2(Mathf.Clamp(badgeText.Length * 11.2f + 36f, 240f, 340f), 38f);

            UnityEngine.UI.Text badgeTxt = CreateText("BadgeLabel", badgeGo.transform, font, badgeText, 15, FontStyle.Bold, accentColor, TextAnchor.MiddleCenter);
            StretchFull(badgeTxt.GetComponent<RectTransform>());

            GameObject iconBox = CreateBorderedPanel("IconFrame", innerGo.transform, roundedSprite, new Color(accentColor.r, accentColor.g, accentColor.b, 0.52f), CardSurfaceInner, 2f);
            RectTransform iconBoxRt = iconBox.GetComponent<RectTransform>();
            iconBoxRt.anchorMin = new Vector2(1f, 1f);
            iconBoxRt.anchorMax = new Vector2(1f, 1f);
            iconBoxRt.pivot = new Vector2(1f, 1f);
            iconBoxRt.anchoredPosition = Vector2.zero;
            iconBoxRt.sizeDelta = new Vector2(88f, 88f);

            GameObject iconGo = new GameObject("Icon");
            iconGo.transform.SetParent(iconBox.transform, false);
            UnityEngine.UI.Image iconImg = iconGo.AddComponent<UnityEngine.UI.Image>();
            iconImg.sprite = iconSprite;
            iconImg.color = accentColor;
            iconImg.preserveAspect = true;
            iconImg.raycastTarget = false;
            RectTransform iconRt = iconGo.GetComponent<RectTransform>();
            iconRt.anchorMin = new Vector2(0.5f, 0.5f);
            iconRt.anchorMax = new Vector2(0.5f, 0.5f);
            iconRt.sizeDelta = new Vector2(52f, 52f);

            // Card Title
            UnityEngine.UI.Text titleTxt = CreateText("CardTitle", innerGo.transform, font, title, 40, FontStyle.Bold, TextPrimary, TextAnchor.MiddleLeft);
            RectTransform titleRt = titleTxt.GetComponent<RectTransform>();
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.pivot = new Vector2(0.5f, 1f);
            titleRt.offsetMin = new Vector2(0f, -118f);
            titleRt.offsetMax = new Vector2(-106f, -60f);

            // Description
            UnityEngine.UI.Text descTxt = CreateText("CardDescription", innerGo.transform, font, description, 21, FontStyle.Normal, TextSecondary, TextAnchor.UpperLeft);
            descTxt.horizontalOverflow = HorizontalWrapMode.Wrap;
            descTxt.verticalOverflow = VerticalWrapMode.Overflow;
            descTxt.lineSpacing = 1.24f;
            RectTransform descRt = descTxt.GetComponent<RectTransform>();
            descRt.anchorMin = new Vector2(0f, 1f);
            descRt.anchorMax = new Vector2(1f, 1f);
            descRt.pivot = new Vector2(0.5f, 1f);
            descRt.offsetMin = new Vector2(0f, -228f);
            descRt.offsetMax = new Vector2(0f, -134f);

            // Tactical Spec Box (balanced vertical rhythm between description and chips)
            GameObject specBox = CreateBorderedPanel("SpecSummaryBox", innerGo.transform, roundedSprite, DividerSubtle, CardSurfaceInner, 1.5f);
            RectTransform specRt = specBox.GetComponent<RectTransform>();
            specRt.anchorMin = new Vector2(0f, 0f);
            specRt.anchorMax = new Vector2(1f, 0f);
            specRt.pivot = new Vector2(0.5f, 0f);
            specRt.anchoredPosition = new Vector2(0f, 184f);
            specRt.sizeDelta = new Vector2(0f, 118f);

            GameObject specMidSep = new GameObject("MidSeparator");
            specMidSep.transform.SetParent(specBox.transform, false);
            UnityEngine.UI.Image sepImg = specMidSep.AddComponent<UnityEngine.UI.Image>();
            sepImg.color = new Color(accentColor.r, accentColor.g, accentColor.b, 0.22f);
            sepImg.raycastTarget = false;
            RectTransform sepRt = specMidSep.GetComponent<RectTransform>();
            sepRt.anchorMin = new Vector2(0f, 0.5f);
            sepRt.anchorMax = new Vector2(1f, 0.5f);
            sepRt.pivot = new Vector2(0.5f, 0.5f);
            sepRt.offsetMin = new Vector2(22f, -0.75f);
            sepRt.offsetMax = new Vector2(-22f, 0.75f);

            CreateSpecRow(specBox.transform, "SpecRow1", font, specLabel1, specValue1, isTopRow: true);
            CreateSpecRow(specBox.transform, "SpecRow2", font, specLabel2, specValue2, isTopRow: false);

            // Feature Chips Row
            GameObject chipsRow = new GameObject("FeatureChipsRow");
            chipsRow.transform.SetParent(innerGo.transform, false);
            RectTransform chipsRt = chipsRow.AddComponent<RectTransform>();
            chipsRt.anchorMin = new Vector2(0f, 0f);
            chipsRt.anchorMax = new Vector2(1f, 0f);
            chipsRt.pivot = new Vector2(0.5f, 0f);
            chipsRt.anchoredPosition = new Vector2(0f, 112f);
            chipsRt.sizeDelta = new Vector2(0f, 46f);

            UnityEngine.UI.HorizontalLayoutGroup chipsLayout = chipsRow.AddComponent<UnityEngine.UI.HorizontalLayoutGroup>();
            chipsLayout.spacing = 12f;
            chipsLayout.childControlWidth = true;
            chipsLayout.childControlHeight = true;
            chipsLayout.childForceExpandWidth = true;
            chipsLayout.childForceExpandHeight = true;

            for (int i = 0; i < chips.Length; i++)
            {
                GameObject chipGo = CreateBorderedPanel($"Chip_{i + 1}", chipsRow.transform, roundedSprite, new Color(accentColor.r, accentColor.g, accentColor.b, 0.25f), CardSurfaceInner, 1.5f);
                UnityEngine.UI.Text chipTxt = CreateText("Label", chipGo.transform, font, chips[i], 16, FontStyle.Bold, TextSecondary, TextAnchor.MiddleCenter);
                StretchFull(chipTxt.GetComponent<RectTransform>());
            }

            // Primary CTA Button at Bottom of Card
            ctaButton = CreateRoundedButton("EnterModeButton", innerGo.transform, roundedSprite, accentColor, new Vector2(0f, 78f));
            RectTransform ctaRt = ctaButton.GetComponent<RectTransform>();
            ctaRt.anchorMin = new Vector2(0f, 0f);
            ctaRt.anchorMax = new Vector2(1f, 0f);
            ctaRt.pivot = new Vector2(0.5f, 0f);
            ctaRt.anchoredPosition = Vector2.zero;
            ctaRt.sizeDelta = new Vector2(0f, 78f);

            UnityEngine.UI.ColorBlock ctaColors = ctaButton.colors;
            ctaColors.normalColor = Color.white;
            ctaColors.highlightedColor = new Color(1.12f, 1.12f, 1.12f, 1f);
            ctaColors.pressedColor = new Color(0.82f, 0.82f, 0.82f, 1f);
            ctaColors.fadeDuration = 0.12f;
            ctaButton.colors = ctaColors;

            UnityEngine.UI.Text ctaTxt = CreateText("Label", ctaButton.transform, font, ctaLabel, 24, FontStyle.Bold, TextDarkOnAccent, TextAnchor.MiddleCenter);
            StretchFull(ctaTxt.GetComponent<RectTransform>());
        }

        private static void CreateSpecRow(Transform parent, string name, Font font, string label, string value, bool isTopRow)
        {
            GameObject row = new GameObject(name);
            row.transform.SetParent(parent, false);
            RectTransform rt = row.AddComponent<RectTransform>();
            rt.anchorMin = isTopRow ? new Vector2(0f, 0.5f) : new Vector2(0f, 0f);
            rt.anchorMax = isTopRow ? new Vector2(1f, 1f) : new Vector2(1f, 0.5f);
            rt.offsetMin = new Vector2(22f, isTopRow ? 2f : 6f);
            rt.offsetMax = new Vector2(-22f, isTopRow ? -6f : -2f);

            UnityEngine.UI.Text lbl = CreateText("Key", row.transform, font, label, 15, FontStyle.Bold, TextMuted, TextAnchor.MiddleLeft);
            RectTransform lblRt = lbl.GetComponent<RectTransform>();
            lblRt.anchorMin = new Vector2(0f, 0f);
            lblRt.anchorMax = new Vector2(0.36f, 1f);
            lblRt.offsetMin = Vector2.zero;
            lblRt.offsetMax = Vector2.zero;

            UnityEngine.UI.Text val = CreateText("Value", row.transform, font, value, 19, FontStyle.Bold, TextPrimary, TextAnchor.MiddleRight);
            RectTransform valRt = val.GetComponent<RectTransform>();
            valRt.anchorMin = new Vector2(0.36f, 0f);
            valRt.anchorMax = new Vector2(1f, 1f);
            valRt.offsetMin = Vector2.zero;
            valRt.offsetMax = Vector2.zero;
        }

        private static void EnsureProceduralAssets()
        {
            EnsureFolder("Assets", "UI");
            EnsureFolder("Assets/UI", "Textures");
            EnsureFolder("Assets", "Materials");
            EnsureFolder("Assets/Materials", "WorldMap");

            GenerateMainMenuBackdropTexture();
            GenerateStoryMapIconTexture();
            GenerateWorldMapGroundAssets();
            AssetDatabase.Refresh();
        }

        private static void GenerateMainMenuBackdropTexture()
        {
            const int w = 1024;
            const int h = 576;
            Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            Color[] pixels = new Color[w * h];

            for (int y = 0; y < h; y++)
            {
                float ny = y / (float)(h - 1);
                for (int x = 0; x < w; x++)
                {
                    float nx = x / (float)(w - 1);

                    // Base deep vertical tactical slate gradient
                    Color topCol = new Color(0.035f, 0.062f, 0.098f, 1f);
                    Color midCol = new Color(0.055f, 0.094f, 0.142f, 1f);
                    Color botCol = new Color(0.025f, 0.044f, 0.072f, 1f);
                    Color c = ny > 0.45f
                        ? Color.Lerp(midCol, topCol, (ny - 0.45f) / 0.55f)
                        : Color.Lerp(botCol, midCol, ny / 0.45f);

                    // Warm tactical glow on left-center (Story side) and cool emerald glow on right-center (Training side)
                    float dxL = (nx - 0.27f) * 1.45f;
                    float dyL = (ny - 0.48f) * 1.35f;
                    float glowL = Mathf.Exp(-(dxL * dxL + dyL * dyL) * 4.2f);
                    c += new Color(0.11f, 0.078f, 0.024f, 0f) * glowL;

                    float dxR = (nx - 0.73f) * 1.45f;
                    float dyR = (ny - 0.48f) * 1.35f;
                    float glowR = Mathf.Exp(-(dxR * dxR + dyR * dyR) * 4.2f);
                    c += new Color(0.020f, 0.088f, 0.068f, 0f) * glowR;

                    // Subtle topographic contour lines using layered Perlin noise
                    float nVal = Mathf.PerlinNoise(nx * 3.8f + 1.3f, ny * 2.2f + 2.7f) * 0.65f
                               + Mathf.PerlinNoise(nx * 7.6f + 5.1f, ny * 4.4f + 0.9f) * 0.35f;
                    float contourBand = Mathf.Abs(Mathf.Repeat(nVal * 11f, 1f) - 0.5f);
                    if (contourBand < 0.028f)
                    {
                        float lineAlpha = (1f - contourBand / 0.028f) * 0.065f;
                        c = Color.Lerp(c, new Color(0.65f, 0.78f, 0.90f, 1f), lineAlpha);
                    }

                    // Faint tactical coordinate grid
                    int gx = x % 64;
                    int gy = y % 64;
                    if (gx == 0 || gy == 0)
                        c = Color.Lerp(c, new Color(0.55f, 0.72f, 0.88f, 1f), 0.032f);

                    // Edge vignette
                    float vx = (nx - 0.5f) * 2f;
                    float vy = (ny - 0.5f) * 2f;
                    float vig = Mathf.Clamp01(1f - (vx * vx * 0.28f + vy * vy * 0.36f));
                    c.r *= Mathf.Lerp(0.60f, 1f, vig);
                    c.g *= Mathf.Lerp(0.60f, 1f, vig);
                    c.b *= Mathf.Lerp(0.60f, 1f, vig);
                    c.a = 1f;

                    pixels[y * w + x] = c;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            File.WriteAllBytes(BackdropTexPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(BackdropTexPath, ImportAssetOptions.ForceUpdate);
        }

        private static void GenerateStoryMapIconTexture()
        {
            // Clean 128x128 monochrome white compass/map emblem (tinted at runtime by Image.color)
            const int size = 128;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Color[] pixels = new Color[size * size];
            float half = (size - 1) * 0.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - half) / half;
                    float dy = (y - half) / half;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float alpha = 0f;

                    // Outer compass ring
                    float ringDist = Mathf.Abs(r - 0.78f);
                    alpha = Mathf.Max(alpha, Mathf.Clamp01(1f - (ringDist - 0.045f) * 28f));

                    // Cardinal ticks (N/S/E/W)
                    bool onAxisX = Mathf.Abs(dy) < 0.045f && r > 0.62f && r < 0.94f;
                    bool onAxisY = Mathf.Abs(dx) < 0.045f && r > 0.62f && r < 0.94f;
                    if (onAxisX || onAxisY)
                        alpha = Mathf.Max(alpha, 1f);

                    // Tilted compass diamond needle
                    float ax = dx * 0.7071f + dy * 0.7071f;
                    float ay = -dx * 0.7071f + dy * 0.7071f;
                    float needleMetric = Mathf.Abs(ax) * 3.1f + Mathf.Abs(ay) * 1.45f;
                    if (needleMetric < 0.85f)
                        alpha = Mathf.Max(alpha, Mathf.Clamp01((0.85f - needleMetric) * 16f));

                    // Center cutout hole
                    if (r < 0.13f)
                        alpha *= Mathf.Clamp01((r - 0.08f) * 20f);

                    pixels[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(alpha));
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            File.WriteAllBytes(StoryIconTexPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(StoryIconTexPath, ImportAssetOptions.ForceUpdate);
        }

        private static void GenerateWorldMapGroundAssets()
        {
            // Matte natural olive-steppe terrain with subtle tactical grid so empty field feels immediately playable & legible
            const int size = 512;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Color[] pixels = new Color[size * size];

            Color steppeBase = new Color(0.355f, 0.425f, 0.270f, 1f);
            Color steppeDry = new Color(0.430f, 0.445f, 0.305f, 1f);
            Color steppeDark = new Color(0.305f, 0.372f, 0.235f, 1f);
            Color gridLine = new Color(0.54f, 0.60f, 0.46f, 1f);

            for (int y = 0; y < size; y++)
            {
                float ny = y / (float)size;
                for (int x = 0; x < size; x++)
                {
                    float nx = x / (float)size;

                    // Seamless tileable organic grass/steppe variation (no bullseye rings)
                    float nLarge = TileableValueNoise(nx, ny, 4, 17);
                    float nMid = TileableValueNoise(nx, ny, 12, 53);
                    float nFine = TileableValueNoise(nx, ny, 32, 97);

                    Color c = Color.Lerp(steppeDark, steppeBase, nLarge);
                    c = Color.Lerp(c, steppeDry, nMid * 0.30f + nFine * 0.12f);

                    // Subtle major grid line on tile border and faint minor sub-grid at midpoint
                    bool majorLine = x <= 1 || y <= 1 || x >= size - 1 || y >= size - 1;
                    bool minorLine = x == size / 2 || y == size / 2;
                    if (majorLine)
                        c = Color.Lerp(c, gridLine, 0.18f);
                    else if (minorLine)
                        c = Color.Lerp(c, gridLine, 0.06f);

                    c.a = 1f;
                    pixels[y * size + x] = c;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            File.WriteAllBytes(GroundTexPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(GroundTexPath, ImportAssetOptions.ForceUpdate);

            TextureImporter importer = AssetImporter.GetAtPath(GroundTexPath) as TextureImporter;
            if (importer != null)
            {
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.filterMode = FilterMode.Trilinear;
                importer.anisoLevel = 8;
                importer.mipmapEnabled = true;
                importer.SaveAndReimport();
            }

            Texture2D groundTex = AssetDatabase.LoadAssetAtPath<Texture2D>(GroundTexPath);
            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");

            Material groundMat = AssetDatabase.LoadAssetAtPath<Material>(GroundMatPath);
            if (groundMat == null)
            {
                groundMat = new Material(urpLit);
                AssetDatabase.CreateAsset(groundMat, GroundMatPath);
            }
            groundMat.shader = urpLit;
            groundMat.SetTexture("_BaseMap", groundTex);
            groundMat.SetTextureScale("_BaseMap", new Vector2(24f, 24f));
            groundMat.SetColor("_BaseColor", Color.white);
            groundMat.SetFloat("_Smoothness", 0.08f);
            groundMat.SetFloat("_Metallic", 0f);
            EditorUtility.SetDirty(groundMat);

            Material borderMat = AssetDatabase.LoadAssetAtPath<Material>(BorderMatPath);
            if (borderMat == null)
            {
                borderMat = new Material(urpLit);
                AssetDatabase.CreateAsset(borderMat, BorderMatPath);
            }
            borderMat.shader = urpLit;
            borderMat.SetColor("_BaseColor", new Color(0.56f, 0.45f, 0.24f, 1f));
            borderMat.SetFloat("_Smoothness", 0.15f);
            EditorUtility.SetDirty(borderMat);

            Material centerMat = AssetDatabase.LoadAssetAtPath<Material>(CenterMarkerMatPath);
            if (centerMat == null)
            {
                centerMat = new Material(urpLit);
                AssetDatabase.CreateAsset(centerMat, CenterMarkerMatPath);
            }
            centerMat.shader = urpLit;
            centerMat.SetColor("_BaseColor", new Color(0.84f, 0.64f, 0.24f, 1f));
            centerMat.SetFloat("_Smoothness", 0.25f);
            EditorUtility.SetDirty(centerMat);

            AssetDatabase.SaveAssets();
        }

        private static void SpawnWorldMapPlayerCommander(WorldMapCameraController mapCamCtrl)
        {
            GameObject fbxAsset = AssetDatabase.LoadAssetAtPath<GameObject>(CommanderFbxPath);
            if (fbxAsset == null)
            {
                Debug.LogWarning($"[MainMenuAndStorySetupTool] Komutan FBX bulunamadı: {CommanderFbxPath}");
                return;
            }

            EnsureFolder("Assets/Materials/WorldMap", "PlayerCommander");
            EnsureFolder("Assets", "Prefabs");

            // Build clean URP Lit materials for soldier body, banner, pole, and rifle
            Dictionary<string, Material> matLookup = BuildCommanderMaterials();

            GameObject playerRoot = new GameObject("WorldMap_PlayerCommander");
            playerRoot.transform.position = Vector3.zero;
            playerRoot.transform.rotation = Quaternion.identity;

            GameObject visualInstance = (GameObject)PrefabUtility.InstantiatePrefab(fbxAsset);
            visualInstance.name = "CommanderVisual";
            visualInstance.transform.SetParent(playerRoot.transform, false);
            visualInstance.transform.localPosition = Vector3.zero;
            // Baked FBX already faces +Z forward in Unity so LookRotation faces movement direction and flag trails behind
            visualInstance.transform.localRotation = Quaternion.identity;
            // Warband campaign map scale so soldier + banner read clearly from 42m top-down camera
            visualInstance.transform.localScale = Vector3.one * 2.15f;

            Transform bannerClothTr = null;
            MeshRenderer[] renderers = visualInstance.GetComponentsInChildren<MeshRenderer>(true);
            foreach (MeshRenderer mr in renderers)
            {
                if (mr.name == "BannerCloth")
                    bannerClothTr = mr.transform;

                Material[] currentMats = mr.sharedMaterials;
                bool replacedAny = false;
                for (int i = 0; i < currentMats.Length; i++)
                {
                    if (currentMats[i] == null)
                        continue;

                    string matName = currentMats[i].name;
                    if (matLookup.TryGetValue(matName, out Material urpMat))
                    {
                        currentMats[i] = urpMat;
                        replacedAny = true;
                    }
                    else if (mr.name == "AssaultRifle")
                    {
                        currentMats[i] = GetOrCreateRifleFallbackMaterial(matName, currentMats[i]);
                        replacedAny = true;
                    }
                }

                if (replacedAny)
                    mr.sharedMaterials = currentMats;
            }

            WorldMapPlayerParty party = playerRoot.AddComponent<WorldMapPlayerParty>();
            SerializedObject partySo = new SerializedObject(party);
            partySo.FindProperty("mapCamera").objectReferenceValue = mapCamCtrl;
            partySo.FindProperty("visualRoot").objectReferenceValue = visualInstance.transform;
            partySo.FindProperty("bannerClothTransform").objectReferenceValue = bannerClothTr;
            partySo.ApplyModifiedPropertiesWithoutUndo();

            PrefabUtility.SaveAsPrefabAsset(playerRoot, CommanderPrefabPath);
            Debug.Log($"[MainMenuAndStorySetupTool] Silahlı ve bayraklı harita komutanı sahneye eklendi ve prefab kaydedildi: {CommanderPrefabPath}");
        }

        private static Dictionary<string, Material> BuildCommanderMaterials()
        {
            Dictionary<string, Material> dict = new Dictionary<string, Material>();
            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");

            for (int i = 1; i <= 4; i++)
            {
                string key = $"blinn{i}.001";
                string baseTexPath = $"{CommanderTexDir}/{key}_baseColor.png";
                string normTexPath = $"{CommanderTexDir}/{key}_normal.png";

                ConfigureNormalMapImporter(normTexPath);

                Texture2D baseTex = AssetDatabase.LoadAssetAtPath<Texture2D>(baseTexPath);
                Texture2D normTex = AssetDatabase.LoadAssetAtPath<Texture2D>(normTexPath);

                Material mat = LoadOrCreateMaterial($"{CommanderMatDir}/Commander_{key}.mat", urpLit);
                mat.SetTexture("_BaseMap", baseTex);
                mat.SetColor("_BaseColor", Color.white);
                mat.SetFloat("_Smoothness", 0.22f);
                mat.SetFloat("_Metallic", 0.08f);
                if (normTex != null)
                {
                    mat.SetTexture("_BumpMap", normTex);
                    mat.SetFloat("_BumpScale", 0.85f);
                    mat.EnableKeyword("_NORMALMAP");
                }
                EditorUtility.SetDirty(mat);
                dict[key] = mat;
            }

            // Banner cloth material
            Texture2D bannerTex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{CommanderTexDir}/CommanderBanner_BaseColor.png");
            Material bannerMat = LoadOrCreateMaterial($"{CommanderMatDir}/Commander_BannerCloth.mat", urpLit);
            bannerMat.SetTexture("_BaseMap", bannerTex);
            bannerMat.SetColor("_BaseColor", Color.white);
            bannerMat.SetFloat("_Smoothness", 0.14f);
            bannerMat.SetFloat("_Metallic", 0.04f);
            EditorUtility.SetDirty(bannerMat);
            dict["BannerCloth_Mat"] = bannerMat;

            // Pole metal material
            Material poleMat = LoadOrCreateMaterial($"{CommanderMatDir}/Commander_BannerPole.mat", urpLit);
            poleMat.SetColor("_BaseColor", new Color(0.18f, 0.19f, 0.21f, 1f));
            poleMat.SetFloat("_Metallic", 0.82f);
            poleMat.SetFloat("_Smoothness", 0.58f);
            EditorUtility.SetDirty(poleMat);
            dict["BannerPole_Mat"] = poleMat;

            // Gold spearhead material
            Material goldMat = LoadOrCreateMaterial($"{CommanderMatDir}/Commander_BannerGold.mat", urpLit);
            goldMat.SetColor("_BaseColor", new Color(0.90f, 0.70f, 0.22f, 1f));
            goldMat.SetFloat("_Metallic", 0.90f);
            goldMat.SetFloat("_Smoothness", 0.72f);
            EditorUtility.SetDirty(goldMat);
            dict["BannerGold_Mat"] = goldMat;

            AssetDatabase.SaveAssets();
            return dict;
        }

        private static Material GetOrCreateRifleFallbackMaterial(string originalName, Material sourceMat)
        {
            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            string safeName = originalName.Replace(".", "_").Replace(" ", "_");
            Material mat = LoadOrCreateMaterial($"{CommanderMatDir}/Rifle_{safeName}.mat", urpLit);

            Color col = new Color(0.16f, 0.17f, 0.19f, 1f);
            if (sourceMat != null && sourceMat.HasProperty("_BaseColor"))
                col = sourceMat.GetColor("_BaseColor");
            else if (sourceMat != null && sourceMat.HasProperty("_Color"))
                col = sourceMat.color;

            mat.SetColor("_BaseColor", col);
            mat.SetFloat("_Metallic", col.r > 0.35f && col.g < 0.28f ? 0.05f : 0.65f);
            mat.SetFloat("_Smoothness", 0.35f);
            EditorUtility.SetDirty(mat);
            return mat;
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

        private static void ConfigureNormalMapImporter(string texPath)
        {
            TextureImporter importer = AssetImporter.GetAtPath(texPath) as TextureImporter;
            if (importer != null && importer.textureType != TextureImporterType.NormalMap)
            {
                importer.textureType = TextureImporterType.NormalMap;
                importer.SaveAndReimport();
            }
        }

        private static void CreateMapBorderRail(Transform parent, string name, Vector3 pos, Vector3 scale, Material mat)
        {
            GameObject rail = GameObject.CreatePrimitive(PrimitiveType.Cube);
            rail.name = name;
            rail.transform.SetParent(parent, false);
            rail.transform.position = pos;
            rail.transform.localScale = scale;
            rail.isStatic = true;
            Object.DestroyImmediate(rail.GetComponent<Collider>());
            MeshRenderer mr = rail.GetComponent<MeshRenderer>();
            if (mr != null && mat != null)
            {
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
            }
        }

        private static void EnsureEventSystem()
        {
            EventSystem es = Object.FindAnyObjectByType<EventSystem>();
            if (es == null)
            {
                GameObject esGo = new GameObject("EventSystem");
                es = esGo.AddComponent<EventSystem>();
            }

#if ENABLE_INPUT_SYSTEM
            StandaloneInputModule legacy = es.GetComponent<StandaloneInputModule>();
            if (legacy != null)
                Object.DestroyImmediate(legacy);

            InputSystemUIInputModule uiModule = es.GetComponent<InputSystemUIInputModule>();
            if (uiModule == null)
                uiModule = es.gameObject.AddComponent<InputSystemUIInputModule>();

            InputActionAsset actions = AssetDatabase.LoadAssetAtPath<InputActionAsset>("Assets/InputSystem_Actions.inputactions");
            if (actions != null && uiModule.actionsAsset == null)
                uiModule.actionsAsset = actions;
#endif
        }

        private static Sprite LoadSprite(string path, bool isNineSlice)
        {
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                bool dirty = false;
                if (importer.textureType != TextureImporterType.Sprite)
                {
                    importer.textureType = TextureImporterType.Sprite;
                    dirty = true;
                }
                if (importer.spriteImportMode != SpriteImportMode.Single)
                {
                    importer.spriteImportMode = SpriteImportMode.Single;
                    dirty = true;
                }
                if (isNineSlice && importer.spriteBorder != new Vector4(16f, 16f, 16f, 16f))
                {
                    importer.spriteBorder = new Vector4(16f, 16f, 16f, 16f);
                    dirty = true;
                }
                if (dirty)
                    importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        private static GameObject CreateBorderedPanel(string name, Transform parent, Sprite roundedSprite, Color borderColor, Color fillColor, float borderPx)
        {
            GameObject outer = CreateRoundedPanel(name, parent, roundedSprite, borderColor);
            outer.GetComponent<UnityEngine.UI.Image>().raycastTarget = false;

            GameObject inner = CreateRoundedPanel("InnerFill", outer.transform, roundedSprite, fillColor);
            inner.GetComponent<UnityEngine.UI.Image>().raycastTarget = false;
            RectTransform innerRt = inner.GetComponent<RectTransform>();
            innerRt.anchorMin = Vector2.zero;
            innerRt.anchorMax = Vector2.one;
            innerRt.offsetMin = new Vector2(borderPx, borderPx);
            innerRt.offsetMax = new Vector2(-borderPx, -borderPx);

            return outer;
        }

        private static UnityEngine.UI.Button CreateBorderedButton(string name, Transform parent, Sprite roundedSprite, Color borderColor, Color fillColor, Vector2 size, float borderPx)
        {
            GameObject outer = CreateRoundedPanel(name, parent, roundedSprite, borderColor);
            outer.GetComponent<RectTransform>().sizeDelta = size;

            GameObject inner = CreateRoundedPanel("InnerFill", outer.transform, roundedSprite, fillColor);
            inner.GetComponent<UnityEngine.UI.Image>().raycastTarget = false;
            RectTransform innerRt = inner.GetComponent<RectTransform>();
            innerRt.anchorMin = Vector2.zero;
            innerRt.anchorMax = Vector2.one;
            innerRt.offsetMin = new Vector2(borderPx, borderPx);
            innerRt.offsetMax = new Vector2(-borderPx, -borderPx);

            UnityEngine.UI.Button btn = outer.AddComponent<UnityEngine.UI.Button>();
            UnityEngine.UI.ColorBlock cb = btn.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(1.16f, 1.16f, 1.16f, 1f);
            cb.pressedColor = new Color(0.84f, 0.87f, 0.90f, 1f);
            cb.fadeDuration = 0.12f;
            btn.colors = cb;
            return btn;
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

            float ix0 = Mathf.Lerp(c00, c10, sx);
            float ix1 = Mathf.Lerp(c01, c11, sx);
            return Mathf.Lerp(ix0, ix1, sy);
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

        private static GameObject CreateRoundedPanel(string name, Transform parent, Sprite roundedSprite, Color color)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            UnityEngine.UI.Image img = go.AddComponent<UnityEngine.UI.Image>();
            img.sprite = roundedSprite;
            img.type = roundedSprite != null ? UnityEngine.UI.Image.Type.Sliced : UnityEngine.UI.Image.Type.Simple;
            img.color = color;
            return go;
        }

        private static UnityEngine.UI.Button CreateRoundedButton(string name, Transform parent, Sprite roundedSprite, Color bgColor, Vector2 size)
        {
            GameObject go = CreateRoundedPanel(name, parent, roundedSprite, bgColor);
            go.GetComponent<RectTransform>().sizeDelta = size;
            UnityEngine.UI.Button btn = go.AddComponent<UnityEngine.UI.Button>();
            UnityEngine.UI.ColorBlock cb = btn.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(1.14f, 1.14f, 1.14f, 1f);
            cb.pressedColor = new Color(0.84f, 0.87f, 0.90f, 1f);
            cb.fadeDuration = 0.12f;
            btn.colors = cb;
            return btn;
        }

        private static UnityEngine.UI.Text CreateText(string name, Transform parent, Font font, string content, int fontSize, FontStyle style, Color color, TextAnchor alignment)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            UnityEngine.UI.Text txt = go.AddComponent<UnityEngine.UI.Text>();
            txt.font = font;
            txt.text = content;
            txt.fontSize = fontSize;
            txt.fontStyle = style;
            txt.color = color;
            txt.alignment = alignment;
            txt.raycastTarget = false;
            return txt;
        }

        private static void AddDropShadow(GameObject target, Color color, Vector2 distance)
        {
            UnityEngine.UI.Shadow shadow = target.AddComponent<UnityEngine.UI.Shadow>();
            shadow.effectColor = color;
            shadow.effectDistance = distance;
        }

        private static void StretchFull(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);
        }

        private static void AddSceneIfExists(List<EditorBuildSettingsScene> list, string scenePath)
        {
            if (File.Exists(scenePath))
                list.Add(new EditorBuildSettingsScene(scenePath, true));
        }

        private static void EnsureFolder(string parent, string child)
        {
            string full = $"{parent}/{child}";
            if (!AssetDatabase.IsValidFolder(full))
                AssetDatabase.CreateFolder(parent, child);
        }
    }
}
