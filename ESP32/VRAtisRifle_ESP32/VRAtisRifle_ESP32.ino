#include <Preferences.h>
#include <WiFi.h>
#include <WiFiUdp.h>
#include <Wire.h>
#include <esp_timer.h>
#include <esp_wifi.h>
#include <math.h>
#include <string.h>

// ============================================================================
// VR ATIŞ SİMÜLASYONU — NİHAİ SAF 6-DOF DUAL-CORE ESP32-S3 FIRMWARE
// Core 1: 1000 Hz Bortz-Koning Exact Quaternion AHRS + 2 Kademeli Akıllı ZUPT + 1000 Hz Tetik Örnekleme
// Core 0: 250 Hz Düşük Gecikmeli Unicast UDP + Parazit Filtreli Omuz/Şarjör/Sıfırlama + HW-504 Joystick (ADC1)
// ============================================================================

// --- AĞ AYARLARI (Yerel wifi_secrets.h varsa otomatik onu kullanır; yoksa şablon değerler derlenir) ---
#if defined(__has_include) && __has_include("wifi_secrets.h")
#include "wifi_secrets.h"
#else
const char* HOTSPOT_SSID     = "YOUR_HOTSPOT_SSID";
const char* HOTSPOT_PASSWORD = "YOUR_HOTSPOT_PASSWORD";

const char* HOME_WIFI_SSID   = "YOUR_HOME_WIFI_SSID";
const char* HOME_WIFI_PASS   = "YOUR_HOME_WIFI_PASSWORD";
#endif
const IPAddress HOME_PC_IP(192, 168, 1, 105);    // Yedek PC yerel IP

const uint16_t UDP_PORT      = 4210;
IPAddress targetIp           = HOME_PC_IP;

// --- PİN VE DONANIM TANIMLAMALARI ---
#define I2C_SDA_PIN  8
#define I2C_SCL_PIN  9
#define TRIGGER_PIN  15     // Tetik Sinyali -> GPIO 15, Diğer uç -> GND
#define SHOULDER_PIN 16     // Omuz Temas Sensörü -> GPIO 16, Diğer plaka -> GND
#define MAG_PIN      17     // Şarjör Çıkar/Tak Kontağı -> GPIO 17, Diğer uç -> GND (Her temas = oyuna 'V')
#define RECENTER_PIN 18     // VR & LED Sıfırlama Butonu -> GPIO 18, Diğer uç -> GND (Ekrana 2 parmak dokunma ile aynı)
#define JOY_VRX_PIN  4      // HW-504 Joystick VRx -> GPIO 4 (ADC1_CH3, Wi-Fi ile çakışmaz | Sağ/Sol)
#define JOY_VRY_PIN  5      // HW-504 Joystick VRy -> GPIO 5 (ADC1_CH4, Wi-Fi ile çakışmaz | İleri/Geri)
#define JOY_SW_PIN   6      // HW-504 Joystick SW  -> GPIO 6 (INPUT_PULLUP, orta basma butonu / Sprint)
#define I2C_CLOCK_HZ 400000 // 400 kHz Fast-Mode I2C

// --- JOYSTICK (HW-504) AYARLARI (Sol Ray Yan Montaj, Pinler Namlu Yönüne / İleriye Bakar, Besleme 3.3V) ---
const uint32_t JOY_SAMPLE_INTERVAL_US = 4000;   // 250 Hz (4 ms) UDP hızıyla senkron ADC1 okuma (Core 0)
const uint32_t JOY_SW_DEBOUNCE_US     = 30000;  // Joystick orta buton (SW) sıçrama koruması (30 ms)
const float    JOY_DEADZONE           = 0.14f;  // %14 dairesel ölü bölge (HW-504 merkez boşluğu / drift engeli)
const float    JOY_SMOOTH_ALPHA       = 0.28f;  // ADC parazit yumuşatma filtresi (hızlı tepki, sıfır titreme)
const float    JOY_EDGE_MARGIN_RAW    = 140.0f; // 0..4095 uç payı (ESP32-S3 ADC tam uçta güvenle +-1000 verir)
// Donanım testi doğrulandı: VRx=Namlu(0)/Geri(4095), VRy=Tavan/Yukarı(0)/Yer/Aşağı(4095)
// Çıkış: Y(+1000=İleri/Namlu, -1000=Geri), X(+1000=Sağa/Yukarı, -1000=Sola/Aşağı)
const bool     JOY_SWAP_XY            = true;   // VRx -> İleri/Geri (Y), VRy -> Yan Adım (X)
const bool     JOY_INVERT_X           = true;   // Yukarı(tavan)=+1000 (Sağ), Aşağı(yer)=-1000 (Sol); tersi için false
const bool     JOY_INVERT_Y           = true;   // Namluya doğru=+1000 (İleri), Kendime doğru=-1000 (Geri)

// --- ZAMANLAMA SABİTLERİ (Core 1: 1000 Hz IMU / Core 0: 250 Hz UDP) ---
const uint32_t IMU_INTERVAL_US        = 1000;  // 1000 Hz (1 ms) deterministik Core 1 döngüsü
const uint32_t UDP_INTERVAL_US        = 4000;  // 250 Hz (4 ms) akıcı Unicast UDP (120 FPS ekrana kare başına 2+ paket)
const uint32_t MIN_UDP_GAP_US         = 1500;  // Wi-Fi TX koruması (1.5 ms)
const uint32_t MIN_SHOT_INTERVAL_US   = 35000; // Tetik ark koruması (35 ms)
const uint8_t  TRIGGER_CONFIRM_MS     = 6;     // Tetik onay eşiği (Core 1 1kHz akıllı sayaç: 6 ms net LOW -> <3 ms ESD/GPIO16 parazit reddi, sıçramada atış kaçmaz)
const uint8_t  TRIGGER_RELEASE_MS     = 15;    // Tetik bırakma eşiği (15 ms net HIGH -> seri atışta hızlı yeniden kurma)
const uint32_t SHOULDER_DEBOUNCE_US   = 30000; // Omuz sensörü sıçrama koruması (30 ms kesintisiz durum)
const uint32_t MAG_CONFIRM_US         = 20000; // Şarjör temas onay süresi (20 ms kesintisiz LOW -> parazit/ESD reddi)
const uint32_t MAG_RELEASE_DEBOUNCE_US= 80000; // Kablolar ayrıldıktan sonra yeniden kurulma (80 ms kesintisiz HIGH)
const uint32_t MIN_MAG_TOGGLE_GAP_US  = 300000;// İki şarjör olayı arası min 300 ms (tek temasta çift tetik koruması)
const uint32_t RECENTER_CONFIRM_US    = 25000; // Sıfırlama butonu onay süresi (25 ms kesintisiz LOW -> parazit/ESD reddi)
const uint32_t RECENTER_RELEASE_US    = 80000; // Sıfırlama butonu bırakma onayı (80 ms kesintisiz HIGH)
const uint32_t MIN_RECENTER_GAP_US    = 500000;// İki sıfırlama basışı arası min 500 ms
const uint32_t DEBUG_INTERVAL_MS      = 1000;  // 1 Hz durum çıktısı
const uint32_t UNICAST_KEEPALIVE_MS   = 6000;  // Unity "VRAT" yanıtı aktifken broadcast susar (Wi-Fi takılmasını önler)

// --- SENSÖR ÖLÇEK SABİTLERİ ---
constexpr float DEG_TO_RAD_F    = PI / 180.0f;
constexpr float RAD_TO_DEG_F    = 180.0f / PI;
constexpr float GYRO_LSB_PER_DS = 32.8f;                          // +-1000 deg/s (FS_SEL=2)
constexpr float GYRO_DEG_SCALE  = 1.0f / GYRO_LSB_PER_DS;         // raw -> deg/s
constexpr float GYRO_RAD_SCALE  = DEG_TO_RAD_F / GYRO_LSB_PER_DS; // raw -> rad/s
constexpr float ACCEL_SCALE     = 1.0f / 8192.0f;                 // +-4g (AFS_SEL=1)

// --- SAF 6-DOF FÜZYON VE 2 KADEMELİ AKILLI ZUPT SABİTLERİ ---
const float WARMUP_BETA               = 1.25f;
const float STEADY_BETA               = 0.012f; // Yalnız tam sabit duruşta çok yumuşak yerçekimi düzeltmesi
const float MOVE_ACCEL_GATE_G         = 0.035f; // |accMag - 1g| > 0.035g iken beta=0 (yanal/merkezkaç ivme koruması)
const float MOVE_ACCEL_DELTA_G        = 0.025f; // Ani ivme değişiminde beta=0
const float MOVE_GYRO_GATE_RAD_S      = 0.14f;  // ~8 deg/s üstü hedef geçişlerinde saf jiroskop (beta=0 -> sıfır Yaw sızıntısı)
const int   ACCEL_SETTLE_SAMPLES_REQ  = 120;    // Hareket bitiminde 120 ms titreşim sönümleme beklemesi

// Kademe 2: Katı Masa / Destekli Sabitlik Kilidi (Elde ince nişanda asla yanlışlıkla devreye girmez)
const float DESK_ACCEL_DELTA_G        = 0.006f;
const float DESK_GYRO_DEV_DEG_S       = 0.22f;
const float DESK_MAX_DRIFT_DEG_S      = 1.20f;
const int   DESK_STILL_SAMPLES_REQ    = 1500;   // 1.5 sn kesintisiz katı masa hareketsizliği şartı
const float DESK_LEARN_ALPHA          = 0.008f; // Yalnız sert masa üstünde yumuşak ofset kilidi

// Kademe 1: Elde Nişan Ultra-Korumalı Termal Telafi (İnce nişanı asla bias sanıp ters kayma üretmez)
const float AIM_ACCEL_DELTA_G         = 0.035f;
const float AIM_LP_ALPHA              = 0.0018f; // ~0.28 Hz LPF (el titremesini/nefesi süzer)
const float AIM_LP_MAX_DEG_S          = 0.30f;   // 0.30 deg/s üstü kasıtlı ince nişandır -> asla öğrenme
const float AIM_LP_RESET_DEG_S        = 0.55f;
const float AIM_INST_MAX_DEG_S        = 2.50f;   // Anlık titreme tavanı
const int   AIM_STILL_SAMPLES_REQ     = 2500;    // 2.5 saniye hedef üstünde kesintisiz sabit nişan şartı
const float AIM_LEARN_ALPHA           = 0.00015f;// Çok yumuşak termal telafi (~6.7 sn zaman sabiti)
const float AIM_MAX_BOOT_SPAN_DEG_S   = 0.45f;   // Elde nişan Kademe-1'in Flash tabanından max sapma sınırı

const float ZUPT_MAX_BOOT_SPAN_DEG_S  = 3.50f;   // Başlangıç/Flash ofsetinden max canlı sapma sınırı (masa kilidi)
const float MAX_THERMAL_SLOPE_LSB_C   = 8.20f;   // Max +-0.25 deg/s/C fiziksel termal eğim sınırı
const uint32_t WARMUP_MS              = 2000;

enum CalRequest : uint8_t {
  CAL_NONE = 0,
  CAL_GYRO_DESK,
  CAL_SCALE_360,
  CAL_RESET_SCALE,
  CAL_CLEAR_ALL
};

// Unity'ye giden 34 byte'lık UDP paketi (Unity Esp32RifleReceiver ile %100 uyumlu)
struct __attribute__((packed)) RifleQuaternionPacket {
  uint8_t header;           // 0xAA doğrulama baytı
  float qw, qx, qy, qz;     // 3D Dönüş Verisi
  uint8_t triggerState;     // 1 = Tetik Basılı, 0 = Serbest
  uint32_t shotCount;       // Toplam tetik basma sayacı
  uint8_t shoulderState;    // 1 = Omuzlandı (Red Dot Açık), 0 = Serbest (Red Dot Kapalı)
  uint8_t magState;         // 1 = Şarjör Kontağı Temaslı (GPIO 17 LOW), 0 = Serbest
  uint16_t magToggleCount;  // Onaylı şarjör temas sayacı (1. temas = çıkar, 2. temas = tak / Unity 'V')
  uint8_t recenterState;    // 1 = Sıfırlama Butonu Basılı (GPIO 18 LOW), 0 = Serbest
  uint16_t recenterCount;   // Onaylı sıfırlama butonu sayacı (Her basış = VR 2-parmak dokunuşu / LED + Yön sıfırlama)
  int16_t joyX;             // Joystick Yatay/Yan Adım (-1000 = Sol .. +1000 = Sağ)
  int16_t joyY;             // Joystick İleri/Geri (-1000 = Geri .. +1000 = İleri / Namlu Yönü)
  uint8_t joySwState;       // 1 = Joystick Orta Buton Basılı (GPIO 6 LOW / Sprint), 0 = Serbest
};
static_assert(sizeof(RifleQuaternionPacket) == 34, "RifleQuaternionPacket 34 byte olmalidir");

