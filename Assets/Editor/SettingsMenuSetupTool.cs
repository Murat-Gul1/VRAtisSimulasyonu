using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using VRAtis.Ballistics;
using VRAtis.Environment;
using VRAtis.Hardware;
using VRAtis.Targets;
using VRAtis.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
#endif

namespace VRAtis.EditorTools
{
    [InitializeOnLoad]
    public static class SettingsMenuSetupTool
    {
        private const string UiTexDir = "Assets/UI/Textures";
        private const string CanvasName = "SettingsUI_Canvas";
        private const string AutoSetupVersionKey = "VRAtis_SettingsUI_V5_Done";

        private static readonly Color PanelBgColor = new Color(0.06f, 0.09f, 0.13f, 0.90f);
        private static readonly Color HeaderBgColor = new Color(0.09f, 0.14f, 0.20f, 0.96f);
        private static readonly Color CardBgColor = new Color(0.11f, 0.16f, 0.22f, 0.92f);
        private static readonly Color AccentColor = new Color(0.24f, 0.72f, 0.56f, 1.0f);
        private static readonly Color TextPrimaryColor = new Color(0.94f, 0.96f, 0.98f, 1.0f);
        private static readonly Color TextMutedColor = new Color(0.65f, 0.73f, 0.80f, 1.0f);
        private static readonly Color DividerColor = new Color(1f, 1f, 1f, 0.10f);

        static SettingsMenuSetupTool()
        {
            EditorApplication.delayCall += TryAutoSetupOnce;
        }

        private static void TryAutoSetupOnce()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || Application.isBatchMode)
                return;

            if (GameObject.Find(CanvasName) != null && EditorPrefs.GetBool(AutoSetupVersionKey, false))
                return;

            if (AssetDatabase.LoadAssetAtPath<Texture2D>($"{UiTexDir}/GearIcon.png") == null)
                return;

