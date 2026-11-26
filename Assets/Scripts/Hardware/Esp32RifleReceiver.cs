using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using Debug = UnityEngine.Debug;

namespace VRAtis.Hardware
{
    /// <summary>
    /// Zero-allocation, high-priority UDP receiver for the 34-byte ESP32 quaternion + trigger + shoulder + magazine + recenter + joystick packet
    /// (backward-compatible with 22-byte, 23-byte, 26-byte, and 29-byte legacy packets).
    /// Includes burst draining and real-time Wi-Fi vs. Unity frame stutter diagnostics.
    /// </summary>
    public class Esp32RifleReceiver : MonoBehaviour
    {
        public const byte PacketHeader = 0xAA;
        public const int PacketSizeBytes = 22;
        public const int MaxPacketSizeBytes = 34;
        private const int SioUdpConnReset = -1744830452;

        [Header("Ağ ve Performans Ayarları")]
        [Tooltip("ESP32 kodundaki UDP_PORT ile aynı olmalıdır (varsayılan 4210).")]
        [SerializeField] private int udpPort = 4210;

        [Tooltip("Bu süre içinde paket gelmezse bağlantı kopmuş sayılır (ms).")]
        [SerializeField] private float connectionTimeoutMs = 500f;

        [Tooltip("Oyun kare hızı hedefi (Galaxy A26 5G ekranı ve akıcı PC testi için 120 FPS).")]
        [SerializeField] private int targetFrameRate = 120;

        [Header("Takılma Teşhis Sistemi (Canlı Telemetri)")]
        [Tooltip("F1 tuşu ile canlı Wi-Fi / FPS teşhis panelini açıp kapatır.")]
        [SerializeField] private bool showDiagnosticsHud = false;

        [Tooltip("Wi-Fi paket gecikmesi veya kare süresi bu eşiği (ms) aşarsa Console'a nedenini yazar.")]
        [SerializeField] private float spikeLogThresholdMs = 90f;

        [Header("Canlı Durum (Sadece Okunur)")]
        [SerializeField] private bool isConnected;
        [SerializeField] private float packetRateHz;
        [SerializeField] private float maxWifiPacketGapMs;
        [SerializeField] private float maxUnityFrameTimeMs;
        [SerializeField] private int burstDrainedPerSec;
        [SerializeField] private uint totalShotCount;
        [SerializeField] private bool isTriggerPressed;
        [SerializeField] private bool isMagContactPressed;
        [SerializeField] private uint totalMagToggleCount;
        [SerializeField] private bool isRecenterButtonPressed;
        [SerializeField] private uint totalRecenterCount;
        [SerializeField] private Vector2 joystickAxis;
        [SerializeField] private bool isJoystickButtonPressed;
        [SerializeField] private Quaternion rawImuQuaternion = Quaternion.identity;

        /// <summary>
        /// Fired on the Unity main thread for each newly observed trigger press.
        /// </summary>
        public event Action<uint> OnShotFired;

        /// <summary>
        /// Fired on the Unity main thread for each newly confirmed GPIO 17 magazine toggle contact (equivalent to 'V').
        /// </summary>
        public event Action<uint> OnMagazineTogglePressed;

        /// <summary>
        /// Fired on the Unity main thread for each newly confirmed GPIO 18 physical recenter button press
        /// (equivalent to 2-finger screen tap: recenters VR head + rifle + guided 2-LED optical zeroing).
        /// </summary>
        public event Action<uint> OnRecenterButtonPressed;

        public Quaternion RawImuQuaternion { get; private set; } = Quaternion.identity;
        public bool IsTriggerPressed { get; private set; }
        public bool IsShoulderPressed { get; private set; }
        public bool IsMagContactPressed { get; private set; }
        public bool IsRecenterButtonPressed { get; private set; }
        public bool HasJoystickData { get; private set; }
        public Vector2 JoystickAxis { get; private set; }
        public bool IsJoystickButtonPressed { get; private set; }
        public uint TotalShotCount { get; private set; }
        public uint TotalMagToggleCount { get; private set; }
        public uint TotalRecenterCount { get; private set; }
        public uint PacketSequence { get; private set; }
        public bool IsConnected { get; private set; }
        public float PacketRateHz { get; private set; }
        public float MaxWifiPacketGapMs { get; private set; }
        public float MaxUnityFrameTimeMs { get; private set; }
        public int BurstDrainedPerSec { get; private set; }