// Core 1 (IMU + 1kHz Tetik) -> Core 0 (Ağ/Serial) kilitli paylaşımlı anlık durum
struct SharedImuSnapshot {
  float qw, qx, qy, qz;
  float gxDegS, gyDegS, gzDegS;
  float tempC;
  float scaleX, scaleY, scaleZ;
  uint32_t maxDtUs;
  uint32_t imuSamplesInSec;
  uint32_t satCount;
  uint32_t i2cErrCount;
  uint32_t pwrResetCount;
  uint32_t shotCount;
  uint32_t triggerGlitchCount;
  uint8_t zuptTier;         // 0=HAREKET, 1=NISAN, 2=SABIT
  bool triggerHeld;
  bool triggerEventPending;
  bool calBusy;
};

Preferences prefs;
WiFiUDP udp;
RifleQuaternionPacket packet;

portMUX_TYPE stateMux = portMUX_INITIALIZER_UNLOCKED;
SharedImuSnapshot sharedState = {
  1.0f, 0.0f, 0.0f, 0.0f,
  0.0f, 0.0f, 0.0f,
  25.0f,
  1.0f, 1.0f, 1.0f,
  1000, 0, 0, 0, 0,
  0, 0,
  0, false, false, false
};

TaskHandle_t imuTaskHandle = nullptr;
TaskHandle_t netTaskHandle = nullptr;
esp_timer_handle_t imuFallbackTimer = nullptr;

uint8_t mpuAddr               = 0x68;
volatile bool cancelCalFlag   = false;
volatile uint8_t pendingCal   = CAL_NONE;
bool hasFlashGyroCal          = false;
bool hasFlashLevelCal         = false;
bool sensorInitialized        = false;

// Tetik (Core 1 1000 Hz Wi-Fi'dan izole akıllı integrator) & Omuz Kararlı Filtre Değişkenleri
bool trigArmedCore1             = true;
uint8_t trigPressScore          = 0;
uint8_t trigReleaseScore        = 0;
bool trigSawLowSinceIdle        = false;
uint32_t lastTriggerPressUs     = 0;
uint32_t triggerShotCountLocal  = 0;
uint32_t triggerGlitchLocal     = 0;
bool triggerEventFlag           = false;

bool shoulderEventFlag          = false;
uint8_t shoulderState           = 0;
bool shoulderPendingTracking    = false;
uint8_t shoulderPendingVal      = 0;
uint32_t shoulderPendingStartUs = 0;

// Şarjör Çıkar/Tak Kontağı Filtre Değişkenleri (GPIO 17 <-> GND: Her onaylı temas = Unity 'V')
bool magEventFlag               = false;
bool magArmed                   = true;
uint16_t magToggleCounter       = 0;
uint32_t lastMagToggleUs        = 0;
bool magLowTracking             = false;
uint32_t magLowStartUs          = 0;
uint16_t magLowSamples          = 0;
bool magHighTracking            = false;
uint32_t magHighStartUs         = 0;
uint32_t magGlitchRejected      = 0;

// VR & LED Sıfırlama Butonu Filtre Değişkenleri (GPIO 18 <-> GND: Her onaylı basış = Ekrana 2 parmak dokunma ile aynı sıfırlama)
bool recenterEventFlag          = false;
bool recenterArmed              = true;
uint16_t recenterCounter        = 0;
uint32_t lastRecenterPressUs    = 0;
bool recenterLowTracking        = false;
uint32_t recenterLowStartUs     = 0;
uint16_t recenterLowSamples     = 0;
bool recenterHighTracking       = false;
uint32_t recenterHighStartUs    = 0;

// HW-504 Analog Joystick Durum Değişkenleri (Yalnız Core 0 netTask erişir; Core 1 IMU tamamen izoledir)
float joyCenterX                = 2048.0f;
float joyCenterY                = 2048.0f;
float joyFiltRawX               = 2048.0f;
float joyFiltRawY               = 2048.0f;
int joyLastRawX                 = 2048;
int joyLastRawY                 = 2048;
int16_t joyOutX                 = 0;
int16_t joyOutY                 = 0;
uint8_t joySwState              = 0;
bool joySwEventFlag             = false;
bool joySwPendingTracking       = false;
uint8_t joySwPendingVal         = 0;
uint32_t joySwPendingStartUs    = 0;
volatile bool pendingJoyRecenter = false;

// Unity Unicast kilit durumu (kilitliyken broadcast kapatılır -> Wi-Fi takılması sıfır)
bool unityClientLocked          = false;
uint32_t lastUnityHeartbeatMs   = 0;

// Ham sensör verileri (Yalnız Core 1 imuTask erişir)
int16_t rawAx = 0, rawAy = 0, rawAz = 0;
int16_t rawTemp = 0;
int16_t rawGx = 0, rawGy = 0, rawGz = 0;

// Kalibrasyon, Termal Eğim, Ölçek ve 2 Kademeli ZUPT durumları (Yalnız Core 1)
float baseGxOffset = 0.0f, baseGyOffset = 0.0f, baseGzOffset = 0.0f; // tRef sıcaklığındaki temiz masa tabanı
float gxOffset = 0.0f, gyOffset = 0.0f, gzOffset = 0.0f;             // tRef referanslı canlı ofset
float thermalKx = 0.0f, thermalKy = 0.0f, thermalKz = 0.0f;          // raw LSB / °C
float biasRefTempC = 25.0f;
float smoothTempC = 25.0f;
bool hasThermalSample = false;

float gyroScaleX = 1.0f, gyroScaleY = 1.0f, gyroScaleZ = 1.0f;       // 360° kalibre edilebilir ölçek çarpanı

// Kalıcı Masa Düzlük (Roll/Pitch Montaj Eğimi) Referansı ve 3D Ortogonal Düzeltme Matrisi
float levelRefAx = 0.0f, levelRefAy = 0.0f, levelRefAz = 1.0f;
float levelR00 = 1.0f, levelR01 = 0.0f, levelR02 = 0.0f;
float levelR10 = 0.0f, levelR11 = 1.0f, levelR12 = 0.0f;
float levelR20 = 0.0f, levelR21 = 0.0f, levelR22 = 1.0f;

float zuptAnchorGx = 0.0f, zuptAnchorGy = 0.0f, zuptAnchorGz = 0.0f;
float lpGxDegS = 0.0f, lpGyDegS = 0.0f, lpGzDegS = 0.0f;
float smoothAccMag = 1.0f;
int deskStillCount = 0;
int aimStillCount = 0;
int accelSettleCount = 0;
uint32_t lastThermalSaveMs = 0;

// Önceki adım açısal hızı (Bortz koning + yamuk integrasyon için)
float prevGxRad = 0.0f, prevGyRad = 0.0f, prevGzRad = 0.0f;
bool hasPrevGyro = false;

// Kuaterniyon durumu (Yalnız Core 1)
float q0 = 1.0f, q1 = 0.0f, q2 = 0.0f, q3 = 0.0f;
uint32_t bootMillis = 0;

// --- YARDIMCILAR ---
static inline TickType_t msToTicksSafe(uint32_t ms) {
  TickType_t ticks = pdMS_TO_TICKS(ms);
  return (ticks > 0) ? ticks : 1;
}

// Pil modunda (USB takılı değilken) ESP32-S3 USB-CDC tamponunun Core 0 UDP görevini tıkamasını önler
static inline bool serialCanWriteNow() {
  return (bool)Serial && (Serial.availableForWrite() >= 64);
}

void writeI2CReg(uint8_t devAddr, uint8_t reg, uint8_t val) {
  Wire.beginTransmission(devAddr);
  Wire.write(reg);
  Wire.write(val);
  Wire.endTransmission();
}

bool readI2CRegSafe(uint8_t devAddr, uint8_t reg, uint8_t& outVal) {
  Wire.beginTransmission(devAddr);
  Wire.write(reg);
  if (Wire.endTransmission(false) != 0) return false;
  if (Wire.requestFrom((uint16_t)devAddr, (uint8_t)1, true) != 1) return false;
  outVal = Wire.read();
  return true;
}

uint8_t readI2CReg(uint8_t devAddr, uint8_t reg) {
  uint8_t val = 0;
  return readI2CRegSafe(devAddr, reg, val) ? val : 0;
}

bool readRawMPU() {
  Wire.beginTransmission(mpuAddr);
  Wire.write(0x3B);
  if (Wire.endTransmission(false) != 0) return false;
  if (Wire.requestFrom((uint16_t)mpuAddr, (uint8_t)14, true) != 14) return false;

  rawAx   = (int16_t)((Wire.read() << 8) | Wire.read());
  rawAy   = (int16_t)((Wire.read() << 8) | Wire.read());
  rawAz   = (int16_t)((Wire.read() << 8) | Wire.read());
  rawTemp = (int16_t)((Wire.read() << 8) | Wire.read());
  rawGx   = (int16_t)((Wire.read() << 8) | Wire.read());
  rawGy   = (int16_t)((Wire.read() << 8) | Wire.read());
  rawGz   = (int16_t)((Wire.read() << 8) | Wire.read());

  float instTempC = ((float)rawTemp / 333.87f) + 21.0f;
  if (!hasThermalSample) {
    smoothTempC = instTempC;
    hasThermalSample = true;
  } else {
    smoothTempC += (instTempC - smoothTempC) * 0.005f;
  }
  return true;
}

// Masa üzerinde ölçülen yerçekimi vektörünü (ax, ay, az) saf dikey (0, 0, +1g) eksenine
// sıfır Yaw burulmasıyla (qz=0) hizalayan 3D ortogonal montaj düzeltme matrisini kurar.
bool updateLevelMountMatrix(float ax, float ay, float az) {
  float norm = sqrtf(ax * ax + ay * ay + az * az);
  if (norm < 0.70f || norm > 1.30f) return false;
  float ux = ax / norm;
  float uy = ay / norm;
  float uz = az / norm;
  if (uz < 0.50f) return false; // Tüfek 60 dereceden fazla yatık/ters ise masa düzlüğü olarak kaydetme

  float w = 1.0f + uz;
  float x = uy;
  float y = -ux;
  float qNorm = sqrtf(w * w + x * x + y * y);
  if (qNorm < 1e-6f) return false;

  float qw = w / qNorm;
  float qx = x / qNorm;
  float qy = y / qNorm;

  levelR00 = 1.0f - 2.0f * qy * qy;
  levelR01 = 2.0f * qx * qy;
  levelR02 = 2.0f * qw * qy;
  levelR10 = 2.0f * qx * qy;
  levelR11 = 1.0f - 2.0f * qx * qx;
  levelR12 = -2.0f * qw * qx;
  levelR20 = -2.0f * qw * qy;
  levelR21 = 2.0f * qw * qx;
  levelR22 = 1.0f - 2.0f * (qx * qx + qy * qy);

  levelRefAx = ux;
  levelRefAy = uy;
  levelRefAz = uz;
  return true;
}

static inline void resetLevelMountMatrix() {
  hasFlashLevelCal = false;
  levelRefAx = 0.0f; levelRefAy = 0.0f; levelRefAz = 1.0f;
  levelR00 = 1.0f; levelR01 = 0.0f; levelR02 = 0.0f;
  levelR10 = 0.0f; levelR11 = 1.0f; levelR12 = 0.0f;
  levelR20 = 0.0f; levelR21 = 0.0f; levelR22 = 1.0f;
}

static inline void applyLevelMountRotation(float& x, float& y, float& z) {
  if (!hasFlashLevelCal) return;
  float nx = levelR00 * x + levelR01 * y + levelR02 * z;
  float ny = levelR10 * x + levelR11 * y + levelR12 * z;
  float nz = levelR20 * x + levelR21 * y + levelR22 * z;
  x = nx;
  y = ny;
  z = nz;
}

