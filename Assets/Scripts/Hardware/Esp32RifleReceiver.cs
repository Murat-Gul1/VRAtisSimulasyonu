using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace VRAtis.Hardware
{
    /// <summary>
    /// Listens for 22-byte binary quaternion + trigger packets from the ESP32 over UDP
    /// on a background thread and exposes thread-safe state and reliable shot events.
    /// </summary>
    public class Esp32RifleReceiver : MonoBehaviour
    {
        public const byte PacketHeader = 0xAA;
        public const int PacketSizeBytes = 22;

        [Header("Ağ Ayarları")]
        [Tooltip("ESP32 kodundaki UDP_PORT ile aynı olmalıdır (varsayılan 4210).")]
        [SerializeField] private int udpPort = 4210;

        [Tooltip("Bu süre içinde paket gelmezse bağlantı kopmuş sayılır (ms).")]
        [SerializeField] private float connectionTimeoutMs = 500f;

        [Header("Otomatik Keşif (Opsiyonel)")]
        [Tooltip("Açılırsa Unity belirli aralıklarla ağa kendi IP keşif paketini yayınlayabilir.")]
        [SerializeField] private bool enableDiscoveryBeacon = false;
        [SerializeField] private int discoveryPort = 4211;
        [SerializeField] private float discoveryIntervalSec = 2f;

        [Header("Canlı Durum (Sadece Okunur)")]
        [SerializeField] private bool isConnected;
        [SerializeField] private float packetRateHz;
        [SerializeField] private uint totalShotCount;
        [SerializeField] private bool isTriggerPressed;
        [SerializeField] private Quaternion rawImuQuaternion = Quaternion.identity;

        /// <summary>
        /// Fired on the Unity main thread for each newly observed trigger press (including missed-packet catchup).
        /// </summary>
        public event Action<uint> OnShotFired;

        /// <summary>
        /// Latest raw quaternion reported by ESP32 Madgwick filter (w, x, y, z mapped into Unity struct fields).
        /// Note: Axis conversion to Unity left-handed space is handled by RifleOrientationController.
        /// </summary>
        public Quaternion RawImuQuaternion { get; private set; } = Quaternion.identity;
        public bool IsTriggerPressed { get; private set; }
        public uint TotalShotCount { get; private set; }
        public uint PacketSequence { get; private set; }
        public bool IsConnected { get; private set; }
        public float PacketRateHz { get; private set; }

        private UdpClient _udpClient;
        private Thread _receiveThread;
        private volatile bool _isRunning;

        // Shared state guarded by _stateLock
        private readonly object _stateLock = new object();
        private Quaternion _sharedQuat = Quaternion.identity;
        private bool _sharedTriggerPressed;
        private uint _sharedShotCount;
        private uint _sharedPacketSeq;
        private bool _hasReceivedAnyPacket;
        private long _lastPacketTicks;
        private int _packetsThisSecond;

        // Main-thread shot synchronization
        private bool _shotBaselineInitialized;
        private uint _lastProcessedShotCount;
        private bool _loggedFirstPacket;
        private float _rateTimer;
        private float _discoveryTimer;

        private void Awake()
        {
            Application.runInBackground = true;
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
                _udpClient = new UdpClient(udpPort);
                _udpClient.Client.ReceiveTimeout = 500;
                _isRunning = true;

                _receiveThread = new Thread(ReceiveLoop)
                {
                    IsBackground = true,
                    Name = "ESP32_UDP_Receiver"
                };
                _receiveThread.Start();
                Debug.Log($"[Esp32RifleReceiver] UDP dinleme başlatıldı (Port: {udpPort}).");
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

            if (_udpClient != null)
            {
                try { _udpClient.Close(); } catch { /* Ignore socket shutdown exceptions */ }
                _udpClient.Dispose();
                _udpClient = null;
            }

            if (_receiveThread != null && _receiveThread.IsAlive)
            {
                _receiveThread.Join(200);
                _receiveThread = null;
            }
        }

        private void ReceiveLoop()
        {
            IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);

            while (_isRunning)
            {
                try
                {
                    byte[] data = _udpClient.Receive(ref remoteEndPoint);
                    int burstCount = 1;

                    // Drain any queued burst in the OS socket buffer so we always use the freshest packet (prevents buffer bloat/lag)
                    while (_isRunning && _udpClient.Available >= PacketSizeBytes)
                    {
                        byte[] newer = _udpClient.Receive(ref remoteEndPoint);
                        if (newer != null && newer.Length >= PacketSizeBytes && newer[0] == PacketHeader)
                        {
                            data = newer;
                            burstCount++;
                        }
                    }

                    if (data == null || data.Length < PacketSizeBytes)
                        continue;

                    if (data[0] != PacketHeader)
                        continue;

                    // ESP32 packet layout (packed, little-endian):
                    // [0] header (1) | [1..4] qw | [5..8] qx | [9..12] qy | [13..16] qz | [17] triggerState | [18..21] shotCount
                    float qw = BitConverter.ToSingle(data, 1);
                    float qx = BitConverter.ToSingle(data, 5);
                    float qy = BitConverter.ToSingle(data, 9);
                    float qz = BitConverter.ToSingle(data, 13);
                    byte trig = data[17];
                    uint shots = BitConverter.ToUInt32(data, 18);

                    if (!IsFinite(qw) || !IsFinite(qx) || !IsFinite(qy) || !IsFinite(qz))
                        continue;

                    float magSq = qw * qw + qx * qx + qy * qy + qz * qz;
                    if (magSq < 1e-6f)
                        continue;

                    // Normalize incoming quaternion defensively
                    float invMag = 1f / Mathf.Sqrt(magSq);
                    Quaternion parsed = new Quaternion(qx * invMag, qy * invMag, qz * invMag, qw * invMag);

                    lock (_stateLock)
                    {
                        _sharedQuat = parsed;
                        _sharedTriggerPressed = trig != 0;
                        _sharedShotCount = shots;
                        _sharedPacketSeq++;
                        _hasReceivedAnyPacket = true;
                        _lastPacketTicks = DateTime.UtcNow.Ticks;
                        _packetsThisSecond += burstCount;
                    }
                }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode == SocketError.TimedOut)
                        continue;

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
                lastTicks = _lastPacketTicks;
            }

            if (hasPacket)
            {
                if (!_loggedFirstPacket)
                {
                    _loggedFirstPacket = true;
                    Debug.Log($"[Esp32RifleReceiver] İlk ESP32 paketi alındı! Atis={latestShots}, Tetik={latestTrigger}");
                }

                double elapsedMs = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - lastTicks).TotalMilliseconds;
                IsConnected = elapsedMs <= connectionTimeoutMs;
                RawImuQuaternion = latestQuat;
                IsTriggerPressed = latestTrigger;
                TotalShotCount = latestShots;
                PacketSequence = latestSeq;

                // Synchronize initial shot counter so shots pressed before Unity launched don't auto-fire on frame 1.
                if (!_shotBaselineInitialized)
                {
                    _lastProcessedShotCount = latestShots;
                    _shotBaselineInitialized = true;
                }
                else if (latestShots > _lastProcessedShotCount)
                {
                    uint deltaShots = latestShots - _lastProcessedShotCount;
                    // Guard against unreasonable jump if ESP32 rebooted cleanly (handled in else-if below)
                    for (uint i = 0; i < Math.Min(deltaShots, 5u); i++)
                    {
                        OnShotFired?.Invoke(_lastProcessedShotCount + i + 1);
                    }
                    _lastProcessedShotCount = latestShots;
                }
                else if (latestShots < _lastProcessedShotCount)
                {
                    // ESP32 was reset while Unity remained open
                    _lastProcessedShotCount = latestShots;
                }
            }
            else
            {
                IsConnected = false;
            }

            _rateTimer += Time.unscaledDeltaTime;
            if (_rateTimer >= 1f)
            {
                int count;
                lock (_stateLock)
                {
                    count = _packetsThisSecond;
                    _packetsThisSecond = 0;
                }
                PacketRateHz = count / _rateTimer;
                _rateTimer = 0f;
            }

            if (enableDiscoveryBeacon)
            {
                _discoveryTimer += Time.unscaledDeltaTime;
                if (_discoveryTimer >= discoveryIntervalSec)
                {
                    _discoveryTimer = 0f;
                    SendDiscoveryBeacon();
                }
            }

            // Mirror to inspector debug fields
            isConnected = IsConnected;
            packetRateHz = PacketRateHz;
            totalShotCount = TotalShotCount;
            isTriggerPressed = IsTriggerPressed;
            rawImuQuaternion = RawImuQuaternion;
        }

        private void SendDiscoveryBeacon()
        {
            if (_udpClient == null)
                return;

            try
            {
                byte[] beacon = { 0x56, 0x52, 0x41, 0x54 }; // "VRAT"
                _udpClient.EnableBroadcast = true;
                _udpClient.Send(beacon, beacon.Length, new IPEndPoint(IPAddress.Broadcast, discoveryPort));
            }
            catch
            {
                // Broadcast may be blocked on some networks; ignore silently
            }
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