        /// <summary>
        /// Seconds elapsed since the latest packet was received by the background socket thread.
        /// </summary>
        public float PacketAgeSeconds { get; private set; }

        private Socket _socket;
        private Thread _receiveThread;
        private volatile bool _isRunning;

        // Pre-allocated receive buffers (0 Bytes GC Allocation in ReceiveLoop)
        private readonly byte[] _recvBuffer = new byte[64];
        private readonly byte[] _latestValidPacket = new byte[MaxPacketSizeBytes];
        private static readonly byte[] UnicastReplyMagic = { (byte)'V', (byte)'R', (byte)'A', (byte)'T' };

        // Shared state guarded by _stateLock
        private readonly object _stateLock = new object();
        private Quaternion _sharedQuat = Quaternion.identity;
        private bool _sharedTriggerPressed;
        private bool _sharedShoulderPressed;
        private bool _sharedHasMagData;
        private bool _sharedMagContactPressed;
        private ushort _sharedMagToggleCount;
        private bool _sharedHasRecenterData;
        private bool _sharedRecenterPressed;
        private ushort _sharedRecenterCount;
        private bool _sharedHasJoyData;
        private float _sharedJoyX;
        private float _sharedJoyY;
        private bool _sharedJoySwPressed;
        private uint _sharedShotCount;
        private uint _sharedPacketSeq;
        private bool _hasReceivedAnyPacket;
        private long _lastPacketStopwatchTicks;
        private float _maxPacketGapMsWindow;
        private int _packetsThisSecond;
        private int _burstDrainedThisSecond;

        // Main-thread state
        private bool _shotBaselineInitialized;
        private uint _lastProcessedShotCount;
        private bool _magBaselineInitialized;
        private ushort _lastProcessedMagToggleCount;
        private bool _recenterBaselineInitialized;
        private ushort _lastProcessedRecenterCount;
        private bool _loggedFirstPacket;
        private float _statsTimer;
        private float _maxFrameTimeMsWindow;
        private GUIStyle _hudStyle;

        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        private void Awake()
        {
            Application.runInBackground = true;
            if (targetFrameRate > 0)
            {
                // On mobile VR, lock 1:1 to hardware VSync (Swappy) to eliminate 120fps-into-60Hz frame-drop judder;
                // on PC Editor/Standalone keep uncapped targetFrameRate.
                QualitySettings.vSyncCount = Application.isMobilePlatform ? 1 : 0;
                Application.targetFrameRate = targetFrameRate;
            }
        }

        private void OnEnable()
        {
            StartReceiver();
        }

        private void OnDisable()
        {
            StopReceiver();
        }

        private void OnApplicationQuit()
        {
            StopReceiver();
        }

        public void StartReceiver()
        {
            if (_isRunning)
                return;

            try
            {
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
                _socket.ReceiveBufferSize = 4096;
                _socket.ReceiveTimeout = 500;

                // Prevent Windows ICMP Port Unreachable from throwing SocketError.ConnectionReset
                try
                {
                    _socket.IOControl((IOControlCode)SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null);
                }
                catch
                {
                    // Ignored on non-Windows platforms (Android)
                }

                _socket.Bind(new IPEndPoint(IPAddress.Any, udpPort));
                _isRunning = true;

                _receiveThread = new Thread(ReceiveLoop)
                {
                    IsBackground = true,
                    Name = "ESP32_UDP_ZeroAlloc",
                    Priority = System.Threading.ThreadPriority.Highest
                };
                _receiveThread.Start();
                Debug.Log($"[Esp32RifleReceiver] Zero-Alloc UDP dinleme başlatıldı (Port: {udpPort}).");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Esp32RifleReceiver] UDP port {udpPort} açılamadı: {ex.Message}");
                StopReceiver();
            }
        }

        public void StopReceiver()
        {
            _isRunning = false;

            if (_socket != null)
            {
                try { _socket.Close(); } catch { }
                _socket.Dispose();
                _socket = null;
            }

            if (_receiveThread != null && _receiveThread.IsAlive)
            {
                _receiveThread.Join(200);
                _receiveThread = null;
            }
        }

        private void ReceiveLoop()
        {
            long prevPacketTicks = 0;
            EndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);
            int replyCountdown = 0;