void initQuatFromAccel(float ax, float ay, float az) {
  float norm = sqrtf(ax * ax + ay * ay + az * az);
  if (norm < 1e-6f) return;
  ax /= norm; ay /= norm; az /= norm;

  if (az < -0.9999f) {
    q0 = 0.0f; q1 = 1.0f; q2 = 0.0f; q3 = 0.0f;
  } else {
    float w = 1.0f + az;
    float x = ay;
    float y = -ax;
    float z = 0.0f;
    float qNorm = sqrtf(w * w + x * x + y * y + z * z);
    if (qNorm > 1e-6f) {
      q0 = w / qNorm;
      q1 = x / qNorm;
      q2 = y / qNorm;
      q3 = z / qNorm;
    }
  }
  hasPrevGyro = false;
}

// --- FLASH (NVS) KALİBRASYON KAYITLARI ---
void saveGyroCalToFlash() {
  if (!prefs.begin("vratis_gyro", false)) return;
  prefs.putBool("ok", hasFlashGyroCal);
  prefs.putFloat("bx", baseGxOffset);
  prefs.putFloat("by", baseGyOffset);
  prefs.putFloat("bz", baseGzOffset);
  prefs.putFloat("tref", biasRefTempC);
  prefs.putFloat("kx", thermalKx);
  prefs.putFloat("ky", thermalKy);
  prefs.putFloat("kz", thermalKz);
  prefs.putFloat("sx", gyroScaleX);
  prefs.putFloat("sy", gyroScaleY);
  prefs.putFloat("sz", gyroScaleZ);
  prefs.putBool("lvl_ok", hasFlashLevelCal);
  prefs.putFloat("lax", levelRefAx);
  prefs.putFloat("lay", levelRefAy);
  prefs.putFloat("laz", levelRefAz);
  prefs.end();
}

void loadGyroCalFromFlash() {
  if (!prefs.begin("vratis_gyro", true)) return;
  hasFlashGyroCal = prefs.getBool("ok", false);
  if (hasFlashGyroCal) {
    baseGxOffset = prefs.getFloat("bx", 0.0f);
    baseGyOffset = prefs.getFloat("by", 0.0f);
    baseGzOffset = prefs.getFloat("bz", 0.0f);
    biasRefTempC = prefs.getFloat("tref", 25.0f);
    thermalKx    = fmaxf(-MAX_THERMAL_SLOPE_LSB_C, fminf(MAX_THERMAL_SLOPE_LSB_C, prefs.getFloat("kx", 0.0f)));
    thermalKy    = fmaxf(-MAX_THERMAL_SLOPE_LSB_C, fminf(MAX_THERMAL_SLOPE_LSB_C, prefs.getFloat("ky", 0.0f)));
    thermalKz    = fmaxf(-MAX_THERMAL_SLOPE_LSB_C, fminf(MAX_THERMAL_SLOPE_LSB_C, prefs.getFloat("kz", 0.0f)));
  }
  float sx = prefs.getFloat("sx", 1.0f);
  float sy = prefs.getFloat("sy", 1.0f);
  float sz = prefs.getFloat("sz", 1.0f);
  gyroScaleX = (sx >= 0.85f && sx <= 1.15f) ? sx : 1.0f;
  gyroScaleY = (sy >= 0.85f && sy <= 1.15f) ? sy : 1.0f;
  gyroScaleZ = (sz >= 0.85f && sz <= 1.15f) ? sz : 1.0f;

  bool lvlOk = prefs.getBool("lvl_ok", false);
  if (lvlOk) {
    float lax = prefs.getFloat("lax", 0.0f);
    float lay = prefs.getFloat("lay", 0.0f);
    float laz = prefs.getFloat("laz", 1.0f);
    hasFlashLevelCal = updateLevelMountMatrix(lax, lay, laz);
  } else {
    resetLevelMountMatrix();
  }
  prefs.end();
}

void clearAllCalFlash() {
  if (prefs.begin("vratis_gyro", false)) {
    prefs.clear();
    prefs.end();
  }
  if (prefs.begin("vratis_imu", false)) {
    prefs.clear();
    prefs.end();
  }
  hasFlashGyroCal = false;
  resetLevelMountMatrix();
  thermalKx = thermalKy = thermalKz = 0.0f;
  gyroScaleX = gyroScaleY = gyroScaleZ = 1.0f;
  Serial.println("[BILGI] Tum Flash kayitlari (Jiroskop + Masa Duzlugu + Olcek) temizlendi.");
}

// Hareketsizlik Kontrollü Jiroskop & Masa Düzlük (Roll/Pitch Montaj Ofseti) Kalibrasyonu:
// 'g' komutuyla çağrıldığında (updateDeskLevelInFlash=true) hem jiroskop sıfır noktasını
// hem de tüfeğin masa üzerindeki dik/düz duruşunu (Roll=0, Pitch=0) kalıcı olarak ESP32 Flash'ına yazar!
void calibrateGyroAndPose(bool updateDeskLevelInFlash) {
  Serial.println("Jiroskop ve yercekimi kalibre ediliyor (2 sn sabit tutun)...");
  double sumGx = 0, sumGy = 0, sumGz = 0;
  double sumAx = 0, sumAy = 0, sumAz = 0;
  double sumTemp = 0;
  int validImu = 0;

  int16_t minGx = 32767, maxGx = -32768;
  int16_t minGy = 32767, maxGy = -32768;
  int16_t minGz = 32767, maxGz = -32768;
  float minAccMag = 99.0f, maxAccMag = 0.0f;

  for (int i = 0; i < 1000; i++) {
    if (readRawMPU()) {
      sumGx += rawGx;
      sumGy += rawGy;
      sumGz += rawGz;
      sumAx += rawAx;
      sumAy += rawAy;
      sumAz += rawAz;
      sumTemp += ((float)rawTemp / 333.87f) + 21.0f;

      if (rawGx < minGx) minGx = rawGx; if (rawGx > maxGx) maxGx = rawGx;
      if (rawGy < minGy) minGy = rawGy; if (rawGy > maxGy) maxGy = rawGy;
      if (rawGz < minGz) minGz = rawGz; if (rawGz > maxGz) maxGz = rawGz;

      float ax = rawAx * ACCEL_SCALE;
      float ay = rawAy * ACCEL_SCALE;
      float az = rawAz * ACCEL_SCALE;
      float am = sqrtf(ax * ax + ay * ay + az * az);
      if (am < minAccMag) minAccMag = am;
      if (am > maxAccMag) maxAccMag = am;
      validImu++;
    }
    vTaskDelay(msToTicksSafe(2));
  }

  if (validImu == 0) {
    Serial.println("[HATA] IMU verisi okunamadi!");
    return;
  }

  float measGx = (float)(sumGx / validImu);
  float measGy = (float)(sumGy / validImu);
  float measGz = (float)(sumGz / validImu);
  float measTempC = (float)(sumTemp / validImu);
  smoothTempC = measTempC;
  hasThermalSample = true;

  float avgAx = (float)(sumAx / validImu) * ACCEL_SCALE;
  float avgAy = (float)(sumAy / validImu) * ACCEL_SCALE;
  float avgAz = (float)(sumAz / validImu) * ACCEL_SCALE;
  float initAccMag = sqrtf(avgAx * avgAx + avgAy * avgAy + avgAz * avgAz);
  smoothAccMag = (initAccMag > 0.85f && initAccMag < 1.15f) ? initAccMag : 1.0f;

  float spreadX = (float)(maxGx - minGx) * GYRO_DEG_SCALE;
  float spreadY = (float)(maxGy - minGy) * GYRO_DEG_SCALE;
  float spreadZ = (float)(maxGz - minGz) * GYRO_DEG_SCALE;
  float maxGyroSpreadDegS = fmaxf(spreadX, fmaxf(spreadY, spreadZ));
  float accelSpreadG = maxAccMag - minAccMag;

  bool isCleanStill = (maxGyroSpreadDegS < 2.4f) && (accelSpreadG < 0.065f);

  if (isCleanStill) {
    if (hasFlashGyroCal) {
      float deltaT = measTempC - biasRefTempC;
      if (fabsf(deltaT) >= 2.5f) {
        float newKx = fmaxf(-MAX_THERMAL_SLOPE_LSB_C, fminf(MAX_THERMAL_SLOPE_LSB_C, (measGx - baseGxOffset) / deltaT));
        float newKy = fmaxf(-MAX_THERMAL_SLOPE_LSB_C, fminf(MAX_THERMAL_SLOPE_LSB_C, (measGy - baseGyOffset) / deltaT));
        float newKz = fmaxf(-MAX_THERMAL_SLOPE_LSB_C, fminf(MAX_THERMAL_SLOPE_LSB_C, (measGz - baseGzOffset) / deltaT));
        thermalKx = (thermalKx == 0.0f) ? newKx : (thermalKx * 0.5f + newKx * 0.5f);
        thermalKy = (thermalKy == 0.0f) ? newKy : (thermalKy * 0.5f + newKy * 0.5f);
        thermalKz = (thermalKz == 0.0f) ? newKz : (thermalKz * 0.5f + newKz * 0.5f);
      }
    }

    baseGxOffset = gxOffset = measGx;
    baseGyOffset = gyOffset = measGy;
    baseGzOffset = gzOffset = measGz;
    biasRefTempC = measTempC;
    hasFlashGyroCal = true;

    // 'g' masa komutu gönderildiyse (veya Flash'ta henüz hiç düzlük kaydı yoksa)
    // sensörün tüfek üzerindeki fiziksel Roll/Pitch montaj eğimini ölçüp Flash'a kalıcı yaz!
    if (updateDeskLevelInFlash || !hasFlashLevelCal) {
      if (updateLevelMountMatrix(avgAx, avgAy, avgAz)) {
        hasFlashLevelCal = true;
      }
    }

    saveGyroCalToFlash();

    float rollMountDeg  = atan2f(levelRefAx, levelRefAz) * RAD_TO_DEG_F;
    float pitchMountDeg = atan2f(levelRefAy, levelRefAz) * RAD_TO_DEG_F;
    Serial.printf("[OK] Kalici Masa Kalibrasyonu Flash'a Kaydedildi! (Temp:%.1fC | Roll Ofset:%+.2f deg, Pitch Ofset:%+.2f deg)\n",
                  measTempC, rollMountDeg, pitchMountDeg);
  } else if (hasFlashGyroCal) {
    gxOffset = baseGxOffset;
    gyOffset = baseGyOffset;
    gzOffset = baseGzOffset;
    Serial.printf("[KORUMA] Acilista hareket algilandi (%.1f deg/s) -> Flash'taki kalici masa kalibrasyonu kullanildi (Temp:%.1fC)!\n",
                  maxGyroSpreadDegS, measTempC);
  } else {
    baseGxOffset = gxOffset = measGx;
    baseGyOffset = gyOffset = measGy;
    baseGzOffset = gzOffset = measGz;
    biasRefTempC = measTempC;
    Serial.printf("[UYARI] Kalibrasyon sirasinda hareket vardi (%.1f deg/s). En iyi sonuc icin tufegi masaya koyup 'g' gonderin!\n",
                  maxGyroSpreadDegS);
  }

  zuptAnchorGx = gxOffset + thermalKx * (smoothTempC - biasRefTempC);
  zuptAnchorGy = gyOffset + thermalKy * (smoothTempC - biasRefTempC);
  zuptAnchorGz = gzOffset + thermalKz * (smoothTempC - biasRefTempC);
  lpGxDegS = lpGyDegS = lpGzDegS = 0.0f;
  deskStillCount = 0;
  aimStillCount = 0;
  accelSettleCount = ACCEL_SETTLE_SAMPLES_REQ;

  float leveledAx = avgAx, leveledAy = avgAy, leveledAz = avgAz;
  applyLevelMountRotation(leveledAx, leveledAy, leveledAz);
  initQuatFromAccel(leveledAx, leveledAy, leveledAz);
  bootMillis = millis();
}

