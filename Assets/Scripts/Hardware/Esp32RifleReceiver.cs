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
    /// Zero-allocation, high-priority UDP receiver for the 22-byte ESP32 quaternion + trigger packet.
    /// Includes burst draining and real-time Wi-Fi vs. Unity frame stutter diagnostics.
    /// </summary>
    public class Esp32RifleReceiver : MonoBehaviour
    {
        public const byte PacketHeader = 0xAA;
        public const int PacketSizeBytes = 22;
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
        [SerializeField] private Quaternion rawImuQuaternion = Quaternion.identity;

        /// <summary>
        /// Fired on the Unity main thread for each newly observed trigger press.
        /// </summary>
        public event Action<uint> OnShotFired;

        public Quaternion RawImuQuaternion { get; private set; } = Quaternion.identity;
        public bool IsTriggerPressed { get; private set; }
        public uint TotalShotCount { get; private set; }
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
        private readonly byte[] _latestValidPacket = new byte[PacketSizeBytes];

        // Shared state guarded by _stateLock
        private readonly object _stateLock = new object();
        private Quaternion _sharedQuat = Quaternion.identity;
        private bool _sharedTriggerPressed;
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
                QualitySettings.vSyncCount = 0;
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

            while (_isRunning)
            {
                try
                {
                    int bytesRead = _socket.Receive(_recvBuffer, 0, _recvBuffer.Length, SocketFlags.None);
                    if (bytesRead < PacketSizeBytes || _recvBuffer[0] != PacketHeader)
                        continue;

                    Buffer.BlockCopy(_recvBuffer, 0, _latestValidPacket, 0, PacketSizeBytes);
                    int burstCount = 1;
                    int drainedExtra = 0;

                    // Zero-alloc burst drain: if Wi-Fi delivered multiple queued packets at once, grab the newest one
                    while (_isRunning && _socket.Available >= PacketSizeBytes)
                    {
                        int extraRead = _socket.Receive(_recvBuffer, 0, _recvBuffer.Length, SocketFlags.None);
                        if (extraRead >= PacketSizeBytes && _recvBuffer[0] == PacketHeader)
                        {
                            Buffer.BlockCopy(_recvBuffer, 0, _latestValidPacket, 0, PacketSizeBytes);
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
                        _sharedTriggerPressed = trig != 0;
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
            uint latestShots;
            uint latestSeq;
            bool hasPacket;
            long lastTicks;

            lock (_stateLock)
            {
                latestQuat = _sharedQuat;
                latestTrigger = _sharedTriggerPressed;
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
                    Debug.Log($"[Esp32RifleReceiver] İlk ESP32 paketi alındı! Atis={latestShots}, Tetik={latestTrigger}");
                }

                double elapsedMs = (currentTicks - lastTicks) * TicksToMs;
                PacketAgeSeconds = (float)(elapsedMs * 0.001);
                IsConnected = elapsedMs <= connectionTimeoutMs;
                RawImuQuaternion = latestQuat;
                IsTriggerPressed = latestTrigger;
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
                    _lastProcessedShotCount = latestShots;
                }
            }
            else
            {
                IsConnected = false;
                PacketAgeSeconds = 999f;
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
                $"Durum: {status} | Paket Hızı: {PacketRateHz:F0} Hz | Atış: {TotalShotCount}\n" +
                $"Maks Wi-Fi Paket Boşluğu: {MaxWifiPacketGapMs:F1} ms (Normal: ~5-12 ms)\n" +
                $"Maks Unity Kare Süresi: {MaxUnityFrameTimeMs:F1} ms ({liveFps:F0} FPS)\n" +
                $"Eritilen Paket Şişmesi: {BurstDrainedPerSec}/sn";

            GUI.Box(new Rect(12f, 12f, 430f, 102f), text, _hudStyle);
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
