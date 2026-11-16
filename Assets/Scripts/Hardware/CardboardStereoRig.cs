using UnityEngine;
using UnityEngine.Rendering.Universal;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace VRAtis.Hardware
{
    /// <summary>
    /// Converts Main Camera between full single-screen mode and Cardboard / VR Shinecon
    /// side-by-side (SBS) stereoscopic VR mode.
    /// Reads true physical Android DisplayMetrics.xdpi (not bucketed densityDpi) and aligns
    /// each eye's optical center to physical lens spacing so large displays fuse with zero double vision.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public class CardboardStereoRig : MonoBehaviour
    {
        private const string PrefsLensSpacingKey = "VRAtis_LensSpacingMm_V1";

        public static bool IsStereoSplitActive { get; private set; }
        public static Rect LeftEyeViewportNorm { get; private set; } = new Rect(0.11f, 0f, 0.39f, 1f);
        public static Rect RightEyeViewportNorm { get; private set; } = new Rect(0.5f, 0f, 0.39f, 1f);
        public static float ActiveLensSpacingMm { get; private set; } = 66f;
        public static float DetectedPhoneWidthMm { get; private set; } = 154f;

        [Header("VR Ekran Bölme (Cardboard / VR Shinecon)")]
        [Tooltip("Açıkken ekranı VR gözlük için sol/sağ göz olarak ikiye böler. Kapatınca tek ekrana döner.")]
        [SerializeField] private bool enableSplitScreen = true;

        [Tooltip("Play Mode içinde F2 tuşu ile tek ekran <-> çift ekran geçişine izin verir.")]
        [SerializeField] private bool allowVKeyToggle = true;

        [Header("VR Kutu Optik & Ekran Hizalama (Çatallanma Önleme)")]
        [Tooltip("VR kutusundaki sol ve sağ mercek merkezleri arası fiziksel mesafe (mm). 66 mm olarak sabitlendi.")]
        [Range(52f, 74f)]
        [SerializeField] private float lensCenterSpacingMm = 66f;

        [Tooltip("Telefonda gerçek fiziksel ekran DPI'sını (Android DisplayMetrics.xdpi) okuyarak ekran enini hesaplar.")]
        [SerializeField] private bool autoDetectPhysicalPhoneWidth = true;

        [Tooltip("Editör önizlemesinde veya sensör/DPI okunamazsa kullanılacak yatay ekran genişliği (mm).")]
        [Range(100f, 180f)]
        [SerializeField] private float fallbackPhoneWidthMm = 154f;

        [Tooltip("Oyun içinde VR HUD altından -1 / +1 mm ince ayar yapılabilsin ve hatırlansın.")]
        [SerializeField] private bool allowLiveCalibrationButtons = false;

        [Header("3D Derinlik ve Görüş Açısı")]
        [Tooltip("Sanal göz kamerası aralığı (mm). Yakındaki tüfeğin sabit odaklı mercekte çiftleşmemesi için 42-48 mm önerilir.")]
        [Range(24f, 66f)]
        [SerializeField] private float virtualEyeIpdMm = 45f;

        [Tooltip("VR çift göz dikey görüş açısı (derece).")]
        [Range(55f, 90f)]
        [SerializeField] private float vrVerticalFov = 68f;

        [Tooltip("Tek ekran modundaki normal dikey görüş açısı (derece).")]
        [Range(45f, 85f)]
        [SerializeField] private float singleScreenFov = 64f;

        [Tooltip("Mercek dışındaki kenar boşluklarını ve orta çizgiyi siyaha boyar.")]
        [SerializeField] private bool drawBlackMaskBorders = true;

        private Camera _mainCam;
        private UniversalAdditionalCameraData _mainUrpData;
        private Camera _backdropClearCam;
        private Camera _leftEyeCam;
        private Camera _rightEyeCam;
        private string _originalMainTag = "MainCamera";
        private bool _appliedStateInitialized;
        private bool _lastAppliedSplitState;
        private float _lastAppliedLensSpacingMm;
        private float _lastAppliedVirtualIpdMm;
        private float _lastAppliedVrFov;
        private float _lastAppliedSingleFov;
        private int _lastScreenWidth;
        private int _lastScreenHeight;
        private float _cachedPhysicalWidthMm;
        private float _dynamicFovDeltaDeg;
        private GUIStyle _calibBtnStyle;

        public float HalfVirtualIpdMeters => IsStereoSplitActive ? (virtualEyeIpdMm * 0.001f) * 0.5f : 0f;

        public void SetDynamicFovOffset(float fovDeltaDeg)
        {
            if (Mathf.Approximately(_dynamicFovDeltaDeg, fovDeltaDeg))
                return;

            _dynamicFovDeltaDeg = fovDeltaDeg;
            if (!Application.isPlaying)
                return;

            if (IsStereoSplitActive)
            {
                float fov = Mathf.Clamp(vrVerticalFov + _dynamicFovDeltaDeg, 35f, 100f);
                if (_leftEyeCam != null) _leftEyeCam.fieldOfView = fov;
                if (_rightEyeCam != null) _rightEyeCam.fieldOfView = fov;
            }
            else if (_mainCam != null)
            {
                _mainCam.fieldOfView = Mathf.Clamp(singleScreenFov + _dynamicFovDeltaDeg, 35f, 95f);
            }
        }

        public bool EnableSplitScreen
        {
            get => enableSplitScreen;
            set
            {
                if (enableSplitScreen == value)
                    return;
                enableSplitScreen = value;
                if (Application.isPlaying)
                    ApplyStereoMode(forceSync: true);
            }
        }

        public void NudgeLensSpacingMm(float deltaMm)
        {
            lensCenterSpacingMm = Mathf.Clamp(lensCenterSpacingMm + deltaMm, 52f, 74f);
            if (allowLiveCalibrationButtons && Application.isMobilePlatform)
            {
                PlayerPrefs.SetFloat(PrefsLensSpacingKey, lensCenterSpacingMm);
                PlayerPrefs.Save();
            }
            ApplyStereoMode(forceSync: false);
        }

        private void Awake()
        {
            _mainCam = GetComponent<Camera>();
            _mainUrpData = GetComponent<UniversalAdditionalCameraData>();
            _originalMainTag = gameObject.tag;

            if (allowLiveCalibrationButtons && Application.isMobilePlatform && PlayerPrefs.HasKey(PrefsLensSpacingKey))
                lensCenterSpacingMm = Mathf.Clamp(PlayerPrefs.GetFloat(PrefsLensSpacingKey, lensCenterSpacingMm), 52f, 74f);

            _cachedPhysicalWidthMm = MeasurePhysicalPhoneWidthMm();
            DetectedPhoneWidthMm = _cachedPhysicalWidthMm;
        }

        private void OnEnable()
        {
            if (Application.isPlaying)
                ApplyStereoMode(forceSync: true);
        }

        private void OnDisable()
        {
            if (Application.isPlaying)
                RestoreSingleScreenState();
        }

        private void OnDestroy()
        {
            if (IsStereoSplitActive)
                IsStereoSplitActive = false;
        }

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            if (allowVKeyToggle && Keyboard.current != null && Keyboard.current.f2Key.wasPressedThisFrame)
                enableSplitScreen = !enableSplitScreen;

            if (Keyboard.current != null)
            {
                if (Keyboard.current.leftBracketKey.wasPressedThisFrame)
                    NudgeLensSpacingMm(-1f);
                else if (Keyboard.current.rightBracketKey.wasPressedThisFrame)
                    NudgeLensSpacingMm(+1f);
            }
#endif

            if (!_appliedStateInitialized ||
                _lastAppliedSplitState != enableSplitScreen ||
                !Mathf.Approximately(_lastAppliedLensSpacingMm, lensCenterSpacingMm) ||
                !Mathf.Approximately(_lastAppliedVirtualIpdMm, virtualEyeIpdMm) ||
                !Mathf.Approximately(_lastAppliedVrFov, vrVerticalFov) ||
                !Mathf.Approximately(_lastAppliedSingleFov, singleScreenFov) ||
                _lastScreenWidth != Screen.width ||
                _lastScreenHeight != Screen.height)
            {
                ApplyStereoMode(forceSync: false);
            }
        }

        private void ApplyStereoMode(bool forceSync)
        {
            if (_mainCam == null)
                _mainCam = GetComponent<Camera>();
            if (_mainUrpData == null)
                _mainUrpData = GetComponent<UniversalAdditionalCameraData>();

            _appliedStateInitialized = true;
            _lastAppliedSplitState = enableSplitScreen;
            _lastAppliedLensSpacingMm = lensCenterSpacingMm;
            _lastAppliedVirtualIpdMm = virtualEyeIpdMm;
            _lastAppliedVrFov = vrVerticalFov;
            _lastAppliedSingleFov = singleScreenFov;
            _lastScreenWidth = Screen.width;
            _lastScreenHeight = Screen.height;
            ActiveLensSpacingMm = lensCenterSpacingMm;

            if (enableSplitScreen)
            {
                EnsureEyeCamerasCreated();
                ComputeOpticalAlignedViewports(out Rect leftRect, out Rect rightRect);
                LeftEyeViewportNorm = leftRect;
                RightEyeViewportNorm = rightRect;

                float halfVirtualIpdMeters = (virtualEyeIpdMm * 0.001f) * 0.5f;
                _leftEyeCam.transform.localPosition = new Vector3(-halfVirtualIpdMeters, 0f, 0f);
                _rightEyeCam.transform.localPosition = new Vector3(+halfVirtualIpdMeters, 0f, 0f);
                _leftEyeCam.transform.localRotation = Quaternion.identity;
                _rightEyeCam.transform.localRotation = Quaternion.identity;

                if (forceSync)
                {
                    SyncEyeCamera(_leftEyeCam, leftRect);
                    SyncEyeCamera(_rightEyeCam, rightRect);
                }
                else
                {
                    float fov = Mathf.Clamp(vrVerticalFov + _dynamicFovDeltaDeg, 35f, 100f);
                    _leftEyeCam.fieldOfView = fov;
                    _rightEyeCam.fieldOfView = fov;
                    _leftEyeCam.rect = leftRect;
                    _rightEyeCam.rect = rightRect;
                }

                _leftEyeCam.gameObject.tag = string.IsNullOrEmpty(_originalMainTag) ? "MainCamera" : _originalMainTag;
                _rightEyeCam.gameObject.tag = "Untagged";

                if (_backdropClearCam != null)
                {
                    _backdropClearCam.depth = _mainCam.depth - 10f;
                    _backdropClearCam.enabled = drawBlackMaskBorders;
                }

                _leftEyeCam.enabled = true;
                _rightEyeCam.enabled = true;
                _mainCam.enabled = false;
                IsStereoSplitActive = true;
            }
            else
            {
                RestoreSingleScreenState();
            }
        }

        private float MeasurePhysicalPhoneWidthMm()
        {
            float widthMm = fallbackPhoneWidthMm;

            if (!Application.isMobilePlatform || !autoDetectPhysicalPhoneWidth)
                return widthMm;

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var windowManager = activity.Call<AndroidJavaObject>("getWindowManager"))
                using (var display = windowManager.Call<AndroidJavaObject>("getDefaultDisplay"))
                using (var metrics = new AndroidJavaObject("android.util.DisplayMetrics"))
                {
                    display.Call("getRealMetrics", metrics);
                    float xdpi = metrics.Get<float>("xdpi");
                    float ydpi = metrics.Get<float>("ydpi");
                    int wPx = metrics.Get<int>("widthPixels");
                    int hPx = metrics.Get<int>("heightPixels");
                    float physDpi = wPx >= hPx ? xdpi : ydpi;
                    float longPx = Mathf.Max(wPx, hPx);

                    if (physDpi > 100f && longPx > 400f)
                        widthMm = Mathf.Clamp((longPx / physDpi) * 25.4f, 115f, 185f);
                }
            }
            catch
            {
                // Fallback to Screen.dpi only if plausible
                if (Screen.dpi > 120f && Screen.width > 0)
                {
                    float longPx = Mathf.Max(Screen.width, Screen.height);
                    widthMm = Mathf.Clamp((longPx / Screen.dpi) * 25.4f, 115f, 185f);
                }
            }
