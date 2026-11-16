using UnityEngine;

namespace VRAtis.Ballistics
{
    /// <summary>
    /// Central atmospheric, wind, and weather state used by both ballistics and environment shaders/audio.
    /// </summary>
    public class EnvironmentManager : MonoBehaviour
    {
        private static readonly int GlobalWindShaderId = Shader.PropertyToID("_GlobalWindVector");

        public static EnvironmentManager Instance { get; private set; }

        [Header("Atmosfer Koşulları")]
        [SerializeField] private float temperatureC = 15f;
        [SerializeField] private float pressureHPa = 1013.25f;
        [Range(0f, 100f)]
        [SerializeField] private float humidityPercent = 50f;
        [SerializeField] private float altitudeMeters = 0f;

        [Header("Fizik Sabitleri")]
        public float gravity = BallisticMath.StandardGravity;
        public float earthRadiusMeters = 6371000f;

        [Header("Rüzgar (Dünya Ekseni m/s)")]
        [Tooltip("X = Sağ/Sol yan rüzgar, Y = Dikey akım, Z = Baş/Arka rüzgar")]
        public Vector3 windSpeedMS = new Vector3(2.5f, 0f, 0f);
        [Tooltip("Yükseklik metre başına rüzgar hızı artışı (m/s per m)")]
        public float windGradientPerMeter = 0.02f;

        [Header("Hava Durumu ve Işık Durumu")]
        public bool isRaining;
        [Range(0f, 100f)]
        public float rainIntensityPercent = 50f;

        [Tooltip("0 = Zifiri Gece, 0.35 = Alacakaranlık, 1 = Güneşli Gündüz (Namlu alevi ve ışık şiddetini kontrol eder).")]
        [Range(0f, 1f)]
        public float daylightFactor = 1.0f;

        public bool isNight;

        [Header("Hesaplanan Değerler (Sadece Okunur)")]
        [SerializeField] private float currentAirDensity = 1.225f;
        [SerializeField] private float currentSpeedOfSound = 340.3f;

        public float TemperatureC => temperatureC;
        public float PressureHPa => pressureHPa;
        public float HumidityPercent => humidityPercent;
        public float AltitudeMeters => altitudeMeters;
        public float CurrentAirDensity => currentAirDensity;
        public float CurrentSpeedOfSound => currentSpeedOfSound;
        public float EffectiveDaylightFactor => isNight ? Mathf.Min(daylightFactor, 0.05f) : daylightFactor;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            RecalculateAtmosphere();
            PushWindToShaders();
        }

        private void OnValidate()
        {
            RecalculateAtmosphere();
            PushWindToShaders();
        }

        private void Update()
        {
            PushWindToShaders();
        }

        public void SetAtmosphere(float tempC, float pressHPa, float humidityPct, float altMeters)
        {
            temperatureC = tempC;
            pressureHPa = pressHPa;
            humidityPercent = Mathf.Clamp(humidityPct, 0f, 100f);
            altitudeMeters = altMeters;
            RecalculateAtmosphere();
        }

        public void RecalculateAtmosphere()
        {
            currentAirDensity = BallisticMath.CalculateAirDensity(
                temperatureC, pressureHPa, humidityPercent, altitudeMeters);
            currentSpeedOfSound = BallisticMath.CalculateSpeedOfSound(temperatureC);
        }

        /// <summary>
        /// Returns wind velocity at <paramref name="altitudeY"/> including altitude gradient along the horizontal wind direction.
        /// </summary>
        public Vector3 GetWindAtAltitude(float altitudeY)
        {
            if (windSpeedMS.sqrMagnitude < 1e-6f)
                return Vector3.zero;

            float h = Mathf.Max(0f, altitudeY);
            Vector3 horizontalWind = new Vector3(windSpeedMS.x, 0f, windSpeedMS.z);
            float horizontalMag = horizontalWind.magnitude;
            if (horizontalMag < 1e-5f)
                return windSpeedMS;

            Vector3 windDir = horizontalWind / horizontalMag;
            return windSpeedMS + windDir * (h * windGradientPerMeter);
        }

        private void PushWindToShaders()
        {
            Shader.SetGlobalVector(GlobalWindShaderId, new Vector4(windSpeedMS.x, windSpeedMS.y, windSpeedMS.z, windSpeedMS.magnitude));
        }
    }
}
