using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using VRAtis.Weapons;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace VRAtis.Hardware
{
    /// <summary>
    /// Tracks 2 rifle-mounted glowing LEDs using the phone's rear camera at low resolution (320x240)
    /// on a background worker thread (0 GC allocations during tracking) and combines pixel positions
    /// with ESP32 + head quaternions to de-foreshorten 3D rifle position (X, Y, Z) at 120Hz smoothness.
    /// </summary>
    [DefaultExecutionOrder(10)]
    public class OpticalRifleTracker : MonoBehaviour
    {
        public enum LedTargetMode
        {
            GreenAndRed = 0,
            GreenAndBlue = 1,
            DualGreen = 2,
            DualRed = 3
        }

        public enum BaselineAxis
        {
            HorizontalCrossbar = 0,
            VerticalStack = 1,
            LongitudinalRail = 2
        }

        public enum CalibrationPhase
        {
            Idle = 0,
            PreparingHeadset = 1,
            WaitingForLeds = 2,
            HoldingSteady = 3,
            CompletedBanner = 4
        }

        private struct BlobCandidate
        {
            public float CenterX;
            public float CenterY;
            public int PixelCount;
            public float TotalWeight;
            public float QualityScore;
            public int MinX;
            public int MaxX;
            public int MinY;
            public int MaxY;
        }

        [Header("Referanslar")]
        [SerializeField] private Transform headCameraTransform;
        [SerializeField] private WeaponAimController weaponAimController;
        [SerializeField] private RedDotSightController redDotSight;
        [SerializeField] private Transform redDotAnchor;
        [SerializeField] private Esp32RifleReceiver rifleReceiver;

        [Header("Optik Takip Ayarları (Telefon Arka Kamerası + 2 LED)")]
        [SerializeField] private bool enableOpticalTracking = true;
        [SerializeField] private bool runWebcamInEditor = false;
        [SerializeField] private bool flipCamera180Override = false;
        [SerializeField] private LedTargetMode targetMode = LedTargetMode.GreenAndRed;
        [SerializeField] private BaselineAxis baselineAxis = BaselineAxis.HorizontalCrossbar;

        [Tooltip("İki LED merkezleri arası fiziksel mesafe (metre). 6 cm = 0.06.")]
        [Range(0.03f, 0.35f)]
        [SerializeField] private float ledSpacingMeters = 0.06f;

        [Tooltip("Opsiyonel kol manivelası ofseti (varsayılan 0: kafa döndürmede tam eksen oranını korur).")]
        [SerializeField] private Vector3 ledOffsetFromPivotMeters = Vector3.zero;

        [Tooltip("Arka ana kameranın yatay görüş açısı (derece). Samsung Galaxy A26 ana kamera ~76°, ultra-geniş ~110°.")]
        [Range(55f, 130f)]
        [SerializeField] private float cameraHorizontalFovDeg = 76f;

        [Tooltip("Arka plan lambalarını elemek için renk baskınlık eşiği (105 önerilir; karanlıkta düşürün, parlak ortamda yükseltin).")]
        [Range(15, 150)]
        [SerializeField] private int minColorDominance = 105;

        [Range(35, 200)]
        [SerializeField] private int minPixelBrightness = 72;

        [Tooltip("Hareket ölçeği: 1.0 = birebir gerçek santimetre (sağ-sol/yukarı-aşağı).")]
        [Range(0.5f, 2.0f)]
        [SerializeField] private float positionScale = 1.0f;

        [Tooltip("İleri-geri (Z) derinlik hassasiyeti çarpanı. Tek kamera piksel gürültüsünde ileri sıçramaları önlemek için 0.22 önerilir.")]
        [Range(0.0f, 1.5f)]
        [SerializeField] private float zDepthScale = 0.22f;

        [Header("Kamera Çözünürlüğü (320x240 Düşük Yük / Akıcı Takip)")]
        [SerializeField] private int requestedWidth = 320;
        [SerializeField] private int requestedHeight = 240;
        [SerializeField] private int requestedFps = 30;

        [Header("Hazır Pozisyon ve Gövde Takibi (LED Görüş Dışı Kaldığında)")]
        [Tooltip("LED'ler kameradan çıktığında veya henüz LED takılmadığında tüfeği vücut yönüyle birlikte taşır.")]
        [SerializeField] private bool enableBodyYawFollowFallback = true;
        [SerializeField] private Vector3 readyLocalOffsetMeters = new Vector3(0.08f, -0.14f, 0.25f);
        [Range(5f, 45f)]
        [SerializeField] private float bodyYawFreeLookDeadzoneDeg = 22f;

        [Header("120Hz Yumuşatma ve Red Dot Otomatik Açma")]
        [Range(4f, 45f)]
        [SerializeField] private float minPositionSmoothSpeed = 12f;
        [Range(15f, 90f)]
        [SerializeField] private float maxPositionSmoothSpeed = 38f;
        [SerializeField] private float maxSingleFrameJumpMeters = 0.18f;
        [SerializeField] private bool autoPowerRedDotNearEye = false;
        [Range(0.06f, 0.25f)]
        [SerializeField] private float redDotWakeLateralMeters = 0.15f;

        [Header("Kılavuzlu Sıfırlama Zamanlayıcıları")]
        [Tooltip("2 parmak dokunuşu sonrası telefonu VR kutusuna takıp tüfeği omuzlamak için bekleme süresi (saniye).")]
        [Range(0f, 10f)]
        [SerializeField] private float vrInsertGraceSeconds = 5.0f;
        [Tooltip("LED'ler görüldükten sonra sıfır kilitlemek için tüfeği sabit tutma süresi (saniye).")]
        [Range(0.4f, 10.0f)]
        [SerializeField] private float steadyHoldSeconds = 5.0f;
        [SerializeField] private float completedBannerDurationSeconds = 2.0f;

        [Header("Optik 2-LED Yatay Kayma Kilidi (Opsiyonel)")]
        [Tooltip("Kapalı (önerilen): Çoklu hedeflerde omuz-göz paralaksından yapay kayma oluşturmaz; kamera yalnız 3B pozisyon (X,Y,Z) izler.")]
        [SerializeField] private bool enableOpticalYawFusion = false;
        [Tooltip("Red Dot (ADS) açıkken optik yatay kayma düzeltme zaman sabiti (sn).")]
        [Range(0.4f, 4.0f)]
        [SerializeField] private float adsYawFusionTimeConstantSec = 1.60f;
        [Tooltip("Hazır duruşta (Red Dot kapalı) optik yatay kayma düzeltme zaman sabiti (sn).")]
        [Range(0.8f, 5.0f)]
        [SerializeField] private float readyYawFusionTimeConstantSec = 2.40f;
        [Tooltip("Alt-piksel kamera gürültüsünü önleyen ölü bölge (derece).")]
        [Range(0.05f, 1.5f)]
        [SerializeField] private float opticalYawDeadbandDeg = 0.35f;
        [Tooltip("Maksimum optik yatay düzeltme hızı (derece/sn). Sıçramasız yumuşak düzeltme sağlar.")]
        [Range(1f, 15f)]
        [SerializeField] private float maxOpticalYawCorrectionDegPerSec = 4f;
        [Tooltip("Bu hızın (derece/sn) üzerindeki hareketlerde optik füzyon bekler, 1000 Hz ESP32 IMU tek başına sürer.")]
        [Range(5f, 45f)]
        [SerializeField] private float maxFusionAngularSpeedDegPerSec = 10f;
        [Tooltip("ADS yanak temasında LED'ler referans açıdan bu kadar saparsa (hedef geçişi/paralaks) füzyon duraklatılır.")]
        [Range(1.0f, 12f)]
        [SerializeField] private float maxAdsBearingDeviationDeg = 2.5f;
        [Range(2.0f, 15f)]
        [SerializeField] private float maxReadyBearingDeviationDeg = 4.0f;
        [Tooltip("Oda lambası gibi sahte yansımalara karşı maksimum tek seferlik açı farkı sınırı (derece).")]
        [Range(8f, 35f)]
        [SerializeField] private float maxOpticalYawInnovationDeg = 18.0f;

        // Public status properties for Settings UI & WeaponAimController
        public bool IsOpticalTrackingEnabled
        {
            get => enableOpticalTracking;
            set
            {
                if (enableOpticalTracking == value)
                    return;
                enableOpticalTracking = value;
                if (value)
                {
                    TryStartCamera();
                    if (!_hasCalibratedZero && IsTrackingRuntimeSupported)
                        _calibrationPhase = CalibrationPhase.WaitingForLeds;
                }
                else
                {
                    StopCamera();
                    _calibrationPhase = CalibrationPhase.Idle;
                }
            }
        }

        public LedTargetMode CurrentTargetMode
        {
            get => targetMode;
            set
            {
                if (targetMode == value)
                    return;
                targetMode = value;
                _pendingZeroCapture = true;
                _resetWorkerContinuity = true;
            }
        }

        public BaselineAxis CurrentBaselineAxis
        {
            get => baselineAxis;
            set
            {
                if (baselineAxis == value)
                    return;
                baselineAxis = value;
                _pendingZeroCapture = true;
            }
        }

        public float LedSpacingMeters
        {
            get => ledSpacingMeters;
            set
            {
                float clamped = Mathf.Clamp(value, 0.03f, 0.35f);
                if (Mathf.Approximately(ledSpacingMeters, clamped))
                    return;
                ledSpacingMeters = clamped;
                _pendingZeroCapture = true;
            }
        }

        public int MinColorDominance
        {
            get => minColorDominance;
            set => minColorDominance = Mathf.Clamp(value, 15, 150);
        }

        public bool IsTrackingRuntimeSupported => enableOpticalTracking && (Application.isMobilePlatform || runWebcamInEditor);
        public bool IsCameraRunning => _webCamTex != null && _webCamTex.isPlaying;
        public bool IsActivelyTrackingPosition => enableOpticalTracking && IsCameraRunning && _hasStableLock && _hasCalibratedZero;
        public bool IsOpticalYawFusionEnabled
        {
            get => enableOpticalYawFusion;
            set
            {
                enableOpticalYawFusion = value;
                if (!value)
                {
                    IsOpticalYawFusionActive = false;
                    _activeOpticalYawRateDegPerSec = 0f;
                }
            }
        }
        public bool IsOpticalYawFusionActive { get; private set; }
        public float LastOpticalYawErrorDeg { get; private set; }
        public float LearnedOpticalYawDriftRateDegPerSec { get; private set; }
        public bool IsLedAFound { get; private set; }
        public bool IsLedBFound { get; private set; }
        public bool HasCalibratedZero => _hasCalibratedZero;
        public CalibrationPhase CurrentCalibrationPhase => IsTrackingRuntimeSupported ? _calibrationPhase : CalibrationPhase.Idle;
        public float CalibrationRemainingSeconds
        {
            get
            {
                switch (_calibrationPhase)
                {
                    case CalibrationPhase.PreparingHeadset:
                        return Mathf.Max(0f, _calPhaseTimer);
                    case CalibrationPhase.HoldingSteady:
                        return Mathf.Max(0f, steadyHoldSeconds - _steadyHoldTimer);
                    case CalibrationPhase.CompletedBanner:
                        return Mathf.Max(0f, _calPhaseTimer);
                    default:
                        return 0f;
                }
            }
        }
        public float CalibrationProgress01
        {
            get
            {
                switch (_calibrationPhase)
                {
                    case CalibrationPhase.PreparingHeadset:
                        return vrInsertGraceSeconds > 0.01f ? Mathf.Clamp01(_calPhaseTimer / vrInsertGraceSeconds) : 0f;
                    case CalibrationPhase.WaitingForLeds:
                        return 0f;
                    case CalibrationPhase.HoldingSteady:
                        return steadyHoldSeconds > 0.01f ? Mathf.Clamp01(_steadyHoldTimer / steadyHoldSeconds) : 1f;
                    case CalibrationPhase.CompletedBanner:
                        return 1f;
                    default:
                        return 0f;
                }
            }
        }
        public float CurrentOpticalDistanceMeters { get; private set; }
        public float LastPixelSeparation { get; private set; }
        public string ActiveCameraDeviceName { get; private set; } = string.Empty;
        public bool IsWaitingCameraPermission { get; private set; }
        public int CameraPixelWidth => _rawCamW;
        public int CameraPixelHeight => _rawCamH;
        public int ProcessedWidth => _procW;
        public int ProcessedHeight => _procH;
        public float CameraUpdateHz { get; private set; }
        public int CameraRotationAngle { get; private set; }
        public bool CameraMirrored { get; private set; }
        public Texture DebugPreviewTexture => _previewTex;

        public bool TryGetSmoothedWorldPos(out Vector3 worldPos)
        {
            worldPos = _smoothedWorldPos;
            return _posInitialized;
        }

        // Webcam & background worker state
        private WebCamTexture _webCamTex;
        private int _rawCamW;
        private int _rawCamH;
        private int _camStride = 1;
        private int _procW;
        private int _procH;
        private Color32[] _mainPixelBuf;
        private Color32[] _workPixelBuf;
        private byte[] _maskBuf;
        private byte[] _visitedBuf;
        private int[] _bfsQueue;
        private readonly List<BlobCandidate> _candidatesA = new List<BlobCandidate>(16);
        private readonly List<BlobCandidate> _candidatesB = new List<BlobCandidate>(16);

        private Thread _workerThread;
        private AutoResetEvent _frameEvent;
        private volatile bool _workerRunning;
        private volatile int _workerBusy;
        private volatile bool _resetWorkerContinuity;
        private float _lastCameraFrameTime;

        // Worker-only temporal tracking state
        private bool _hasPrevPixels;
        private Vector2 _prevA;
        private Vector2 _prevB;
        private Vector2 _filteredMid;
        private bool _hasFilteredMid;
        private float _prevSepPx;
        private float _filteredSepPx;
        private float _filteredProjDepthMeters;
        private int _workerMissedFrames;

        // Thread-shared frame snapshot
        private Quaternion _snapCamRot;
        private Quaternion _snapRifleRot;
        private int _snapVideoAngle;
        private bool _snapMirrored;
        private LedTargetMode _snapMode;
        private BaselineAxis _snapAxis;
        private float _snapLedSpacing;
        private Vector3 _snapLedOffsetFromPivot;
        private float _snapFovDeg;
        private int _snapMinDom;
        private int _snapMinBright;
        private bool _snapWantPreview;

        // Thread-shared detection result
        private readonly object _resultLock = new object();
        private bool _resDetected;
        private bool _resLedA;
        private bool _resLedB;
        private Vector3 _resRawCamPos;
        private Quaternion _resSnapCamRot = Quaternion.identity;
        private float _resDistanceMeters;
        private float _resSepPx;
        private uint _resSeq;
        private uint _consumedSeq;

        // Optional debug preview texture (uploaded ONLY when Settings preview panel requests it)
        private bool _previewRequested;
        private Texture2D _previewTex;
        private Color32[] _previewWorkerBuf;
        private Color32[] _previewMainBuf;
        private volatile bool _previewDirty;

        // Calibration & 120Hz smoothing state
        private CalibrationPhase _calibrationPhase = CalibrationPhase.Idle;
        private float _calPhaseTimer;
        private float _steadyHoldTimer;
        private float _steadyLostSeconds;
        private bool _hasCalibratedZero;
        private bool _pendingZeroCapture;
        private int _zeroAvgCount;
        private Vector3 _startupCandidateRawPos;
        private float _reacquireRampTimer;
        private Vector3 _zeroRawCamPos;
        private Vector3 _zeroTargetCamLocalOffset;
        private Vector3 _currentCamLocalOffset;
        private Vector3 _smoothedLocalOffset;
        private Vector3 _smoothedWorldPos;
        private bool _posInitialized;
        private Vector3 _lastAcceptedRawCamPos;
        private bool _hasLastAcceptedRaw;
        private int _spikeRejectCount;
        private float _unseenSeconds = 99f;
        private bool _hasStableLock;
        private float _bodyYawDeg;
        private bool _bodyYawInitialized;
        private bool _hasFallbackRef;
        private float _fallbackRefRifleYaw;
        private float _fallbackRefBodyYaw;
        private Vector3 _fallbackRelBodyOffset;
        private HeadTrackingCameraController _headTracker;
        private RifleOrientationController _rifleOrientationController;
        private float _androidPermRetryTimer;
        private bool _permissionRequestedOnce;

        // Layer 2: 2-LED Optical-IMU Yaw Lock state
        private bool _hasReadyOptYawRef;
        private float _readyBaseCamBearingDeg;
        private float _readyOptToRifleYawOffsetDeg;
        private bool _wasAimingForOpticalYaw;
        private bool _hasAdsOptYawRef;
        private float _adsBaseCamBearingDeg;
        private float _adsOptToRifleYawOffsetDeg;
        private float _learnedYawDriftRateDegPerSec;
        private float _activeOpticalYawRateDegPerSec;
        private float _lastOpticalYawUpdateTime;

        private void Awake()
        {
            if (headCameraTransform == null && Camera.main != null)
                headCameraTransform = Camera.main.transform;

            if (weaponAimController == null)
                weaponAimController = GetComponent<WeaponAimController>();

            if (redDotSight == null)
                redDotSight = GetComponentInChildren<RedDotSightController>(true);

            if (redDotAnchor == null && redDotSight != null)
                redDotAnchor = redDotSight.transform;

            if (rifleReceiver == null)
                rifleReceiver = FindAnyObjectByType<Esp32RifleReceiver>();

            _headTracker = FindAnyObjectByType<HeadTrackingCameraController>();
            _rifleOrientationController = GetComponent<RifleOrientationController>();
            if (_rifleOrientationController == null)
                _rifleOrientationController = FindAnyObjectByType<RifleOrientationController>();

            _currentCamLocalOffset = readyLocalOffsetMeters;
            _smoothedLocalOffset = readyLocalOffsetMeters;
            _zeroTargetCamLocalOffset = readyLocalOffsetMeters;
            _fallbackRelBodyOffset = readyLocalOffsetMeters;
            _smoothedWorldPos = transform.position;
            _calibrationPhase = IsTrackingRuntimeSupported ? CalibrationPhase.WaitingForLeds : CalibrationPhase.Idle;
        }

        private void OnEnable()
        {
            if (enableOpticalTracking)
                TryStartCamera();
        }

        private void OnDisable()
        {
            StopCamera();
        }

        private void OnDestroy()
        {
            StopCamera();
            if (_previewTex != null)
            {
                Destroy(_previewTex);
                _previewTex = null;
            }
        }

        /// <summary>
        /// Requests preview texture updates only while a calibration UI panel is open.
        /// </summary>
        public void SetPreviewActive(bool active)
        {
            _previewRequested = active;
        }

        /// <summary>
        /// Zeroes the optical tracker so the rifle's current physical position matches Ready stance
        /// (or starts a guided VR-insert + steady-aim countdown when triggered outside Settings menu).
        /// </summary>
        public void RecenterOpticalZero(bool startGuidedCountdown = true)
        {
            if (headCameraTransform != null)
            {
                Vector3 flatFwd = new Vector3(headCameraTransform.forward.x, 0f, headCameraTransform.forward.z);
                if (flatFwd.sqrMagnitude > 1e-4f)
                {
                    _bodyYawDeg = Mathf.Atan2(flatFwd.x, flatFwd.z) * Mathf.Rad2Deg;
                    _bodyYawInitialized = true;
                }
            }

            _spikeRejectCount = 0;
            _hasFallbackRef = false;
            _fallbackRelBodyOffset = readyLocalOffsetMeters;
            _resetWorkerContinuity = true;

            if (!IsTrackingRuntimeSupported)
            {
                _calibrationPhase = CalibrationPhase.Idle;
                return;
            }

            if (!startGuidedCountdown && _hasStableLock)
            {
                Quaternion zeroSnapCamRot;
                lock (_resultLock)
                {
                    _zeroRawCamPos = _resRawCamPos;
                    zeroSnapCamRot = _resSnapCamRot;
                }
                _zeroAvgCount = 1;
                _lastAcceptedRawCamPos = _zeroRawCamPos;
                _hasLastAcceptedRaw = true;
                _zeroTargetCamLocalOffset = readyLocalOffsetMeters;
                _currentCamLocalOffset = readyLocalOffsetMeters;
                _smoothedLocalOffset = readyLocalOffsetMeters;
                _hasCalibratedZero = true;
                _pendingZeroCapture = false;
                _calibrationPhase = CalibrationPhase.CompletedBanner;
                _calPhaseTimer = completedBannerDurationSeconds;
                ResetOpticalYawFusionState();
                CaptureReadyOpticalYawReference(_zeroRawCamPos, zeroSnapCamRot);
            }
            else
            {
                _pendingZeroCapture = false;
                _zeroAvgCount = 0;
                _steadyHoldTimer = 0f;
                _steadyLostSeconds = 0f;
                _hasCalibratedZero = false;
                _hasStableLock = false;
                _hasLastAcceptedRaw = false;
                _currentCamLocalOffset = readyLocalOffsetMeters;
                _smoothedLocalOffset = readyLocalOffsetMeters;
                _posInitialized = false;
                ResetOpticalYawFusionState();

                if (startGuidedCountdown && vrInsertGraceSeconds > 0.1f)
                {
                    _calibrationPhase = CalibrationPhase.PreparingHeadset;
                    _calPhaseTimer = vrInsertGraceSeconds;
                }
                else
                {
                    _calibrationPhase = CalibrationPhase.WaitingForLeds;
                    _calPhaseTimer = 0f;
                }
            }
        }

        private void Update()
        {
            if (headCameraTransform == null && Camera.main != null)
                headCameraTransform = Camera.main.transform;

            if (!enableOpticalTracking)
                return;

            if (_webCamTex == null)
            {
                HandleCameraInitRetry();
                return;
            }

            if (!_webCamTex.isPlaying)
                return;

            if (_webCamTex.width < 32 || _webCamTex.height < 32)
                return;

            EnsureBuffersAllocated(_webCamTex.width, _webCamTex.height);

            if (_webCamTex.didUpdateThisFrame)
            {
                float now = Time.unscaledTime;
                if (_lastCameraFrameTime > 0f)
                {
                    float camDt = now - _lastCameraFrameTime;
                    if (camDt > 0.002f && camDt < 0.5f)
                        CameraUpdateHz = Mathf.Lerp(CameraUpdateHz > 0f ? CameraUpdateHz : (1f / camDt), 1f / camDt, 0.20f);
                }
                _lastCameraFrameTime = now;

                if (Interlocked.CompareExchange(ref _workerBusy, 1, 0) == 0)
                {
                    _webCamTex.GetPixels32(_mainPixelBuf);
                    CopyDownsampledFrame(_mainPixelBuf, _workPixelBuf, _rawCamW, _procW, _procH, _camStride);

                    _snapCamRot = headCameraTransform != null ? headCameraTransform.rotation : Quaternion.identity;
                    _snapRifleRot = transform.rotation;
                    int rawAngle = _webCamTex.videoRotationAngle;
                    if (_headTracker != null && _headTracker.IsRoll180Compensated)
                        rawAngle = (rawAngle + 180) % 360;
                    if (flipCamera180Override)
                        rawAngle = (rawAngle + 180) % 360;
                    _snapVideoAngle = rawAngle;
                    _snapMirrored = _webCamTex.videoVerticallyMirrored;
                    CameraRotationAngle = rawAngle;
                    CameraMirrored = _snapMirrored;
                    _snapMode = targetMode;
                    _snapAxis = baselineAxis;
                    _snapLedSpacing = ledSpacingMeters;
                    _snapLedOffsetFromPivot = ledOffsetFromPivotMeters;
                    _snapFovDeg = cameraHorizontalFovDeg;
                    _snapMinDom = minColorDominance;
                    _snapMinBright = minPixelBrightness;
                    _snapWantPreview = _previewRequested;

                    _frameEvent.Set();
                }
            }

            if (_previewRequested && _previewDirty && _previewTex != null && _previewMainBuf != null)
            {
                lock (_resultLock)
                {
                    Array.Copy(_previewWorkerBuf, _previewMainBuf, _previewMainBuf.Length);
                    _previewDirty = false;
                }
                _previewTex.SetPixels32(_previewMainBuf);
                _previewTex.Apply(false, false);
            }
        }

        private static void CopyDownsampledFrame(Color32[] src, Color32[] dst, int srcW, int dstW, int dstH, int stride)
        {
            if (stride <= 1)
            {
                Array.Copy(src, dst, dst.Length);
                return;
            }

            for (int y = 0; y < dstH; y++)
            {
                int srcRow = (y * stride) * srcW;
                int dstRow = y * dstW;
                for (int x = 0; x < dstW; x++)
                    dst[dstRow + x] = src[srcRow + x * stride];
            }
        }

        private void LateUpdate()
        {
            if (headCameraTransform == null)
                return;

            bool gotNewResult = false;
            bool detectedNow = false;
            bool foundA = false;
            bool foundB = false;
            Vector3 rawCamPos = Vector3.zero;
            Quaternion resSnapCamRot = headCameraTransform.rotation;
            float distMeters = 0f;
            float sepPxCopy = 0f;

            lock (_resultLock)
            {
                foundA = _resLedA;
                foundB = _resLedB;
                if (_resSeq != _consumedSeq)
                {
                    _consumedSeq = _resSeq;
                    gotNewResult = true;
                    detectedNow = _resDetected;
                    rawCamPos = _resRawCamPos;
                    resSnapCamRot = _resSnapCamRot;
                    distMeters = _resDistanceMeters;
                    sepPxCopy = _resSepPx;
                }
            }

            IsLedAFound = foundA;
            IsLedBFound = foundB;

            float dt = Mathf.Clamp(Time.unscaledDeltaTime, 0.001f, 0.05f);

            if (!enableOpticalTracking)
            {
                _calibrationPhase = CalibrationPhase.Idle;
            }
            else if (_calibrationPhase == CalibrationPhase.PreparingHeadset)
            {
                _calPhaseTimer = Mathf.Max(0f, _calPhaseTimer - dt);
                if (_calPhaseTimer <= 0f)
                {
                    _calibrationPhase = CalibrationPhase.WaitingForLeds;
                    _steadyHoldTimer = 0f;
                    _steadyLostSeconds = 0f;
                    _zeroAvgCount = 0;
                }
            }
            else if (_calibrationPhase == CalibrationPhase.CompletedBanner)
            {
                _calPhaseTimer = Mathf.Max(0f, _calPhaseTimer - dt);
                if (_calPhaseTimer <= 0f)
                    _calibrationPhase = CalibrationPhase.Idle;
            }

            Vector3 rFwd = new Vector3(transform.forward.x, 0f, transform.forward.z);
            bool hasRifleYaw = rifleReceiver != null && rifleReceiver.IsConnected && rFwd.sqrMagnitude > 1e-4f;
            float rifleFlatYawDeg = hasRifleYaw ? Mathf.Atan2(rFwd.x, rFwd.z) * Mathf.Rad2Deg : 0f;

            Vector3 cFwd = new Vector3(headCameraTransform.forward.x, 0f, headCameraTransform.forward.z);
            float camFlatYawDeg = cFwd.sqrMagnitude > 1e-4f ? Mathf.Atan2(cFwd.x, cFwd.z) * Mathf.Rad2Deg : 0f;

            bool acceptedOpticalSample = false;

            if (gotNewResult && detectedNow)
            {
                bool rejectOutlier = _hasStableLock
                    && _hasLastAcceptedRaw
                    && _unseenSeconds < 0.25f
                    && Vector3.Distance(rawCamPos, _lastAcceptedRawCamPos) > maxSingleFrameJumpMeters
                    && _spikeRejectCount < 2;

                if (rejectOutlier)
                {
                    _spikeRejectCount++;
                    _unseenSeconds += dt;
                }
                else
                {
                    acceptedOpticalSample = true;
                    _spikeRejectCount = 0;
                    _unseenSeconds = 0f;
                    _lastAcceptedRawCamPos = rawCamPos;
                    _hasLastAcceptedRaw = true;
                    CurrentOpticalDistanceMeters = distMeters;
                    LastPixelSeparation = sepPxCopy;

                    if (_calibrationPhase == CalibrationPhase.PreparingHeadset)
                    {
                        // Grace period while user slides phone into VR goggles & shoulders rifle:
                        // do not lock optical zero or enable head-locked tracking yet.
                        _hasStableLock = false;
                    }
                    else if (_pendingZeroCapture)
                    {
                        _zeroRawCamPos = rawCamPos;
                        _zeroTargetCamLocalOffset = readyLocalOffsetMeters;
                        _zeroAvgCount = 1;
                        _hasCalibratedZero = true;
                        _pendingZeroCapture = false;
                        _calibrationPhase = CalibrationPhase.CompletedBanner;
                        _calPhaseTimer = completedBannerDurationSeconds;
                        ResetOpticalYawFusionState();
                        CaptureReadyOpticalYawReference(_zeroRawCamPos, resSnapCamRot);
                    }
                    else if (!_hasCalibratedZero)
                    {
                        if (_calibrationPhase != CalibrationPhase.HoldingSteady)
                        {
                            _calibrationPhase = CalibrationPhase.HoldingSteady;
                            _startupCandidateRawPos = rawCamPos;
                            _zeroAvgCount = 1;
                            _steadyHoldTimer = 0f;
                            _steadyLostSeconds = 0f;
                        }
                        else
                        {
                            float dx = rawCamPos.x - _startupCandidateRawPos.x;
                            float dy = rawCamPos.y - _startupCandidateRawPos.y;
                            float dLat = Mathf.Sqrt(dx * dx + dy * dy);
                            float dZ = Mathf.Abs(rawCamPos.z - _startupCandidateRawPos.z);
                            if (dLat > 0.055f || dZ > 0.12f)
                            {
                                // Rifle moved noticeably while getting into shoulder stance: restart hold timer cleanly
                                _startupCandidateRawPos = rawCamPos;
                                _zeroAvgCount = 1;
                                _steadyHoldTimer = 0f;
                            }
                            else
                            {
                                _zeroAvgCount++;
                                _startupCandidateRawPos = Vector3.Lerp(_startupCandidateRawPos, rawCamPos, 1f / Mathf.Min(_zeroAvgCount, 24));
                            }
                        }

                        _currentCamLocalOffset = readyLocalOffsetMeters;
                        _hasStableLock = false;
                    }
                    else
                    {
                        if (_zeroAvgCount > 0 && _zeroAvgCount < 8 && Vector3.Distance(rawCamPos, _zeroRawCamPos) < 0.045f)
                        {
                            _zeroAvgCount++;
                            _zeroRawCamPos = Vector3.Lerp(_zeroRawCamPos, rawCamPos, 1f / _zeroAvgCount);
                        }

                        bool wasLocked = _hasStableLock;

                        // Full 1:1 depth-ratio parallax compensation so pure head rotation cancels out in world space
                        float lateralRatio = Mathf.Clamp(_zeroTargetCamLocalOffset.z / Mathf.Max(0.24f, _zeroRawCamPos.z), 0.60f, 1.30f);
                        Vector3 deltaCam = rawCamPos - _zeroRawCamPos;
                        float deadX = Mathf.MoveTowards(deltaCam.x, 0f, 0.003f);
                        float deadY = Mathf.MoveTowards(deltaCam.y, 0f, 0.003f);
                        float deadbandedZ = Mathf.MoveTowards(deltaCam.z, 0f, 0.015f);
                        float zDelta = Mathf.Clamp(deadbandedZ * zDepthScale * positionScale, -0.06f, 0.06f);
                        Vector3 desiredLocal = new Vector3(
                            _zeroTargetCamLocalOffset.x + deadX * lateralRatio * positionScale,
                            _zeroTargetCamLocalOffset.y + deadY * lateralRatio * positionScale,
                            _zeroTargetCamLocalOffset.z + zDelta);

                        // Keep tracking inside realistic shouldered bounds so the rifle cannot slide off-screen
                        desiredLocal.x = Mathf.Clamp(desiredLocal.x, readyLocalOffsetMeters.x - 0.18f, readyLocalOffsetMeters.x + 0.18f);
                        desiredLocal.y = Mathf.Clamp(desiredLocal.y, readyLocalOffsetMeters.y - 0.18f, readyLocalOffsetMeters.y + 0.18f);
                        desiredLocal.z = Mathf.Clamp(desiredLocal.z, readyLocalOffsetMeters.z - 0.06f, readyLocalOffsetMeters.z + 0.06f);

                        _currentCamLocalOffset = desiredLocal;
                        _hasStableLock = true;
                        _hasFallbackRef = false;
                        if (!wasLocked)
                            _reacquireRampTimer = 0.25f;
                    }
                }
            }
            else if (enableOpticalTracking && IsCameraRunning)
            {
                _unseenSeconds += dt;
                // Immediately release head-locked transform the moment a frame reports LEDs missing
                if ((gotNewResult && !detectedNow) || _unseenSeconds > 0.08f)
                    _hasStableLock = false;
            }
            else
            {
                _unseenSeconds = 99f;
                _hasStableLock = false;
            }

            if (enableOpticalTracking && _calibrationPhase == CalibrationPhase.HoldingSteady)
            {
                if (_unseenSeconds <= 0.12f)
                {
                    _steadyLostSeconds = 0f;
                    _steadyHoldTimer += dt;
                    if (_steadyHoldTimer >= steadyHoldSeconds)
                    {
                        if (_headTracker != null)
                        {
                            _headTracker.RecenterHeadYawOnly();
                            Vector3 reFwd = new Vector3(headCameraTransform.forward.x, 0f, headCameraTransform.forward.z);
                            if (reFwd.sqrMagnitude > 1e-4f)
                                camFlatYawDeg = Mathf.Atan2(reFwd.x, reFwd.z) * Mathf.Rad2Deg;
                        }

                        _zeroRawCamPos = _startupCandidateRawPos;
                        _zeroTargetCamLocalOffset = readyLocalOffsetMeters;
                        _lastAcceptedRawCamPos = _zeroRawCamPos;
                        _hasLastAcceptedRaw = true;
                        _hasCalibratedZero = true;
                        _hasStableLock = true;
                        _hasFallbackRef = false;
                        _bodyYawDeg = camFlatYawDeg;
                        _bodyYawInitialized = true;
                        _currentCamLocalOffset = readyLocalOffsetMeters;
                        _smoothedLocalOffset = readyLocalOffsetMeters;
                        _posInitialized = true;
                        _reacquireRampTimer = 0.25f;
                        _calibrationPhase = CalibrationPhase.CompletedBanner;
                        _calPhaseTimer = completedBannerDurationSeconds;

                        if (_rifleOrientationController != null)
                            _rifleOrientationController.RecenterToHeadForward();

                        ResetOpticalYawFusionState();
                        CaptureReadyOpticalYawReference(_zeroRawCamPos, headCameraTransform.rotation);
                    }
                }
                else
                {
                    _steadyLostSeconds += dt;
                    if (_steadyLostSeconds > 0.35f)
                    {
                        _calibrationPhase = CalibrationPhase.WaitingForLeds;
                        _steadyHoldTimer = 0f;
                        _zeroAvgCount = 0;
                    }
                }
            }

            Vector3 headOrigin = _headTracker != null ? _headTracker.ReadyWorldPosition : headCameraTransform.position;

            if (_hasStableLock)
            {
                if (!_posInitialized)
                {
                    _smoothedLocalOffset = _currentCamLocalOffset;
                    _posInitialized = true;
                }
                else
                {
                    float ramp = 1f;
                    if (_reacquireRampTimer > 0f)
                    {
                        _reacquireRampTimer = Mathf.Max(0f, _reacquireRampTimer - dt);
                        ramp = 0.45f;
                    }

                    float dx = _currentCamLocalOffset.x - _smoothedLocalOffset.x;
                    float dy = _currentCamLocalOffset.y - _smoothedLocalOffset.y;
                    float xyGap = Mathf.Sqrt(dx * dx + dy * dy);
                    float xyFactor = Mathf.Clamp01(xyGap / 0.10f);
                    float cutoffXy = Mathf.Lerp(minPositionSmoothSpeed, maxPositionSmoothSpeed, xyFactor) * ramp;
                    float blendXy = 1f - Mathf.Exp(-cutoffXy * dt);

                    float zGap = Mathf.Abs(_currentCamLocalOffset.z - _smoothedLocalOffset.z);
                    float zFactor = Mathf.Clamp01(zGap / 0.14f);
                    float cutoffZ = Mathf.Lerp(minPositionSmoothSpeed * 0.35f, maxPositionSmoothSpeed * 0.40f, zFactor) * ramp;
                    float blendZ = 1f - Mathf.Exp(-cutoffZ * dt);

                    _smoothedLocalOffset.x = Mathf.Lerp(_smoothedLocalOffset.x, _currentCamLocalOffset.x, blendXy);
                    _smoothedLocalOffset.y = Mathf.Lerp(_smoothedLocalOffset.y, _currentCamLocalOffset.y, blendXy);
                    _smoothedLocalOffset.z = Mathf.Lerp(_smoothedLocalOffset.z, _currentCamLocalOffset.z, blendZ);
                }

                _smoothedWorldPos = headOrigin + headCameraTransform.rotation * _smoothedLocalOffset;
            }
            else if (enableBodyYawFollowFallback)
            {
                Vector3 targetWorldPos = ComputeBodyYawFallbackWorldPos(headOrigin, hasRifleYaw, rifleFlatYawDeg, camFlatYawDeg);
                if (!_posInitialized)
                {
                    _smoothedWorldPos = targetWorldPos;
                    _posInitialized = true;
                }
                else if (hasRifleYaw)
                {
                    // Follow body/shoulder ESP32 orbit smoothly from exact last position without pulling back to ready pose
                    float cutoff = Mathf.Max(36f, maxPositionSmoothSpeed);
                    _smoothedWorldPos = Vector3.Lerp(_smoothedWorldPos, targetWorldPos, 1f - Mathf.Exp(-cutoff * dt));
                }
                else
                {
                    // Hold exact last position when neither LEDs nor ESP32 yaw moved
                    _smoothedWorldPos = targetWorldPos;
                }
                _smoothedLocalOffset = Quaternion.Inverse(headCameraTransform.rotation) * (_smoothedWorldPos - headOrigin);
            }
            else
            {
                return;
            }

            if ((transform.position - _smoothedWorldPos).sqrMagnitude > 1e-8f)
                transform.position = _smoothedWorldPos;

            UpdateOpticalYawFusion(dt, acceptedOpticalSample, rawCamPos, resSnapCamRot, hasRifleYaw);
            UpdateAutoRedDotNearEye();
        }

        private void ResetOpticalYawFusionState()
        {
            _hasReadyOptYawRef = false;
            _hasAdsOptYawRef = false;
            _learnedYawDriftRateDegPerSec = 0f;
            _activeOpticalYawRateDegPerSec = 0f;
            _lastOpticalYawUpdateTime = 0f;
            IsOpticalYawFusionActive = false;
            LastOpticalYawErrorDeg = 0f;
            LearnedOpticalYawDriftRateDegPerSec = 0f;
        }

        private void CaptureReadyOpticalYawReference(Vector3 ledCamPos, Quaternion camWorldRot)
        {
            if (ledCamPos.z < 0.14f || ledCamPos.z > 1.20f)
                return;

            Vector3 optWorldDir = camWorldRot * ledCamPos;
            Vector3 flatOptDir = new Vector3(optWorldDir.x, 0f, optWorldDir.z);
            Vector3 flatRifleFwd = new Vector3(transform.forward.x, 0f, transform.forward.z);
            if (flatOptDir.sqrMagnitude < 1e-4f || flatRifleFwd.sqrMagnitude < 1e-4f)
                return;

            float optWorldYawDeg = Mathf.Atan2(flatOptDir.x, flatOptDir.z) * Mathf.Rad2Deg;
            float curRifleYawDeg = Mathf.Atan2(flatRifleFwd.x, flatRifleFwd.z) * Mathf.Rad2Deg;

            _readyBaseCamBearingDeg = Mathf.Atan2(ledCamPos.x, ledCamPos.z) * Mathf.Rad2Deg;
            _readyOptToRifleYawOffsetDeg = Mathf.DeltaAngle(optWorldYawDeg, curRifleYawDeg);
            _hasReadyOptYawRef = true;
        }

        /// <summary>
        /// Layer 2 (2-LED Optical-IMU Yaw Lock): uses the phone rear camera's drift-free 2-LED bearing
        /// to gently cancel slow 6-DOF horizontal yaw drift on ESP32/head tracking without interfering
        /// with fast turns, free head-look, or gravity-referenced Pitch/Roll.
        /// </summary>
        private void UpdateOpticalYawFusion(
            float dt,
            bool gotValidSample,
            Vector3 rawCamPos,
            Quaternion sampleCamWorldRot,
            bool hasRifleYaw)
        {
            bool isAiming = weaponAimController != null && weaponAimController.IsAiming;
            if (isAiming != _wasAimingForOpticalYaw)
            {
                _wasAimingForOpticalYaw = isAiming;
                _hasAdsOptYawRef = false;
                _activeOpticalYawRateDegPerSec = 0f;
            }

            bool inCalibrationCountdown = _calibrationPhase == CalibrationPhase.PreparingHeadset
                || _calibrationPhase == CalibrationPhase.HoldingSteady;

            if (!enableOpticalYawFusion
                || !enableOpticalTracking
                || !IsCameraRunning
                || inCalibrationCountdown
                || _rifleOrientationController == null
                || !hasRifleYaw)
            {
                _activeOpticalYawRateDegPerSec = Mathf.MoveTowards(_activeOpticalYawRateDegPerSec, 0f, 35f * dt);
                IsOpticalYawFusionActive = false;
                return;
            }

            float rifleSpeed = _rifleOrientationController.EstimatedAngularSpeedDegPerSec;
            float headSpeed = _headTracker != null ? _headTracker.EstimatedHeadSpeedDegPerSec : 0f;
            bool steadyEnough = rifleSpeed < maxFusionAngularSpeedDegPerSec && headSpeed < maxFusionAngularSpeedDegPerSec;

            if (!steadyEnough || _unseenSeconds > 0.12f)
            {
                _activeOpticalYawRateDegPerSec = Mathf.MoveTowards(_activeOpticalYawRateDegPerSec, 0f, 40f * dt);
                IsOpticalYawFusionActive = false;
                return;
            }

            if (gotValidSample)
            {
                float now = Time.unscaledTime;
                float opticalDt = _lastOpticalYawUpdateTime > 0f
                    ? Mathf.Clamp(now - _lastOpticalYawUpdateTime, 0.01f, 0.15f)
                    : 0.033f;
                _lastOpticalYawUpdateTime = now;

                bool geometryValid = rawCamPos.z >= 0.14f
                    && rawCamPos.z <= 1.15f
                    && Mathf.Abs((sampleCamWorldRot * Vector3.forward).y) < 0.65f;

                if (geometryValid)
                {
                    Vector3 optWorldDir = sampleCamWorldRot * rawCamPos;
                    Vector3 flatOptDir = new Vector3(optWorldDir.x, 0f, optWorldDir.z);
                    Vector3 flatRifleFwd = new Vector3(transform.forward.x, 0f, transform.forward.z);

                    if (flatOptDir.sqrMagnitude > 1e-4f && flatRifleFwd.sqrMagnitude > 1e-4f)
                    {
                        float camBearingDeg = Mathf.Atan2(rawCamPos.x, rawCamPos.z) * Mathf.Rad2Deg;
                        float optWorldYawDeg = Mathf.Atan2(flatOptDir.x, flatOptDir.z) * Mathf.Rad2Deg;
                        float curRifleYawDeg = Mathf.Atan2(flatRifleFwd.x, flatRifleFwd.z) * Mathf.Rad2Deg;

                        bool sampleFused = false;

                        if (isAiming)
                        {
                            float aimBlend = weaponAimController != null ? weaponAimController.AimBlend : 1f;
                            if (aimBlend >= 0.96f)
                            {
                                if (!_hasAdsOptYawRef)
                                {
                                    if (rifleSpeed < 24f && headSpeed < 24f)
                                    {
                                        _adsBaseCamBearingDeg = camBearingDeg;
                                        _adsOptToRifleYawOffsetDeg = Mathf.DeltaAngle(optWorldYawDeg, curRifleYawDeg);
                                        _hasAdsOptYawRef = true;
                                    }
                                }
                                else
                                {
                                    float bearingDev = Mathf.Abs(Mathf.DeltaAngle(_adsBaseCamBearingDeg, camBearingDeg));
                                    if (bearingDev <= maxAdsBearingDeviationDeg)
                                    {
                                        float expectedRifleYaw = optWorldYawDeg + _adsOptToRifleYawOffsetDeg;
                                        float errDeg = Mathf.DeltaAngle(curRifleYawDeg, expectedRifleYaw);
                                        if (Mathf.Abs(errDeg) <= maxOpticalYawInnovationDeg)
                                        {
                                            float effErr = Mathf.Abs(errDeg) <= opticalYawDeadbandDeg
                                                ? 0f
                                                : errDeg - Mathf.Sign(errDeg) * opticalYawDeadbandDeg;

                                            // Pure gentle proportional damping only (no integrator windup from shoulder-vs-eye parallax)
                                            _learnedYawDriftRateDegPerSec = 0f;

                                            float targetRate = Mathf.Clamp(
                                                effErr / Mathf.Max(0.40f, adsYawFusionTimeConstantSec),
                                                -maxOpticalYawCorrectionDegPerSec,
                                                maxOpticalYawCorrectionDegPerSec);

                                            _activeOpticalYawRateDegPerSec = Mathf.Lerp(_activeOpticalYawRateDegPerSec, targetRate, 0.30f);
                                            LastOpticalYawErrorDeg = errDeg;
                                            IsOpticalYawFusionActive = true;
                                            sampleFused = true;
                                        }
                                    }
                                }
                            }
                        }
                        else if (_hasCalibratedZero && _hasReadyOptYawRef)
                        {
                            float bearingDev = Mathf.Abs(Mathf.DeltaAngle(_readyBaseCamBearingDeg, camBearingDeg));
                            if (bearingDev <= maxReadyBearingDeviationDeg)
                            {
                                float expectedRifleYaw = optWorldYawDeg + _readyOptToRifleYawOffsetDeg;
                                float errDeg = Mathf.DeltaAngle(curRifleYawDeg, expectedRifleYaw);
                                if (Mathf.Abs(errDeg) <= maxOpticalYawInnovationDeg)
                                {
                                    float readyDeadband = opticalYawDeadbandDeg * 1.5f;
                                    float effErr = Mathf.Abs(errDeg) <= readyDeadband
                                        ? 0f
                                        : errDeg - Mathf.Sign(errDeg) * readyDeadband;

                                    float maxReadyRate = maxOpticalYawCorrectionDegPerSec * 0.5f;
                                    float targetRate = Mathf.Clamp(
                                        effErr / Mathf.Max(0.5f, readyYawFusionTimeConstantSec),
                                        -maxReadyRate,
                                        maxReadyRate);

                                    _activeOpticalYawRateDegPerSec = Mathf.Lerp(_activeOpticalYawRateDegPerSec, targetRate, 0.35f);
                                    LastOpticalYawErrorDeg = errDeg;
                                    IsOpticalYawFusionActive = true;
                                    sampleFused = true;
                                }
                            }
                        }

                        if (!sampleFused)
                        {
                            _activeOpticalYawRateDegPerSec = Mathf.MoveTowards(_activeOpticalYawRateDegPerSec, 0f, 30f * opticalDt);
                            IsOpticalYawFusionActive = false;
                        }
                    }
                }
            }

            LearnedOpticalYawDriftRateDegPerSec = _learnedYawDriftRateDegPerSec;

            if (IsOpticalYawFusionActive && Mathf.Abs(_activeOpticalYawRateDegPerSec) > 0.005f)
            {
                _rifleOrientationController.ApplyOpticalYawCorrection(_activeOpticalYawRateDegPerSec * dt);
            }
        }

        private Vector3 ComputeBodyYawFallbackWorldPos(Vector3 headOrigin, bool hasRifleYaw, float rifleFlatYawDeg, float camFlatYawDeg)
        {
            if (hasRifleYaw)
            {
                if (!_hasFallbackRef)
                {
                    _hasFallbackRef = true;
                    _fallbackRefRifleYaw = rifleFlatYawDeg;
                    _fallbackRefBodyYaw = camFlatYawDeg;
                    if (_posInitialized)
                    {
                        Vector3 relWorld = _smoothedWorldPos - headOrigin;
                        _fallbackRelBodyOffset = Quaternion.Inverse(Quaternion.Euler(0f, camFlatYawDeg, 0f)) * relWorld;
                    }
                    else
                    {
                        _fallbackRelBodyOffset = readyLocalOffsetMeters;
                    }
                }

                // Keep exact last height/lateral/forward placement relative to body; never yank back to readyLocalOffsetMeters
                _bodyYawDeg = _fallbackRefBodyYaw + Mathf.DeltaAngle(_fallbackRefRifleYaw, rifleFlatYawDeg);
                _bodyYawInitialized = true;
                _currentCamLocalOffset = _fallbackRelBodyOffset;

                return headOrigin + Quaternion.Euler(0f, _bodyYawDeg, 0f) * _fallbackRelBodyOffset;
            }

            _hasFallbackRef = false;
            if (_posInitialized)
                return _smoothedWorldPos;

            _bodyYawDeg = camFlatYawDeg;
            _bodyYawInitialized = true;
            _currentCamLocalOffset = readyLocalOffsetMeters;
            return headOrigin + Quaternion.Euler(0f, _bodyYawDeg, 0f) * readyLocalOffsetMeters;
        }

        private void UpdateAutoRedDotNearEye()
        {
            if (!autoPowerRedDotNearEye || redDotSight == null || headCameraTransform == null)
                return;

            if (redDotSight.AlwaysKeepReticleOn)
                return;

            // Leave control to WeaponAimController when manual Q ADS / shoulder switch is active
            if (weaponAimController != null && weaponAimController.IsAiming)
                return;

            Transform sightT = redDotAnchor != null ? redDotAnchor : redDotSight.transform;
            Vector3 eyeToSight = sightT.position - headCameraTransform.position;
            float alongFwd = Vector3.Dot(eyeToSight, headCameraTransform.forward);
            if (alongFwd < 0.08f || alongFwd > 0.85f)
            {
                redDotSight.SetReticlePower(0f);
                return;
            }

            Vector3 lateral = eyeToSight - headCameraTransform.forward * alongFwd;
            float latMag = lateral.magnitude;
            float power = 1f - Mathf.SmoothStep(redDotWakeLateralMeters * 0.45f, redDotWakeLateralMeters, latMag);
            redDotSight.SetReticlePower(power);
        }

        private void HandleCameraInitRetry()
        {
            if (!Application.isMobilePlatform && !runWebcamInEditor)
                return;

            _androidPermRetryTimer -= Time.unscaledDeltaTime;
            if (_androidPermRetryTimer > 0f)
                return;

            _androidPermRetryTimer = 0.6f;
            TryStartCamera();
        }

        private void TryStartCamera()
        {
            if (!Application.isMobilePlatform && !runWebcamInEditor)
                return;

            if (_webCamTex != null && _webCamTex.isPlaying)
                return;

#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
            {
                IsWaitingCameraPermission = true;
                if (!_permissionRequestedOnce)
                {
                    _permissionRequestedOnce = true;
                    Permission.RequestUserPermission(Permission.Camera);
                }
                return;
            }
#endif
            IsWaitingCameraPermission = false;

            WebCamDevice[] devices = WebCamTexture.devices;
            if (devices == null || devices.Length == 0)
                return;

            string chosenName = devices[0].name;
            for (int i = 0; i < devices.Length; i++)
            {
                if (!devices[i].isFrontFacing)
                {
                    chosenName = devices[i].name;
                    break;
                }
            }

            ActiveCameraDeviceName = chosenName;
            _webCamTex = new WebCamTexture(chosenName, requestedWidth, requestedHeight, requestedFps);
            _webCamTex.Play();
        }

        private void StopCamera()
        {
            StopWorkerThread();

            if (_webCamTex != null)
            {
                if (_webCamTex.isPlaying)
                    _webCamTex.Stop();
                Destroy(_webCamTex);
                _webCamTex = null;
            }

            _hasStableLock = false;
            _hasFallbackRef = false;
            _hasLastAcceptedRaw = false;
            _spikeRejectCount = 0;
            _resetWorkerContinuity = true;
            CameraUpdateHz = 0f;
            _lastCameraFrameTime = 0f;
            IsLedAFound = false;
            IsLedBFound = false;
        }

        private void EnsureBuffersAllocated(int rawW, int rawH)
        {
            int stride = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(rawW / 360f, rawH / 270f)));
            int procW = Mathf.Max(16, rawW / stride);
            int procH = Mathf.Max(16, rawH / stride);

            if (_rawCamW == rawW && _rawCamH == rawH && _procW == procW && _procH == procH && _mainPixelBuf != null)
                return;

            StopWorkerThread();

            _rawCamW = rawW;
            _rawCamH = rawH;
            _camStride = stride;
            _procW = procW;
            _procH = procH;

            int rawTotal = rawW * rawH;
            int procTotal = procW * procH;
            _mainPixelBuf = new Color32[rawTotal];
            _workPixelBuf = new Color32[procTotal];
            _maskBuf = new byte[procTotal];
            _visitedBuf = new byte[procTotal];
            _bfsQueue = new int[procTotal];
            _previewWorkerBuf = new Color32[procTotal];
            _previewMainBuf = new Color32[procTotal];

            if (_previewTex != null)
                Destroy(_previewTex);
            _previewTex = new Texture2D(procW, procH, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            _resetWorkerContinuity = true;
            StartWorkerThread();
        }

        private void StartWorkerThread()
        {
            if (_workerRunning)
                return;

            _frameEvent = new AutoResetEvent(false);
            _workerRunning = true;
            _workerBusy = 0;
            _workerThread = new Thread(DecodeWorkerLoop)
            {
                IsBackground = true,
                Name = "VRAtis_OpticalLedTracker",
                Priority = System.Threading.ThreadPriority.Normal
            };
            _workerThread.Start();
        }

        private void StopWorkerThread()
        {
            _workerRunning = false;
            _frameEvent?.Set();

            if (_workerThread != null && _workerThread.IsAlive)
            {
                _workerThread.Join(250);
                _workerThread = null;
            }

            _frameEvent?.Dispose();
            _frameEvent = null;
            _workerBusy = 0;
        }

        private void DecodeWorkerLoop()
        {
            while (_workerRunning)
            {
                if (!_frameEvent.WaitOne(100))
                    continue;

                if (!_workerRunning)
                    break;

                try
                {
                    ProcessCameraFrame();
                }
                catch (Exception)
                {
                    // Guard worker thread from transient shutdown disposal races
                }
                finally
                {
                    _workerBusy = 0;
                }
            }
        }

        private void ProcessCameraFrame()
        {
            if (_resetWorkerContinuity)
            {
                _resetWorkerContinuity = false;
                _hasPrevPixels = false;
                _hasFilteredMid = false;
                _filteredSepPx = 0f;
                _filteredProjDepthMeters = 0f;
                _workerMissedFrames = 0;
            }

            int w = _procW;
            int h = _procH;
            int total = w * h;
            bool dualSameColor = _snapMode == LedTargetMode.DualGreen || _snapMode == LedTargetMode.DualRed;

            ClassifyColorMask(w, h, dualSameColor);
            AttachHotCoresToColoredHalos(w, h, total);
            ExtractBlobCandidates(w, h, total, dualSameColor);

            bool hasA = _candidatesA.Count > 0;
            bool hasB = dualSameColor ? (_candidatesA.Count >= 2) : (_candidatesB.Count > 0);

            BlobCandidate blobA = default;
            BlobCandidate blobB = default;
            bool validPair = false;

            if (hasA && hasB)
                validPair = PickBestPair(w, dualSameColor, out blobA, out blobB, out float rawSepPx);

            if (validPair)
            {
                float dx = blobB.CenterX - blobA.CenterX;
                float dy = blobB.CenterY - blobA.CenterY;
                float rawSepPx = Mathf.Sqrt(dx * dx + dy * dy);

                Vector2 rawMid = new Vector2((blobA.CenterX + blobB.CenterX) * 0.5f, (blobA.CenterY + blobB.CenterY) * 0.5f);
                if (!_hasFilteredMid)
                {
                    _filteredMid = rawMid;
                    _hasFilteredMid = true;
                }
                else
                {
                    float midDist = Vector2.Distance(rawMid, _filteredMid);
                    if (midDist > 0.45f)
                    {
                        float stepBlend = Mathf.Clamp01((midDist - 0.35f) / 1.5f);
                        _filteredMid = Vector2.Lerp(_filteredMid, rawMid, Mathf.Lerp(0.45f, 1.0f, stepBlend));
                    }
                }

                if (!_hasPrevPixels || _filteredSepPx <= 1f)
                {
                    _filteredSepPx = rawSepPx;
                }
                else
                {
                    float sepDiff = Mathf.Abs(rawSepPx - _filteredSepPx);
                    if (sepDiff > 1.35f)
                    {
                        // Rate-limit single-frame sepPx glitches (+-4% max) and apply deadband so halo breathing cannot cause Z jumps
                        float limitedSep = Mathf.Clamp(rawSepPx, _filteredSepPx * 0.96f, _filteredSepPx * 1.04f);
                        _filteredSepPx = Mathf.Lerp(_filteredSepPx, limitedSep, 0.15f);
                    }
                }

                _prevA = new Vector2(blobA.CenterX, blobA.CenterY);
                _prevB = new Vector2(blobB.CenterX, blobB.CenterY);
                _prevSepPx = _filteredSepPx;
                _hasPrevPixels = true;
                _workerMissedFrames = 0;

                Vector3 rawCamPos = ReconstructCameraSpacePosition(w, h, _filteredMid.x, _filteredMid.y, _filteredSepPx, out float ledDistMeters);
                lock (_resultLock)
                {
                    _resDetected = true;
                    _resLedA = true;
                    _resLedB = true;
                    _resRawCamPos = rawCamPos;
                    _resSnapCamRot = _snapCamRot;
                    _resDistanceMeters = ledDistMeters;
                    _resSepPx = _filteredSepPx;
                    _resSeq++;
                }
            }
            else
            {
                _workerMissedFrames++;
                if (_workerMissedFrames > 15)
                {
                    _hasPrevPixels = false;
                    _hasFilteredMid = false;
                    _filteredSepPx = 0f;
                    _filteredProjDepthMeters = 0f;
                }

                if (hasA) blobA = _candidatesA[0];
                if (hasB) blobB = dualSameColor ? _candidatesA[1] : _candidatesB[0];

                lock (_resultLock)
                {
                    _resDetected = false;
                    _resLedA = hasA;
                    _resLedB = hasB;
                    _resSeq++;
                }
            }

            if (_snapWantPreview)
            {
                BuildDebugPreviewBuffer(w, h, total, hasA, hasB, blobA, blobB, validPair);
            }
        }

        private void ClassifyColorMask(int w, int h, bool dualSameColor)
        {
            int total = w * h;
            int minDom = _snapMinDom;
            int minBright = _snapMinBright;
            bool targetRedSecond = _snapMode == LedTargetMode.GreenAndRed;
            bool singleRedOnly = _snapMode == LedTargetMode.DualRed;

            Array.Clear(_visitedBuf, 0, total);

            for (int i = 0; i < total; i++)
            {
                Color32 px = _workPixelBuf[i];
                int r = px.r;
                int g = px.g;
                int b = px.b;
                int maxC = r > g ? (r > b ? r : b) : (g > b ? g : b);

                if (maxC < minBright)
                {
                    _maskBuf[i] = 0;
                    continue;
                }

                int minC = r < g ? (r < b ? r : b) : (g < b ? g : b);

                if (singleRedOnly)
                {
                    int maxOther = g > b ? g : b;
                    if (r - maxOther >= minDom)
                        _maskBuf[i] = 1;
                    else if (maxC >= 210 && minC >= 120)
                        _maskBuf[i] = 3;
                    else
                        _maskBuf[i] = 0;
                    continue;
                }

                int greenDom = g - (r > b ? r : b);
                if (dualSameColor)
                {
                    if (greenDom >= minDom)
                        _maskBuf[i] = 1;
                    else if (maxC >= 210 && minC >= 120)
                        _maskBuf[i] = 3;
                    else
                        _maskBuf[i] = 0;
                    continue;
                }

                int secDom = targetRedSecond
                    ? (r - (g > b ? g : b))
                    : (b - (r > g ? r : g));

                if (greenDom >= minDom && greenDom >= secDom)
                    _maskBuf[i] = 1;
                else if (secDom >= minDom)
                    _maskBuf[i] = 2;
                else if (maxC >= 210 && minC >= 120)
                    _maskBuf[i] = 3;
                else
                    _maskBuf[i] = 0;
            }
        }

        /// <summary>
        /// Converts saturated white-clipped LED dome cores (mask 3) back into their surrounding
        /// colored halo label (mask 1 or 2) so bright LEDs form one solid disk instead of broken rings,
        /// while ignoring large pure-white room lights with no colored halo.
        /// </summary>
        private void AttachHotCoresToColoredHalos(int w, int h, int total)
        {
            float areaScale = Mathf.Max(1f, (w * h) / (320f * 240f));
            int smallCorePx = Mathf.RoundToInt(45f * areaScale);
            int maxHotPx = Mathf.RoundToInt(450f * areaScale);

            for (int i = 0; i < total; i++)
            {
                if (_maskBuf[i] != 3 || _visitedBuf[i] != 0)
                    continue;

                int head = 0;
                int tail = 0;
                _bfsQueue[tail++] = i;
                _visitedBuf[i] = 1;

                int touchA = 0;
                int touchB = 0;

                while (head < tail)
                {
                    int idx = _bfsQueue[head++];
                    int y = idx / w;
                    int x = idx - y * w;

                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int ny = y + dy;
                        if (ny < 0 || ny >= h)
                            continue;
                        int row = ny * w;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0)
                                continue;
                            int nx = x + dx;
                            if (nx < 0 || nx >= w)
                                continue;

                            int nIdx = row + nx;
                            byte nm = _maskBuf[nIdx];
                            if (nm == 1)
                            {
                                touchA++;
                            }
                            else if (nm == 2)
                            {
                                touchB++;
                            }
                            else if (nm == 3 && _visitedBuf[nIdx] == 0 && tail < _bfsQueue.Length)
                            {
                                _visitedBuf[nIdx] = 1;
                                _bfsQueue[tail++] = nIdx;
                            }
                        }
                    }
                }

                byte assignLabel = 0;
                int minTouches = tail <= smallCorePx ? 2 : 3;
                if (tail <= maxHotPx && (touchA >= minTouches || touchB >= minTouches))
                    assignLabel = touchA >= touchB ? (byte)1 : (byte)2;

                for (int k = 0; k < tail; k++)
                    _maskBuf[_bfsQueue[k]] = assignLabel;
            }

            Array.Clear(_visitedBuf, 0, total);
        }

        private void ExtractBlobCandidates(int w, int h, int total, bool dualSameColor)
        {
            _candidatesA.Clear();
            _candidatesB.Clear();

            for (int i = 0; i < total; i++)
            {
                byte label = _maskBuf[i];
                if (label == 0 || _visitedBuf[i] != 0)
                    continue;

                BlobCandidate blob = FloodFillBlob(i, label, w, h);
                if (blob.PixelCount < 2)
                    continue;

                if (label == 1)
                    InsertSortedBlob(_candidatesA, blob);
                else if (!dualSameColor)
                    InsertSortedBlob(_candidatesB, blob);
            }
        }

        private BlobCandidate FloodFillBlob(int startIdx, byte targetLabel, int w, int h)
        {
            int head = 0;
            int tail = 0;
            _bfsQueue[tail++] = startIdx;
            _visitedBuf[startIdx] = 1;

            int count = 0;
            int hotCorePx = 0;
            int brightPx = 0;
            int maxDom = 0;
            float sumX = 0f;
            float sumY = 0f;
            float sumW = 0f;
            int minX = w, maxX = 0, minY = h, maxY = 0;
            bool checkRed = (_snapMode == LedTargetMode.DualRed) || (targetLabel == 2 && _snapMode == LedTargetMode.GreenAndRed);
            bool checkBlue = targetLabel == 2 && _snapMode == LedTargetMode.GreenAndBlue;
            float areaScale = Mathf.Max(1f, (w * h) / (320f * 240f));
            int maxFloodPx = Mathf.RoundToInt(6000f * areaScale);
            int glarePx = Mathf.RoundToInt(650f * areaScale);
            int countCap = Mathf.RoundToInt(100f * areaScale);

            while (head < tail)
            {
                int idx = _bfsQueue[head++];
                int y = idx / w;
                int x = idx - y * w;

                Color32 px = _workPixelBuf[idx];
                int r = px.r;
                int g = px.g;
                int b = px.b;
                int maxC = r > g ? (r > b ? r : b) : (g > b ? g : b);
                int dom = checkRed ? (r - (g > b ? g : b))
                    : (checkBlue ? (b - (r > g ? r : g)) : (g - (r > b ? r : b)));
                if (dom < 0)
                    dom = 0;
                if (dom > maxDom)
                    maxDom = dom;

                // Heavily weight bright LED cores so soft cloth/breadboard reflections cannot pull the center off-bulb
                int weight;
                if (maxC >= 225)
                {
                    weight = 320 + dom * 2;
                    hotCorePx++;
                }
                else if (maxC >= 185)
                {
                    weight = 80 + dom * 2;
                    brightPx++;
                }
                else
                {
                    weight = 8 + dom;
                }

                sumX += x * weight;
                sumY += y * weight;
                sumW += weight;
                count++;

                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;

                if (count > maxFloodPx)
                    break;

                if (x > 0) TryEnqueue(idx - 1, targetLabel, ref tail);
                if (x + 1 < w) TryEnqueue(idx + 1, targetLabel, ref tail);
                if (y > 0) TryEnqueue(idx - w, targetLabel, ref tail);
                if (y + 1 < h) TryEnqueue(idx + w, targetLabel, ref tail);
            }

            float quality = (hotCorePx * 260f + brightPx * 45f + maxDom * 6f * areaScale + Mathf.Min(count, countCap) * 2f) / areaScale;
            if (count > glarePx)
                quality *= glarePx / (float)count;

            return new BlobCandidate
            {
                CenterX = sumW > 0f ? sumX / sumW : minX,
                CenterY = sumW > 0f ? sumY / sumW : minY,
                PixelCount = count,
                TotalWeight = sumW,
                QualityScore = quality,
                MinX = minX,
                MaxX = maxX,
                MinY = minY,
                MaxY = maxY
            };
        }

        private bool PickBestPair(int w, bool dualSameColor, out BlobCandidate bestA, out BlobCandidate bestB, out float bestSepPx)
        {
            bestA = default;
            bestB = default;
            bestSepPx = 0f;
            bool found = false;
            float bestScore = float.NegativeInfinity;

            float linearScale = Mathf.Max(1f, w / 320f);
            float minSepPx = 15f * linearScale;
            int countA = _candidatesA.Count;
            int countB = dualSameColor ? countA : _candidatesB.Count;

            for (int i = 0; i < countA; i++)
            {
                BlobCandidate a = _candidatesA[i];
                int jStart = dualSameColor ? (i + 1) : 0;
                for (int j = jStart; j < countB; j++)
                {
                    BlobCandidate b = dualSameColor ? _candidatesA[j] : _candidatesB[j];
                    float dx = b.CenterX - a.CenterX;
                    float dy = b.CenterY - a.CenterY;
                    float sep = Mathf.Sqrt(dx * dx + dy * dy);
                    if (sep < minSepPx || sep > w * 0.90f)
                        continue;

                    BlobCandidate candA = a;
                    BlobCandidate candB = b;
                    float score = (candA.QualityScore + candB.QualityScore) * 0.05f;

                    if (_hasPrevPixels)
                    {
                        float dA = Vector2.Distance(new Vector2(candA.CenterX, candA.CenterY), _prevA);
                        float dB = Vector2.Distance(new Vector2(candB.CenterX, candB.CenterY), _prevB);
                        if (dualSameColor)
                        {
                            float dASwap = Vector2.Distance(new Vector2(candB.CenterX, candB.CenterY), _prevA);
                            float dBSwap = Vector2.Distance(new Vector2(candA.CenterX, candA.CenterY), _prevB);
                            if (dASwap + dBSwap < dA + dB)
                            {
                                (candA, candB) = (candB, candA);
                                dA = dASwap;
                                dB = dBSwap;
                            }
                        }

                        score -= ((dA + dB) * 2.5f + Mathf.Abs(sep - _prevSepPx) * 4.0f) / linearScale;
                    }

                    if (!found || score > bestScore)
                    {
                        found = true;
                        bestScore = score;
                        bestA = candA;
                        bestB = candB;
                        bestSepPx = sep;
                    }
                }
            }

            return found;
        }

        private void TryEnqueue(int neighborIdx, byte targetLabel, ref int tail)
        {
            if (_visitedBuf[neighborIdx] == 0 && _maskBuf[neighborIdx] == targetLabel && tail < _bfsQueue.Length)
            {
                _visitedBuf[neighborIdx] = 1;
                _bfsQueue[tail++] = neighborIdx;
            }
        }

        private static void InsertSortedBlob(List<BlobCandidate> list, BlobCandidate item)
        {
            int idx = list.Count;
            for (int i = 0; i < list.Count; i++)
            {
                if (item.QualityScore > list[i].QualityScore)
                {
                    idx = i;
                    break;
                }
            }
            list.Insert(idx, item);
            if (list.Count > 6)
                list.RemoveAt(list.Count - 1);
        }

        private Vector3 ReconstructCameraSpacePosition(int w, int h, float midX, float midY, float sepPx, out float ledDistanceMeters)
        {
            // Convert pixel coordinates to centered right-handed image plane offset [-0.5..+0.5]
            float uNorm = (midX / w) - 0.5f;
            float vNorm = (midY / h) - 0.5f;
            if (_snapMirrored)
                vNorm = -vNorm;

            float rightNorm = uNorm;
            float upNorm = vNorm;
            int rotSteps = ((_snapVideoAngle % 360) + 360) % 360;
            if (rotSteps == 90)
            {
                rightNorm = vNorm;
                upNorm = -uNorm;
            }
            else if (rotSteps == 180)
            {
                rightNorm = -uNorm;
                upNorm = -vNorm;
            }
            else if (rotSteps == 270)
            {
                rightNorm = -vNorm;
                upNorm = uNorm;
            }

            bool rotated90 = rotSteps == 90 || rotSteps == 270;
            float viewWidthPx = rotated90 ? h : w;
            float viewHeightPx = rotated90 ? w : h;

            float halfFovRad = _snapFovDeg * 0.5f * Mathf.Deg2Rad;
            float focalPx = (viewWidthPx * 0.5f) / Mathf.Max(0.25f, Mathf.Tan(halfFovRad));

            Vector3 localAxis = _snapAxis == BaselineAxis.HorizontalCrossbar
                ? Vector3.right * _snapLedSpacing
                : (_snapAxis == BaselineAxis.VerticalStack
                    ? Vector3.up * _snapLedSpacing
                    : Vector3.forward * _snapLedSpacing);

            // De-foreshorten baseline using real-time ESP32 rifle orientation + phone head rotation
            Vector3 worldBaseline = _snapRifleRot * localAxis;
            Vector3 camBaseline = Quaternion.Inverse(_snapCamRot) * worldBaseline;
            float projBaselineMeters = Mathf.Sqrt(camBaseline.x * camBaseline.x + camBaseline.y * camBaseline.y);
            float baselineFloor = _snapAxis == BaselineAxis.LongitudinalRail ? 0.45f : 0.90f;
            projBaselineMeters = Mathf.Max(projBaselineMeters, _snapLedSpacing * baselineFloor);

            float zMeters = Mathf.Clamp((focalPx * projBaselineMeters) / Mathf.Max(4f, sepPx), 0.06f, 1.80f);
            if (_filteredProjDepthMeters <= 0.01f)
                _filteredProjDepthMeters = zMeters;
            else
                _filteredProjDepthMeters = Mathf.Lerp(_filteredProjDepthMeters, zMeters, 0.14f);

            float imgWidthMetric = _filteredProjDepthMeters * 2f * Mathf.Tan(halfFovRad);
            float imgHeightMetric = imgWidthMetric * (viewHeightPx / Math.Max(1f, viewWidthPx));

            float xMeters = rightNorm * imgWidthMetric;
            float yMeters = upNorm * imgHeightMetric;
            ledDistanceMeters = _filteredProjDepthMeters;

            Vector3 ledCamPos = new Vector3(xMeters, yMeters, _filteredProjDepthMeters);
            if (_snapLedOffsetFromPivot.sqrMagnitude > 1e-6f)
            {
                Vector3 camPivotOffset = Quaternion.Inverse(_snapCamRot) * (_snapRifleRot * _snapLedOffsetFromPivot);
                return ledCamPos - camPivotOffset;
            }

            return ledCamPos;
        }

        private void BuildDebugPreviewBuffer(
            int w,
            int h,
            int total,
            bool hasA,
            bool hasB,
            BlobCandidate blobA,
            BlobCandidate blobB,
            bool validPair)
        {
            bool flip180 = (((_snapVideoAngle % 360) + 360) % 360) == 180;

            // Tint detected color mask pixels subtly for immediate calibration feedback
            for (int i = 0; i < total; i++)
            {
                int dstIdx = flip180 ? (total - 1 - i) : i;
                byte m = _maskBuf[i];
                if (m == 1)
                    _previewWorkerBuf[dstIdx] = new Color32(40, 255, 90, 255);
                else if (m == 2)
                    _previewWorkerBuf[dstIdx] = new Color32(255, 80, 90, 255);
                else
                    _previewWorkerBuf[dstIdx] = _workPixelBuf[i];
            }

            if (hasA)
            {
                int ax = flip180 ? (w - 1 - (int)blobA.CenterX) : (int)blobA.CenterX;
                int ay = flip180 ? (h - 1 - (int)blobA.CenterY) : (int)blobA.CenterY;
                DrawCrosshair(_previewWorkerBuf, w, h, ax, ay, new Color32(0, 255, 140, 255));
            }
            if (hasB)
            {
                int bx = flip180 ? (w - 1 - (int)blobB.CenterX) : (int)blobB.CenterX;
                int by = flip180 ? (h - 1 - (int)blobB.CenterY) : (int)blobB.CenterY;
                DrawCrosshair(_previewWorkerBuf, w, h, bx, by, validPair ? new Color32(255, 220, 40, 255) : new Color32(255, 60, 60, 255));
            }

            lock (_resultLock)
            {
                _previewDirty = true;
            }
        }

        private static void DrawCrosshair(Color32[] buf, int w, int h, int cx, int cy, Color32 col)
        {
            int radius = Mathf.Clamp(w / 52, 6, 16);
            for (int d = -radius; d <= radius; d++)
            {
                int px = cx + d;
                int py = cy + d;
                if (px >= 0 && px < w && cy >= 0 && cy < h)
                    buf[cy * w + px] = col;
                if (cx >= 0 && cx < w && py >= 0 && py < h)
                    buf[py * w + cx] = col;
            }
        }
    }
}