#endif
            return widthMm;
        }

        private void ComputeOpticalAlignedViewports(out Rect leftRect, out Rect rightRect)
        {
            float phoneWidthMm = _cachedPhysicalWidthMm > 80f ? _cachedPhysicalWidthMm : fallbackPhoneWidthMm;
            DetectedPhoneWidthMm = phoneWidthMm;

            float eyeWidthNorm = Mathf.Clamp(lensCenterSpacingMm / Mathf.Max(65f, phoneWidthMm), 0.25f, 0.498f);
            float centerGapHalf = drawBlackMaskBorders ? 0.0012f : 0f;
            leftRect = new Rect(0.5f - centerGapHalf - eyeWidthNorm, 0f, eyeWidthNorm, 1f);
            rightRect = new Rect(0.5f + centerGapHalf, 0f, eyeWidthNorm, 1f);
        }

        private void RestoreSingleScreenState()
        {
            if (_backdropClearCam != null)
                _backdropClearCam.enabled = false;
            if (_leftEyeCam != null)
            {
                _leftEyeCam.gameObject.tag = "Untagged";
                _leftEyeCam.enabled = false;
            }
            if (_rightEyeCam != null)
                _rightEyeCam.enabled = false;

            if (_mainCam != null)
            {
                _mainCam.rect = new Rect(0f, 0f, 1f, 1f);
                _mainCam.fieldOfView = Mathf.Clamp(singleScreenFov + _dynamicFovDeltaDeg, 35f, 95f);
                _mainCam.enabled = true;
            }

            IsStereoSplitActive = false;
        }

        private void EnsureEyeCamerasCreated()
        {
            if (_backdropClearCam == null)
            {
                _backdropClearCam = FindOrCreateEyeCamera("VR_BlackBackdropCamera");
                _backdropClearCam.clearFlags = CameraClearFlags.SolidColor;
                _backdropClearCam.backgroundColor = Color.black;
                _backdropClearCam.cullingMask = 0;
                _backdropClearCam.useOcclusionCulling = false;
                _backdropClearCam.allowHDR = false;
                _backdropClearCam.allowMSAA = false;
                _backdropClearCam.rect = new Rect(0f, 0f, 1f, 1f);
                UniversalAdditionalCameraData bdUrp = _backdropClearCam.GetComponent<UniversalAdditionalCameraData>();
                if (bdUrp != null)
                {
                    bdUrp.renderShadows = false;
                    bdUrp.renderPostProcessing = false;
                }
            }
            if (_leftEyeCam == null)
                _leftEyeCam = FindOrCreateEyeCamera("VR_LeftEyeCamera");
            if (_rightEyeCam == null)
                _rightEyeCam = FindOrCreateEyeCamera("VR_RightEyeCamera");
        }

        private Camera FindOrCreateEyeCamera(string eyeName)
        {
            Transform existing = transform.Find(eyeName);
            GameObject go = existing != null ? existing.gameObject : new GameObject(eyeName);
            if (existing == null)
                go.transform.SetParent(transform, false);

            Camera cam = go.GetComponent<Camera>();
            if (cam == null)
                cam = go.AddComponent<Camera>();

            if (go.GetComponent<UniversalAdditionalCameraData>() == null)
                go.AddComponent<UniversalAdditionalCameraData>();

            return cam;
        }

        private void SyncEyeCamera(Camera eyeCam, Rect viewportRect)
        {
            if (eyeCam == null || _mainCam == null)
                return;

            eyeCam.clearFlags = _mainCam.clearFlags;
            eyeCam.backgroundColor = _mainCam.backgroundColor;
            eyeCam.cullingMask = _mainCam.cullingMask;
            eyeCam.nearClipPlane = _mainCam.nearClipPlane;
            eyeCam.farClipPlane = _mainCam.farClipPlane;
            eyeCam.fieldOfView = Mathf.Clamp(vrVerticalFov + _dynamicFovDeltaDeg, 35f, 100f);
            eyeCam.depth = _mainCam.depth;
            eyeCam.allowHDR = _mainCam.allowHDR;
            eyeCam.allowMSAA = _mainCam.allowMSAA;
            eyeCam.useOcclusionCulling = _mainCam.useOcclusionCulling;
            eyeCam.rect = viewportRect;

            UniversalAdditionalCameraData eyeUrp = eyeCam.GetComponent<UniversalAdditionalCameraData>();
            if (eyeUrp != null && _mainUrpData != null)
            {
                eyeUrp.renderShadows = _mainUrpData.renderShadows;
                eyeUrp.renderPostProcessing = _mainUrpData.renderPostProcessing;
                eyeUrp.antialiasing = _mainUrpData.antialiasing;
                eyeUrp.antialiasingQuality = _mainUrpData.antialiasingQuality;
                eyeUrp.volumeLayerMask = _mainUrpData.volumeLayerMask;
                eyeUrp.requiresDepthOption = _mainUrpData.requiresDepthOption;
                eyeUrp.requiresColorOption = _mainUrpData.requiresColorOption;
            }
        }

        private void OnGUI()
        {
            if (!IsStereoSplitActive || !Application.isPlaying || !allowLiveCalibrationButtons)
                return;

            if (_calibBtnStyle == null)
            {
                _calibBtnStyle = new GUIStyle(GUI.skin.button)
                {
                    fontSize = 15,
                    fontStyle = FontStyle.Bold
                };
            }

            float btnW = 68f;
            float btnH = 38f;
            float yBottom = Screen.height - btnH - 22f;
            float leftCenterX = (LeftEyeViewportNorm.x + LeftEyeViewportNorm.width * 0.5f) * Screen.width;
            float rightCenterX = (RightEyeViewportNorm.x + RightEyeViewportNorm.width * 0.5f) * Screen.width;

            if (GUI.Button(new Rect(leftCenterX - btnW - 8f, yBottom, btnW, btnH), "-1 mm", _calibBtnStyle) ||
                GUI.Button(new Rect(rightCenterX - btnW - 8f, yBottom, btnW, btnH), "-1 mm", _calibBtnStyle))
                NudgeLensSpacingMm(-1f);

            if (GUI.Button(new Rect(leftCenterX + 8f, yBottom, btnW, btnH), "+1 mm", _calibBtnStyle) ||
                GUI.Button(new Rect(rightCenterX + 8f, yBottom, btnW, btnH), "+1 mm", _calibBtnStyle))
                NudgeLensSpacingMm(+1f);
        }
    }
}