            while (_isRunning)
            {
                try
                {
                    int bytesRead = _socket.ReceiveFrom(_recvBuffer, 0, _recvBuffer.Length, SocketFlags.None, ref remoteEp);
                    if (bytesRead < PacketSizeBytes || _recvBuffer[0] != PacketHeader)
                        continue;

                    // Send a lightweight 4-byte "VRAT" unicast registration reply on first packet and every ~100 packets
                    // so the ESP32 immediately switches from 100ms-buffered Wi-Fi Broadcast to 200 Hz direct Unicast!
                    if (replyCountdown <= 0)
                    {
                        replyCountdown = 100;
                        try { _socket.SendTo(UnicastReplyMagic, 0, UnicastReplyMagic.Length, SocketFlags.None, remoteEp); } catch { }
                    }
                    else
                    {
                        replyCountdown--;
                    }

                    int latestPacketLen = Mathf.Min(bytesRead, MaxPacketSizeBytes);
                    for (int k = 22; k < MaxPacketSizeBytes; k++)
                        _latestValidPacket[k] = 0;
                    Buffer.BlockCopy(_recvBuffer, 0, _latestValidPacket, 0, latestPacketLen);
                    int burstCount = 1;
                    int drainedExtra = 0;

                    // Zero-alloc burst drain: if Wi-Fi delivered multiple queued packets at once, grab the newest one
                    while (_isRunning && _socket.Available >= PacketSizeBytes)
                    {
                        int extraRead = _socket.Receive(_recvBuffer, 0, _recvBuffer.Length, SocketFlags.None);
                        if (extraRead >= PacketSizeBytes && _recvBuffer[0] == PacketHeader)
                        {
                            latestPacketLen = Mathf.Min(extraRead, MaxPacketSizeBytes);
                            for (int k = 22; k < MaxPacketSizeBytes; k++)
                                _latestValidPacket[k] = 0;
                            Buffer.BlockCopy(_recvBuffer, 0, _latestValidPacket, 0, latestPacketLen);
                            burstCount++;
                            drainedExtra++;
                        }
                    }

                    float qw = BitConverter.ToSingle(_latestValidPacket, 1);
                    float qx = BitConverter.ToSingle(_latestValidPacket, 5);
                    float qy = BitConverter.ToSingle(_latestValidPacket, 9);
                    float qz = BitConverter.ToSingle(_latestValidPacket, 13);
                    byte trig = _latestValidPacket[17];
                    uint shots = BitConverter.ToUInt32(_latestValidPacket, 18);
                    bool shoulder = (latestPacketLen >= 23 && _latestValidPacket[22] != 0) || ((trig & 0x02) != 0);
                    bool hasMagField = latestPacketLen >= 26;
                    bool magContact = hasMagField && _latestValidPacket[23] != 0;
                    ushort magToggles = hasMagField ? BitConverter.ToUInt16(_latestValidPacket, 24) : (ushort)0;
                    bool hasRecenterField = latestPacketLen >= 29;
                    bool recenterPressed = hasRecenterField && _latestValidPacket[26] != 0;
                    ushort recenterCount = hasRecenterField ? BitConverter.ToUInt16(_latestValidPacket, 27) : (ushort)0;
                    bool hasJoyField = latestPacketLen >= 34;
                    short rawJoyX = hasJoyField ? BitConverter.ToInt16(_latestValidPacket, 29) : (short)0;
                    short rawJoyY = hasJoyField ? BitConverter.ToInt16(_latestValidPacket, 31) : (short)0;
                    bool joySwPressed = hasJoyField && _latestValidPacket[33] != 0;

                    if (!IsFinite(qw) || !IsFinite(qx) || !IsFinite(qy) || !IsFinite(qz))
                        continue;

                    float magSq = qw * qw + qx * qx + qy * qy + qz * qz;
                    if (magSq < 1e-6f)
                        continue;

                    float invMag = 1f / Mathf.Sqrt(magSq);
                    Quaternion parsed = new Quaternion(qx * invMag, qy * invMag, qz * invMag, qw * invMag);

                    long nowTicks = Stopwatch.GetTimestamp();
                    float gapMs = 0f;
                    if (prevPacketTicks != 0)
                    {
                        gapMs = (float)((nowTicks - prevPacketTicks) * TicksToMs);
                    }
                    prevPacketTicks = nowTicks;

                    lock (_stateLock)
                    {
                        _sharedQuat = parsed;
                        _sharedTriggerPressed = (trig & 0x01) != 0;
                        _sharedShoulderPressed = shoulder;
                        if (hasMagField)
                        {
                            _sharedHasMagData = true;
                            _sharedMagContactPressed = magContact;
                            _sharedMagToggleCount = magToggles;
                        }
                        if (hasRecenterField)
                        {
                            _sharedHasRecenterData = true;
                            _sharedRecenterPressed = recenterPressed;
                            _sharedRecenterCount = recenterCount;
                        }
                        if (hasJoyField)
                        {
                            _sharedHasJoyData = true;
                            _sharedJoyX = Mathf.Clamp(rawJoyX * 0.001f, -1f, 1f);
                            _sharedJoyY = Mathf.Clamp(rawJoyY * 0.001f, -1f, 1f);
                            _sharedJoySwPressed = joySwPressed;
                        }
                        _sharedShotCount = shots;
                        _sharedPacketSeq++;
                        _hasReceivedAnyPacket = true;
                        _lastPacketStopwatchTicks = nowTicks;
                        _packetsThisSecond += burstCount;
                        _burstDrainedThisSecond += drainedExtra;
                        if (gapMs > _maxPacketGapMsWindow && gapMs < connectionTimeoutMs)
                        {
                            _maxPacketGapMsWindow = gapMs;
                        }
                    }
                }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode == SocketError.TimedOut ||
                        ex.SocketErrorCode == SocketError.ConnectionReset ||
                        ex.SocketErrorCode == SocketError.Interrupted)
                    {
                        continue;
                    }