// 360° Jiroskop Ölçek Faktörü Kalibrasyonu:
// MPU-9250/6500 çiplerindeki +-1..5% fabrika ölçek hatasını ölçüp Flash'a kaydeder!
void calibrateGyroScale360() {
  Serial.println("=================================================================");
  Serial.println("[360 DERECE JIROSKOP OLCEK KALIBRASYONU]");
  Serial.println("1) Tufegi duz bir referans cizgisine (masa kenari) koyun.");
  Serial.println("2) Ilk 1.5 sn sabit tutun -> hazir mesaji gelince yatayda TAM 1 TUR (360 derece) cevirin.");
  Serial.println("3) Tam ayni cizgiye gelince 1.5 sn sabit tutun (iptal icin 'x').");
  Serial.println("=================================================================");

  double sumGx = 0, sumGy = 0, sumGz = 0;
  int cnt = 0;
  uint32_t t0 = millis();
  while (millis() - t0 < 1500 && !cancelCalFlag) {
    if (readRawMPU()) {
      sumGx += rawGx;
      sumGy += rawGy;
      sumGz += rawGz;
      cnt++;
    }
    vTaskDelay(msToTicksSafe(1));
  }
  if (cancelCalFlag || cnt < 200) {
    cancelCalFlag = false;
    Serial.println("[IPTAL] Olcek kalibrasyonu iptal edildi.");
    return;
  }

  float refOffX = (float)(sumGx / cnt);
  float refOffY = (float)(sumGy / cnt);
  float refOffZ = (float)(sumGz / cnt);

  Serial.println(">>> SIMDI tufegi TAM 1 TUR (360 derece) cevirin ve ayni hizada 1.5 sn sabit durun...");

  double accXDeg = 0.0, accYDeg = 0.0, accZDeg = 0.0;
  float prevDx = 0.0f, prevDy = 0.0f, prevDz = 0.0f;
  bool first = true;
  float stillAfterTurnSec = 0.0f;
  uint32_t startMs = millis();
  uint32_t prevUs = micros();
  uint32_t lastPrintMs = startMs;

  while (millis() - startMs < 25000 && !cancelCalFlag) {
    uint32_t nowUs = micros();
    float dt = (nowUs - prevUs) * 1e-6f;
    prevUs = nowUs;
    if (dt > 0.05f) dt = 0.001f;

    if (readRawMPU()) {
      float dx = ((float)rawGx - refOffX) * GYRO_DEG_SCALE;
      float dy = ((float)rawGy - refOffY) * GYRO_DEG_SCALE;
      float dz = ((float)rawGz - refOffZ) * GYRO_DEG_SCALE;

      if (!first) {
        accXDeg += 0.5 * (prevDx + dx) * dt;
        accYDeg += 0.5 * (prevDy + dy) * dt;
        accZDeg += 0.5 * (prevDz + dz) * dt;
      }
      prevDx = dx; prevDy = dy; prevDz = dz;
      first = false;

      float speedDegS = sqrtf(dx * dx + dy * dy + dz * dz);
      double maxTurn = fmax(fabs(accXDeg), fmax(fabs(accYDeg), fabs(accZDeg)));

      if (maxTurn >= 270.0 && speedDegS < 1.35f) {
        stillAfterTurnSec += dt;
        if (stillAfterTurnSec >= 1.5f) {
          break;
        }
      } else {
        stillAfterTurnSec = 0.0f;
      }

      if (millis() - lastPrintMs >= 500) {
        lastPrintMs = millis();
        Serial.printf("  Anlik Donus -> X:%+.1f | Y:%+.1f | Z:%+.1f deg\n", accXDeg, accYDeg, accZDeg);
      }
    }
    vTaskDelay(msToTicksSafe(1));
  }

  if (cancelCalFlag) {
    cancelCalFlag = false;
    Serial.println("[IPTAL] Olcek kalibrasyonu iptal edildi.");
    return;
  }

  double absX = fabs(accXDeg), absY = fabs(accYDeg), absZ = fabs(accZDeg);
  double totalNorm = sqrt(absX * absX + absY * absY + absZ * absZ);
  double domVal = fmax(absX, fmax(absY, absZ));

  if (domVal < 270.0 || totalNorm < 270.0) {
    Serial.printf("[HATA] 360 tur tamamlanamadi (X:%.1f Y:%.1f Z:%.1f). Tekrar 's' ile deneyin.\n",
                  accXDeg, accYDeg, accZDeg);
    return;
  }

  if ((domVal / totalNorm) < 0.992) {
    float tiltDeg = acosf((float)(domVal / totalNorm)) * RAD_TO_DEG_F;
    float globalScale = (float)(360.0 / totalNorm);
    Serial.printf("[BILGI] 3D Toplam Donus: %.2f deg (PCB %.1f deg acili | Gercek olcek ~%.4f).\n",
                  totalNorm, tiltDeg, globalScale);
    Serial.println("        Kart egimli oldugu icin tek eksen carpani degistirilmedi (1.000 korunuyor).");
    return;
  }

  float computedScale = (float)(360.0 / domVal);
  if (computedScale < 0.85f || computedScale > 1.15f) {
    Serial.printf("[HATA] Hesaplanan olcek (%.4f) guvenli aralik disinda [0.85..1.15]. Tekrar deneyin.\n", computedScale);
    return;
  }

  const char* axisName = "Z";
  if (absX >= absY && absX >= absZ) {
    gyroScaleX = computedScale;
    axisName = "X";
  } else if (absY >= absX && absY >= absZ) {
    gyroScaleY = computedScale;
    axisName = "Y";
  } else {
    gyroScaleZ = computedScale;
    axisName = "Z";
  }

  saveGyroCalToFlash();
  Serial.printf("[OK] %s Ekseni 360 Olcek Kalibre Edildi! Olculen: %.2f deg -> Yeni Carpan: %.4f (Flash'a kaydedildi)\n",
                axisName, domVal, computedScale);
  Serial.printf("     Aktif Olcekler -> X:%.4f  Y:%.4f  Z:%.4f\n", gyroScaleX, gyroScaleY, gyroScaleZ);
  hasPrevGyro = false;
}

void configureMpuRegisters(bool doSoftReset) {
  if (doSoftReset) {
    writeI2CReg(mpuAddr, 0x6B, 0x80); // Cihaz sıfırlama
    vTaskDelay(msToTicksSafe(50));
  }
  writeI2CReg(mpuAddr, 0x6B, 0x01); // PLL saat kaynağı (X Gyro)
  vTaskDelay(msToTicksSafe(15));

  writeI2CReg(mpuAddr, 0x19, 0x00); // 1 kHz dahili örnekleme
  writeI2CReg(mpuAddr, 0x1A, 0x01); // Gyro DLPF = 184 Hz (2.9 ms ultra düşük gecikme, hızlı savurmada keskin tepki)
  writeI2CReg(mpuAddr, 0x1B, 0x10); // Jiroskop: +-1000 deg/s
  writeI2CReg(mpuAddr, 0x1C, 0x08); // İvmeölçer: +-4g
  writeI2CReg(mpuAddr, 0x1D, 0x03); // İvmeölçer DLPF = 41 Hz
}

// 9-darbe SCL bus temizleme + STOP koşulu: reset/güç kesintisi sırasında MPU'nun SDA'yı LOW kilitlemesini açar
void pulseI2cBusClear() {
  pinMode(I2C_SDA_PIN, INPUT_PULLUP);
  pinMode(I2C_SCL_PIN, OUTPUT);
  for (int i = 0; i < 9; i++) {
    digitalWrite(I2C_SCL_PIN, LOW);
    delayMicroseconds(6);
    digitalWrite(I2C_SCL_PIN, HIGH);
    delayMicroseconds(6);
  }
  pinMode(I2C_SDA_PIN, OUTPUT);
  digitalWrite(I2C_SDA_PIN, LOW);
  delayMicroseconds(6);
  digitalWrite(I2C_SCL_PIN, HIGH);
  delayMicroseconds(6);
  digitalWrite(I2C_SDA_PIN, HIGH);
  delayMicroseconds(6);
  pinMode(I2C_SDA_PIN, INPUT_PULLUP);
}

// Uzun jumper kabloda anlık I2C kilitlenmesi (SDA takılması / NACK) olursa 9-darbe bus kurtarma + yeniden başlatma
void recoverI2cBusAndMpu() {
  Wire.end();
  pulseI2cBusClear();

  Wire.begin(I2C_SDA_PIN, I2C_SCL_PIN);
  Wire.setClock(I2C_CLOCK_HZ);
  Wire.setTimeOut(5);

  bool found = false;
  Wire.beginTransmission(0x68);
  if (Wire.endTransmission() == 0) {
    mpuAddr = 0x68;
    found = true;
  } else {
    Wire.beginTransmission(0x69);
    if (Wire.endTransmission() == 0) {
      mpuAddr = 0x69;
      found = true;
    }
  }
  if (!found) return;

  configureMpuRegisters(false);
  if (!sensorInitialized) {
    loadGyroCalFromFlash();
    if (hasFlashGyroCal) {
      gxOffset = baseGxOffset;
      gyOffset = baseGyOffset;
      gzOffset = baseGzOffset;
    }
    sensorInitialized = true;
    if (serialCanWriteNow()) {
      Serial.println("[KURTARMA] MPU-9250/6500 sonradan uyandi -> Flash kalibrasyonu yuklendi!");
    }
  }
}

void initSensors() {
  bool found = false;
  for (int attempt = 0; attempt < 5 && !found; attempt++) {
    if (attempt > 0) {
      Wire.end();
      delay(50);
    }
    pulseI2cBusClear();

    Wire.begin(I2C_SDA_PIN, I2C_SCL_PIN);
    Wire.setClock(I2C_CLOCK_HZ);
    Wire.setTimeOut(10);
    delay(20);

    Wire.beginTransmission(0x68);
    if (Wire.endTransmission() == 0) {
      mpuAddr = 0x68;
      found = true;
    } else {
      Wire.beginTransmission(0x69);
      if (Wire.endTransmission() == 0) {
        mpuAddr = 0x69;
        found = true;
      }
    }
  }

  if (!found) {
    Serial.println("[HATA] MPU-9250/6500 5 denemede bulunamadi! I2C kablolarini kontrol edin.");
    return;
  }

  configureMpuRegisters(true);

  uint8_t whoAmI = readI2CReg(mpuAddr, 0x75);
  loadGyroCalFromFlash();
  sensorInitialized = true;

  Serial.println("============================================================");
  Serial.printf("Sensor Adresi : 0x%02X | WHO_AM_I: 0x%02X\n", mpuAddr, whoAmI);
  Serial.println("Mod           : SAF 6-DOF + 2 Kademeli Akilli ZUPT (Sifir Pusula Sapmasi)");
  Serial.println("Mimari        : Core 1: 1000Hz Bortz-Exp IMU | Core 0: 250Hz Unicast UDP");
  Serial.printf("Flash Olcek   : X:%.4f  Y:%.4f  Z:%.4f\n", gyroScaleX, gyroScaleY, gyroScaleZ);
  if (hasFlashGyroCal) {
    Serial.printf("Flash Termal  : Ref:%.1fC | Egim(LSB/C): %.2f, %.2f, %.2f\n",
                  biasRefTempC, thermalKx, thermalKy, thermalKz);
  }
  if (hasFlashLevelCal) {
    float rollMountDeg  = atan2f(levelRefAx, levelRefAz) * RAD_TO_DEG_F;
    float pitchMountDeg = atan2f(levelRefAy, levelRefAz) * RAD_TO_DEG_F;
    Serial.printf("Flash Duzluk  : Aktif (Roll:%+.2f deg, Pitch:%+.2f deg montaj ofseti sifirli)\n",
                  rollMountDeg, pitchMountDeg);
  }
  Serial.println("============================================================");

  // Açılışta Flash'taki masa düzlük kaydını koru (yalnız 'g' komutu veya ilk kurulum yeni düzlük yazar)
  calibrateGyroAndPose(false);
}

