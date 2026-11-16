using System;
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
    /// Controls the bottom-right Settings icon, expandable Settings window,
    /// Weather sub-panel with independent Checkboxes (Rain / Fog) and
    /// bidirectional Time-of-Day Radio Buttons synced with DynamicTimeOfDay keys 1-5.
    /// </summary>
    public class SettingsMenuController : MonoBehaviour
    {
        [Header("Referanslar")]
        [SerializeField] private DynamicTimeOfDay timeOfDayController;
        [SerializeField] private EnvironmentManager environmentManager;

        [Header("UI Panelleri ve Butonlar")]
        [SerializeField] private Button settingsGearButton;
        [SerializeField] private GameObject settingsWindowRoot;
        [SerializeField] private GameObject mainCategoryPage;
        [SerializeField] private GameObject weatherCategoryPage;
        [SerializeField] private Button openWeatherCategoryButton;
        [SerializeField] private Button weatherBackButton;
        [SerializeField] private Button closeWindowButton;

        [Header("Hava Koşulları (Checkbox)")]
        [SerializeField] private Toggle rainCheckbox;
        [SerializeField] private Toggle fogCheckbox;

        [Header("Günün Zamanı (Radio Butonlar - 1..5 Tuşları)")]
        [SerializeField] private Toggle[] timePresetToggles;
        [SerializeField] private float[] timePresetHours = { 7.2f, 12.5f, 17.1f, 18.6f, 21.5f };
        [SerializeField] private Toggle autoTimeAdvanceCheckbox;

        [Header("VR Görünürlük")]
        [Tooltip("Açılırsa yalnızca telefondaki VR çift ekran modunda sağ alt ayar simgesini gizler.")]
        [SerializeField] private bool hideCornerIconInMobileVr = false;

        public event Action<bool, bool> OnWeatherTogglesChanged;

        public bool IsMenuOpen => settingsWindowRoot != null && settingsWindowRoot.activeSelf;
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

            WireUiEvents();

            if (settingsWindowRoot != null)
                settingsWindowRoot.SetActive(false);

            ShowMainCategoryPage();
        }

        private void Start()
        {
            if (environmentManager != null)
                IsRainSelected = environmentManager.isRaining;

            if (rainCheckbox != null)
                rainCheckbox.SetIsOnWithoutNotify(IsRainSelected);
            if (fogCheckbox != null)
                fogCheckbox.SetIsOnWithoutNotify(IsFogSelected);

            SyncTimeWidgetsFromScene();
        }

        private void Update()
        {
            if (settingsGearButton != null)
            {
                bool hideGear = hideCornerIconInMobileVr &&
                                Application.isMobilePlatform &&
                                CardboardStereoRig.IsStereoSplitActive &&
                                !IsMenuOpen;

                if (settingsGearButton.gameObject.activeSelf == hideGear)
                    settingsGearButton.gameObject.SetActive(!hideGear);
            }

#if ENABLE_INPUT_SYSTEM
            if (IsMenuOpen && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
                CloseWindow();
#endif

            if (IsMenuOpen && weatherCategoryPage != null && weatherCategoryPage.activeInHierarchy)
                SyncTimeWidgetsFromScene();
        }

        public void ToggleWindow()
        {
            if (settingsWindowRoot == null)
                return;

            bool next = !settingsWindowRoot.activeSelf;
            settingsWindowRoot.SetActive(next);
            if (next)
            {
                ShowMainCategoryPage();
                SyncTimeWidgetsFromScene();
            }
        }

        public void CloseWindow()
        {
            if (settingsWindowRoot != null)
                settingsWindowRoot.SetActive(false);
        }

        public void ShowMainCategoryPage()
        {
            if (mainCategoryPage != null)
                mainCategoryPage.SetActive(true);
            if (weatherCategoryPage != null)
                weatherCategoryPage.SetActive(false);
        }

        public void ShowWeatherCategoryPage()
        {
            if (mainCategoryPage != null)
                mainCategoryPage.SetActive(false);
            if (weatherCategoryPage != null)
                weatherCategoryPage.SetActive(true);

            if (rainCheckbox != null)
                rainCheckbox.SetIsOnWithoutNotify(IsRainSelected);
            if (fogCheckbox != null)
                fogCheckbox.SetIsOnWithoutNotify(IsFogSelected);

            SyncTimeWidgetsFromScene();
        }

        private void WireUiEvents()
        {
            if (settingsGearButton != null)
            {
                settingsGearButton.onClick.RemoveListener(ToggleWindow);
                settingsGearButton.onClick.AddListener(ToggleWindow);
            }

            if (openWeatherCategoryButton != null)
            {
                openWeatherCategoryButton.onClick.RemoveListener(ShowWeatherCategoryPage);
                openWeatherCategoryButton.onClick.AddListener(ShowWeatherCategoryPage);
            }

            if (weatherBackButton != null)
            {
                weatherBackButton.onClick.RemoveListener(ShowMainCategoryPage);
                weatherBackButton.onClick.AddListener(ShowMainCategoryPage);
            }

            if (closeWindowButton != null)
            {
                closeWindowButton.onClick.RemoveListener(CloseWindow);
                closeWindowButton.onClick.AddListener(CloseWindow);
            }

            if (rainCheckbox != null)
            {
                rainCheckbox.onValueChanged.RemoveAllListeners();
                rainCheckbox.onValueChanged.AddListener(HandleRainChanged);
            }

            if (fogCheckbox != null)
            {
                fogCheckbox.onValueChanged.RemoveAllListeners();
                fogCheckbox.onValueChanged.AddListener(HandleFogChanged);
            }

            if (autoTimeAdvanceCheckbox != null)
            {
                autoTimeAdvanceCheckbox.onValueChanged.RemoveAllListeners();
                autoTimeAdvanceCheckbox.onValueChanged.AddListener(HandleAutoTimeChanged);
            }

            if (timePresetToggles != null)
            {
                for (int i = 0; i < timePresetToggles.Length; i++)
                {
                    Toggle toggle = timePresetToggles[i];
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
        }

        private void HandleRainChanged(bool isOn)
        {
            IsRainSelected = isOn;
            if (environmentManager != null)
                environmentManager.isRaining = isOn;

            OnWeatherTogglesChanged?.Invoke(IsRainSelected, IsFogSelected);
        }

        private void HandleFogChanged(bool isOn)
        {
            IsFogSelected = isOn;
            OnWeatherTogglesChanged?.Invoke(IsRainSelected, IsFogSelected);
        }

        private void HandleTimePresetSelected(int index)
        {
            if (timeOfDayController == null || timePresetHours == null || index < 0 || index >= timePresetHours.Length)
                return;

            timeOfDayController.ApplyTimePreset(timePresetHours[index]);

            if (autoTimeAdvanceCheckbox != null)
                autoTimeAdvanceCheckbox.SetIsOnWithoutNotify(false);
        }

        private void HandleAutoTimeChanged(bool isOn)
        {
            if (timeOfDayController != null)
                timeOfDayController.SetAutoAdvanceTime(isOn);
        }

        private void SyncTimeWidgetsFromScene()
        {
            if (timeOfDayController == null)
                return;

            if (autoTimeAdvanceCheckbox != null)
                autoTimeAdvanceCheckbox.SetIsOnWithoutNotify(timeOfDayController.AutoAdvanceTime);

            if (timePresetToggles == null || timePresetHours == null)
                return;

            float current = timeOfDayController.CurrentTimeOfDayHours;
            int matchedIndex = -1;
            float bestDiff = 0.35f;

            int count = Mathf.Min(timePresetToggles.Length, timePresetHours.Length);
            for (int i = 0; i < count; i++)
            {
                float diff = Mathf.Abs(Mathf.DeltaAngle(current * 15f, timePresetHours[i] * 15f)) / 15f;
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    matchedIndex = i;
                }
            }

            for (int i = 0; i < timePresetToggles.Length; i++)
            {
                if (timePresetToggles[i] != null)
                    timePresetToggles[i].SetIsOnWithoutNotify(i == matchedIndex);
            }
        }
    }
}
