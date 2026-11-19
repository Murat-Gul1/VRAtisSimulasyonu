using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using VRAtis.Ballistics;
using VRAtis.Environment;
using VRAtis.Hardware;
using VRAtis.Targets;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace VRAtis.UI
{
    /// <summary>
    /// Controls the Settings gear button, Settings window, Weather checkboxes,
    /// Time-of-Day radio presets, and live VR Shinecon lens spacing controls.
    /// When VR split-screen mode is active, mirrors the entire UI symmetrically inside
    /// both Left and Right eye viewports at zero parallax so VR lenses fuse with zero double vision.
    /// </summary>
    public class SettingsMenuController : MonoBehaviour
    {
        private class UiView
        {
            public RectTransform RootRect;
            public Button GearButton;
            public RectTransform GearRect;
            public Image GearBackground;
            public GameObject WindowRoot;
            public RectTransform WindowRect;
            public GameObject MainCategoryPage;
            public GameObject WeatherCategoryPage;
            public Button OpenWeatherButton;
            public Button WeatherBackButton;
            public Button CloseWindowButton;
            public Toggle RainCheckbox;
            public Toggle FogCheckbox;
            public Toggle[] TimePresetToggles;
            public Toggle AutoTimeAdvanceCheckbox;
            public Toggle VrSplitCheckbox;
            public Button LensMinusButton;
            public Button LensPlusButton;
            public Button LensResetButton;
            public Text LensValueText;
            public Text ScreenWidthText;
            public GameObject OpticalCategoryPage;
            public Button OpenOpticalButton;
            public Button OpticalBackButton;
            public RawImage OpticPreviewImage;
            public Text OpticStatusText;
            public Button OpticZeroButton;
            public Toggle OpticEnableCheckbox;
            public Button OpticModeButton;
            public Text OpticModeLabel;
            public Button OpticAxisButton;
            public Text OpticAxisLabel;
            public Button OpticSpacingMinusButton;
            public Text OpticSpacingValueText;
            public Button OpticSpacingPlusButton;
            public Button OpticDomMinusButton;
            public Text OpticDomValueText;
            public Button OpticDomPlusButton;
            public GameObject TargetCategoryPage;
            public Button OpenTargetsButton;
            public Button TargetBackButton;
            public Text TargetSummaryText;
            public Button[] TargetCountButtons;
            public Image[] TargetCountBg;
            public Button[] TargetDistButtons;
            public Image[] TargetDistBg;
            public Button TargetDistMinusButton;
            public Text TargetDistValueText;
            public Button TargetDistPlusButton;
            public Button[] TargetSpacingButtons;
            public Image[] TargetSpacingBg;
            public Button TargetSpacingMinusButton;
            public Text TargetSpacingValueText;
            public Button TargetSpacingPlusButton;
            public Button TargetClassicRangeButton;
            public Image TargetClassicRangeBg;
            public Button TargetClearMarksButton;
            public GameObject CalibrationBannerRoot;
            public RectTransform CalibrationBannerRect;
            public Image CalibrationAccentBar;
            public Text CalibrationTitleText;
            public Text CalibrationDetailText;
            public RectTransform CalibrationProgressFillRect;
            public Image CalibrationProgressFillImage;
        }

        [Header("Referanslar")]
        [SerializeField] private DynamicTimeOfDay timeOfDayController;
        [SerializeField] private EnvironmentManager environmentManager;
        [SerializeField] private CardboardStereoRig stereoRig;
        [SerializeField] private OpticalRifleTracker opticalTracker;
        [SerializeField] private TargetRangeManager targetRangeManager;

        private static readonly int[] TargetCountPresets = { 0, 1, 2, 3, 4, 5, 6 };
        private static readonly int[] TargetDistancePresets = { 20, 25, 50, 100, 200 };
        private static readonly int[] TargetSpacingPresets = { 1, 3, 5, 10, 20, 30 };
        private static readonly Color ActiveChipColor = new Color(0.24f, 0.72f, 0.56f, 0.96f);
        private static readonly Color InactiveChipColor = new Color(0.11f, 0.16f, 0.22f, 0.92f);

        [Header("Ana UI Kökü")]
        [SerializeField] private RectTransform primaryEyeRoot;
        [SerializeField] private float[] timePresetHours = { 7.2f, 12.5f, 17.1f, 18.6f, 21.5f };

        private readonly List<UiView> _views = new List<UiView>(2);
        private UiView _primaryView;
        private UiView _mirrorView;

        private bool _lastStereoSplit;
        private Rect _lastLeftRect;
        private Rect _lastRightRect;
        private bool _stereoLayoutInitialized;
        private float _lastDisplayedLensMm = -1f;
        private float _lastDisplayedPhoneMm = -1f;
        private float _nextOpticalUiRefreshTime;
        private OpticalRifleTracker.CalibrationPhase _lastBannerPhase = (OpticalRifleTracker.CalibrationPhase)(-1);
        private int _lastBannerSec = -1;
        private int _lastBannerTenths = -1;
        private int _lastBannerLedMask = -1;
        private bool _lastBannerCameraRunning;
        private bool _lastBannerWaitingPerm;
        private bool _lastBannerHiddenByMenu = true;

        public event Action<bool, bool> OnWeatherTogglesChanged;

        public bool IsMenuOpen => _primaryView != null && _primaryView.WindowRoot != null && _primaryView.WindowRoot.activeSelf;
        public bool IsRainSelected { get; private set; }
        public bool IsFogSelected { get; private set; }

        private void Awake()
        {
            if (timeOfDayController == null)
                timeOfDayController = FindAnyObjectByType<DynamicTimeOfDay>();
            if (environmentManager == null)
                environmentManager = EnvironmentManager.Instance != null
                    ? EnvironmentManager.Instance
                    : FindAnyObjectByType<EnvironmentManager>();
            if (stereoRig == null)
                stereoRig = FindAnyObjectByType<CardboardStereoRig>();
            if (opticalTracker == null)
                opticalTracker = FindAnyObjectByType<OpticalRifleTracker>();
            if (targetRangeManager == null)
                targetRangeManager = FindAnyObjectByType<TargetRangeManager>();

            if (primaryEyeRoot == null)
                primaryEyeRoot = transform.Find("PrimaryEye_UIRoot") as RectTransform;

            if (primaryEyeRoot == null)
                return;

            EnsureCalibrationBannerExists(primaryEyeRoot);

            _primaryView = ResolveAndBindView(primaryEyeRoot);
            _views.Add(_primaryView);

            // Create symmetric stereo mirror root for the right eye in VR SBS mode
            Transform existingMirror = transform.Find("RightEyeMirror_UIRoot");
            GameObject mirrorGo = existingMirror != null
                ? existingMirror.gameObject
                : Instantiate(primaryEyeRoot.gameObject, transform);
            mirrorGo.name = "RightEyeMirror_UIRoot";

            _mirrorView = ResolveAndBindView(mirrorGo.GetComponent<RectTransform>());
            _views.Add(_mirrorView);

            SetWindowOpen(false);
            ShowMainCategoryPage();
        }

        private void Start()
        {
            if (environmentManager != null)
                IsRainSelected = environmentManager.isRaining;

            SyncWeatherTogglesToViews();
            SyncTimeWidgetsFromScene();
            SyncVrWidgetsToViews();
            SyncOpticalWidgetsToViews();
            SyncTargetWidgetsToViews();
            ApplyStereoLayoutIfNeeded(force: true);
            SyncCalibrationBannerToViews();
        }

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            if (IsMenuOpen && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
                CloseWindow();
#endif

            if (IsMenuOpen)
            {
                if (_primaryView?.WeatherCategoryPage != null && _primaryView.WeatherCategoryPage.activeSelf)
                    SyncTimeWidgetsFromScene();

                if (_primaryView?.MainCategoryPage != null && _primaryView.MainCategoryPage.activeSelf)
                    SyncVrWidgetsToViews();

                if (_primaryView?.OpticalCategoryPage != null && _primaryView.OpticalCategoryPage.activeSelf
                    && Time.unscaledTime >= _nextOpticalUiRefreshTime)
                {
                    _nextOpticalUiRefreshTime = Time.unscaledTime + 0.10f;
                    SyncOpticalWidgetsToViews();
                }
            }

            SyncCalibrationBannerToViews();
        }

        private void LateUpdate()
        {
            ApplyStereoLayoutIfNeeded(force: false);
        }

        public void ToggleWindow()
        {
            SetWindowOpen(!IsMenuOpen);
            if (IsMenuOpen)
            {
                ShowMainCategoryPage();
                SyncWeatherTogglesToViews();
                SyncTimeWidgetsFromScene();
                SyncVrWidgetsToViews();
            }
        }

        public void CloseWindow()
        {
            SetWindowOpen(false);
        }

        public void ShowMainCategoryPage()
        {
            SetCategoryPageActive(showMain: true, showWeather: false, showOptical: false, showTargets: false);
            SyncVrWidgetsToViews();
        }

        public void ShowWeatherCategoryPage()
        {
            SetCategoryPageActive(showMain: false, showWeather: true, showOptical: false, showTargets: false);
            SyncWeatherTogglesToViews();
            SyncTimeWidgetsFromScene();
        }

        public void ShowOpticalCategoryPage()
        {
            SetCategoryPageActive(showMain: false, showWeather: false, showOptical: true, showTargets: false);
            _nextOpticalUiRefreshTime = 0f;
            SyncOpticalWidgetsToViews();
        }

        public void ShowTargetCategoryPage()
        {
            SetCategoryPageActive(showMain: false, showWeather: false, showOptical: false, showTargets: true);
            SyncTargetWidgetsToViews();
        }

        private void SetCategoryPageActive(bool showMain, bool showWeather, bool showOptical, bool showTargets)
        {
            for (int i = 0; i < _views.Count; i++)
            {
                if (_views[i].MainCategoryPage != null)
                    _views[i].MainCategoryPage.SetActive(showMain);
                if (_views[i].WeatherCategoryPage != null)
                    _views[i].WeatherCategoryPage.SetActive(showWeather);
                if (_views[i].OpticalCategoryPage != null)
                    _views[i].OpticalCategoryPage.SetActive(showOptical);
                if (_views[i].TargetCategoryPage != null)
                    _views[i].TargetCategoryPage.SetActive(showTargets);
            }

            RefreshOpticalPreviewActiveState();
        }

        private void SetWindowOpen(bool open)
        {
            for (int i = 0; i < _views.Count; i++)
            {
                if (_views[i].WindowRoot != null)
                    _views[i].WindowRoot.SetActive(open);
            }

            RefreshOpticalPreviewActiveState();
        }

        private UiView ResolveAndBindView(RectTransform root)
        {
            Transform gearTr = root.Find("BottomRight_SettingsButton");
            Transform winTr = root.Find("SettingsWindow");
            Transform calTr = root.Find("OpticalCalibrationBanner");
            Transform contentTr = winTr != null ? winTr.Find("ContentArea") : null;
            Transform mainTr = contentTr != null ? contentTr.Find("MainCategoryPage") : null;
            Transform weatherTr = contentTr != null ? contentTr.Find("WeatherCategoryPage") : null;
            Transform opticalTr = contentTr != null ? contentTr.Find("OpticalCategoryPage") : null;
            Transform targetTr = contentTr != null ? contentTr.Find("TargetCategoryPage") : null;
            Transform vrCardTr = mainTr != null ? mainTr.Find("VRCalibrationCard") : null;

            UiView view = new UiView
            {
                RootRect = root,
                GearButton = gearTr != null ? gearTr.GetComponent<Button>() : null,
                GearRect = gearTr as RectTransform,
                GearBackground = gearTr != null ? gearTr.GetComponent<Image>() : null,
                WindowRoot = winTr != null ? winTr.gameObject : null,
                WindowRect = winTr as RectTransform,
                MainCategoryPage = mainTr != null ? mainTr.gameObject : null,
                WeatherCategoryPage = weatherTr != null ? weatherTr.gameObject : null,
                OpticalCategoryPage = opticalTr != null ? opticalTr.gameObject : null,
                TargetCategoryPage = targetTr != null ? targetTr.gameObject : null,
                OpenWeatherButton = FindChildComponent<Button>(mainTr, "WeatherMenuButton"),
                OpenOpticalButton = FindChildComponent<Button>(mainTr, "OpticalMenuButton"),
                OpenTargetsButton = FindChildComponent<Button>(mainTr, "TargetsMenuButton"),
                WeatherBackButton = FindChildComponent<Button>(weatherTr, "WeatherSubHeader/BackButton"),
                OpticalBackButton = FindChildComponent<Button>(opticalTr, "OpticSubHeader/BackButton"),
                TargetBackButton = FindChildComponent<Button>(targetTr, "TargetSubHeader/BackButton"),
                TargetSummaryText = FindChildComponent<Text>(targetTr, "TargetSummaryBox/TargetSummaryText"),
                TargetCountButtons = new Button[TargetCountPresets.Length],
                TargetCountBg = new Image[TargetCountPresets.Length],
                TargetDistButtons = new Button[TargetDistancePresets.Length],
                TargetDistBg = new Image[TargetDistancePresets.Length],
                TargetDistMinusButton = FindChildComponent<Button>(targetTr, "TargetDistStepRow/DistMinusButton"),
                TargetDistValueText = FindChildComponent<Text>(targetTr, "TargetDistStepRow/ValueBox/DistValueText"),
                TargetDistPlusButton = FindChildComponent<Button>(targetTr, "TargetDistStepRow/DistPlusButton"),
                TargetSpacingButtons = new Button[TargetSpacingPresets.Length],
                TargetSpacingBg = new Image[TargetSpacingPresets.Length],
                TargetSpacingMinusButton = FindChildComponent<Button>(targetTr, "TargetSpacingStepRow/SpacingMinusButton"),
                TargetSpacingValueText = FindChildComponent<Text>(targetTr, "TargetSpacingStepRow/ValueBox/SpacingValueText"),
                TargetSpacingPlusButton = FindChildComponent<Button>(targetTr, "TargetSpacingStepRow/SpacingPlusButton"),
                TargetClassicRangeButton = FindChildComponent<Button>(targetTr, "TargetUtilityRow/ClassicRangeButton"),
                TargetClassicRangeBg = FindChildComponent<Image>(targetTr, "TargetUtilityRow/ClassicRangeButton"),
                TargetClearMarksButton = FindChildComponent<Button>(targetTr, "TargetUtilityRow/ClearMarksButton"),
                CloseWindowButton = FindChildComponent<Button>(winTr, "HeaderBar/CloseButton"),
                RainCheckbox = FindChildComponent<Toggle>(weatherTr, "RainCheckbox"),
                FogCheckbox = FindChildComponent<Toggle>(weatherTr, "FogCheckbox"),
                AutoTimeAdvanceCheckbox = FindChildComponent<Toggle>(weatherTr, "AutoTimeCheckbox"),
                VrSplitCheckbox = FindChildComponent<Toggle>(vrCardTr, "VrSplitCheckbox"),
                LensMinusButton = FindChildComponent<Button>(vrCardTr, "LensControlRow/LensMinusButton"),
                LensPlusButton = FindChildComponent<Button>(vrCardTr, "LensControlRow/LensPlusButton"),
                LensResetButton = FindChildComponent<Button>(vrCardTr, "LensControlRow/LensResetButton"),
                LensValueText = FindChildComponent<Text>(vrCardTr, "LensControlRow/LensValueText"),
                ScreenWidthText = FindChildComponent<Text>(vrCardTr, "VrHeaderRow/VrScreenWidthText"),
                OpticPreviewImage = FindChildComponent<RawImage>(opticalTr, "OpticPreviewRow/PreviewFrame/OpticPreviewImage"),
                OpticStatusText = FindChildComponent<Text>(opticalTr, "OpticPreviewRow/StatusColumn/StatusBox/OpticStatusText"),
                OpticZeroButton = FindChildComponent<Button>(opticalTr, "OpticPreviewRow/StatusColumn/OpticZeroButton"),
                OpticEnableCheckbox = FindChildComponent<Toggle>(opticalTr, "OpticEnableCheckbox"),
                OpticModeButton = FindChildComponent<Button>(opticalTr, "OpticModeButton"),
                OpticModeLabel = FindChildComponent<Text>(opticalTr, "OpticModeButton/Label"),
                OpticAxisButton = FindChildComponent<Button>(opticalTr, "OpticAxisButton"),
                OpticAxisLabel = FindChildComponent<Text>(opticalTr, "OpticAxisButton/Label"),
                OpticSpacingMinusButton = FindChildComponent<Button>(opticalTr, "OpticSpacingRow/SpacingMinusButton"),
                OpticSpacingValueText = FindChildComponent<Text>(opticalTr, "OpticSpacingRow/ValueBox/SpacingValueText"),
                OpticSpacingPlusButton = FindChildComponent<Button>(opticalTr, "OpticSpacingRow/SpacingPlusButton"),
                OpticDomMinusButton = FindChildComponent<Button>(opticalTr, "OpticDomRow/DomMinusButton"),
                OpticDomValueText = FindChildComponent<Text>(opticalTr, "OpticDomRow/ValueBox/DomValueText"),
                OpticDomPlusButton = FindChildComponent<Button>(opticalTr, "OpticDomRow/DomPlusButton"),
                CalibrationBannerRoot = calTr != null ? calTr.gameObject : null,
                CalibrationBannerRect = calTr as RectTransform,
                CalibrationAccentBar = FindChildComponent<Image>(calTr, "AccentBar"),
                CalibrationTitleText = FindChildComponent<Text>(calTr, "TitleText"),
                CalibrationDetailText = FindChildComponent<Text>(calTr, "DetailText"),
                CalibrationProgressFillRect = FindChildComponent<RectTransform>(calTr, "ProgressTrack/ProgressFill"),
                CalibrationProgressFillImage = FindChildComponent<Image>(calTr, "ProgressTrack/ProgressFill"),
                TimePresetToggles = new Toggle[timePresetHours != null ? timePresetHours.Length : 0]
            };

            for (int i = 0; i < view.TimePresetToggles.Length; i++)
                view.TimePresetToggles[i] = FindChildComponent<Toggle>(weatherTr, $"TimePreset_{i + 1}");

            for (int i = 0; i < TargetCountPresets.Length; i++)
            {
                string path = $"TargetCountRow/CountBtn_{TargetCountPresets[i]}";
                view.TargetCountButtons[i] = FindChildComponent<Button>(targetTr, path);
                view.TargetCountBg[i] = FindChildComponent<Image>(targetTr, path);
            }

            for (int i = 0; i < TargetDistancePresets.Length; i++)
            {
                string path = $"TargetDistPresetRow/DistBtn_{TargetDistancePresets[i]}";
                view.TargetDistButtons[i] = FindChildComponent<Button>(targetTr, path);
                view.TargetDistBg[i] = FindChildComponent<Image>(targetTr, path);
            }

            for (int i = 0; i < TargetSpacingPresets.Length; i++)
            {
                string path = $"TargetSpacingPresetRow/SpacingBtn_{TargetSpacingPresets[i]}";
                view.TargetSpacingButtons[i] = FindChildComponent<Button>(targetTr, path);
                view.TargetSpacingBg[i] = FindChildComponent<Image>(targetTr, path);
            }

            BindViewListeners(view);
            return view;
        }

        private void BindViewListeners(UiView view)
        {
            BindClick(view.GearButton, ToggleWindow);
            BindClick(view.OpenWeatherButton, ShowWeatherCategoryPage);
            BindClick(view.OpenOpticalButton, ShowOpticalCategoryPage);
            BindClick(view.OpenTargetsButton, ShowTargetCategoryPage);
            BindClick(view.WeatherBackButton, ShowMainCategoryPage);
            BindClick(view.OpticalBackButton, ShowMainCategoryPage);
            BindClick(view.TargetBackButton, ShowMainCategoryPage);
            BindClick(view.CloseWindowButton, CloseWindow);
            BindClick(view.OpticZeroButton, HandleOpticZero);
            BindClick(view.OpticModeButton, CycleOpticMode);
            BindClick(view.OpticAxisButton, CycleOpticAxis);
            BindClick(view.OpticSpacingMinusButton, () => NudgeOpticSpacing(-0.01f));
            BindClick(view.OpticSpacingPlusButton, () => NudgeOpticSpacing(+0.01f));
            BindClick(view.OpticDomMinusButton, () => NudgeOpticDom(-5));
            BindClick(view.OpticDomPlusButton, () => NudgeOpticDom(+5));
            BindToggle(view.OpticEnableCheckbox, HandleOpticEnableChanged);

            for (int i = 0; i < TargetCountPresets.Length; i++)
            {
                int val = TargetCountPresets[i];
                BindClick(view.TargetCountButtons[i], () => HandleTargetCountSelected(val));
            }

            for (int i = 0; i < TargetDistancePresets.Length; i++)
            {
                float val = TargetDistancePresets[i];
                BindClick(view.TargetDistButtons[i], () => HandleTargetDistanceSelected(val));
            }

            BindClick(view.TargetDistMinusButton, () => NudgeTargetDistance(-5f));
            BindClick(view.TargetDistPlusButton, () => NudgeTargetDistance(+5f));

            for (int i = 0; i < TargetSpacingPresets.Length; i++)
            {
                float val = TargetSpacingPresets[i];
                BindClick(view.TargetSpacingButtons[i], () => HandleTargetSpacingSelected(val));
            }

            BindClick(view.TargetSpacingMinusButton, () => NudgeTargetSpacing(-1f));
            BindClick(view.TargetSpacingPlusButton, () => NudgeTargetSpacing(+1f));
            BindClick(view.TargetClassicRangeButton, HandleClassicRangeSelected);
            BindClick(view.TargetClearMarksButton, HandleClearTargetMarks);

            if (view.RainCheckbox != null)
            {
                view.RainCheckbox.onValueChanged.RemoveAllListeners();
                view.RainCheckbox.onValueChanged.AddListener(HandleRainChanged);
            }

            if (view.FogCheckbox != null)
            {
                view.FogCheckbox.onValueChanged.RemoveAllListeners();
                view.FogCheckbox.onValueChanged.AddListener(HandleFogChanged);
            }

            if (view.AutoTimeAdvanceCheckbox != null)
            {
                view.AutoTimeAdvanceCheckbox.onValueChanged.RemoveAllListeners();
                view.AutoTimeAdvanceCheckbox.onValueChanged.AddListener(HandleAutoTimeChanged);
            }

            if (view.TimePresetToggles != null)
            {
                for (int i = 0; i < view.TimePresetToggles.Length; i++)
                {
                    Toggle toggle = view.TimePresetToggles[i];
                    if (toggle == null)
                        continue;

                    int capturedIndex = i;
                    toggle.onValueChanged.RemoveAllListeners();
                    toggle.onValueChanged.AddListener(isOn =>
                    {
                        if (isOn)
                            HandleTimePresetSelected(capturedIndex);
                    });
                }
            }

            BindClick(view.LensMinusButton, () => NudgeVrLensSpacing(-1f));
            BindClick(view.LensPlusButton, () => NudgeVrLensSpacing(+1f));
            BindClick(view.LensResetButton, ResetVrLensSpacing);

            if (view.VrSplitCheckbox != null)
            {
                view.VrSplitCheckbox.onValueChanged.RemoveAllListeners();
                view.VrSplitCheckbox.onValueChanged.AddListener(HandleVrSplitChanged);
            }
        }

        private void ApplyStereoLayoutIfNeeded(bool force)
        {
            if (_primaryView == null || _primaryView.RootRect == null)
                return;

            bool splitActive = CardboardStereoRig.IsStereoSplitActive;
            Rect leftRect = CardboardStereoRig.LeftEyeViewportNorm;
            Rect rightRect = CardboardStereoRig.RightEyeViewportNorm;

            if (!force &&
                _stereoLayoutInitialized &&
                _lastStereoSplit == splitActive &&
                (!splitActive || (_lastLeftRect == leftRect && _lastRightRect == rightRect)))
            {
                return;
            }

            _stereoLayoutInitialized = true;
            _lastStereoSplit = splitActive;
            _lastLeftRect = leftRect;
            _lastRightRect = rightRect;

            if (splitActive)
            {
                ApplyEyeContainerBounds(_primaryView.RootRect, leftRect.xMin, leftRect.xMax);
                ConfigureStereoViewGeometry(_primaryView, isSplit: true);

                if (_mirrorView != null && _mirrorView.RootRect != null)
                {
                    _mirrorView.RootRect.gameObject.SetActive(true);
                    ApplyEyeContainerBounds(_mirrorView.RootRect, rightRect.xMin, rightRect.xMax);
                    ConfigureStereoViewGeometry(_mirrorView, isSplit: true);
                }
            }
            else
            {
                ApplyEyeContainerBounds(_primaryView.RootRect, 0f, 1f);
                ConfigureStereoViewGeometry(_primaryView, isSplit: false);

                if (_mirrorView != null && _mirrorView.RootRect != null)
                    _mirrorView.RootRect.gameObject.SetActive(false);
            }

            SyncVrWidgetsToViews(force: true);
        }

        private static void ApplyEyeContainerBounds(RectTransform rt, float xMin, float xMax)
        {
            rt.anchorMin = new Vector2(xMin, 0f);
            rt.anchorMax = new Vector2(xMax, 1f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static void ConfigureStereoViewGeometry(UiView view, bool isSplit)
        {
            if (view.GearRect != null)
            {
                view.GearRect.anchorMin = new Vector2(1f, 0f);
                view.GearRect.anchorMax = new Vector2(1f, 0f);
                view.GearRect.pivot = new Vector2(1f, 0f);
                view.GearRect.sizeDelta = isSplit ? new Vector2(50f, 50f) : new Vector2(64f, 64f);
                view.GearRect.anchoredPosition = isSplit ? new Vector2(-120f, 96f) : new Vector2(-28f, 28f);
            }

            if (view.GearBackground != null)
            {
                Color c = view.GearBackground.color;
                c.a = isSplit ? 0.56f : 0.90f;
                view.GearBackground.color = c;
            }

            if (view.WindowRect != null)
            {
                if (isSplit)
                {
                    view.WindowRect.anchorMin = new Vector2(0.5f, 0.5f);
                    view.WindowRect.anchorMax = new Vector2(0.5f, 0.5f);
                    view.WindowRect.pivot = new Vector2(0.5f, 0.5f);
                    view.WindowRect.anchoredPosition = Vector2.zero;
                    view.WindowRect.sizeDelta = new Vector2(430f, 560f);
                    view.WindowRect.localScale = new Vector3(0.82f, 0.82f, 1f);
                }
                else
                {
                    view.WindowRect.anchorMin = new Vector2(1f, 0f);
                    view.WindowRect.anchorMax = new Vector2(1f, 0f);
                    view.WindowRect.pivot = new Vector2(1f, 0f);
                    view.WindowRect.anchoredPosition = new Vector2(-28f, 104f);
                    view.WindowRect.sizeDelta = new Vector2(430f, 560f);
                    view.WindowRect.localScale = Vector3.one;
                }
            }

            if (view.CalibrationBannerRect != null)
            {
                view.CalibrationBannerRect.anchorMin = new Vector2(0.5f, 0.5f);
                view.CalibrationBannerRect.anchorMax = new Vector2(0.5f, 0.5f);
                view.CalibrationBannerRect.pivot = new Vector2(0.5f, 0.5f);
                if (isSplit)
                {
                    view.CalibrationBannerRect.anchoredPosition = new Vector2(0f, 225f);
                    view.CalibrationBannerRect.sizeDelta = new Vector2(368f, 102f);
                    view.CalibrationBannerRect.localScale = new Vector3(0.92f, 0.92f, 1f);
                }
                else
                {
                    view.CalibrationBannerRect.anchoredPosition = new Vector2(0f, 320f);
                    view.CalibrationBannerRect.sizeDelta = new Vector2(520f, 110f);
                    view.CalibrationBannerRect.localScale = Vector3.one;
                }
            }
        }

        private void HandleRainChanged(bool isOn)
        {
            IsRainSelected = isOn;
            if (environmentManager != null)
                environmentManager.isRaining = isOn;

            SyncWeatherTogglesToViews();
            OnWeatherTogglesChanged?.Invoke(IsRainSelected, IsFogSelected);
        }

        private void HandleFogChanged(bool isOn)
        {
            IsFogSelected = isOn;
            SyncWeatherTogglesToViews();
            OnWeatherTogglesChanged?.Invoke(IsRainSelected, IsFogSelected);
        }

        private void HandleTimePresetSelected(int index)
        {
            if (timeOfDayController == null || timePresetHours == null || index < 0 || index >= timePresetHours.Length)
                return;

            timeOfDayController.ApplyTimePreset(timePresetHours[index]);
            SyncTimeWidgetsFromScene();
        }

        private void HandleAutoTimeChanged(bool isOn)
        {
            if (timeOfDayController != null)
                timeOfDayController.SetAutoAdvanceTime(isOn);

            SyncTimeWidgetsFromScene();
        }

        private void HandleVrSplitChanged(bool isOn)
        {
            if (stereoRig == null)
                stereoRig = FindAnyObjectByType<CardboardStereoRig>();

            if (stereoRig != null)
                stereoRig.EnableSplitScreen = isOn;

            ApplyStereoLayoutIfNeeded(force: true);
        }

        private void NudgeVrLensSpacing(float deltaMm)
        {
            if (stereoRig == null)
                stereoRig = FindAnyObjectByType<CardboardStereoRig>();

            if (stereoRig != null)
                stereoRig.NudgeLensSpacingMm(deltaMm);

            ApplyStereoLayoutIfNeeded(force: true);
            SyncVrWidgetsToViews(force: true);
        }

        private void ResetVrLensSpacing()
        {
            if (stereoRig == null)
                stereoRig = FindAnyObjectByType<CardboardStereoRig>();

            if (stereoRig != null)
                stereoRig.ResetLensSpacingToDefault();

            ApplyStereoLayoutIfNeeded(force: true);
            SyncVrWidgetsToViews(force: true);
        }

        private void SyncWeatherTogglesToViews()
        {
            for (int i = 0; i < _views.Count; i++)
            {
                if (_views[i].RainCheckbox != null)
                    _views[i].RainCheckbox.SetIsOnWithoutNotify(IsRainSelected);
                if (_views[i].FogCheckbox != null)
                    _views[i].FogCheckbox.SetIsOnWithoutNotify(IsFogSelected);
            }
        }

        private void SyncTimeWidgetsFromScene()
        {
            if (timeOfDayController == null || timePresetHours == null)
                return;

            float current = timeOfDayController.CurrentTimeOfDayHours;
            int matchedIndex = -1;
            float bestDiff = 0.35f;

            for (int i = 0; i < timePresetHours.Length; i++)
            {
                float diff = Mathf.Abs(Mathf.DeltaAngle(current * 15f, timePresetHours[i] * 15f)) / 15f;
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    matchedIndex = i;
                }
            }

            bool autoAdvance = timeOfDayController.AutoAdvanceTime;
            for (int v = 0; v < _views.Count; v++)
            {
                UiView view = _views[v];
                if (view.AutoTimeAdvanceCheckbox != null)
                    view.AutoTimeAdvanceCheckbox.SetIsOnWithoutNotify(autoAdvance);

                if (view.TimePresetToggles == null)
                    continue;

                int count = Mathf.Min(view.TimePresetToggles.Length, timePresetHours.Length);
                for (int i = 0; i < count; i++)
                {
                    if (view.TimePresetToggles[i] != null)
                        view.TimePresetToggles[i].SetIsOnWithoutNotify(i == matchedIndex);
                }
            }
        }

        private void SyncVrWidgetsToViews(bool force = false)
        {
            float lensMm = stereoRig != null ? stereoRig.LensCenterSpacingMm : CardboardStereoRig.ActiveLensSpacingMm;
            float phoneMm = CardboardStereoRig.DetectedPhoneWidthMm;
            bool splitOn = stereoRig != null ? stereoRig.EnableSplitScreen : CardboardStereoRig.IsStereoSplitActive;

            if (!force && Mathf.Approximately(lensMm, _lastDisplayedLensMm) && Mathf.Approximately(phoneMm, _lastDisplayedPhoneMm))
                return;

            _lastDisplayedLensMm = lensMm;
            _lastDisplayedPhoneMm = phoneMm;

            for (int v = 0; v < _views.Count; v++)
            {
                UiView view = _views[v];
                if (view.LensValueText != null)
                    view.LensValueText.text = $"{lensMm:F0} mm";
                if (view.ScreenWidthText != null)
                    view.ScreenWidthText.text = $"Ekran: {phoneMm:F0} mm";
                if (view.VrSplitCheckbox != null)
                    view.VrSplitCheckbox.SetIsOnWithoutNotify(splitOn);
            }
        }

        private OpticalRifleTracker ResolveOpticalTracker()
        {
            if (opticalTracker == null)
                opticalTracker = FindAnyObjectByType<OpticalRifleTracker>();
            return opticalTracker;
        }

        private TargetRangeManager ResolveTargetRangeManager()
        {
            if (targetRangeManager == null)
                targetRangeManager = FindAnyObjectByType<TargetRangeManager>();
            return targetRangeManager;
        }

        private void HandleTargetCountSelected(int count)
        {
            TargetRangeManager mgr = ResolveTargetRangeManager();
            if (mgr != null)
                mgr.SetTargetCount(count);
            SyncTargetWidgetsToViews();
        }

        private void HandleTargetDistanceSelected(float meters)
        {
            TargetRangeManager mgr = ResolveTargetRangeManager();
            if (mgr != null)
                mgr.SetDistanceMeters(meters);
            SyncTargetWidgetsToViews();
        }

        private void NudgeTargetDistance(float deltaMeters)
        {
            TargetRangeManager mgr = ResolveTargetRangeManager();
            if (mgr != null)
                mgr.NudgeDistanceMeters(deltaMeters);
            SyncTargetWidgetsToViews();
        }

        private void HandleTargetSpacingSelected(float meters)
        {
            TargetRangeManager mgr = ResolveTargetRangeManager();
            if (mgr != null)
                mgr.SetSpacingMeters(meters);
            SyncTargetWidgetsToViews();
        }

        private void NudgeTargetSpacing(float deltaMeters)
        {
            TargetRangeManager mgr = ResolveTargetRangeManager();
            if (mgr != null)
                mgr.NudgeSpacingMeters(deltaMeters);
            SyncTargetWidgetsToViews();
        }

        private void HandleClassicRangeSelected()
        {
            TargetRangeManager mgr = ResolveTargetRangeManager();
            if (mgr != null)
                mgr.ActivateClassicRange();
            SyncTargetWidgetsToViews();
        }

        private void HandleClearTargetMarks()
        {
            TargetRangeManager mgr = ResolveTargetRangeManager();
            if (mgr != null)
                mgr.ClearAllImpactMarksAndSwings();
        }

        private void SyncTargetWidgetsToViews()
        {
            TargetRangeManager mgr = ResolveTargetRangeManager();
            bool customActive = mgr != null && mgr.IsCustomDrillActive;
            int count = mgr != null ? mgr.TargetCount : 1;
            int distInt = mgr != null ? Mathf.RoundToInt(mgr.DistanceMeters) : 25;
            int spacingInt = mgr != null ? Mathf.RoundToInt(mgr.SpacingMeters) : 5;

            string summary;
            if (mgr == null)
                summary = "TargetRangeManager bulunamadı";
            else if (!customActive)
                summary = "Aktif: Klasik Poligon (25 / 100 / 300 / 500 m)";
            else if (count <= 0)
                summary = "Aktif: Sahada Hedef Yok (0) — Drill için 1-6 seçin";
            else
                summary = $"Drill: {count} Hedef  •  {distInt} m Mesafe  •  {spacingInt} m Aralık";

            for (int v = 0; v < _views.Count; v++)
            {
                UiView view = _views[v];
                if (view.TargetSummaryText != null)
                    view.TargetSummaryText.text = summary;
                if (view.TargetDistValueText != null)
                    view.TargetDistValueText.text = $"{distInt} m";
                if (view.TargetSpacingValueText != null)
                    view.TargetSpacingValueText.text = $"{spacingInt} m aralık";

                for (int i = 0; i < TargetCountPresets.Length; i++)
                {
                    if (view.TargetCountBg != null && view.TargetCountBg[i] != null)
                        view.TargetCountBg[i].color = (customActive && count == TargetCountPresets[i]) ? ActiveChipColor : InactiveChipColor;
                }

                for (int i = 0; i < TargetDistancePresets.Length; i++)
                {
                    if (view.TargetDistBg != null && view.TargetDistBg[i] != null)
                        view.TargetDistBg[i].color = (customActive && distInt == TargetDistancePresets[i]) ? ActiveChipColor : InactiveChipColor;
                }

                for (int i = 0; i < TargetSpacingPresets.Length; i++)
                {
                    if (view.TargetSpacingBg != null && view.TargetSpacingBg[i] != null)
                        view.TargetSpacingBg[i].color = (customActive && spacingInt == TargetSpacingPresets[i]) ? ActiveChipColor : InactiveChipColor;
                }

                if (view.TargetClassicRangeBg != null)
                    view.TargetClassicRangeBg.color = !customActive ? ActiveChipColor : InactiveChipColor;
            }
        }

        private void RefreshOpticalPreviewActiveState()
        {
            OpticalRifleTracker tracker = ResolveOpticalTracker();
            if (tracker == null)
                return;

            bool shouldShowPreview = IsMenuOpen
                && _primaryView != null
                && _primaryView.OpticalCategoryPage != null
                && _primaryView.OpticalCategoryPage.activeSelf;

            tracker.SetPreviewActive(shouldShowPreview);
        }

        private void HandleOpticEnableChanged(bool isOn)
        {
            OpticalRifleTracker tracker = ResolveOpticalTracker();
            if (tracker != null)
                tracker.IsOpticalTrackingEnabled = isOn;

            RefreshOpticalPreviewActiveState();
            SyncOpticalWidgetsToViews();
        }

        private void CycleOpticMode()
        {
            OpticalRifleTracker tracker = ResolveOpticalTracker();
            if (tracker == null)
                return;

            int next = ((int)tracker.CurrentTargetMode + 1) % 4;
            tracker.CurrentTargetMode = (OpticalRifleTracker.LedTargetMode)next;
            SyncOpticalWidgetsToViews();
        }

        private void CycleOpticAxis()
        {
            OpticalRifleTracker tracker = ResolveOpticalTracker();
            if (tracker == null)
                return;

            int next = ((int)tracker.CurrentBaselineAxis + 1) % 3;
            tracker.CurrentBaselineAxis = (OpticalRifleTracker.BaselineAxis)next;
            SyncOpticalWidgetsToViews();
        }

        private void NudgeOpticSpacing(float deltaMeters)
        {
            OpticalRifleTracker tracker = ResolveOpticalTracker();
            if (tracker == null)
                return;

            tracker.LedSpacingMeters += deltaMeters;
            SyncOpticalWidgetsToViews();
        }

        private void NudgeOpticDom(int delta)
        {
            OpticalRifleTracker tracker = ResolveOpticalTracker();
            if (tracker == null)
                return;

            tracker.MinColorDominance += delta;
            SyncOpticalWidgetsToViews();
        }

        private void HandleOpticZero()
        {
            OpticalRifleTracker tracker = ResolveOpticalTracker();
            if (tracker == null)
                return;

            tracker.RecenterOpticalZero(startGuidedCountdown: false);
            SyncOpticalWidgetsToViews();
        }

        private void SyncOpticalWidgetsToViews()
        {
            OpticalRifleTracker tracker = ResolveOpticalTracker();
            bool enabled = tracker != null && tracker.IsOpticalTrackingEnabled;
            OpticalRifleTracker.LedTargetMode mode = tracker != null
                ? tracker.CurrentTargetMode
                : OpticalRifleTracker.LedTargetMode.GreenAndRed;
            OpticalRifleTracker.BaselineAxis axis = tracker != null
                ? tracker.CurrentBaselineAxis
                : OpticalRifleTracker.BaselineAxis.HorizontalCrossbar;
            float spacingCm = tracker != null ? tracker.LedSpacingMeters * 100f : 6f;
            int dom = tracker != null ? tracker.MinColorDominance : 105;
            Texture previewTex = tracker != null ? tracker.DebugPreviewTexture : null;
            string statusText = BuildOpticalStatusText(tracker);

            for (int v = 0; v < _views.Count; v++)
            {
                UiView view = _views[v];
                if (view.OpticEnableCheckbox != null)
                    view.OpticEnableCheckbox.SetIsOnWithoutNotify(enabled);
                if (view.OpticModeLabel != null)
                    view.OpticModeLabel.text = FormatLedMode(mode);
                if (view.OpticAxisLabel != null)
                    view.OpticAxisLabel.text = FormatBaselineAxis(axis);
                if (view.OpticSpacingValueText != null)
                    view.OpticSpacingValueText.text = $"{spacingCm:F0} cm";
                if (view.OpticDomValueText != null)
                    view.OpticDomValueText.text = dom.ToString();
                if (view.OpticStatusText != null)
                    view.OpticStatusText.text = statusText;
                if (view.OpticZeroButton != null)
                    view.OpticZeroButton.interactable = tracker != null;
                if (view.OpticPreviewImage != null)
                {
                    if (view.OpticPreviewImage.texture != previewTex)
                        view.OpticPreviewImage.texture = previewTex;
                    view.OpticPreviewImage.enabled = previewTex != null;
                }
            }
        }

        private static string FormatLedMode(OpticalRifleTracker.LedTargetMode mode)
        {
            switch (mode)
            {
                case OpticalRifleTracker.LedTargetMode.GreenAndRed:
                    return "LED Modu: Yeşil + Kırmızı (Önerilen)";
                case OpticalRifleTracker.LedTargetMode.GreenAndBlue:
                    return "LED Modu: Yeşil + Mavi";
                case OpticalRifleTracker.LedTargetMode.DualGreen:
                    return "LED Modu: Çift Yeşil";
                case OpticalRifleTracker.LedTargetMode.DualRed:
                    return "LED Modu: Çift Kırmızı";
                default:
                    return "LED Modu: Yeşil + Kırmızı";
            }
        }

        private static string FormatBaselineAxis(OpticalRifleTracker.BaselineAxis axis)
        {
            switch (axis)
            {
                case OpticalRifleTracker.BaselineAxis.HorizontalCrossbar:
                    return "Dizilim: Yatay (Sağ-Sol)";
                case OpticalRifleTracker.BaselineAxis.VerticalStack:
                    return "Dizilim: Dikey (Üst-Alt)";
                case OpticalRifleTracker.BaselineAxis.LongitudinalRail:
                    return "Dizilim: Namlu Boyu (Ön-Arka)";
                default:
                    return "Dizilim: Yatay";
            }
        }

        private static string BuildOpticalStatusText(OpticalRifleTracker tracker)
        {
            if (tracker == null)
                return "Durum: Optik takip bileşeni yok";
            if (!tracker.IsOpticalTrackingEnabled)
                return "Durum: Kapalı (Yukarıdan açın)";
            if (tracker.IsWaitingCameraPermission)
                return "Durum: Kamera izni bekleniyor...";
            if (!tracker.IsCameraRunning)
                return "Durum: Kamera kapalı / Başlatılıyor";
            if (tracker.IsActivelyTrackingPosition)
            {
                string yawLockTag = !tracker.IsOpticalYawFusionEnabled
                    ? "Kapalı"
                    : (tracker.IsOpticalYawFusionActive
                        ? $"KİLİTLİ ({tracker.LastOpticalYawErrorDeg:+0.0;-0.0;0.0}°)"
                        : "Beklemede");
                return $"Durum: KİLİTLENDİ (A:✓ B:✓) | Yaw: {yawLockTag}\nMesafe: {tracker.CurrentOpticalDistanceMeters * 100f:F0} cm ({tracker.LastPixelSeparation:F0}px)\nKam: {tracker.ProcessedWidth}x{tracker.ProcessedHeight} {tracker.CameraUpdateHz:F0}Hz Açı:{tracker.CameraRotationAngle}°";
            }

            string markA = tracker.IsLedAFound ? "✓" : "✗";
            string markB = tracker.IsLedBFound ? "✓" : "✗";
            if (tracker.CurrentCalibrationPhase == OpticalRifleTracker.CalibrationPhase.PreparingHeadset)
                return $"Durum: Hazırlık geri sayımı ({Mathf.CeilToInt(tracker.CalibrationRemainingSeconds)} sn)\nKam: {tracker.ProcessedWidth}x{tracker.ProcessedHeight} {tracker.CameraUpdateHz:F0}Hz Açı:{tracker.CameraRotationAngle}°";
            if (tracker.CurrentCalibrationPhase == OpticalRifleTracker.CalibrationPhase.HoldingSteady)
                return $"Durum: Sabit tutuluyor ({tracker.CalibrationRemainingSeconds:F1} sn) A:{markA} B:{markB}\nKam: {tracker.ProcessedWidth}x{tracker.ProcessedHeight} {tracker.CameraUpdateHz:F0}Hz Açı:{tracker.CameraRotationAngle}°";

            return $"Durum: LED Aranıyor (A:{markA} B:{markB})\nKam: {tracker.ProcessedWidth}x{tracker.ProcessedHeight} {tracker.CameraUpdateHz:F0}Hz Açı:{tracker.CameraRotationAngle}°";
        }

        private static void EnsureCalibrationBannerExists(RectTransform eyeRoot)
        {
            if (eyeRoot == null || eyeRoot.Find("OpticalCalibrationBanner") != null)
                return;

            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            Image gearImg = FindChildComponent<Image>(eyeRoot, "BottomRight_SettingsButton");
            Sprite panelSprite = gearImg != null ? gearImg.sprite : null;

            GameObject bannerGo = new GameObject("OpticalCalibrationBanner");
            bannerGo.transform.SetParent(eyeRoot, false);
            RectTransform bannerRt = bannerGo.AddComponent<RectTransform>();
            bannerRt.anchorMin = new Vector2(0.5f, 0.5f);
            bannerRt.anchorMax = new Vector2(0.5f, 0.5f);
            bannerRt.pivot = new Vector2(0.5f, 0.5f);
            bannerRt.anchoredPosition = new Vector2(0f, 225f);
            bannerRt.sizeDelta = new Vector2(368f, 102f);

            Image bgImg = bannerGo.AddComponent<Image>();
            bgImg.sprite = panelSprite;
            bgImg.type = panelSprite != null ? Image.Type.Sliced : Image.Type.Simple;
            bgImg.color = new Color(0.05f, 0.08f, 0.13f, 0.86f);
            bgImg.raycastTarget = false;

            // Top accent stripe
            GameObject accentGo = new GameObject("AccentBar");
            accentGo.transform.SetParent(bannerGo.transform, false);
            RectTransform accentRt = accentGo.AddComponent<RectTransform>();
            accentRt.anchorMin = new Vector2(0.03f, 1f);
            accentRt.anchorMax = new Vector2(0.97f, 1f);
            accentRt.pivot = new Vector2(0.5f, 1f);
            accentRt.anchoredPosition = new Vector2(0f, -4f);
            accentRt.sizeDelta = new Vector2(0f, 5f);
            Image accentImg = accentGo.AddComponent<Image>();
            accentImg.color = new Color(0.24f, 0.72f, 0.98f, 1f);
            accentImg.raycastTarget = false;

            // Headline / Countdown
            GameObject titleGo = new GameObject("TitleText");
            titleGo.transform.SetParent(bannerGo.transform, false);
            RectTransform titleRt = titleGo.AddComponent<RectTransform>();
            titleRt.anchorMin = new Vector2(0.04f, 0.44f);
            titleRt.anchorMax = new Vector2(0.96f, 0.90f);
            titleRt.offsetMin = Vector2.zero;
            titleRt.offsetMax = Vector2.zero;
            Text titleTxt = titleGo.AddComponent<Text>();
            titleTxt.font = font;
            titleTxt.fontSize = 22;
            titleTxt.fontStyle = FontStyle.Bold;
            titleTxt.alignment = TextAnchor.MiddleCenter;
            titleTxt.color = new Color(0.95f, 0.98f, 1.0f, 1f);
            titleTxt.resizeTextForBestFit = true;
            titleTxt.resizeTextMinSize = 14;
            titleTxt.resizeTextMaxSize = 24;
            titleTxt.raycastTarget = false;

            // Detail instruction / LED check
            GameObject detailGo = new GameObject("DetailText");
            detailGo.transform.SetParent(bannerGo.transform, false);
            RectTransform detailRt = detailGo.AddComponent<RectTransform>();
            detailRt.anchorMin = new Vector2(0.04f, 0.16f);
            detailRt.anchorMax = new Vector2(0.96f, 0.46f);
            detailRt.offsetMin = Vector2.zero;
            detailRt.offsetMax = Vector2.zero;
            Text detailTxt = detailGo.AddComponent<Text>();
            detailTxt.font = font;
            detailTxt.fontSize = 16;
            detailTxt.alignment = TextAnchor.MiddleCenter;
            detailTxt.color = new Color(0.76f, 0.84f, 0.92f, 1f);
            detailTxt.resizeTextForBestFit = true;
            detailTxt.resizeTextMinSize = 11;
            detailTxt.resizeTextMaxSize = 17;
            detailTxt.raycastTarget = false;

            // Bottom progress bar
            GameObject trackGo = new GameObject("ProgressTrack");
            trackGo.transform.SetParent(bannerGo.transform, false);
            RectTransform trackRt = trackGo.AddComponent<RectTransform>();
            trackRt.anchorMin = new Vector2(0.05f, 0f);
            trackRt.anchorMax = new Vector2(0.95f, 0f);
            trackRt.pivot = new Vector2(0.5f, 0f);
            trackRt.anchoredPosition = new Vector2(0f, 8f);
            trackRt.sizeDelta = new Vector2(0f, 7f);
            Image trackImg = trackGo.AddComponent<Image>();
            trackImg.color = new Color(1f, 1f, 1f, 0.14f);
            trackImg.raycastTarget = false;

            GameObject fillGo = new GameObject("ProgressFill");
            fillGo.transform.SetParent(trackGo.transform, false);
            RectTransform fillRt = fillGo.AddComponent<RectTransform>();
            fillRt.anchorMin = new Vector2(0f, 0f);
            fillRt.anchorMax = new Vector2(0f, 1f);
            fillRt.offsetMin = Vector2.zero;
            fillRt.offsetMax = Vector2.zero;
            Image fillImg = fillGo.AddComponent<Image>();
            fillImg.color = new Color(0.24f, 0.72f, 0.98f, 1f);
            fillImg.raycastTarget = false;

            bannerGo.SetActive(false);
        }

        private void SyncCalibrationBannerToViews()
        {
            OpticalRifleTracker tracker = ResolveOpticalTracker();
            OpticalRifleTracker.CalibrationPhase phase = tracker != null
                ? tracker.CurrentCalibrationPhase
                : OpticalRifleTracker.CalibrationPhase.Idle;

            bool shouldShow = tracker != null
                && !IsMenuOpen
                && phase != OpticalRifleTracker.CalibrationPhase.Idle;

            if (!shouldShow)
            {
                if (!_lastBannerHiddenByMenu)
                {
                    _lastBannerHiddenByMenu = true;
                    _lastBannerPhase = (OpticalRifleTracker.CalibrationPhase)(-1);
                    for (int i = 0; i < _views.Count; i++)
                    {
                        if (_views[i].CalibrationBannerRoot != null && _views[i].CalibrationBannerRoot.activeSelf)
                            _views[i].CalibrationBannerRoot.SetActive(false);
                    }
                }
                return;
            }

            float rem = tracker.CalibrationRemainingSeconds;
            float progress = Mathf.Clamp01(tracker.CalibrationProgress01);
            int secCeil = Mathf.Max(1, Mathf.CeilToInt(rem));
            int tenths = Mathf.Max(0, Mathf.RoundToInt(rem * 10f));
            int ledMask = (tracker.IsLedAFound ? 1 : 0) | (tracker.IsLedBFound ? 2 : 0);
            bool camRunning = tracker.IsCameraRunning;
            bool waitingPerm = tracker.IsWaitingCameraPermission;

            for (int i = 0; i < _views.Count; i++)
            {
                UiView view = _views[i];
                if (view.CalibrationBannerRoot != null && !view.CalibrationBannerRoot.activeSelf)
                    view.CalibrationBannerRoot.SetActive(true);
                if (view.CalibrationProgressFillRect != null)
                    view.CalibrationProgressFillRect.anchorMax = new Vector2(progress, 1f);
            }

            bool textDirty = _lastBannerHiddenByMenu
                || phase != _lastBannerPhase
                || (phase == OpticalRifleTracker.CalibrationPhase.PreparingHeadset && secCeil != _lastBannerSec)
                || (phase == OpticalRifleTracker.CalibrationPhase.HoldingSteady && tenths != _lastBannerTenths)
                || (phase == OpticalRifleTracker.CalibrationPhase.WaitingForLeds &&
                    (ledMask != _lastBannerLedMask || camRunning != _lastBannerCameraRunning || waitingPerm != _lastBannerWaitingPerm));

            _lastBannerHiddenByMenu = false;
            if (!textDirty)
                return;

            _lastBannerPhase = phase;
            _lastBannerSec = secCeil;
            _lastBannerTenths = tenths;
            _lastBannerLedMask = ledMask;
            _lastBannerCameraRunning = camRunning;
            _lastBannerWaitingPerm = waitingPerm;

            Color accent;
            string title;
            string detail;

            switch (phase)
            {
                case OpticalRifleTracker.CalibrationPhase.PreparingHeadset:
                    accent = new Color(0.24f, 0.72f, 0.98f, 1f);
                    title = $"YÖN SIFIRLANDI  •  {secCeil}";
                    detail = "Telefonu VR'a takın ve tüfeği omuzlayın";
                    break;

                case OpticalRifleTracker.CalibrationPhase.WaitingForLeds:
                    accent = new Color(0.96f, 0.72f, 0.22f, 1f);
                    title = "NİŞAN POZİSYONUNDA BEKLEYİN";
                    if (waitingPerm)
                        detail = "Kamera izni bekleniyor...";
                    else if (!camRunning)
                        detail = "Arka kamera başlatılıyor...";
                    else
                        detail = $"LED bekleniyor  •  1:{(tracker.IsLedAFound ? "✓" : "✗")}  2:{(tracker.IsLedBFound ? "✓" : "✗")}";
                    break;

                case OpticalRifleTracker.CalibrationPhase.HoldingSteady:
                    accent = new Color(0.24f, 0.88f, 0.58f, 1f);
                    title = $"LED GÖRÜLDÜ — SABİT TUTUN  {rem:F1}s";
                    detail = "Tüfeği omuzda kıpırdatmadan bekleyin...";
                    break;

                default: // CompletedBanner
                    accent = new Color(0.22f, 0.94f, 0.45f, 1f);
                    title = "✓ SIFIRLAMA TAMAMLANDI!";
                    detail = $"Mesafe: {tracker.CurrentOpticalDistanceMeters * 100f:F0} cm  —  Atışa hazır";
                    break;
            }

            for (int i = 0; i < _views.Count; i++)
            {
                UiView view = _views[i];
                if (view.CalibrationAccentBar != null)
                    view.CalibrationAccentBar.color = accent;
                if (view.CalibrationProgressFillImage != null)
                    view.CalibrationProgressFillImage.color = accent;
                if (view.CalibrationTitleText != null)
                    view.CalibrationTitleText.text = title;
                if (view.CalibrationDetailText != null)
                    view.CalibrationDetailText.text = detail;
            }
        }

        private static T FindChildComponent<T>(Transform root, string path) where T : Component
        {
            if (root == null)
                return null;
            Transform target = root.Find(path);
            return target != null ? target.GetComponent<T>() : null;
        }

        private static void BindClick(Button btn, Action callback)
        {
            if (btn == null || callback == null)
                return;

            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() => callback());
        }

        private static void BindToggle(Toggle toggle, Action<bool> callback)
        {
            if (toggle == null || callback == null)
                return;

            toggle.onValueChanged.RemoveAllListeners();
            toggle.onValueChanged.AddListener(isOn => callback(isOn));
        }
    }
}