// Koning Telafili Exact Lie-Group Quaternion Exponential + Dinamik İvme Korumalı 6-DOF AHRS
void updateAHRS6DofExact(
    float gx, float gy, float gz,
    float ax, float ay, float az,
    float dt,
    float beta) {

  // 1) 2. Derece Bortz Koning + Yamuk (Trapezoidal) Dönüş Vektörü:
  //    dTheta = 0.5*(w_prev + w)*dt + (dt^2 / 12) * (w_prev x w)
  float dtx, dty, dtz;
  if (hasPrevGyro) {
    float halfDt = 0.5f * dt;
    float coningK = (dt * dt) * (1.0f / 12.0f);
    float cx = (prevGyRad * gz - prevGzRad * gy) * coningK;
    float cy = (prevGzRad * gx - prevGxRad * gz) * coningK;
    float cz = (prevGxRad * gy - prevGyRad * gx) * coningK;
    dtx = (prevGxRad + gx) * halfDt + cx;
    dty = (prevGyRad + gy) * halfDt + cy;
    dtz = (prevGzRad + gz) * halfDt + cz;
  } else {
    dtx = gx * dt;
    dty = gy * dt;
    dtz = gz * dt;
    hasPrevGyro = true;
  }
  prevGxRad = gx;
  prevGyRad = gy;
  prevGzRad = gz;

  // 2) Exact Quaternion Exponential: q = q * exp(0.5 * dTheta)
  float thetaSq = dtx * dtx + dty * dty + dtz * dtz;
  float dqw, dqx, dqy, dqz;
  if (thetaSq > 1e-10f) {
    float theta = sqrtf(thetaSq);
    float halfTheta = 0.5f * theta;
    float s = sinf(halfTheta) / theta;
    dqw = cosf(halfTheta);
    dqx = dtx * s;
    dqy = dty * s;
    dqz = dtz * s;
  } else {
    dqw = 1.0f - 0.125f * thetaSq;
    dqx = 0.5f * dtx;
    dqy = 0.5f * dty;
    dqz = 0.5f * dtz;
  }

  float n0 = q0 * dqw - q1 * dqx - q2 * dqy - q3 * dqz;
  float n1 = q0 * dqx + q1 * dqw + q2 * dqz - q3 * dqy;
  float n2 = q0 * dqy - q1 * dqz + q2 * dqw + q3 * dqx;
  float n3 = q0 * dqz + q1 * dqy - q2 * dqx + q3 * dqw;
  q0 = n0; q1 = n1; q2 = n2; q3 = n3;

  // 3) Yalnız hareket yokken (beta > 0) yerçekimi gradyanı ile Pitch/Roll hizalama (Yaw'a diktir)
  if (beta > 0.0f) {
    float accNormSq = ax * ax + ay * ay + az * az;
    if (accNormSq > 1e-12f) {
      float recipNorm = 1.0f / sqrtf(accNormSq);
      ax *= recipNorm; ay *= recipNorm; az *= recipNorm;

      float _2q0 = 2.0f * q0, _2q1 = 2.0f * q1, _2q2 = 2.0f * q2, _2q3 = 2.0f * q3;
      float _4q0 = 4.0f * q0, _4q1 = 4.0f * q1, _4q2 = 4.0f * q2;
      float _8q1 = 8.0f * q1, _8q2 = 8.0f * q2;
      float q0q0 = q0 * q0, q1q1 = q1 * q1, q2q2 = q2 * q2, q3q3 = q3 * q3;

      float s0 = _4q0 * q2q2 + _2q2 * ax + _4q0 * q1q1 - _2q1 * ay;
      float s1 = _4q1 * q3q3 - _2q3 * ax + 4.0f * q0q0 * q1 - _2q0 * ay - _4q1 + _8q1 * q1q1 + _8q1 * q2q2 + _4q1 * az;
      float s2 = 4.0f * q0q0 * q2 + _2q0 * ax + _4q2 * q3q3 - _2q3 * ay - _4q2 + _8q2 * q1q1 + _8q2 * q2q2 + _4q2 * az;
      float s3 = 4.0f * q1q1 * q3 - _2q1 * ax + 4.0f * q2q2 * q3 - _2q2 * ay;

      float sNormSq = s0 * s0 + s1 * s1 + s2 * s2 + s3 * s3;
      if (sNormSq > 1e-12f) {
        recipNorm = 1.0f / sqrtf(sNormSq);
        float step = beta * dt * recipNorm;
        q0 -= step * s0;
        q1 -= step * s1;
        q2 -= step * s2;
        q3 -= step * s3;
      }
    }
  }

  float qNormSq = q0 * q0 + q1 * q1 + q2 * q2 + q3 * q3;
  if (qNormSq > 1e-12f) {
    float recipNorm = 1.0f / sqrtf(qNormSq);
    q0 *= recipNorm;
    q1 *= recipNorm;
    q2 *= recipNorm;
    q3 *= recipNorm;
  }
}

// --- CORE 1 YÜKSEK ÖNCELİKLİ 1000 Hz SAF 6-DOF IMU + TETİK ÖRNEKLEME GÖREVİ ---
// Core 1 Wi-Fi/UDP tarafından asla bloklanmaz; sızıntılı integrator (leaky counter) sayesinde
// <3 ms GPIO16 çapraz etkileşim/ESD parazitini reddederken mekanik kontak sıçramalarında atış kaçırmaz.
static inline void sampleTrigger1kHz(uint32_t nowUs) {
  bool trigLow = (digitalRead(TRIGGER_PIN) == LOW);
  if (trigArmedCore1) {
    if (trigLow) {
      trigSawLowSinceIdle = true;
      if (trigPressScore < TRIGGER_CONFIRM_MS) {
        trigPressScore++;
      }
      if ((trigPressScore >= TRIGGER_CONFIRM_MS) &&
          ((uint32_t)(nowUs - lastTriggerPressUs) >= MIN_SHOT_INTERVAL_US)) {
        trigArmedCore1 = false;
        trigPressScore = 0;
        trigReleaseScore = 0;
        trigSawLowSinceIdle = false;
        lastTriggerPressUs = nowUs;
        triggerShotCountLocal++;

        portENTER_CRITICAL(&stateMux);
        sharedState.shotCount = triggerShotCountLocal;
        sharedState.triggerHeld = true;
        sharedState.triggerEventPending = true;
        portEXIT_CRITICAL(&stateMux);
      }
    } else {
      // Tek 1 ms kontak sıçraması tüm ilerlemeyi sıfırlamaz (2 azaltır); kısa parazit 0'a inip reddedilir
      if (trigPressScore > 2) {
        trigPressScore -= 2;
      } else {
        if (trigSawLowSinceIdle) {
          triggerGlitchLocal++;
          trigSawLowSinceIdle = false;
          portENTER_CRITICAL(&stateMux);
          sharedState.triggerGlitchCount = triggerGlitchLocal;
          portEXIT_CRITICAL(&stateMux);
        }
        trigPressScore = 0;
      }
    }
  } else {
    if (!trigLow) {
      if (trigReleaseScore < TRIGGER_RELEASE_MS) {
        trigReleaseScore++;
      }
      if (trigReleaseScore >= TRIGGER_RELEASE_MS) {
        trigArmedCore1 = true;
        trigReleaseScore = 0;
        trigPressScore = 0;
        trigSawLowSinceIdle = false;

        portENTER_CRITICAL(&stateMux);
        sharedState.triggerHeld = false;
        portEXIT_CRITICAL(&stateMux);
      }
    } else {
      // Bırakma sıçramasına dayanıklı sayaç (seri atışta yeniden kurma gecikmez)
      if (trigReleaseScore > 2) {
        trigReleaseScore -= 2;
      } else {
        trigReleaseScore = 0;
      }
    }
  }
}

#if (configTICK_RATE_HZ < 1000)
void imuFallbackTimerCb(void* arg) {
  if (imuTaskHandle != nullptr) {
    xTaskNotifyGive(imuTaskHandle);
  }
}
#endif

