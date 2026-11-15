using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace VRAtis.Hardware
{
    /// <summary>
    /// Converts the ESP32 Madgwick quaternion into Unity's left-handed coordinate space
    /// and aligns horizontal Yaw to the player's forward view while preserving gravity Pitch and Roll.
    /// </summary>
    public class RifleOrientationController : MonoBehaviour
    {
        public enum SensorMountPreset
        {
            /// <summary>
            /// PCB tüfeğin arkasında dikey, ön yüzü atıcıya bakıyor:
            /// Sensör +X = Sağ, +Y = Yukarı (Ray), -Z = İleri (Namlu).
            /// </summary>
            VerticalBackFacingShooter,
            /// <summary>
            /// PCB yatay düz: Sensör +X = Sağ, +Y = İleri (Namlu), +Z = Yukarı.
            /// </summary>
            StandardMpuFlatForward,
            CustomAxisMapping
        }

        [Header("Bağlantılar")]
        [SerializeField] private Esp32RifleReceiver receiver;
        [Tooltip("Yaw sıfırlama sırasında referans alınacak kamera (boş bırakılırsa Main Camera kullanılır).")]
        [SerializeField] private Transform headCameraTransform;

        [Header("Sensör Eksen Eşlemesi (MPU-9250 -> Unity)")]
        [Tooltip("PCB montaj yönünü seçin. Fotoğraftaki dikey montaj için VerticalBackFacingShooter kullanın.")]
        [SerializeField] private SensorMountPreset mountPreset = SensorMountPreset.VerticalBackFacingShooter;

        [Tooltip("Hangi sensör ekseni Unity sağ (X), yukarı (Y), ileri (Z) eksenine karşılık gelir.")]
        [SerializeField] private Vector3Int unityAxisFromSensor = new Vector3Int(1, 3, 2);
        [SerializeField] private bool invertPitch;
        [SerializeField] private bool invertYaw;
        [SerializeField] private bool invertRoll;

        [Header("İnce Montaj Ofseti (Derece)")]
        [SerializeField] private Vector3 mountOffsetEuler;

        [Header("Nişan Sıfırlama (Yaw Zeroing)")]
        [SerializeField] private bool autoZeroYawOnFirstPacket = true;

        [Header("Yumuşatma (Keskin 1:1 tepki için 0 bırakın)")]
        [Range(0f, 60f)]
        [SerializeField] private float smoothingSpeed = 0f;

        private Quaternion _yawCalibrationOffset = Quaternion.identity;
        private bool _hasAutoZeroed;

        private void Awake()
        {
            if (receiver == null)
                receiver = FindAnyObjectByType<Esp32RifleReceiver>();

            if (headCameraTransform == null && Camera.main != null)
                headCameraTransform = Camera.main.transform;
        }

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame)
            {
                RecenterToHeadForward();
            }
#endif

            if (receiver == null || !receiver.IsConnected)
                return;

            Quaternion converted = ConvertImuToUnity(receiver.RawImuQuaternion);

            if (autoZeroYawOnFirstPacket && !_hasAutoZeroed)
            {
                _hasAutoZeroed = true;
                ApplyYawCalibration(converted);
            }

            Quaternion targetRotation = _yawCalibrationOffset * converted;

            if (smoothingSpeed <= 0f)
            {
                transform.rotation = targetRotation;
            }
            else
            {
                float t = 1f - Mathf.Exp(-smoothingSpeed * Time.unscaledDeltaTime);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, t);
            }
        }

        /// <summary>
        /// Aligns the rifle's horizontal forward direction with the headset's current forward direction
        /// while keeping true gravity-referenced Pitch and Roll untouched.
        /// </summary>
        public void RecenterToHeadForward()
        {
            if (receiver == null)
                return;

            Quaternion converted = ConvertImuToUnity(receiver.RawImuQuaternion);
            ApplyYawCalibration(converted);
            Debug.Log("[RifleOrientationController] Tüfek yönü görüş hattına sıfırlandı.");
        }

        private void ApplyYawCalibration(Quaternion converted)
        {
            Transform refCam = headCameraTransform != null ? headCameraTransform : (Camera.main != null ? Camera.main.transform : null);
            Vector3 desiredForward = refCam != null ? refCam.forward : Vector3.forward;
            Vector3 flatDesiredForward = new Vector3(desiredForward.x, 0f, desiredForward.z);
            if (flatDesiredForward.sqrMagnitude < 1e-4f)
                flatDesiredForward = Vector3.forward;

            Vector3 currentForward = converted * Vector3.forward;
            Vector3 flatCurrentForward = new Vector3(currentForward.x, 0f, currentForward.z);
            if (flatCurrentForward.sqrMagnitude < 1e-4f)
                flatCurrentForward = Vector3.forward;

            float deltaYaw = Vector3.SignedAngle(flatCurrentForward, flatDesiredForward, Vector3.up);
            _yawCalibrationOffset = Quaternion.AngleAxis(deltaYaw, Vector3.up);
        }

        private Quaternion ConvertImuToUnity(Quaternion imuQ)
        {
            Quaternion unityQ;

            if (mountPreset == SensorMountPreset.VerticalBackFacingShooter)
            {
                unityQ = new Quaternion(-imuQ.x, -imuQ.z, -imuQ.y, imuQ.w) * Quaternion.Euler(90f, 0f, 0f);
            }
            else if (mountPreset == SensorMountPreset.StandardMpuFlatForward)
            {
                unityQ = new Quaternion(-imuQ.x, -imuQ.z, -imuQ.y, imuQ.w);
            }
            else
            {
                float[] s = { 0f, imuQ.x, imuQ.y, imuQ.z };
                float ux = ExtractAxis(s, unityAxisFromSensor.x);
                float uy = ExtractAxis(s, unityAxisFromSensor.y);
                float uz = ExtractAxis(s, unityAxisFromSensor.z);
                unityQ = new Quaternion(-ux, -uy, -uz, imuQ.w);
            }

            if (invertPitch || invertYaw || invertRoll)
            {
                Vector3 euler = unityQ.eulerAngles;
                if (invertPitch) euler.x = -euler.x;
                if (invertYaw) euler.y = -euler.y;
                if (invertRoll) euler.z = -euler.z;
                unityQ = Quaternion.Euler(euler);
            }

            if (mountOffsetEuler.sqrMagnitude > 1e-5f)
            {
                unityQ *= Quaternion.Euler(mountOffsetEuler);
            }

            return unityQ;
        }

        private static float ExtractAxis(float[] components, int signedAxisIndex)
        {
            int idx = Mathf.Clamp(Mathf.Abs(signedAxisIndex), 1, 3);
            float sign = signedAxisIndex < 0 ? -1f : 1f;
            return components[idx] * sign;
        }
    }
}