                    if (_isRunning)
                        Debug.LogWarning($"[Esp32RifleReceiver] Soket uyarısı: {ex.SocketErrorCode}");
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_isRunning)
                        Debug.LogWarning($"[Esp32RifleReceiver] Paket okuma hatası: {ex.Message}");
                }
            }
        }

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && Keyboard.current.f1Key.wasPressedThisFrame)
            {
                showDiagnosticsHud = !showDiagnosticsHud;
            }
#endif

            float frameMs = Time.unscaledDeltaTime * 1000f;
            if (frameMs > _maxFrameTimeMsWindow && Time.frameCount > 10)
            {
                _maxFrameTimeMsWindow = frameMs;
            }

            Quaternion latestQuat;
            bool latestTrigger;
            bool latestShoulder;
            bool latestHasMagData;
            bool latestMagContact;
            ushort latestMagToggles;
            bool latestHasRecenterData;
            bool latestRecenterPressed;
            ushort latestRecenterCount;
            bool latestHasJoyData;
            float latestJoyX;
            float latestJoyY;
            bool latestJoySwPressed;
            uint latestShots;
            uint latestSeq;
            bool hasPacket;
            long lastTicks;

            lock (_stateLock)
            {
                latestQuat = _sharedQuat;
                latestTrigger = _sharedTriggerPressed;
                latestShoulder = _sharedShoulderPressed;
                latestHasMagData = _sharedHasMagData;
                latestMagContact = _sharedMagContactPressed;
                latestMagToggles = _sharedMagToggleCount;
                latestHasRecenterData = _sharedHasRecenterData;
                latestRecenterPressed = _sharedRecenterPressed;
                latestRecenterCount = _sharedRecenterCount;
                latestHasJoyData = _sharedHasJoyData;
                latestJoyX = _sharedJoyX;
                latestJoyY = _sharedJoyY;
                latestJoySwPressed = _sharedJoySwPressed;
                latestShots = _sharedShotCount;
                latestSeq = _sharedPacketSeq;
                hasPacket = _hasReceivedAnyPacket;
                lastTicks = _lastPacketTicksOrDefault();
            }

            long currentTicks = Stopwatch.GetTimestamp();

            if (hasPacket)
            {
                if (!_loggedFirstPacket)
                {
                    _loggedFirstPacket = true;
                    Debug.Log($"[Esp32RifleReceiver] İlk ESP32 paketi alındı! Atis={latestShots}, Tetik={latestTrigger}, Sarjor={latestMagToggles}");
                }

                double elapsedMs = (currentTicks - lastTicks) * TicksToMs;
                PacketAgeSeconds = (float)(elapsedMs * 0.001);
                IsConnected = elapsedMs <= connectionTimeoutMs;
                RawImuQuaternion = latestQuat;
                IsTriggerPressed = latestTrigger;
                IsShoulderPressed = latestShoulder;
                TotalShotCount = latestShots;
                PacketSequence = latestSeq;

                if (!_shotBaselineInitialized)
                {
                    _lastProcessedShotCount = latestShots;
                    _shotBaselineInitialized = true;
                }
                else if (latestShots > _lastProcessedShotCount)
                {
                    uint deltaShots = latestShots - _lastProcessedShotCount;
                    for (uint i = 0; i < Math.Min(deltaShots, 5u); i++)
                    {
                        OnShotFired?.Invoke(_lastProcessedShotCount + i + 1);
                    }
                    _lastProcessedShotCount = latestShots;
                }
                else if (latestShots < _lastProcessedShotCount)
                {
                    // Treat as ESP32 reboot/reset when counter drops to 0 or jumps far backward;
                    // ignore small 1-2 backward steps from rare out-of-order UDP packets.
                    if (latestShots == 0 || (_lastProcessedShotCount - latestShots) > 2u)
                    {
                        _lastProcessedShotCount = latestShots;
                    }
                }

                if (latestHasMagData)
                {
                    IsMagContactPressed = latestMagContact;
                    TotalMagToggleCount = latestMagToggles;

                    if (!_magBaselineInitialized)
                    {
                        _lastProcessedMagToggleCount = latestMagToggles;
                        _magBaselineInitialized = true;
                    }
                    else if (latestMagToggles != _lastProcessedMagToggleCount)
                    {
                        // Unsigned 16-bit forward delta: handles wraparound cleanly and ignores ESP32 reboot reset to 0
                        ushort forwardDelta = (ushort)(latestMagToggles - _lastProcessedMagToggleCount);
                        if (forwardDelta > 0 && forwardDelta <= 8)
                        {
                            int emitCount = Math.Min((int)forwardDelta, 2);
                            for (int i = 0; i < emitCount; i++)
                            {
                                ushort seqVal = (ushort)(_lastProcessedMagToggleCount + i + 1);
                                OnMagazineTogglePressed?.Invoke(seqVal);
                            }
                        }
                        _lastProcessedMagToggleCount = latestMagToggles;
                    }
                }

                if (latestHasRecenterData)
                {
                    IsRecenterButtonPressed = latestRecenterPressed;
                    TotalRecenterCount = latestRecenterCount;

                    if (!_recenterBaselineInitialized)
                    {
                        _lastProcessedRecenterCount = latestRecenterCount;
                        _recenterBaselineInitialized = true;
                    }
                    else if (latestRecenterCount != _lastProcessedRecenterCount)
                    {
                        ushort forwardDelta = (ushort)(latestRecenterCount - _lastProcessedRecenterCount);
                        if (forwardDelta > 0 && forwardDelta <= 8)
                        {
                            ushort seqVal = (ushort)(_lastProcessedRecenterCount + forwardDelta);
                            OnRecenterButtonPressed?.Invoke(seqVal);
                        }
                        _lastProcessedRecenterCount = latestRecenterCount;
                    }
                }

                if (latestHasJoyData && IsConnected)
                {
                    HasJoystickData = true;
                    JoystickAxis = new Vector2(latestJoyX, latestJoyY);
                    IsJoystickButtonPressed = latestJoySwPressed;
                }
                else
                {
                    JoystickAxis = Vector2.zero;
                    IsJoystickButtonPressed = false;
                }
            }
            else
            {
                IsConnected = false;
                PacketAgeSeconds = 999f;
                JoystickAxis = Vector2.zero;
                IsJoystickButtonPressed = false;
            }

            _statsTimer += Time.unscaledDeltaTime;
            if (_statsTimer >= 1f)
            {
                int pktCount;
                int drainedCount;
                float maxWifiGap;

                lock (_stateLock)
                {
                    pktCount = _packetsThisSecond;
                    drainedCount = _burstDrainedThisSecond;
                    maxWifiGap = _maxPacketGapMsWindow;
                    _packetsThisSecond = 0;
                    _burstDrainedThisSecond = 0;
                    _maxPacketGapMsWindow = 0f;
                }

                PacketRateHz = pktCount / _statsTimer;
                BurstDrainedPerSec = drainedCount;
                MaxWifiPacketGapMs = maxWifiGap;
                MaxUnityFrameTimeMs = _maxFrameTimeMsWindow;
                _maxFrameTimeMsWindow = 0f;
                _statsTimer = 0f;

                if (IsConnected && spikeLogThresholdMs > 0f)
                {
                    if (MaxWifiPacketGapMs >= spikeLogThresholdMs)
                    {
                        Debug.LogWarning($"[Teşhis - Wi-Fi Gecikmesi] Son 1 sn içinde iki UDP paketi arası {MaxWifiPacketGapMs:F1} ms açıldı (Biriken/Eritilen Paket: {BurstDrainedPerSec}). Tahminleyici devreye girdi.");
                    }
                    if (MaxUnityFrameTimeMs >= spikeLogThresholdMs)
                    {
                        Debug.LogWarning($"[Teşhis - PC/Unity Kare Takılması] Son 1 sn içinde Unity kare süresi {MaxUnityFrameTimeMs:F1} ms sürdü (Wi-Fi maks boşluk: {MaxWifiPacketGapMs:F1} ms).");
                    }
                }
            }

            // Mirror to inspector
            isConnected = IsConnected;
            packetRateHz = PacketRateHz;
            maxWifiPacketGapMs = MaxWifiPacketGapMs;
            maxUnityFrameTimeMs = MaxUnityFrameTimeMs;
            burstDrainedPerSec = BurstDrainedPerSec;
            totalShotCount = TotalShotCount;
            isTriggerPressed = IsTriggerPressed;
            isMagContactPressed = IsMagContactPressed;
            totalMagToggleCount = TotalMagToggleCount;
            isRecenterButtonPressed = IsRecenterButtonPressed;
            totalRecenterCount = TotalRecenterCount;
            joystickAxis = JoystickAxis;
            isJoystickButtonPressed = IsJoystickButtonPressed;
            rawImuQuaternion = RawImuQuaternion;
        }

        private long _lastPacketTicksOrDefault() => _lastPacketStopwatchTicks;

        private void OnGUI()
        {
            if (!showDiagnosticsHud)
                return;

            if (_hudStyle == null)
            {
                _hudStyle = new GUIStyle(GUI.skin.box)
                {
                    fontSize = 13,
                    alignment = TextAnchor.UpperLeft,
                    normal = { textColor = Color.white }
                };
            }

            string status = IsConnected ? "BAĞLI" : "BEKLENİYOR";
            float liveFps = 1f / Mathf.Max(0.0001f, Time.unscaledDeltaTime);

            if (CardboardStereoRig.IsStereoSplitActive)
            {
                Rect leftNorm = CardboardStereoRig.LeftEyeViewportNorm;
                Rect rightNorm = CardboardStereoRig.RightEyeViewportNorm;
                float eyePxW = leftNorm.width * Screen.width;
                float boxW = Mathf.Clamp(eyePxW * 0.65f, 180f, 300f);
                float boxH = 56f;
                float yPos = Mathf.Max(28f, Screen.height * 0.14f);
                string vrText =
                    $"FPS: {liveFps:F0} ({MaxUnityFrameTimeMs:F1} ms) | Mercek: {CardboardStereoRig.ActiveLensSpacingMm:F0}mm\n" +
                    $"ESP32: {status} ({PacketRateHz:F0} Hz) | En: {CardboardStereoRig.DetectedPhoneWidthMm:F0}mm";

                float leftX = leftNorm.x * Screen.width + (eyePxW - boxW) * 0.5f;
                float rightX = rightNorm.x * Screen.width + (rightNorm.width * Screen.width - boxW) * 0.5f;
                GUI.Box(new Rect(leftX, yPos, boxW, boxH), vrText, _hudStyle);
                GUI.Box(new Rect(rightX, yPos, boxW, boxH), vrText, _hudStyle);
                return;
            }

            string text =
                $"[ESP32 Telemetri - Gizlemek için F1]\n" +
                $"Durum: {status} | Paket Hızı: {PacketRateHz:F0} Hz | Atış: {TotalShotCount} | Şarjör: {TotalMagToggleCount}\n" +
                $"Joystick: X={JoystickAxis.x:+0.00;-0.00;0.00} Y={JoystickAxis.y:+0.00;-0.00;0.00} SW={(IsJoystickButtonPressed ? 1 : 0)}\n" +
                $"Maks Wi-Fi Paket Boşluğu: {MaxWifiPacketGapMs:F1} ms (Normal: ~5-12 ms)\n" +
                $"Maks Unity Kare Süresi: {MaxUnityFrameTimeMs:F1} ms ({liveFps:F0} FPS) | Eritilen: {BurstDrainedPerSec}/sn";

            GUI.Box(new Rect(12f, 12f, 445f, 114f), text, _hudStyle);
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