void imuTaskFn(void* param) {
  (void)param;

#if (configTICK_RATE_HZ >= 1000)
  TickType_t lastWakeTick = xTaskGetTickCount();
#endif

  uint32_t lastImuUs = micros();
  uint32_t maxDtUsLocal = 0;
  uint32_t samplesLocal = 0;
  uint32_t satLocal = 0;
  uint32_t i2cErrLocal = 0;
  uint32_t pwrResetLocal = 0;
  uint32_t consecI2cErr = 0;
  uint32_t healthCheckTick = 0;
  uint32_t lastStatResetMs = millis();

  for (;;) {
    // 1) Seri porttan istenen kalibrasyon komutlarını güvenli şekilde Core 1'de yürüt
    uint8_t req = pendingCal;
    if (req != CAL_NONE) {
      pendingCal = CAL_NONE;
      portENTER_CRITICAL(&stateMux);
      sharedState.calBusy = true;
      portEXIT_CRITICAL(&stateMux);

      if (req == CAL_GYRO_DESK) {
        calibrateGyroAndPose(true);
      } else if (req == CAL_SCALE_360) {
        calibrateGyroScale360();
      } else if (req == CAL_RESET_SCALE) {
        gyroScaleX = gyroScaleY = gyroScaleZ = 1.0f;
        saveGyroCalToFlash();
        Serial.println("[BILGI] Jiroskop olcek carpanlari 1.000 fabrika degerine sifirlandi.");
      } else if (req == CAL_CLEAR_ALL) {
        clearAllCalFlash();
      }

      portENTER_CRITICAL(&stateMux);
      sharedState.calBusy = false;
      portEXIT_CRITICAL(&stateMux);

      lastImuUs = micros();
#if (configTICK_RATE_HZ >= 1000)
      lastWakeTick = xTaskGetTickCount();
#endif
      continue;
    }

    // 2) Deterministik 1000 Hz (1 ms) bekleme
#if (configTICK_RATE_HZ >= 1000)
    vTaskDelayUntil(&lastWakeTick, 1);
#else
    ulTaskNotifyTake(pdTRUE, pdMS_TO_TICKS(5));
#endif

    uint32_t nowUs = micros();

    // 2.5) Deterministik 1000 Hz Tetik Örnekleme (Wi-Fi veya I2C gecikmesinden tamamen izole)
    sampleTrigger1kHz(nowUs);

    // 3) 1000 Hz MPU-9250/6500 Okuma + Gevşek Kablo Koruması + 2 Kademeli ZUPT + Bortz Exact 6-DOF AHRS
    if (readRawMPU()) {
      bool justRecoveredI2c = (consecI2cErr > 0);
      consecI2cErr = 0;

      healthCheckTick++;
      if (justRecoveredI2c || healthCheckTick >= 2000) {
        healthCheckTick = 0;
        uint8_t gyroCfg = 0, pwrMgmt = 0;
        if (readI2CRegSafe(mpuAddr, 0x1B, gyroCfg) && readI2CRegSafe(mpuAddr, 0x6B, pwrMgmt)) {
          if (gyroCfg != 0x10 || (pwrMgmt & 0x40) != 0) {
            configureMpuRegisters(false);
            hasPrevGyro = false;
            pwrResetLocal++;
            if (serialCanWriteNow()) {
              Serial.println("[UYARI] MPU-9250 guc kesintisi algilandi -> +-1000 deg/s yeniden kuruldu!");
            }
            lastImuUs = micros();
            continue;
          }
        }
      }

      uint32_t dtUs = nowUs - lastImuUs;
      lastImuUs = nowUs;

      if (dtUs == 0 || dtUs > 25000) {
        dtUs = IMU_INTERVAL_US;
        hasPrevGyro = false;
      }
      if (dtUs > maxDtUsLocal) maxDtUsLocal = dtUs;
      float dt = dtUs * 1e-6f;

      samplesLocal++;
      if (abs((int)rawGx) > 32000 || abs((int)rawGy) > 32000 || abs((int)rawGz) > 32000) {
        satLocal++;
      }

      float ax = rawAx * ACCEL_SCALE;
      float ay = rawAy * ACCEL_SCALE;
      float az = rawAz * ACCEL_SCALE;
      float accMag = sqrtf(ax * ax + ay * ay + az * az);
      smoothAccMag += (accMag - smoothAccMag) * 0.04f;
      float accDelta = fabsf(accMag - smoothAccMag);
      float accErr1g = fabsf(accMag - 1.0f);

      float dTemp = smoothTempC - biasRefTempC;
      float effOffX = gxOffset + thermalKx * dTemp;
      float effOffY = gyOffset + thermalKy * dTemp;
      float effOffZ = gzOffset + thermalKz * dTemp;

      if (deskStillCount == 0) {
        zuptAnchorGx = rawGx;
        zuptAnchorGy = rawGy;
        zuptAnchorGz = rawGz;
      }

      float devX = (rawGx - zuptAnchorGx) * GYRO_DEG_SCALE;
      float devY = (rawGy - zuptAnchorGy) * GYRO_DEG_SCALE;
      float devZ = (rawGz - zuptAnchorGz) * GYRO_DEG_SCALE;
      float devDegS = sqrtf(devX * devX + devY * devY + devZ * devZ);

      float corrXDegS = ((float)rawGx - effOffX) * GYRO_DEG_SCALE;
      float corrYDegS = ((float)rawGy - effOffY) * GYRO_DEG_SCALE;
      float corrZDegS = ((float)rawGz - effOffZ) * GYRO_DEG_SCALE;
      float rateDegS = sqrtf(corrXDegS * corrXDegS + corrYDegS * corrYDegS + corrZDegS * corrZDegS);

      lpGxDegS += (corrXDegS - lpGxDegS) * AIM_LP_ALPHA;
      lpGyDegS += (corrYDegS - lpGyDegS) * AIM_LP_ALPHA;
      lpGzDegS += (corrZDegS - lpGzDegS) * AIM_LP_ALPHA;
      float lpMagDegS = sqrtf(lpGxDegS * lpGxDegS + lpGyDegS * lpGyDegS + lpGzDegS * lpGzDegS);

      float maxRawSpan = ZUPT_MAX_BOOT_SPAN_DEG_S * GYRO_LSB_PER_DS;
      bool deskAccelStill = (accMag > 0.85f && accMag < 1.15f) && (accDelta < DESK_ACCEL_DELTA_G);
      bool aimAccelStill  = (accMag > 0.82f && accMag < 1.18f) && (accDelta < AIM_ACCEL_DELTA_G);

      uint8_t activeZuptTier = 0;

      // --- KADEME 2: KATI MASA / DESTEKLİ SABİTLİK KİLİDİ ---
      if (deskAccelStill && devDegS < DESK_GYRO_DEV_DEG_S && rateDegS < DESK_MAX_DRIFT_DEG_S) {
        if (deskStillCount < DESK_STILL_SAMPLES_REQ) {
          deskStillCount++;
        } else {
          activeZuptTier = 2;
          float resX = (float)rawGx - effOffX;
          float resY = (float)rawGy - effOffY;
          float resZ = (float)rawGz - effOffZ;
          gxOffset = fmaxf(baseGxOffset - maxRawSpan, fminf(baseGxOffset + maxRawSpan, gxOffset + resX * DESK_LEARN_ALPHA));
          gyOffset = fmaxf(baseGyOffset - maxRawSpan, fminf(baseGyOffset + maxRawSpan, gyOffset + resY * DESK_LEARN_ALPHA));
          gzOffset = fmaxf(baseGzOffset - maxRawSpan, fminf(baseGzOffset + maxRawSpan, gzOffset + resZ * DESK_LEARN_ALPHA));

          zuptAnchorGx += ((float)rawGx - zuptAnchorGx) * 0.005f;
          zuptAnchorGy += ((float)rawGy - zuptAnchorGy) * 0.005f;
          zuptAnchorGz += ((float)rawGz - zuptAnchorGz) * 0.005f;

          if (deskStillCount < 5000) deskStillCount++;
          uint32_t nowMs = millis();
          if (hasFlashGyroCal && deskStillCount >= 4000 && fabsf(dTemp) >= 3.0f && (nowMs - lastThermalSaveMs >= 120000)) {
            float curLearnX = gxOffset + thermalKx * dTemp;
            float curLearnY = gyOffset + thermalKy * dTemp;
            float curLearnZ = gzOffset + thermalKz * dTemp;
            float obsKx = fmaxf(-MAX_THERMAL_SLOPE_LSB_C, fminf(MAX_THERMAL_SLOPE_LSB_C, (curLearnX - baseGxOffset) / dTemp));
            float obsKy = fmaxf(-MAX_THERMAL_SLOPE_LSB_C, fminf(MAX_THERMAL_SLOPE_LSB_C, (curLearnY - baseGyOffset) / dTemp));
            float obsKz = fmaxf(-MAX_THERMAL_SLOPE_LSB_C, fminf(MAX_THERMAL_SLOPE_LSB_C, (curLearnZ - baseGzOffset) / dTemp));
            float diffSlope = fabsf(obsKx - thermalKx) + fabsf(obsKy - thermalKy) + fabsf(obsKz - thermalKz);
            if (diffSlope >= 0.35f) {
              thermalKx = (thermalKx == 0.0f) ? obsKx : (thermalKx * 0.5f + obsKx * 0.5f);
              thermalKy = (thermalKy == 0.0f) ? obsKy : (thermalKy * 0.5f + obsKy * 0.5f);
              thermalKz = (thermalKz == 0.0f) ? obsKz : (thermalKz * 0.5f + obsKz * 0.5f);
              gxOffset = baseGxOffset;
              gyOffset = baseGyOffset;
              gzOffset = baseGzOffset;
              lastThermalSaveMs = nowMs;
              saveGyroCalToFlash();
            }
          }
        }
      } else {
        deskStillCount = 0;
      }

      // --- KADEME 1: ELDE NİŞAN ULTRA-KORUMALI TERMAL SAPMA TELAFİSİ ---
      if (activeZuptTier == 0) {
        if (aimAccelStill && lpMagDegS < AIM_LP_MAX_DEG_S && rateDegS < AIM_INST_MAX_DEG_S) {
          if (aimStillCount < AIM_STILL_SAMPLES_REQ) {
            aimStillCount++;
          } else {
            activeZuptTier = 1;
            float aimMaxSpan = AIM_MAX_BOOT_SPAN_DEG_S * GYRO_LSB_PER_DS;
            float lpRawX = lpGxDegS * GYRO_LSB_PER_DS;
            float lpRawY = lpGyDegS * GYRO_LSB_PER_DS;
            float lpRawZ = lpGzDegS * GYRO_LSB_PER_DS;
            gxOffset = fmaxf(baseGxOffset - aimMaxSpan, fminf(baseGxOffset + aimMaxSpan, gxOffset + lpRawX * AIM_LEARN_ALPHA));
            gyOffset = fmaxf(baseGyOffset - aimMaxSpan, fminf(baseGyOffset + aimMaxSpan, gyOffset + lpRawY * AIM_LEARN_ALPHA));
            gzOffset = fmaxf(baseGzOffset - aimMaxSpan, fminf(baseGzOffset + aimMaxSpan, gzOffset + lpRawZ * AIM_LEARN_ALPHA));
          }
        } else if (!aimAccelStill || rateDegS >= AIM_INST_MAX_DEG_S || lpMagDegS >= AIM_LP_RESET_DEG_S) {
          aimStillCount = 0;
        } else if (aimStillCount > 3) {
          aimStillCount -= 3;
        } else {
          aimStillCount = 0;
        }
      } else {
        aimStillCount = AIM_STILL_SAMPLES_REQ;
      }

      effOffX = gxOffset + thermalKx * dTemp;
      effOffY = gyOffset + thermalKy * dTemp;
      effOffZ = gzOffset + thermalKz * dTemp;

      float gxDeg = ((float)rawGx - effOffX) * GYRO_DEG_SCALE * gyroScaleX;
      float gyDeg = ((float)rawGy - effOffY) * GYRO_DEG_SCALE * gyroScaleY;
      float gzDeg = ((float)rawGz - effOffZ) * GYRO_DEG_SCALE * gyroScaleZ;

      float gx = gxDeg * DEG_TO_RAD_F;
      float gy = gyDeg * DEG_TO_RAD_F;
      float gz = gzDeg * DEG_TO_RAD_F;
      float gyroMag = sqrtf(gx * gx + gy * gy + gz * gz);

      if (activeZuptTier == 2 && gyroMag < (0.12f * DEG_TO_RAD_F)) {
        gx = gy = gz = 0.0f;
        gxDeg = gyDeg = gzDeg = 0.0f;
        gyroMag = 0.0f;
      }

      bool inDynamicMotion = (accDelta > MOVE_ACCEL_DELTA_G) || (accErr1g > MOVE_ACCEL_GATE_G) || (gyroMag > MOVE_GYRO_GATE_RAD_S);
      float activeBeta = 0.0f;
      if (millis() - bootMillis < WARMUP_MS) {
        activeBeta = WARMUP_BETA;
      } else if (inDynamicMotion) {
        accelSettleCount = 0;
        activeBeta = 0.0f;
      } else {
        if (accelSettleCount < ACCEL_SETTLE_SAMPLES_REQ) {
          accelSettleCount++;
          activeBeta = STEADY_BETA * ((float)accelSettleCount / (float)ACCEL_SETTLE_SAMPLES_REQ);
        } else {
          activeBeta = STEADY_BETA;
        }
      }

      float calGx = gx, calGy = gy, calGz = gz;
      float calAx = ax, calAy = ay, calAz = az;
      applyLevelMountRotation(calGx, calGy, calGz);
      applyLevelMountRotation(calAx, calAy, calAz);

      updateAHRS6DofExact(calGx, calGy, calGz, calAx, calAy, calAz, dt, activeBeta);

      uint32_t nowMs = millis();
      bool statRollover = (nowMs - lastStatResetMs >= DEBUG_INTERVAL_MS);
      portENTER_CRITICAL(&stateMux);
      sharedState.qw = q0;
      sharedState.qx = q1;
      sharedState.qy = q2;
      sharedState.qz = q3;
      sharedState.gxDegS = gxDeg;
      sharedState.gyDegS = gyDeg;
      sharedState.gzDegS = gzDeg;
      sharedState.tempC = smoothTempC;
      sharedState.scaleX = gyroScaleX;
      sharedState.scaleY = gyroScaleY;
      sharedState.scaleZ = gyroScaleZ;
      sharedState.zuptTier = activeZuptTier;
      if (statRollover) {
        sharedState.maxDtUs = maxDtUsLocal;
        sharedState.imuSamplesInSec = samplesLocal;
        sharedState.satCount = satLocal;
        sharedState.i2cErrCount = i2cErrLocal;
        sharedState.pwrResetCount = pwrResetLocal;
      }
      portEXIT_CRITICAL(&stateMux);

      if (statRollover) {
        lastStatResetMs = nowMs;
        maxDtUsLocal = 0;
        samplesLocal = 0;
        satLocal = 0;
        i2cErrLocal = 0;
        pwrResetLocal = 0;
      }
    } else {
      i2cErrLocal++;
      consecI2cErr++;
      if (consecI2cErr >= 5) {
        hasPrevGyro = false;
      }
      if (consecI2cErr == 6 || (consecI2cErr > 6 && (consecI2cErr % 80 == 0))) {
        recoverI2cBusAndMpu();
        pwrResetLocal++;
        lastImuUs = micros();
      }
      uint32_t nowMs = millis();
      if (nowMs - lastStatResetMs >= DEBUG_INTERVAL_MS) {
        portENTER_CRITICAL(&stateMux);
        sharedState.maxDtUs = maxDtUsLocal;
        sharedState.imuSamplesInSec = samplesLocal;
        sharedState.satCount = satLocal;
        sharedState.i2cErrCount = i2cErrLocal;
        sharedState.pwrResetCount = pwrResetLocal;
        portEXIT_CRITICAL(&stateMux);
        lastStatResetMs = nowMs;
        maxDtUsLocal = 0;
        samplesLocal = 0;
        satLocal = 0;
        i2cErrLocal = 0;
        pwrResetLocal = 0;
      }
    }
  }
}

// --- HW-504 JOYSTICK (ADC1) MERKEZ KALİBRASYONU VE OKUMA (Yalnız Core 0 / Setup) ---
static inline float normalizeJoystickAxis(float rawVal, float center) {
  float diff = rawVal - center;
  float span = (diff >= 0.0f)
      ? fmaxf(400.0f, (4095.0f - JOY_EDGE_MARGIN_RAW) - center)
      : fmaxf(400.0f, center - JOY_EDGE_MARGIN_RAW);
  float n = diff / span;
  if (n > 1.0f) n = 1.0f;
  if (n < -1.0f) n = -1.0f;
  return n;
}

