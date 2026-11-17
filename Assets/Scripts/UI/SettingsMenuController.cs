using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using VRAtis.Ballistics;
using VRAtis.Environment;
using VRAtis.Hardware;
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
        }

        [Header("Referanslar")]
        [SerializeField] private DynamicTimeOfDay timeOfDayController;
        [SerializeField] private EnvironmentManager environmentManager;
        [SerializeField] private CardboardStereoRig stereoRig;

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

            if (primaryEyeRoot == null)
                primaryEyeRoot = transform.Find("PrimaryEye_UIRoot") as RectTransform;

            if (primaryEyeRoot == null)
                return;

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
            ApplyStereoLayoutIfNeeded(force: true);
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
            }
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
            for (int i = 0; i < _views.Count; i++)
            {
                if (_views[i].MainCategoryPage != null)
                    _views[i].MainCategoryPage.SetActive(true);
                if (_views[i].WeatherCategoryPage != null)
                    _views[i].WeatherCategoryPage.SetActive(false);
            }
            SyncVrWidgetsToViews();
        }

        public void ShowWeatherCategoryPage()
        {
            for (int i = 0; i < _views.Count; i++)
            {
                if (_views[i].MainCategoryPage != null)
                    _views[i].MainCategoryPage.SetActive(false);
                if (_views[i].WeatherCategoryPage != null)
                    _views[i].WeatherCategoryPage.SetActive(true);
            }

            SyncWeatherTogglesToViews();
            SyncTimeWidgetsFromScene();
        }

        private void SetWindowOpen(bool open)
        {
            for (int i = 0; i < _views.Count; i++)
            {
                if (_views[i].WindowRoot != null)
                    _views[i].WindowRoot.SetActive(open);
            }
        }

        private UiView ResolveAndBindView(RectTransform root)
        {
            Transform gearTr = root.Find("BottomRight_SettingsButton");
            Transform winTr = root.Find("SettingsWindow");
            Transform contentTr = winTr != null ? winTr.Find("ContentArea") : null;
            Transform mainTr = contentTr != null ? contentTr.Find("MainCategoryPage") : null;
            Transform weatherTr = contentTr != null ? contentTr.Find("WeatherCategoryPage") : null;
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
                OpenWeatherButton = FindChildComponent<Button>(mainTr, "WeatherMenuButton"),
                WeatherBackButton = FindChildComponent<Button>(weatherTr, "WeatherSubHeader/BackButton"),
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
                TimePresetToggles = new Toggle[timePresetHours != null ? timePresetHours.Length : 0]
            };

            for (int i = 0; i < view.TimePresetToggles.Length; i++)
                view.TimePresetToggles[i] = FindChildComponent<Toggle>(weatherTr, $"TimePreset_{i + 1}");

            BindViewListeners(view);
            return view;
        }

        private void BindViewListeners(UiView view)
        {
            BindClick(view.GearButton, ToggleWindow);
            BindClick(view.OpenWeatherButton, ShowWeatherCategoryPage);
            BindClick(view.WeatherBackButton, ShowMainCategoryPage);
            BindClick(view.CloseWindowButton, CloseWindow);

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
    }
}