            SetupSettingsMenu();
        }

        [MenuItem("VRAtis/Ayarlar Menüsünü Kur")]
        public static void SetupSettingsMenu()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            EnsureEventSystem();

            Sprite roundedSprite = LoadSprite($"{UiTexDir}/RoundedPanel_9Slice.png", isNineSlice: true);
            Sprite gearSprite = LoadSprite($"{UiTexDir}/GearIcon.png", isNineSlice: false);
            Sprite weatherSprite = LoadSprite($"{UiTexDir}/WeatherIcon.png", isNineSlice: false);
            Sprite targetSprite = LoadSprite($"{UiTexDir}/TargetIcon.png", isNineSlice: false) ?? gearSprite;
            Sprite radioRingSprite = LoadSprite($"{UiTexDir}/RadioRing.png", isNineSlice: false);
            Sprite radioDotSprite = LoadSprite($"{UiTexDir}/RadioDot.png", isNineSlice: false);
            Sprite checkRingSprite = LoadSprite($"{UiTexDir}/CheckBoxRing.png", isNineSlice: false);
            Sprite checkMarkSprite = LoadSprite($"{UiTexDir}/CheckMark.png", isNineSlice: false);
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            GameObject existing = GameObject.Find(CanvasName);
            if (existing != null)
                Object.DestroyImmediate(existing);

            GameObject canvasGo = new GameObject(CanvasName);
            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 50;

            CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            canvasGo.AddComponent<GraphicRaycaster>();

            GameObject primaryRootGo = new GameObject("PrimaryEye_UIRoot");
            primaryRootGo.transform.SetParent(canvasGo.transform, false);
            RectTransform primaryRootRt = primaryRootGo.AddComponent<RectTransform>();
            StretchFull(primaryRootRt);

            // 1. Bottom-Right Gear Icon Button (repositioned symmetrically per-eye when VR split-screen is active)
            Button gearBtn = CreateRoundedButton(
                "BottomRight_SettingsButton",
                primaryRootGo.transform,
                roundedSprite,
                PanelBgColor,
                new Vector2(64f, 64f));
            RectTransform gearRt = gearBtn.GetComponent<RectTransform>();
            gearRt.anchorMin = new Vector2(1f, 0f);
            gearRt.anchorMax = new Vector2(1f, 0f);
            gearRt.pivot = new Vector2(1f, 0f);
            gearRt.anchoredPosition = new Vector2(-28f, 28f);

            GameObject gearIconGo = new GameObject("GearIcon");
            gearIconGo.transform.SetParent(gearBtn.transform, false);
            Image gearIconImg = gearIconGo.AddComponent<Image>();
            gearIconImg.sprite = gearSprite;
            gearIconImg.color = TextPrimaryColor;
            gearIconImg.raycastTarget = false;
            RectTransform gearIconRt = gearIconGo.GetComponent<RectTransform>();
            gearIconRt.anchorMin = new Vector2(0.5f, 0.5f);
            gearIconRt.anchorMax = new Vector2(0.5f, 0.5f);
            gearIconRt.sizeDelta = new Vector2(36f, 36f);

            // 2. Settings Window Root (docked bottom-right on single screen, centered per-eye in VR)
            GameObject windowGo = CreateRoundedPanel("SettingsWindow", primaryRootGo.transform, roundedSprite, PanelBgColor);
            RectTransform winRt = windowGo.GetComponent<RectTransform>();
            winRt.anchorMin = new Vector2(1f, 0f);
            winRt.anchorMax = new Vector2(1f, 0f);
            winRt.pivot = new Vector2(1f, 0f);
            winRt.anchoredPosition = new Vector2(-28f, 104f);
            winRt.sizeDelta = new Vector2(430f, 560f);

            // Header Bar
            GameObject headerGo = CreateRoundedPanel("HeaderBar", windowGo.transform, roundedSprite, HeaderBgColor);
            RectTransform headerRt = headerGo.GetComponent<RectTransform>();
            headerRt.anchorMin = new Vector2(0f, 1f);
            headerRt.anchorMax = new Vector2(1f, 1f);
            headerRt.pivot = new Vector2(0.5f, 1f);
            headerRt.anchoredPosition = Vector2.zero;
            headerRt.sizeDelta = new Vector2(0f, 52f);

            Text titleTxt = CreateText("TitleText", headerGo.transform, font, "AYARLAR", 20, FontStyle.Bold, TextPrimaryColor, TextAnchor.MiddleLeft);
            RectTransform titleRt = titleTxt.GetComponent<RectTransform>();
            titleRt.anchorMin = new Vector2(0f, 0f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.offsetMin = new Vector2(20f, 0f);
            titleRt.offsetMax = new Vector2(-56f, 0f);

            Button closeBtn = CreateRoundedButton("CloseButton", headerGo.transform, roundedSprite, new Color(1f, 1f, 1f, 0.08f), new Vector2(34f, 34f));
            RectTransform closeRt = closeBtn.GetComponent<RectTransform>();
            closeRt.anchorMin = new Vector2(1f, 0.5f);
            closeRt.anchorMax = new Vector2(1f, 0.5f);
            closeRt.pivot = new Vector2(1f, 0.5f);
            closeRt.anchoredPosition = new Vector2(-10f, 0f);
            Text closeX = CreateText("X", closeBtn.transform, font, "✕", 18, FontStyle.Bold, TextPrimaryColor, TextAnchor.MiddleCenter);
            StretchFull(closeX.GetComponent<RectTransform>());

            // Content Area beneath Header
            GameObject contentGo = new GameObject("ContentArea");
            contentGo.transform.SetParent(windowGo.transform, false);
            RectTransform contentRt = contentGo.AddComponent<RectTransform>();
            contentRt.anchorMin = Vector2.zero;
            contentRt.anchorMax = Vector2.one;
            contentRt.offsetMin = new Vector2(16f, 14f);
            contentRt.offsetMax = new Vector2(-16f, -62f);

            // Page 1: Main Categories ("Hava Durumu")
            GameObject mainPage = new GameObject("MainCategoryPage");
            mainPage.transform.SetParent(contentGo.transform, false);
            StretchFull(mainPage.AddComponent<RectTransform>());
            VerticalLayoutGroup mainLayout = mainPage.AddComponent<VerticalLayoutGroup>();
            mainLayout.spacing = 10f;
            mainLayout.childAlignment = TextAnchor.UpperCenter;
            mainLayout.childControlWidth = true;
            mainLayout.childControlHeight = true;
            mainLayout.childForceExpandWidth = true;
            mainLayout.childForceExpandHeight = false;

            Button weatherMenuBtn = CreateCategoryButton(
                "WeatherMenuButton",
                mainPage.transform,
                roundedSprite,
                weatherSprite,
                font,
                "Hava Durumu",
                "Yağmur, sis ve günün saati ayarları");

            CreateCategoryButton(
                "OpticalMenuButton",
                mainPage.transform,
                roundedSprite,
                gearSprite,
                font,
                "Optik Takip (Kamera & LED)",
                "2 LED ile tüfek 3B konum takibi & kalibrasyon");

            CreateCategoryButton(
                "TargetsMenuButton",
                mainPage.transform,
                roundedSprite,
                targetSprite,
                font,
                "Hedef",
                "Hedef mesafesi, sayısı (0-6) ve yan yana aralık ayarı");

            CreateVrCalibrationCard(mainPage.transform, roundedSprite, checkRingSprite, checkMarkSprite, font);

            // Page 2: Weather Selection Sub-Panel
            GameObject weatherPage = CreateSubPageContainer("WeatherCategoryPage", contentGo.transform, 5f);
            CreateSubPageHeader("WeatherSubHeader", weatherPage.transform, roundedSprite, font, "HAVA DURUMU");

            CreateSectionLabel("ConditionHeader", weatherPage.transform, font, "HAVA KOŞULLARI (Çoklu Seçim)");
            Toggle rainToggle = CreateToggleRow("RainCheckbox", weatherPage.transform, roundedSprite, checkRingSprite, checkMarkSprite, font, "Yağmurlu Hava", null, 36f);
            Toggle fogToggle = CreateToggleRow("FogCheckbox", weatherPage.transform, roundedSprite, checkRingSprite, checkMarkSprite, font, "Sisli Hava", null, 36f);

            CreateDivider("Divider_1", weatherPage.transform);
            CreateSectionLabel("TimeHeader", weatherPage.transform, font, "GÜNÜN ZAMANI (1 - 5 Tuşları)");

            ToggleGroup timeGroup = weatherPage.AddComponent<ToggleGroup>();
            timeGroup.allowSwitchOff = false;

            string[] presetLabels =
            {
                "[1] Sabah (Gün Doğumu - 07:12)",
                "[2] Öğle (Güneşli Gündüz - 12:30)",
                "[3] İkindi (Altın Saat - 17:06)",
                "[4] Alacakaranlık (18:36)",
                "[5] Gece (Yıldızlı - 21:30)"
            };

            Toggle[] timeToggles = new Toggle[presetLabels.Length];
            for (int i = 0; i < presetLabels.Length; i++)
                timeToggles[i] = CreateToggleRow($"TimePreset_{i + 1}", weatherPage.transform, roundedSprite, radioRingSprite, radioDotSprite, font, presetLabels[i], timeGroup, 34f);

            CreateDivider("Divider_2", weatherPage.transform);
            Toggle autoTimeToggle = CreateToggleRow("AutoTimeCheckbox", weatherPage.transform, roundedSprite, checkRingSprite, checkMarkSprite, font, "[T] Otomatik Zaman Akışı", null, 34f);

            // Page 3: Optical Camera & LED Tracker Sub-Panel
            GameObject opticalPage = BuildOpticalCategoryPage(contentGo.transform, roundedSprite, checkRingSprite, checkMarkSprite, font);

            // Page 4: Sport Drill & Range Targets Sub-Panel ("Hedef")
            GameObject targetPage = BuildTargetCategoryPage(contentGo.transform, roundedSprite, font);

            TargetRangeManager rangeMgr = SteelGongSetupTool.EnsureTargetRangeManager(null, null);

            // Wire controller onto Canvas root
            SettingsMenuController menuCtrl = canvasGo.AddComponent<SettingsMenuController>();
            SerializedObject so = new SerializedObject(menuCtrl);
            so.FindProperty("timeOfDayController").objectReferenceValue = Object.FindAnyObjectByType<DynamicTimeOfDay>();
            so.FindProperty("environmentManager").objectReferenceValue = Object.FindAnyObjectByType<EnvironmentManager>();
            so.FindProperty("stereoRig").objectReferenceValue = Object.FindAnyObjectByType<CardboardStereoRig>();
            so.FindProperty("opticalTracker").objectReferenceValue = Object.FindAnyObjectByType<OpticalRifleTracker>();
            so.FindProperty("targetRangeManager").objectReferenceValue = rangeMgr;
            so.FindProperty("primaryEyeRoot").objectReferenceValue = primaryRootRt;
            so.ApplyModifiedPropertiesWithoutUndo();

            weatherPage.SetActive(false);
            opticalPage.SetActive(false);
            targetPage.SetActive(false);
            windowGo.SetActive(false);

            EditorUtility.SetDirty(canvasGo);
            EditorSceneManager.MarkSceneDirty(canvasGo.scene);
            EditorSceneManager.SaveOpenScenes();
            EditorPrefs.SetBool(AutoSetupVersionKey, true);
            Debug.Log("[SettingsMenuSetupTool] V5 Kuruldu: Hedef Drill Menüsü (0-6 hedef, 20-200m+ mesafe, 1-30m aralık) + VR Stereo Ayarlar!");
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

        private static GameObject CreateRoundedPanel(string name, Transform parent, Sprite roundedSprite, Color color)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            Image img = go.AddComponent<Image>();
            img.sprite = roundedSprite;
            img.type = Image.Type.Sliced;
            img.color = color;
            return go;
        }

        private static Button CreateRoundedButton(string name, Transform parent, Sprite roundedSprite, Color bgColor, Vector2 size)
        {
            GameObject go = CreateRoundedPanel(name, parent, roundedSprite, bgColor);
            go.GetComponent<RectTransform>().sizeDelta = size;
            Button btn = go.AddComponent<Button>();
            ColorBlock cb = btn.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(1.18f, 1.18f, 1.18f, 1f);
            cb.pressedColor = new Color(0.80f, 0.85f, 0.90f, 1f);
            btn.colors = cb;
            return btn;
        }

        private static Button CreateCategoryButton(string name, Transform parent, Sprite roundedSprite, Sprite iconSprite, Font font, string title, string subtitle)
        {
            Button btn = CreateRoundedButton(name, parent, roundedSprite, CardBgColor, new Vector2(0f, 64f));
            LayoutElement catLe = btn.gameObject.AddComponent<LayoutElement>();
            catLe.minHeight = 64f;
            catLe.preferredHeight = 64f;

            GameObject iconGo = new GameObject("Icon");
            iconGo.transform.SetParent(btn.transform, false);
            Image iconImg = iconGo.AddComponent<Image>();
            iconImg.sprite = iconSprite;
            iconImg.color = AccentColor;
            iconImg.raycastTarget = false;
            RectTransform iconRt = iconGo.GetComponent<RectTransform>();
            iconRt.anchorMin = new Vector2(0f, 0.5f);
            iconRt.anchorMax = new Vector2(0f, 0.5f);
            iconRt.pivot = new Vector2(0f, 0.5f);
            iconRt.anchoredPosition = new Vector2(14f, 0f);
            iconRt.sizeDelta = new Vector2(32f, 32f);

            Text titleTxt = CreateText("Title", btn.transform, font, title, 18, FontStyle.Bold, TextPrimaryColor, TextAnchor.UpperLeft);
            RectTransform tRt = titleTxt.GetComponent<RectTransform>();
            tRt.anchorMin = new Vector2(0f, 0f);
            tRt.anchorMax = new Vector2(1f, 1f);
            tRt.offsetMin = new Vector2(58f, 8f);
            tRt.offsetMax = new Vector2(-36f, -10f);

            Text subTxt = CreateText("Subtitle", btn.transform, font, subtitle, 13, FontStyle.Normal, TextMutedColor, TextAnchor.LowerLeft);
            RectTransform sRt = subTxt.GetComponent<RectTransform>();
            sRt.anchorMin = new Vector2(0f, 0f);
            sRt.anchorMax = new Vector2(1f, 1f);
            sRt.offsetMin = new Vector2(58f, 10f);
            sRt.offsetMax = new Vector2(-36f, -8f);

            Text arrowTxt = CreateText("Arrow", btn.transform, font, "›", 24, FontStyle.Bold, TextMutedColor, TextAnchor.MiddleRight);
            RectTransform aRt = arrowTxt.GetComponent<RectTransform>();
            aRt.anchorMin = new Vector2(1f, 0f);
            aRt.anchorMax = new Vector2(1f, 1f);
            aRt.pivot = new Vector2(1f, 0.5f);
            aRt.anchoredPosition = new Vector2(-14f, 0f);
            aRt.sizeDelta = new Vector2(24f, 0f);

            return btn;
        }

        private static void CreateVrCalibrationCard(
            Transform parent,
            Sprite roundedSprite,
            Sprite boxSprite,
            Sprite markSprite,
            Font font)
        {
            GameObject cardGo = CreateRoundedPanel("VRCalibrationCard", parent, roundedSprite, CardBgColor);
            LayoutElement cardLe = cardGo.AddComponent<LayoutElement>();
            cardLe.minHeight = 142f;
            cardLe.preferredHeight = 142f;

            VerticalLayoutGroup cardLayout = cardGo.AddComponent<VerticalLayoutGroup>();
            cardLayout.padding = new RectOffset(12, 12, 10, 10);
            cardLayout.spacing = 8f;
            cardLayout.childControlWidth = true;
            cardLayout.childControlHeight = true;
            cardLayout.childForceExpandWidth = true;
            cardLayout.childForceExpandHeight = false;

            GameObject headerRow = CreateRowContainer("VrHeaderRow", cardGo.transform, 22f);
            Text headerLbl = CreateText("VrTitleText", headerRow.transform, font, "VR GÖZLÜK & MERCEK", 13, FontStyle.Bold, AccentColor, TextAnchor.MiddleLeft);
            StretchFull(headerLbl.GetComponent<RectTransform>());
            Text screenLbl = CreateText("VrScreenWidthText", headerRow.transform, font, "Ekran: 154 mm", 12, FontStyle.Normal, TextMutedColor, TextAnchor.MiddleRight);
            StretchFull(screenLbl.GetComponent<RectTransform>());

            GameObject lensRow = CreateRowContainer("LensControlRow", cardGo.transform, 36f);
            HorizontalLayoutGroup hLayout = lensRow.AddComponent<HorizontalLayoutGroup>();
            hLayout.spacing = 8f;
            hLayout.childControlWidth = true;
            hLayout.childControlHeight = true;
            hLayout.childForceExpandWidth = false;
            hLayout.childForceExpandHeight = true;

            CreateCompactButton("LensMinusButton", lensRow.transform, roundedSprite, HeaderBgColor, font, "-1", 54f);

            GameObject valContainer = new GameObject("LensValueText");
            valContainer.transform.SetParent(lensRow.transform, false);
            Text valTxt = valContainer.AddComponent<Text>();
            valTxt.font = font;
            valTxt.text = "66 mm";
            valTxt.fontSize = 16;
            valTxt.fontStyle = FontStyle.Bold;
            valTxt.color = TextPrimaryColor;
            valTxt.alignment = TextAnchor.MiddleCenter;
            valTxt.raycastTarget = false;
            LayoutElement valLe = valContainer.AddComponent<LayoutElement>();
            valLe.flexibleWidth = 1f;
            valLe.minWidth = 80f;

            CreateCompactButton("LensPlusButton", lensRow.transform, roundedSprite, HeaderBgColor, font, "+1", 54f);
            CreateCompactButton("LensResetButton", lensRow.transform, roundedSprite, new Color(0.14f, 0.28f, 0.25f, 0.95f), font, "66 mm", 76f);

            Toggle vrSplit = CreateToggleRow("VrSplitCheckbox", cardGo.transform, roundedSprite, boxSprite, markSprite, font, "VR Çift Ekran Bölme (F2)", null, 34f);
            Image splitBg = vrSplit.GetComponent<Image>();
            if (splitBg != null)
                splitBg.color = HeaderBgColor;
        }

        private static GameObject CreateSubPageContainer(string name, Transform parent, float spacing)
        {
            GameObject page = new GameObject(name);
            page.transform.SetParent(parent, false);
            StretchFull(page.AddComponent<RectTransform>());
            VerticalLayoutGroup layout = page.AddComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            return page;
        }

        private static void CreateSubPageHeader(string name, Transform parent, Sprite roundedSprite, Font font, string title)
        {
            GameObject subHeader = CreateRowContainer(name, parent, 38f);
            Button backBtn = CreateRoundedButton("BackButton", subHeader.transform, roundedSprite, CardBgColor, new Vector2(86f, 34f));
            RectTransform backRt = backBtn.GetComponent<RectTransform>();
            backRt.anchorMin = new Vector2(0f, 0.5f);
            backRt.anchorMax = new Vector2(0f, 0.5f);
            backRt.pivot = new Vector2(0f, 0.5f);
            backRt.anchoredPosition = Vector2.zero;
            Text backLbl = CreateText("Label", backBtn.transform, font, "← Geri", 15, FontStyle.Bold, TextPrimaryColor, TextAnchor.MiddleCenter);
            StretchFull(backLbl.GetComponent<RectTransform>());

            Text titleLbl = CreateText("SubTitle", subHeader.transform, font, title, 16, FontStyle.Bold, AccentColor, TextAnchor.MiddleRight);
            RectTransform tRt = titleLbl.GetComponent<RectTransform>();
            tRt.anchorMin = Vector2.zero;
            tRt.anchorMax = Vector2.one;
            tRt.offsetMin = new Vector2(96f, 0f);
            tRt.offsetMax = Vector2.zero;
        }

        private static GameObject BuildOpticalCategoryPage(
            Transform parent,
            Sprite roundedSprite,
            Sprite boxSprite,
            Sprite markSprite,
            Font font)
        {
            GameObject page = CreateSubPageContainer("OpticalCategoryPage", parent, 6f);
            CreateSubPageHeader("OpticSubHeader", page.transform, roundedSprite, font, "OPTİK TAKİP (LED)");

            GameObject previewRow = CreateRowContainer("OpticPreviewRow", page.transform, 118f);
            HorizontalLayoutGroup previewLayout = previewRow.AddComponent<HorizontalLayoutGroup>();
            previewLayout.spacing = 10f;
            previewLayout.childControlWidth = true;
            previewLayout.childControlHeight = true;
            previewLayout.childForceExpandWidth = false;
            previewLayout.childForceExpandHeight = true;

            GameObject frameGo = CreateRoundedPanel("PreviewFrame", previewRow.transform, roundedSprite, new Color(0.03f, 0.05f, 0.08f, 1f));
            LayoutElement frameLe = frameGo.AddComponent<LayoutElement>();
            frameLe.minWidth = 152f;
            frameLe.preferredWidth = 152f;

            GameObject rawGo = new GameObject("OpticPreviewImage");
            rawGo.transform.SetParent(frameGo.transform, false);
            RawImage rawImg = rawGo.AddComponent<RawImage>();
            rawImg.color = new Color(0.85f, 0.90f, 0.96f, 1f);
            rawImg.raycastTarget = false;
            RectTransform rawRt = rawImg.GetComponent<RectTransform>();
            rawRt.anchorMin = Vector2.zero;
            rawRt.anchorMax = Vector2.one;
            rawRt.offsetMin = new Vector2(4f, 4f);
            rawRt.offsetMax = new Vector2(-4f, -4f);

            GameObject statusCol = new GameObject("StatusColumn");
            statusCol.transform.SetParent(previewRow.transform, false);
            LayoutElement colLe = statusCol.AddComponent<LayoutElement>();
            colLe.flexibleWidth = 1f;
            VerticalLayoutGroup colLayout = statusCol.AddComponent<VerticalLayoutGroup>();
            colLayout.spacing = 6f;
            colLayout.childControlWidth = true;
            colLayout.childControlHeight = true;
            colLayout.childForceExpandWidth = true;
            colLayout.childForceExpandHeight = false;

            GameObject statusBox = CreateRoundedPanel("StatusBox", statusCol.transform, roundedSprite, CardBgColor);
            LayoutElement boxLe = statusBox.AddComponent<LayoutElement>();
            boxLe.flexibleHeight = 1f;
            boxLe.minHeight = 72f;
            Text statusTxt = CreateText("OpticStatusText", statusBox.transform, font, "Kamera bekleniyor...\nLED A: YOK | LED B: YOK\nMesafe: -- cm", 13, FontStyle.Normal, TextPrimaryColor, TextAnchor.MiddleLeft);
            RectTransform stRt = statusTxt.GetComponent<RectTransform>();
            stRt.anchorMin = Vector2.zero;
            stRt.anchorMax = Vector2.one;
            stRt.offsetMin = new Vector2(10f, 6f);
            stRt.offsetMax = new Vector2(-10f, -6f);

            Button zeroBtn = CreateRoundedButton("OpticZeroButton", statusCol.transform, roundedSprite, new Color(0.14f, 0.28f, 0.25f, 0.95f), new Vector2(0f, 34f));
            LayoutElement zeroLe = zeroBtn.gameObject.AddComponent<LayoutElement>();
            zeroLe.minHeight = 34f;
            zeroLe.preferredHeight = 34f;
            Text zeroTxt = CreateText("Label", zeroBtn.transform, font, "Mevcut Konumu Sıfırla", 13, FontStyle.Bold, TextPrimaryColor, TextAnchor.MiddleCenter);
            StretchFull(zeroTxt.GetComponent<RectTransform>());

            CreateToggleRow("OpticEnableCheckbox", page.transform, roundedSprite, boxSprite, markSprite, font, "Optik 3B Konum Takibi Açık", null, 34f);

            Button modeBtn = CreateRoundedButton("OpticModeButton", page.transform, roundedSprite, CardBgColor, new Vector2(0f, 34f));
            LayoutElement modeLe = modeBtn.gameObject.AddComponent<LayoutElement>();
            modeLe.minHeight = 34f;
            modeLe.preferredHeight = 34f;
            Text modeLbl = CreateText("Label", modeBtn.transform, font, "Hedef Modu: Yeşil + Kırmızı", 14, FontStyle.Bold, TextPrimaryColor, TextAnchor.MiddleCenter);
            StretchFull(modeLbl.GetComponent<RectTransform>());

            Button axisBtn = CreateRoundedButton("OpticAxisButton", page.transform, roundedSprite, CardBgColor, new Vector2(0f, 34f));
            LayoutElement axisLe = axisBtn.gameObject.AddComponent<LayoutElement>();
            axisLe.minHeight = 34f;
            axisLe.preferredHeight = 34f;
            Text axisLbl = CreateText("Label", axisBtn.transform, font, "LED Dizilimi: Yatay (Sağ-Sol)", 14, FontStyle.Normal, TextPrimaryColor, TextAnchor.MiddleCenter);
            StretchFull(axisLbl.GetComponent<RectTransform>());

            CreateStepAdjustRow("OpticSpacingRow", page.transform, roundedSprite, font, "SpacingMinusButton", "SpacingValueText", "SpacingPlusButton", "-2 cm", "LED Aralığı: 14 cm", "+2 cm");
            CreateStepAdjustRow("OpticDomRow", page.transform, roundedSprite, font, "DomMinusButton", "DomValueText", "DomPlusButton", "-", "Renk Eşiği: 36", "+");

            return page;
        }

        private static GameObject BuildTargetCategoryPage(Transform parent, Sprite roundedSprite, Font font)
        {
            GameObject page = CreateSubPageContainer("TargetCategoryPage", parent, 6f);
            CreateSubPageHeader("TargetSubHeader", page.transform, roundedSprite, font, "HEDEF VE DRİL AYARLARI");

            GameObject summaryBox = CreateRoundedPanel("TargetSummaryBox", page.transform, roundedSprite, HeaderBgColor);
            LayoutElement sumLe = summaryBox.AddComponent<LayoutElement>();
            sumLe.minHeight = 34f;
            sumLe.preferredHeight = 34f;
            Text sumTxt = CreateText("TargetSummaryText", summaryBox.transform, font, "Aktif: Klasik Poligon (25 / 100 / 300 / 500 m)", 13, FontStyle.Bold, TextPrimaryColor, TextAnchor.MiddleCenter);
            StretchFull(sumTxt.GetComponent<RectTransform>());

            // 1. Target Count (0..6)
            CreateSectionLabel("TargetCountHeader", page.transform, font, "HEDEF SAYISI (0 - 6)");
            CreateChipRow("TargetCountRow", page.transform, roundedSprite, font, 34f,
                new[] { ("CountBtn_0", "0"), ("CountBtn_1", "1"), ("CountBtn_2", "2"), ("CountBtn_3", "3"), ("CountBtn_4", "4"), ("CountBtn_5", "5"), ("CountBtn_6", "6") });

            CreateDivider("TargetDiv1", page.transform);

            // 2. Target Distance (20, 25, 50, 100, 200m + -5/+5m fine adjust)
            CreateSectionLabel("TargetDistHeader", page.transform, font, "HEDEF MESAFESİ");
            CreateChipRow("TargetDistPresetRow", page.transform, roundedSprite, font, 34f,
                new[] { ("DistBtn_20", "20m"), ("DistBtn_25", "25m"), ("DistBtn_50", "50m"), ("DistBtn_100", "100m"), ("DistBtn_200", "200m") });
            CreateStepAdjustRow("TargetDistStepRow", page.transform, roundedSprite, font, "DistMinusButton", "DistValueText", "DistPlusButton", "-5 m", "25 m", "+5 m");

            CreateDivider("TargetDiv2", page.transform);

            // 3. Target Spacing (1..30m, default 5m)
            CreateSectionLabel("TargetSpacingHeader", page.transform, font, "HEDEF ARALIĞI (YAN YANA: 1 - 30 m)");
            CreateChipRow("TargetSpacingPresetRow", page.transform, roundedSprite, font, 34f,
                new[] { ("SpacingBtn_1", "1m"), ("SpacingBtn_3", "3m"), ("SpacingBtn_5", "5m"), ("SpacingBtn_10", "10m"), ("SpacingBtn_20", "20m"), ("SpacingBtn_30", "30m") });
            CreateStepAdjustRow("TargetSpacingStepRow", page.transform, roundedSprite, font, "SpacingMinusButton", "SpacingValueText", "SpacingPlusButton", "-1 m", "5 m aralık", "+1 m");

            CreateDivider("TargetDiv3", page.transform);

            // 4. Utility Row: Classic Multi-Distance Range vs Clear Marks
            GameObject utilRow = CreateRowContainer("TargetUtilityRow", page.transform, 36f);
            HorizontalLayoutGroup utilLayout = utilRow.AddComponent<HorizontalLayoutGroup>();
            utilLayout.spacing = 8f;
            utilLayout.childControlWidth = true;
            utilLayout.childControlHeight = true;
            utilLayout.childForceExpandWidth = true;
            utilLayout.childForceExpandHeight = true;

            Button classicBtn = CreateRoundedButton("ClassicRangeButton", utilRow.transform, roundedSprite, AccentColor, Vector2.zero);
            Text classicLbl = CreateText("Label", classicBtn.transform, font, "Klasik Poligon (25-500m)", 13, FontStyle.Bold, TextPrimaryColor, TextAnchor.MiddleCenter);
            StretchFull(classicLbl.GetComponent<RectTransform>());

            Button clearBtn = CreateRoundedButton("ClearMarksButton", utilRow.transform, roundedSprite, CardBgColor, Vector2.zero);
            Text clearLbl = CreateText("Label", clearBtn.transform, font, "İzleri Sıfırla", 13, FontStyle.Bold, TextPrimaryColor, TextAnchor.MiddleCenter);
            StretchFull(clearLbl.GetComponent<RectTransform>());

            return page;
        }

        private static void CreateChipRow(string rowName, Transform parent, Sprite roundedSprite, Font font, float height, (string btnName, string label)[] chips)
        {
            GameObject row = CreateRowContainer(rowName, parent, height);
            HorizontalLayoutGroup hLayout = row.AddComponent<HorizontalLayoutGroup>();
            hLayout.spacing = 5f;
            hLayout.childControlWidth = true;
            hLayout.childControlHeight = true;
            hLayout.childForceExpandWidth = true;
            hLayout.childForceExpandHeight = true;

            for (int i = 0; i < chips.Length; i++)
            {
                Button btn = CreateRoundedButton(chips[i].btnName, row.transform, roundedSprite, CardBgColor, Vector2.zero);
                Text txt = CreateText("Label", btn.transform, font, chips[i].label, 13, FontStyle.Bold, TextPrimaryColor, TextAnchor.MiddleCenter);
                StretchFull(txt.GetComponent<RectTransform>());
            }
        }

        private static void CreateStepAdjustRow(
            string rowName,
            Transform parent,
            Sprite roundedSprite,
            Font font,
            string minusBtnName,
            string valueTxtName,
            string plusBtnName,
            string minusLabel,
            string initialValue,
            string plusLabel)
        {
            GameObject row = CreateRowContainer(rowName, parent, 34f);
            HorizontalLayoutGroup hLayout = row.AddComponent<HorizontalLayoutGroup>();
            hLayout.spacing = 8f;
            hLayout.childControlWidth = true;
            hLayout.childControlHeight = true;
            hLayout.childForceExpandWidth = false;
            hLayout.childForceExpandHeight = true;

            CreateCompactButton(minusBtnName, row.transform, roundedSprite, CardBgColor, font, minusLabel, 72f);

            GameObject valGo = CreateRoundedPanel("ValueBox", row.transform, roundedSprite, HeaderBgColor);
            LayoutElement valLe = valGo.AddComponent<LayoutElement>();
            valLe.flexibleWidth = 1f;
            Text valTxt = CreateText(valueTxtName, valGo.transform, font, initialValue, 14, FontStyle.Bold, TextPrimaryColor, TextAnchor.MiddleCenter);
            StretchFull(valTxt.GetComponent<RectTransform>());

            CreateCompactButton(plusBtnName, row.transform, roundedSprite, CardBgColor, font, plusLabel, 72f);
        }

        private static Button CreateCompactButton(string name, Transform parent, Sprite roundedSprite, Color bgColor, Font font, string label, float width)
        {
            Button btn = CreateRoundedButton(name, parent, roundedSprite, bgColor, new Vector2(width, 36f));
            LayoutElement le = btn.gameObject.AddComponent<LayoutElement>();
            le.minWidth = width;
            le.preferredWidth = width;
            Text txt = CreateText("Label", btn.transform, font, label, 14, FontStyle.Bold, TextPrimaryColor, TextAnchor.MiddleCenter);
            StretchFull(txt.GetComponent<RectTransform>());
            return btn;
        }

        private static Toggle CreateToggleRow(
            string name,
            Transform parent,
            Sprite roundedSprite,
            Sprite boxSprite,
            Sprite markSprite,
            Font font,
            string labelText,
            ToggleGroup group,
            float rowHeight)
        {
            GameObject rowGo = CreateRoundedPanel(name, parent, roundedSprite, CardBgColor);
            rowGo.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, rowHeight);
            LayoutElement rowLe = rowGo.AddComponent<LayoutElement>();
            rowLe.minHeight = rowHeight;
            rowLe.preferredHeight = rowHeight;

            Toggle toggle = rowGo.AddComponent<Toggle>();
            toggle.group = group;
            toggle.transition = Selectable.Transition.ColorTint;

            // Background ring / box
            GameObject bgGo = new GameObject("Background");
            bgGo.transform.SetParent(rowGo.transform, false);
            Image bgImg = bgGo.AddComponent<Image>();
            bgImg.sprite = boxSprite;
            bgImg.color = TextMutedColor;
            RectTransform bgRt = bgGo.GetComponent<RectTransform>();
            bgRt.anchorMin = new Vector2(0f, 0.5f);
            bgRt.anchorMax = new Vector2(0f, 0.5f);
            bgRt.pivot = new Vector2(0f, 0.5f);
            bgRt.anchoredPosition = new Vector2(12f, 0f);
            bgRt.sizeDelta = new Vector2(20f, 20f);

            // Checkmark / radio dot inside
            GameObject markGo = new GameObject("Checkmark");
            markGo.transform.SetParent(bgGo.transform, false);
            Image markImg = markGo.AddComponent<Image>();
            markImg.sprite = markSprite;
            markImg.color = AccentColor;
            RectTransform markRt = markGo.GetComponent<RectTransform>();
            markRt.anchorMin = new Vector2(0.5f, 0.5f);
            markRt.anchorMax = new Vector2(0.5f, 0.5f);
            markRt.sizeDelta = new Vector2(18f, 18f);

            Text lbl = CreateText("Label", rowGo.transform, font, labelText, 15, FontStyle.Normal, TextPrimaryColor, TextAnchor.MiddleLeft);
            RectTransform lblRt = lbl.GetComponent<RectTransform>();
            lblRt.anchorMin = Vector2.zero;
            lblRt.anchorMax = Vector2.one;
            lblRt.offsetMin = new Vector2(42f, 0f);
            lblRt.offsetMax = new Vector2(-10f, 0f);

            toggle.targetGraphic = rowGo.GetComponent<Image>();
            toggle.graphic = markImg;
            return toggle;
        }

        private static GameObject CreateRowContainer(string name, Transform parent, float height)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(0f, height);
            LayoutElement le = go.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;
            return go;
        }

        private static void CreateSectionLabel(string name, Transform parent, Font font, string text)
        {
            GameObject go = CreateRowContainer(name, parent, 22f);
            Text txt = CreateText("Text", go.transform, font, text, 13, FontStyle.Bold, TextMutedColor, TextAnchor.LowerLeft);
            StretchFull(txt.GetComponent<RectTransform>());
        }

        private static void CreateDivider(string name, Transform parent)
        {
            GameObject go = CreateRowContainer(name, parent, 2f);
            Image img = go.AddComponent<Image>();
            img.color = DividerColor;
            img.raycastTarget = false;
        }

        private static Text CreateText(string name, Transform parent, Font font, string content, int size, FontStyle style, Color color, TextAnchor align)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            Text txt = go.AddComponent<Text>();
            txt.font = font;
            txt.text = content;
            txt.fontSize = size;
            txt.fontStyle = style;
            txt.color = color;
            txt.alignment = align;
            txt.raycastTarget = false;
            return txt;
        }

        private static void StretchFull(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}