void calibrateJoystickCenter() {
  long sumX = 0;
  long sumY = 0;
  int minX = 4095, maxX = 0;
  int minY = 4095, maxY = 0;
  const int SAMPLES = 64;

  for (int i = 0; i < SAMPLES; i++) {
    int rx = analogRead(JOY_VRX_PIN);
    int ry = analogRead(JOY_VRY_PIN);
    sumX += rx;
    sumY += ry;
    if (rx < minX) minX = rx;
    if (rx > maxX) maxX = rx;
    if (ry < minY) minY = ry;
    if (ry > maxY) maxY = ry;
    delayMicroseconds(500);
  }

  float avgX = (float)sumX / SAMPLES;
  float avgY = (float)sumY / SAMPLES;
  int spreadX = maxX - minX;
  int spreadY = maxY - minY;

  bool validX = (avgX >= 1100.0f && avgX <= 3000.0f && spreadX < 350);
  bool validY = (avgY >= 1100.0f && avgY <= 3000.0f && spreadY < 350);

  joyCenterX = validX ? avgX : 2048.0f;
  joyCenterY = validY ? avgY : 2048.0f;
  joyFiltRawX = joyCenterX;
  joyFiltRawY = joyCenterY;
  joyLastRawX = (int)lroundf(avgX);
  joyLastRawY = (int)lroundf(avgY);
  joyOutX = 0;
  joyOutY = 0;

  if (serialCanWriteNow()) {
    if (validX && validY) {
      Serial.printf("[JOYSTICK] Merkez kalibre edildi -> VRx(GPIO%d):%.0f (yayilim:%d) | VRy(GPIO%d):%.0f (yayilim:%d) | SW(GPIO%d):%u\n",
                    JOY_VRX_PIN, joyCenterX, spreadX, JOY_VRY_PIN, joyCenterY, spreadY,
                    JOY_SW_PIN, (digitalRead(JOY_SW_PIN) == LOW) ? 1 : 0);
    } else {
      Serial.printf("[UYARI] Joystick merkezde/bagli degil (VRx:%.0f[%d], VRy:%.0f[%d]) -> varsayilan 2048 kullanildi! Baglayinca 'j' gonderin.\n",
                    avgX, spreadX, avgY, spreadY);
    }
  }
}

void sampleJoystickAnalog() {
  int rx = (analogRead(JOY_VRX_PIN) + analogRead(JOY_VRX_PIN)) >> 1;
  int ry = (analogRead(JOY_VRY_PIN) + analogRead(JOY_VRY_PIN)) >> 1;
  joyLastRawX = rx;
  joyLastRawY = ry;

  joyFiltRawX += ((float)rx - joyFiltRawX) * JOY_SMOOTH_ALPHA;
  joyFiltRawY += ((float)ry - joyFiltRawY) * JOY_SMOOTH_ALPHA;

  float nx = normalizeJoystickAxis(joyFiltRawX, joyCenterX);
  float ny = normalizeJoystickAxis(joyFiltRawY, joyCenterY);

  if (JOY_SWAP_XY) {
    float tmp = nx;
    nx = ny;
    ny = tmp;
  }
  if (JOY_INVERT_X) nx = -nx;
  if (JOY_INVERT_Y) ny = -ny;

  // Dairesel ölü bölge (radial deadzone): çapraz yürüyüşte eksen kilitlenmesi yapmaz, 0..1000 arası pürüzsüz başlar
  float mag = sqrtf(nx * nx + ny * ny);
  if (mag <= JOY_DEADZONE) {
    nx = 0.0f;
    ny = 0.0f;
  } else {
    float scaled = (mag - JOY_DEADZONE) / (1.0f - JOY_DEADZONE);
    if (scaled > 1.0f) scaled = 1.0f;
    float k = scaled / mag;
    nx *= k;
    ny *= k;
  }

  joyOutX = (int16_t)lroundf(nx * 1000.0f);
  joyOutY = (int16_t)lroundf(ny * 1000.0f);
}

// --- CORE 0 AĞ, 250 Hz UNICAST UDP VE SERİ PORT GÖREVİ ---
void sendUDPPacket(bool forceTriggerHigh, bool forceMagHigh, bool forceRecenterHigh, const SharedImuSnapshot& snap) {
  if (WiFi.status() != WL_CONNECTED) return;

  packet.header         = 0xAA;
  packet.qw             = snap.qw;
  packet.qx             = snap.qx;
  packet.qy             = snap.qy;
  packet.qz             = snap.qz;
  packet.triggerState   = (forceTriggerHigh || snap.triggerHeld) ? 1 : 0;
  packet.shotCount      = snap.shotCount;
  packet.shoulderState  = shoulderState;
  packet.magState       = (forceMagHigh || !magArmed) ? 1 : 0;
  packet.magToggleCount = magToggleCounter;
  packet.recenterState  = (forceRecenterHigh || !recenterArmed) ? 1 : 0;
  packet.recenterCount  = recenterCounter;
  packet.joyX           = joyOutX;
  packet.joyY           = joyOutY;
  packet.joySwState     = joySwState;

  udp.beginPacket(targetIp, UDP_PORT);
  udp.write((const uint8_t*)&packet, sizeof(packet));
  udp.endPacket();
}

void connectBestWifi() {
  WiFi.mode(WIFI_STA);
  WiFi.disconnect(true);
  vTaskDelay(pdMS_TO_TICKS(80));
  WiFi.setAutoReconnect(true);

  bool hotspotConfigured = (strcmp(HOTSPOT_SSID, "TELEFON_HOTSPOT_ADI") != 0) &&
                           (strcmp(HOTSPOT_SSID, "YOUR_HOTSPOT_SSID") != 0) &&
                           (strlen(HOTSPOT_SSID) > 0);
  bool hotspotFound = false;
  bool homeFound = false;

  Serial.println("Wi-Fi aglari taraniyor...");
  int netCount = WiFi.scanNetworks();
  for (int i = 0; i < netCount; i++) {
    String ssid = WiFi.SSID(i);
    if (hotspotConfigured && ssid == HOTSPOT_SSID) hotspotFound = true;
    if (ssid == HOME_WIFI_SSID) homeFound = true;
  }
  WiFi.scanDelete();

  bool useHotspot = hotspotFound || (hotspotConfigured && !homeFound);
  const char* chosenSsid = useHotspot ? HOTSPOT_SSID : HOME_WIFI_SSID;
  const char* chosenPass = useHotspot ? HOTSPOT_PASSWORD : HOME_WIFI_PASS;

  Serial.printf("Baglaniliyor -> %s (%s)\n", chosenSsid, useHotspot ? "Telefon Hotspot" : "Ev Wi-Fi / Unicast");
  WiFi.begin(chosenSsid, chosenPass);

  while (WiFi.status() != WL_CONNECTED) {
    vTaskDelay(pdMS_TO_TICKS(200));
    Serial.print(".");
  }

  WiFi.setSleep(false);
  esp_wifi_set_ps(WIFI_PS_NONE);

  if (useHotspot) {
    IPAddress gw = WiFi.gatewayIP();
    targetIp = (gw != IPAddress(0, 0, 0, 0)) ? gw : WiFi.broadcastIP();
  } else {
    targetIp = HOME_PC_IP;
  }

  udp.begin(UDP_PORT);

  Serial.println("\n[OK] Wi-Fi baglandi! ESP32 IP: " + WiFi.localIP().toString()
                 + " -> Hedef UDP IP (Unicast 250Hz): " + targetIp.toString() + ":" + String(UDP_PORT));
}

void pollUnityClientDiscovery() {
  int pktSize = udp.parsePacket();
  while (pktSize > 0) {
    char buf[8];
    int r = udp.read(buf, sizeof(buf));
    if (r >= 4 && buf[0] == 'V' && buf[1] == 'R' && buf[2] == 'A' && buf[3] == 'T') {
      IPAddress remote = udp.remoteIP();
      if (remote != IPAddress(0, 0, 0, 0) && remote != WiFi.localIP()) {
        bool ipChanged = (remote != targetIp);
        targetIp = remote;
        unityClientLocked = true;
        lastUnityHeartbeatMs = millis();
        if (ipChanged && serialCanWriteNow()) {
          Serial.println("[UNICAST KILIT] Unity istemcisi otomatik kesfedildi -> " + targetIp.toString());
        }
      }
    }
    pktSize = udp.parsePacket();
  }
}

void handleSerialCommands() {
  while (Serial.available()) {
    char cmd = Serial.read();
    if (cmd == 'x' || cmd == 'X') {
      cancelCalFlag = true;
    } else if (cmd == 'g' || cmd == 'G') {
      cancelCalFlag = false;
      pendingCal = CAL_GYRO_DESK;
    } else if (cmd == 's' || cmd == 'S') {
      cancelCalFlag = false;
      pendingCal = CAL_SCALE_360;
    } else if (cmd == 'r' || cmd == 'R') {
      pendingCal = CAL_RESET_SCALE;
    } else if (cmd == 'c' || cmd == 'C') {
      pendingCal = CAL_CLEAR_ALL;
    } else if (cmd == 'j' || cmd == 'J') {
      pendingJoyRecenter = true;
    }
  }
}

void netTaskFn(void* param) {
  (void)param;

  connectBestWifi();
  Serial.println("[KOMUTLAR] 'g': Sabit Masa Kalibresi | 's': 360 Derece Olcek Kalibresi | 'r': Olcek Sifirla | 'c': Flash Temizle | 'j': Joystick Merkez Sifirla");

  uint32_t lastUdpUs = micros();
  uint32_t lastJoyUs = micros();
  uint32_t lastDebugMs = millis();
#if (configTICK_RATE_HZ >= 1000)
  TickType_t lastNetWake = xTaskGetTickCount();
#endif

  for (;;) {
    uint32_t nowUs = micros();

    // 1) Core 1 (1000 Hz Wi-Fi'dan izole) onaylı tetik olayını çek
    uint32_t latestShotCount = 0;
    bool core1TriggerEvent = false;
    portENTER_CRITICAL(&stateMux);
    latestShotCount = sharedState.shotCount;
    if (sharedState.triggerEventPending) {
      core1TriggerEvent = true;
      sharedState.triggerEventPending = false;
    }
    portEXIT_CRITICAL(&stateMux);

    if (core1TriggerEvent) {
      triggerEventFlag = true;
      if (serialCanWriteNow()) {
        Serial.printf("[TETIK] GPIO%d onaylandi -> Atis #%lu | Omuz(GPIO%d):%u\n",
                      TRIGGER_PIN, (unsigned long)latestShotCount, SHOULDER_PIN, shoulderState);
      }
    }

    // 2) Omuz Sensörü Kararlı Debounce Filtresi (GPIO 16: 30 ms kesintisiz durum değişimi)
    uint8_t rawShoulder = (digitalRead(SHOULDER_PIN) == LOW) ? 1 : 0;
    if (rawShoulder != shoulderState) {
      if (!shoulderPendingTracking || shoulderPendingVal != rawShoulder) {
        shoulderPendingTracking = true;
        shoulderPendingVal = rawShoulder;
        shoulderPendingStartUs = nowUs;
      } else if (nowUs - shoulderPendingStartUs >= SHOULDER_DEBOUNCE_US) {
        shoulderState = rawShoulder;
        shoulderPendingTracking = false;
        shoulderEventFlag = true;
        if (serialCanWriteNow()) {
          Serial.printf("[OMUZ] GPIO%d -> %s | Tetik(GPIO%d):%u\n",
                        SHOULDER_PIN,
                        shoulderState ? "OMUZLANDI (Red Dot ACIK)" : "SERBEST (Red Dot KAPALI)",
                        TRIGGER_PIN,
                        (digitalRead(TRIGGER_PIN) == LOW) ? 1 : 0);
        }
      }
    } else {
      shoulderPendingTracking = false;
    }

    // 2.5) Şarjör Çıkar/Tak Kontağı Filtresi (GPIO 17 <-> GND: 20 ms kesintisiz LOW -> 1 toggle/'V', 80 ms HIGH -> yeniden kur)
    bool magLow = (digitalRead(MAG_PIN) == LOW);
    if (magArmed) {
      if (magLow) {
        if (!magLowTracking) {
          magLowTracking = true;
          magLowStartUs = nowUs;
          magLowSamples = 1;
        } else {
          magLowSamples++;
          if ((nowUs - magLowStartUs >= MAG_CONFIRM_US) &&
              (magLowSamples >= 10) &&
              (nowUs - lastMagToggleUs >= MIN_MAG_TOGGLE_GAP_US)) {
            magArmed = false;
            magLowTracking = false;
            magLowSamples = 0;
            magHighTracking = false;
            lastMagToggleUs = nowUs;
            magToggleCounter++;
            magEventFlag = true;
            if (serialCanWriteNow()) {
              Serial.printf("[SARJOR] GPIO%d temas onaylandi -> Toggle #%u (%s / Unity 'V')\n",
                            MAG_PIN,
                            (unsigned)magToggleCounter,
                            (magToggleCounter & 1U) ? "CIKAR" : "TAK");
            }
          }
        }
      } else if (magLowTracking) {
        magGlitchRejected++;
        magLowTracking = false;
        magLowSamples = 0;
      }
    } else {
      if (!magLow) {
        if (!magHighTracking) {
          magHighTracking = true;
          magHighStartUs = nowUs;
        } else if (nowUs - magHighStartUs >= MAG_RELEASE_DEBOUNCE_US) {
          magArmed = true;
          magHighTracking = false;
        }
      } else {
        magHighTracking = false;
      }
    }

    // 2.6) VR & LED Sıfırlama Butonu Filtresi (GPIO 18 <-> GND: 25 ms kesintisiz LOW -> Ekrana 2 parmak dokunma ile aynı)
    bool recenterLow = (digitalRead(RECENTER_PIN) == LOW);
    if (recenterArmed) {
      if (recenterLow) {
        if (!recenterLowTracking) {
          recenterLowTracking = true;
          recenterLowStartUs = nowUs;
          recenterLowSamples = 1;
        } else {
          recenterLowSamples++;
          if ((nowUs - recenterLowStartUs >= RECENTER_CONFIRM_US) &&
              (recenterLowSamples >= 12) &&
              (nowUs - lastRecenterPressUs >= MIN_RECENTER_GAP_US)) {
            recenterArmed = false;
            recenterLowTracking = false;
            recenterLowSamples = 0;
            recenterHighTracking = false;
            lastRecenterPressUs = nowUs;
            recenterCounter++;
            recenterEventFlag = true;
            if (serialCanWriteNow()) {
              Serial.printf("[SIFIRLA] GPIO%d butonu onaylandi -> Sifirlama #%u (VR 2-Parmak / Kafa + Tufek + LED Sifirlama)\n",
                            RECENTER_PIN, (unsigned)recenterCounter);
            }
          }
        }
      } else if (recenterLowTracking) {
        recenterLowTracking = false;
        recenterLowSamples = 0;
      }
    } else {
      if (!recenterLow) {
        if (!recenterHighTracking) {
          recenterHighTracking = true;
          recenterHighStartUs = nowUs;
        } else if (nowUs - recenterHighStartUs >= RECENTER_RELEASE_US) {
          recenterArmed = true;
          recenterHighTracking = false;
        }
      } else {
        recenterHighTracking = false;
      }
    }

    // 2.7) HW-504 Analog Joystick (GPIO 4/5 ADC1) & Orta Basma Butonu (GPIO 6 SW)
    if (pendingJoyRecenter) {
      pendingJoyRecenter = false;
      calibrateJoystickCenter();
    }
    if (nowUs - lastJoyUs >= JOY_SAMPLE_INTERVAL_US) {
      lastJoyUs = nowUs;
      sampleJoystickAnalog();
    }
    uint8_t rawJoySw = (digitalRead(JOY_SW_PIN) == LOW) ? 1 : 0;
    if (rawJoySw != joySwState) {
      if (!joySwPendingTracking || joySwPendingVal != rawJoySw) {
        joySwPendingTracking = true;
        joySwPendingVal = rawJoySw;
        joySwPendingStartUs = nowUs;
      } else if (nowUs - joySwPendingStartUs >= JOY_SW_DEBOUNCE_US) {
        joySwState = rawJoySw;
        joySwPendingTracking = false;
        joySwEventFlag = true;
        if (serialCanWriteNow()) {
          Serial.printf("[JOYSTICK-SW] GPIO%d -> %s\n", JOY_SW_PIN, joySwState ? "BASILI (Sprint)" : "SERBEST");
        }
      }
    } else {
      joySwPendingTracking = false;
    }

    // 3) 250 Hz Deterministik Unicast UDP Göndericisi (Acil tetik/omuz/şarjör/sıfırlama/SW olayları anında gider)
    bool timeForRegularUdp  = (nowUs - lastUdpUs >= UDP_INTERVAL_US);
    bool urgentEventPending = (triggerEventFlag || shoulderEventFlag || magEventFlag || recenterEventFlag || joySwEventFlag) && (nowUs - lastUdpUs >= MIN_UDP_GAP_US);

    if (urgentEventPending || timeForRegularUdp) {
      bool isTriggerShot  = triggerEventFlag;
      bool isMagToggle    = magEventFlag;
      bool isRecenterShot = recenterEventFlag;
      triggerEventFlag    = false;
      shoulderEventFlag   = false;
      magEventFlag        = false;
      recenterEventFlag   = false;
      joySwEventFlag      = false;
      lastUdpUs           = nowUs;

      SharedImuSnapshot snap;
      portENTER_CRITICAL(&stateMux);
      snap = sharedState;
      portEXIT_CRITICAL(&stateMux);

      sendUDPPacket(isTriggerShot, isMagToggle, isRecenterShot, snap);
    }

    // 4) Seri komutlar, Unity Unicast keşfi ve 1 Hz teşhis çıktısı
    handleSerialCommands();
    pollUnityClientDiscovery();

    uint32_t nowMs = millis();
    if (nowMs - lastDebugMs >= DEBUG_INTERVAL_MS) {
      lastDebugMs = nowMs;

      // Unity "VRAT" yanıtı 6 sn'den uzun süredir gelmiyorsa kilidi aç ve hafif alt-ağ keşif paketi yolla.
      // Unity bağlıyken broadcast gönderilmez -> telefon hotspot'unda 1 Hz Wi-Fi takılması sıfırlanır!
      if (unityClientLocked && (nowMs - lastUnityHeartbeatMs > UNICAST_KEEPALIVE_MS)) {
        unityClientLocked = false;
      }
      if (!unityClientLocked) {
        IPAddress bcast = WiFi.broadcastIP();
        if (bcast != IPAddress(0, 0, 0, 0) && bcast != targetIp) {
          udp.beginPacket(bcast, UDP_PORT);
          udp.write((const uint8_t*)&packet, sizeof(packet));
          udp.endPacket();
        }
      }

      if (serialCanWriteNow()) {
        SharedImuSnapshot snap;
        portENTER_CRITICAL(&stateMux);
        snap = sharedState;
        portEXIT_CRITICAL(&stateMux);

        if (!snap.calBusy) {
          uint8_t liveTrig = (digitalRead(TRIGGER_PIN) == LOW) ? 1 : 0;
          uint8_t liveMag  = (digitalRead(MAG_PIN) == LOW) ? 1 : 0;
          uint8_t liveRec  = (digitalRead(RECENTER_PIN) == LOW) ? 1 : 0;
          const char* modeTag = (snap.zuptTier == 2) ? "6DOF-SABIT"
                              : (snap.zuptTier == 1 ? "6DOF-NISAN" : "6DOF-HAREKET");

          Serial.printf("[%s] %luHz maxDt:%.1fms | Temp:%.1fC | G(%+.2f,%+.2f,%+.2f) | Sc(%.3f,%.3f,%.3f) | T:%u O:%u S:%u(%u) R:%u(%u) A:%lu | J(X:%+d,Y:%+d,SW:%u raw:%d,%d)",
                        modeTag,
                        (unsigned long)snap.imuSamplesInSec,
                        snap.maxDtUs * 0.001f,
                        snap.tempC,
                        snap.gxDegS, snap.gyDegS, snap.gzDegS,
                        snap.scaleX, snap.scaleY, snap.scaleZ,
                        liveTrig, shoulderState, liveMag, (unsigned)magToggleCounter,
                        liveRec, (unsigned)recenterCounter, (unsigned long)snap.shotCount,
                        (int)joyOutX, (int)joyOutY, joySwState, joyLastRawX, joyLastRawY);
          if (snap.satCount > 0) {
            Serial.printf(" | SAT:%lu", (unsigned long)snap.satCount);
          }
          if (snap.i2cErrCount > 0) {
            Serial.printf(" | I2C_ERR:%lu", (unsigned long)snap.i2cErrCount);
          }
          if (snap.pwrResetCount > 0) {
            Serial.printf(" | PWR_RST:%lu", (unsigned long)snap.pwrResetCount);
          }
          if (snap.triggerGlitchCount > 0) {
            Serial.printf(" | PARAZIT_ENGEL:%lu", (unsigned long)snap.triggerGlitchCount);
          }
          if (magGlitchRejected > 0) {
            Serial.printf(" | SARJOR_PARAZIT:%lu", (unsigned long)magGlitchRejected);
          }
          Serial.println();
        }
      }
    }

#if (configTICK_RATE_HZ >= 1000)
    vTaskDelayUntil(&lastNetWake, 1);
#else
    vTaskDelay(msToTicksSafe(1));
#endif
  }
}

void setup() {
  Serial.begin(115200);
  delay(300);

  pinMode(TRIGGER_PIN, INPUT_PULLUP);
  pinMode(SHOULDER_PIN, INPUT_PULLUP);
  pinMode(MAG_PIN, INPUT_PULLUP);
  pinMode(RECENTER_PIN, INPUT_PULLUP);
  pinMode(JOY_SW_PIN, INPUT_PULLUP);

  analogReadResolution(12);
  analogSetPinAttenuation(JOY_VRX_PIN, ADC_11db);
  analogSetPinAttenuation(JOY_VRY_PIN, ADC_11db);
  calibrateJoystickCenter();

  initSensors();

  portENTER_CRITICAL(&stateMux);
  sharedState.qw = q0;
  sharedState.qx = q1;
  sharedState.qy = q2;
  sharedState.qz = q3;
  sharedState.tempC = smoothTempC;
  sharedState.scaleX = gyroScaleX;
  sharedState.scaleY = gyroScaleY;
  sharedState.scaleZ = gyroScaleZ;
  portEXIT_CRITICAL(&stateMux);

  trigArmedCore1 = (digitalRead(TRIGGER_PIN) == HIGH);
  lastTriggerPressUs = micros() - MIN_SHOT_INTERVAL_US;
  shoulderState = (digitalRead(SHOULDER_PIN) == LOW) ? 1 : 0;
  magArmed = (digitalRead(MAG_PIN) == HIGH);
  lastMagToggleUs = micros() - MIN_MAG_TOGGLE_GAP_US;
  recenterArmed = (digitalRead(RECENTER_PIN) == HIGH);
  lastRecenterPressUs = micros() - MIN_RECENTER_GAP_US;
  joySwState = (digitalRead(JOY_SW_PIN) == LOW) ? 1 : 0;

  portENTER_CRITICAL(&stateMux);
  sharedState.triggerHeld = !trigArmedCore1;
  portEXIT_CRITICAL(&stateMux);

  // Core 1: İzole 1000 Hz Saf 6-DOF IMU Görevi (Yüksek Öncelik = 6)
  xTaskCreatePinnedToCore(
      imuTaskFn,
      "IMU_1kHz_Core1",
      6144,
      nullptr,
      6,
      &imuTaskHandle,
      1);

#if (configTICK_RATE_HZ < 1000)
  esp_timer_create_args_t tArgs = {};
  tArgs.callback = &imuFallbackTimerCb;
  tArgs.name = "imu_1khz";
  if (esp_timer_create(&tArgs, &imuFallbackTimer) == ESP_OK) {
    esp_timer_start_periodic(imuFallbackTimer, IMU_INTERVAL_US);
  }
#endif

  // Core 0: Wi-Fi + 250 Hz Unicast UDP + Tetik/Omuz + Serial Görevi (Öncelik = 2)
  xTaskCreatePinnedToCore(
      netTaskFn,
      "NET_UDP_Core0",
      6144,
      nullptr,
      2,
      &netTaskHandle,
      0);
}

void loop() {
  // Tüm iş Core 1 (1000 Hz IMU) ve Core 0 (250 Hz UDP) görevlerine bölündü
  vTaskDelay(pdMS_TO_TICKS(1000));
}
