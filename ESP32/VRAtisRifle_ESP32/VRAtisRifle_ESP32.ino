#include <Preferences.h>
#include <WiFi.h>
#include <WiFiUdp.h>
#include <Wire.h>
#include <esp_timer.h>
#include <esp_wifi.h>
#include <math.h>
#include <string.h>

// --- AĞ AYARLARI (Önce Telefon Hotspot'u arar; bulamazsa Ev Wi-Fi Alt Ağ Yayınına bağlanır) ---
const char* HOTSPOT_SSID     = "YOUR_HOTSPOT_SSID";         // Galaxy A26 Hotspot adın
const char* HOTSPOT_PASSWORD = "YOUR_WIFI_PASSWORD";  // Galaxy A26 Hotspot şifren

const char* HOME_WIFI_SSID   = "YOUR_HOME_WIFI_SSID";            // PC/Ev testleri için modem adı
const char* HOME_WIFI_PASS   = "YOUR_WIFI_PASSWORD";
const IPAddress HOME_PC_IP(192, 168, 1, 105);    // Yedek PC yerel IP

const uint16_t UDP_PORT      = 4210;
IPAddress targetIp           = HOME_PC_IP;

// --- PİN VE DONANIM TANIMLAMALARI ---
#define I2C_SDA_PIN  8
#define I2C_SCL_PIN  9
#define TRIGGER_PIN  15     // Tetik Sinyali -> GPIO 15, Diğer uç -> GND
#define SHOULDER_PIN 16     // Omuz Temas Sensörü -> GPIO 16, Diğer plaka -> GND
#define I2C_CLOCK_HZ 400000 // 400 kHz Fast-Mode I2C

// --- AK8963 DAHİLİ MANYETOMETRE (9-DOF) ADRES VE KAYITÇILARI ---
#define AK8963_ADDR      0x0C
#define AK8963_WIA_REG   0x00 // Beklenen kimlik: 0x48
#define AK8963_ST1_REG   0x02 // Data Ready (bit 0)
#define AK8963_HXL_REG   0x03 // 6 byte veri + 1 byte ST2 (0x09)
#define AK8963_CNTL1_REG 0x0A
#define AK8963_CNTL2_REG 0x0B
#define AK8963_ASAX_REG  0x10

// --- ZAMANLAMA SABİTLERİ (Core 1: 1000 Hz IMU / Core 0: 200 Hz UDP) ---
const uint32_t IMU_INTERVAL_US        = 1000;  // 1000 Hz (1 ms) deterministik Core 1 döngüsü
const uint32_t MAG_INTERVAL_US        = 10000; // 100 Hz (10 ms) isteğe bağlı AK8963
const uint32_t UDP_INTERVAL_US        = 5000;  // 200 Hz (5 ms) UDP paket gönderimi
const uint32_t MIN_UDP_GAP_US         = 2000;  // Wi-Fi TX koruması (2 ms)
const uint32_t MIN_SHOT_INTERVAL_US   = 35000; // Tetik ark koruması (35 ms)
const uint32_t RELEASE_DEBOUNCE_US    = 25000; // Tetik bırakma kararlılığı (25 ms)
const uint32_t SHOULDER_DEBOUNCE_US   = 30000; // Omuz sensörü sıçrama koruması (30 ms)
const uint32_t DEBUG_INTERVAL_MS      = 1000;  // 1 Hz durum çıktısı

// --- SENSÖR ÖLÇEK SABİTLERİ ---
constexpr float DEG_TO_RAD_F    = PI / 180.0f;
constexpr float RAD_TO_DEG_F    = 180.0f / PI;
constexpr float GYRO_LSB_PER_DS = 32.8f;                        // +-1000 deg/s (FS_SEL=2)
constexpr float GYRO_DEG_SCALE  = 1.0f / GYRO_LSB_PER_DS;       // raw -> deg/s
constexpr float GYRO_RAD_SCALE  = DEG_TO_RAD_F / GYRO_LSB_PER_DS; // raw -> rad/s
constexpr float ACCEL_SCALE     = 1.0f / 8192.0f;               // +-4g (AFS_SEL=1)
constexpr float MAG_UT_SCALE    = 4912.0f / 32760.0f;           // AK8963 16-bit (~0.15 uT/LSB)

// --- 6-DOF / 9-DOF FÜZYON VE 2 KADEMELİ AKILLI ZUPT SABİTLERİ ---
const float WARMUP_BETA               = 1.25f;
const float STEADY_BETA               = 0.012f; // Yalnız tam sabit duruşta çok yumuşak yerçekimi düzeltmesi
const float MOVE_ACCEL_GATE_G         = 0.035f; // |accMag - 1g| > 0.035g iken beta=0 (yanal/merkezkaç ivme koruması)
const float MOVE_ACCEL_DELTA_G        = 0.025f; // Ani ivme değişiminde beta=0
const float MOVE_GYRO_GATE_RAD_S      = 0.14f;  // ~8 deg/s üstü hedef geçişlerinde saf jiroskop (beta=0 -> sıfır Yaw sızıntısı)
const int   ACCEL_SETTLE_SAMPLES_REQ  = 120;    // Hareket bitiminde 120 ms titreşim sönümleme beklemesi
const float KP_MAG_YAW                = 0.15f;  // Yalnız '9' komutuyla 9-DOF açılırsa yumuşak pusula kazancı
const float KI_MAG_YAW                = 0.02f;

// Kademe 2: Katı Masa / Destekli Sabitlik Kilidi (0.00 deg/s kayma)
const float DESK_ACCEL_DELTA_G        = 0.015f;
const float DESK_GYRO_DEV_DEG_S       = 0.65f;
const float DESK_MAX_DRIFT_DEG_S      = 1.60f;
const int   DESK_STILL_SAMPLES_REQ    = 300;    // 300 ms kesintisiz sabitlik (1000 Hz)
const float DESK_LEARN_ALPHA          = 0.012f; // 1000 Hz'de ~0.08 sn hızlı ofset kilidi

// Kademe 1: Elde Nişan Ultra-Korumalı Termal Telafi (İnce nişanı asla bias sanıp ters kayma üretmez!)
const float AIM_ACCEL_DELTA_G         = 0.035f;
const float AIM_LP_ALPHA              = 0.0018f; // ~0.28 Hz LPF (el titremesini/nefesi süzer)
const float AIM_LP_MAX_DEG_S          = 0.30f;   // 0.30 deg/s üstü kasıtlı ince nişandır -> asla öğrenme!
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
  CAL_MAG,
  CAL_CLEAR_ALL
};

// Unity'ye giden 23 byte'lık UDP paketi (Unity Esp32RifleReceiver ile %100 uyumlu)
struct __attribute__((packed)) RifleQuaternionPacket {
  uint8_t header;         // 0xAA doğrulama baytı
  float qw, qx, qy, qz;   // 3D Dönüş Verisi
  uint8_t triggerState;   // 1 = Tetik Basılı, 0 = Serbest
  uint32_t shotCount;     // Toplam tetik basma sayacı
  uint8_t shoulderState;  // 1 = Omuzlandı (Red Dot Açık), 0 = Serbest (Red Dot Kapalı)
};

// Core 1 (IMU) -> Core 0 (Ağ/Serial) kilitli paylaşımlı anlık durum
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
  uint8_t zuptTier;     // 0=HAREKET, 1=NISAN, 2=SABIT
  bool magAnomaly;
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
  0, false, false
};

TaskHandle_t imuTaskHandle = nullptr;
TaskHandle_t netTaskHandle = nullptr;
esp_timer_handle_t imuFallbackTimer = nullptr;

uint8_t mpuAddr               = 0x68;
volatile bool nineDofEnabled  = false; // Varsayılan: 6-DOF + 2 Kademeli Akıllı ZUPT
volatile bool cancelCalFlag   = false;
volatile uint8_t pendingCal   = CAL_NONE;
bool hasMagnetometer          = false;
bool hasMagCal                = false;
bool hasFlashGyroCal          = false;
bool magAnomalyActive         = false;

// Tetik & Omuz Kesme Değişkenleri
volatile bool triggerEventFlag        = false;
volatile bool triggerArmed            = true;
volatile uint32_t interruptCounter    = 0;
volatile uint32_t lastTriggerPressUs  = 0;
uint32_t highStateStartUs             = 0;

volatile bool shoulderEventFlag       = false;
volatile uint8_t shoulderState        = 0;
volatile uint32_t lastShoulderIrqUs   = 0;

// Ham sensör verileri (Yalnız Core 1 imuTask erişir)
int16_t rawAx = 0, rawAy = 0, rawAz = 0;
int16_t rawTemp = 0;
int16_t rawGx = 0, rawGy = 0, rawGz = 0;
int16_t rawMx = 0, rawMy = 0, rawMz = 0;

// Kalibrasyon, Termal Eğim, Ölçek ve 2 Kademeli ZUPT durumları (Yalnız Core 1)
float baseGxOffset = 0.0f, baseGyOffset = 0.0f, baseGzOffset = 0.0f; // tRef sıcaklığındaki temiz masa tabanı
float gxOffset = 0.0f, gyOffset = 0.0f, gzOffset = 0.0f;             // tRef referanslı canlı ofset
float thermalKx = 0.0f, thermalKy = 0.0f, thermalKz = 0.0f;          // raw LSB / °C
float biasRefTempC = 25.0f;
float smoothTempC = 25.0f;
bool hasThermalSample = false;

float gyroScaleX = 1.0f, gyroScaleY = 1.0f, gyroScaleZ = 1.0f;       // 360° kalibre edilebilir ölçek çarpanı

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

// Manyetometre fabrika ASA çarpanı + Hard/Soft-Iron kalibrasyonu
float magAsaX = 1.0f, magAsaY = 1.0f, magAsaZ = 1.0f;
float magOffX = 0.0f, magOffY = 0.0f, magOffZ = 0.0f;
float magScaleX = 1.0f, magScaleY = 1.0f, magScaleZ = 1.0f;
float magRefFieldUt = 45.0f;
float calMx = 0.0f, calMy = 0.0f, calMz = 0.0f;
bool magSampleValid = false;

// Kuaterniyon & 9-DOF Yaw integral durumu
float q0 = 1.0f, q1 = 0.0f, q2 = 0.0f, q3 = 0.0f;
float yawBiasEarthZ = 0.0f;
uint32_t bootMillis = 0;

// --- KESMELER (IRAM) ---
void IRAM_ATTR onTriggerPress() {
  uint32_t now = micros();
  if (triggerArmed && (now - lastTriggerPressUs >= MIN_SHOT_INTERVAL_US)) {
    triggerArmed = false;
    lastTriggerPressUs = now;
    interruptCounter = interruptCounter + 1;
    triggerEventFlag = true;
  }
}

void IRAM_ATTR onShoulderChange() {
  uint32_t now = micros();
  if (now - lastShoulderIrqUs >= SHOULDER_DEBOUNCE_US) {
    lastShoulderIrqUs = now;
    shoulderState = (digitalRead(SHOULDER_PIN) == LOW) ? 1 : 0;
    shoulderEventFlag = true;
  }
}

// --- I2C YARDIMCILARI (Yalnız setup() ve Core 1 imuTask çağırır) ---
static inline TickType_t msToTicksSafe(uint32_t ms) {
  TickType_t ticks = pdMS_TO_TICKS(ms);
  return (ticks > 0) ? ticks : 1;
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

bool readAlignedMagUt(float& outRawMpuX, float& outRawMpuY, float& outRawMpuZ) {
  if (!hasMagnetometer) return false;

  uint8_t st1 = readI2CReg(AK8963_ADDR, AK8963_ST1_REG);
  if ((st1 & 0x01) == 0) return false;

  Wire.beginTransmission(AK8963_ADDR);
  Wire.write(AK8963_HXL_REG);
  if (Wire.endTransmission(false) != 0) return false;
  if (Wire.requestFrom((uint16_t)AK8963_ADDR, (uint8_t)7, true) != 7) return false;

  uint8_t xL = Wire.read();
  uint8_t xH = Wire.read();
  uint8_t yL = Wire.read();
  uint8_t yH = Wire.read();
  uint8_t zL = Wire.read();
  uint8_t zH = Wire.read();
  uint8_t st2 = Wire.read();

  if (st2 & 0x08) return false;

  rawMx = (int16_t)((xH << 8) | xL);
  rawMy = (int16_t)((yH << 8) | yL);
  rawMz = (int16_t)((zH << 8) | zL);

  float akX = rawMx * magAsaX * MAG_UT_SCALE;
  float akY = rawMy * magAsaY * MAG_UT_SCALE;
  float akZ = rawMz * magAsaZ * MAG_UT_SCALE;

  // AK8963 -> MPU-6500 fiziksel kalıp dönüşümü
  outRawMpuX =  akY;
  outRawMpuY =  akX;
  outRawMpuZ = -akZ;
  return true;
}

void rotateVecByQuat(float qw, float qx, float qy, float qz, float vx, float vy, float vz, float& rx, float& ry, float& rz) {
  float tx = 2.0f * (qy * vz - qz * vy);
  float ty = 2.0f * (qz * vx - qx * vz);
  float tz = 2.0f * (qx * vy - qy * vx);
  rx = vx + qw * tx + (qy * tz - qz * ty);
  ry = vy + qw * ty + (qz * tx - qx * tz);
  rz = vz + qw * tz + (qx * ty - qy * tx);
}

void initQuatFromAccelAndMag(float ax, float ay, float az, float mx, float my, float mz, bool useMag) {
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

  if (useMag) {
    float hx, hy, hz;
    rotateVecByQuat(q0, q1, q2, q3, mx, my, mz, hx, hy, hz);
    float hLen = hypotf(hx, hy);
    if (hLen > 1e-4f) {
      float halfPsi = 0.5f * atan2f(-hy, hx);
      float cz = cosf(halfPsi);
      float sz = sinf(halfPsi);
      float n0 = cz * q0 - sz * q3;
      float n1 = cz * q1 - sz * q2;
      float n2 = cz * q2 + sz * q1;
      float n3 = cz * q3 + sz * q0;
      float nNorm = sqrtf(n0 * n0 + n1 * n1 + n2 * n2 + n3 * n3);
      if (nNorm > 1e-6f) {
        q0 = n0 / nNorm;
        q1 = n1 / nNorm;
        q2 = n2 / nNorm;
        q3 = n3 / nNorm;
      }
    }
  }
  yawBiasEarthZ = 0.0f;
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
  prefs.end();
}

void saveMagCalToFlash() {
  if (!prefs.begin("vratis_imu", false)) return;
  prefs.putBool("ok", true);
  prefs.putFloat("ox", magOffX);
  prefs.putFloat("oy", magOffY);
  prefs.putFloat("oz", magOffZ);
  prefs.putFloat("sx", magScaleX);
  prefs.putFloat("sy", magScaleY);
  prefs.putFloat("sz", magScaleZ);
  prefs.putFloat("ref", magRefFieldUt);
  prefs.end();
}

void loadMagCalFromFlash() {
  if (!prefs.begin("vratis_imu", true)) return;
  hasMagCal = prefs.getBool("ok", false);
  if (hasMagCal) {
    magOffX = prefs.getFloat("ox", 0.0f);
    magOffY = prefs.getFloat("oy", 0.0f);
    magOffZ = prefs.getFloat("oz", 0.0f);
    magScaleX = prefs.getFloat("sx", 1.0f);
    magScaleY = prefs.getFloat("sy", 1.0f);
    magScaleZ = prefs.getFloat("sz", 1.0f);
    magRefFieldUt = prefs.getFloat("ref", 45.0f);
  }
  prefs.end();
}

void clearAllCalFlash() {
  if (prefs.begin("vratis_imu", false)) {
    prefs.clear();
    prefs.end();
  }
  if (prefs.begin("vratis_gyro", false)) {
    prefs.clear();
    prefs.end();
  }
  hasMagCal = false;
  hasFlashGyroCal = false;
  magOffX = magOffY = magOffZ = 0.0f;
  magScaleX = magScaleY = magScaleZ = 1.0f;
  thermalKx = thermalKy = thermalKz = 0.0f;
  gyroScaleX = gyroScaleY = gyroScaleZ = 1.0f;
  Serial.println("[BILGI] Tum Flash kayitlari (Jiroskop + Olcek + Pusula) temizlendi.");
}

bool finalizeMagSphere(float minX, float maxX, float minY, float maxY, float minZ, float maxZ) {
  float rx = 0.5f * (maxX - minX);
  float ry = 0.5f * (maxY - minY);
  float rz = 0.5f * (maxZ - minZ);
  if (rx < 15.0f || ry < 15.0f || rz < 12.0f) return false;

  float avgRad = (rx + ry + rz) / 3.0f;
  if (avgRad < 18.0f || avgRad > 140.0f) return false;

  magOffX = 0.5f * (maxX + minX);
  magOffY = 0.5f * (maxY + minY);
  magOffZ = 0.5f * (maxZ + minZ);
  magScaleX = avgRad / rx;
  magScaleY = avgRad / ry;
  magScaleZ = avgRad / rz;
  magRefFieldUt = avgRad;
  hasMagCal = true;
  saveMagCalToFlash();
  Serial.printf("[9-DOF OK] Pusula kalibre edildi & kaydedildi! Ofset:(%.1f, %.1f, %.1f) Yaricap:%.1f uT\n",
                magOffX, magOffY, magOffZ, magRefFieldUt);
  return true;
}

void calibrateMagnetometer() {
  if (!hasMagnetometer) {
    Serial.println("[UYARI] Bu modulde AK8963 manyetometre bulunamadi (MPU-6500).");
    return;
  }

  Serial.println("====================================================");
  Serial.println("[9-DOF PUSULA KALIBRASYONU BASLADI - 10 SANIYE]");
  Serial.println("Lutfen tufegi yavasca her yone cevirin (8 cizin, yukari-asagi-sag-sol dondurun)...");
  Serial.println("====================================================");

  float minX = 1e6f, minY = 1e6f, minZ = 1e6f;
  float maxX = -1e6f, maxY = -1e6f, maxZ = -1e6f;
  uint32_t startMs = millis();

  while (millis() - startMs < 10000 && !cancelCalFlag) {
    float mx, my, mz;
    if (readAlignedMagUt(mx, my, mz)) {
      if (fabsf(mx) < 600.0f && fabsf(my) < 600.0f && fabsf(mz) < 600.0f) {
        if (mx < minX) minX = mx; if (mx > maxX) maxX = mx;
        if (my < minY) minY = my; if (my > maxY) maxY = my;
        if (mz < minZ) minZ = mz; if (mz > maxZ) maxZ = mz;
      }
    }
    vTaskDelay(pdMS_TO_TICKS(10));
  }

  if (cancelCalFlag) {
    cancelCalFlag = false;
    Serial.println("[IPTAL] Pusula kalibrasyonu iptal edildi.");
    return;
  }

  if (!finalizeMagSphere(minX, maxX, minY, maxY, minZ, maxZ)) {
    Serial.println("[HATA] Yeterli donus alinamadi; tufegi tum eksenlerde biraz daha cevirip 'm' ile tekrar deneyin.");
  }
}

// Hareketsizlik Kontrollü Jiroskop & Poz Kalibrasyonu:
// Tüfek açılışta elde hareketliyse bozuk bias almaz; Flash'taki temiz masa kalibrasyonunu kullanır!
void calibrateGyroAndPose(bool saveToFlashIfStill) {
  Serial.println("Jiroskop ve yercekimi kalibre ediliyor (2 sn sabit tutun)...");
  double sumGx = 0, sumGy = 0, sumGz = 0;
  double sumAx = 0, sumAy = 0, sumAz = 0;
  double sumTemp = 0;
  double sumMx = 0, sumMy = 0, sumMz = 0;
  int validImu = 0;
  int validMag = 0;

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
    if (nineDofEnabled && hasMagnetometer && (i % 5 == 0)) {
      float mx, my, mz;
      if (readAlignedMagUt(mx, my, mz)) {
        sumMx += (mx - magOffX) * magScaleX;
        sumMy += (my - magOffY) * magScaleY;
        sumMz += (mz - magOffZ) * magScaleZ;
        validMag++;
      }
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

  float spreadX = (float)(maxGx - minGx) * GYRO_DEG_SCALE;
  float spreadY = (float)(maxGy - minGy) * GYRO_DEG_SCALE;
  float spreadZ = (float)(maxGz - minGz) * GYRO_DEG_SCALE;
  float maxGyroSpreadDegS = fmaxf(spreadX, fmaxf(spreadY, spreadZ));
  float accelSpreadG = maxAccMag - minAccMag;

  bool isCleanStill = (maxGyroSpreadDegS < 2.4f) && (accelSpreadG < 0.065f);

  if (isCleanStill) {
    // Önceden farklı sıcaklıkta kayıtlı temiz masa kalibrasyonu varsa termal eğimi öğren!
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

    if (saveToFlashIfStill) {
      saveGyroCalToFlash();
    }
    Serial.printf("[OK] Temiz sabit kalibrasyon tamam! (Temp:%.1fC | Salinim:%.2f deg/s | Flash Kayitli)\n",
                  measTempC, maxGyroSpreadDegS);
  } else if (hasFlashGyroCal) {
    // Tüfek açılışta elde kıpırdıyordu -> bozuk ofset yerine Flash'taki temiz masa kalibrasyonunu koru!
    gxOffset = baseGxOffset;
    gyOffset = baseGyOffset;
    gzOffset = baseGzOffset;
    Serial.printf("[KORUMA] Acilista hareket algilandi (%.1f deg/s) -> Flash'taki temiz kalibrasyon kullanildi (Temp:%.1fC)!\n",
                  maxGyroSpreadDegS, measTempC);
  } else {
    // Henüz hiç Flash kaydı yoksa mevcut ölçümü geçici taban al, ama Flash'a kaydetme
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

  float avgAx = (float)(sumAx / validImu) * ACCEL_SCALE;
  float avgAy = (float)(sumAy / validImu) * ACCEL_SCALE;
  float avgAz = (float)(sumAz / validImu) * ACCEL_SCALE;
  float initAccMag = sqrtf(avgAx * avgAx + avgAy * avgAy + avgAz * avgAz);
  smoothAccMag = (initAccMag > 0.85f && initAccMag < 1.15f) ? initAccMag : 1.0f;

  bool initWithMag = nineDofEnabled && hasMagnetometer && hasMagCal && validMag > 0;
  float avgMx = initWithMag ? (float)(sumMx / validMag) : 0.0f;
  float avgMy = initWithMag ? (float)(sumMy / validMag) : 0.0f;
  float avgMz = initWithMag ? (float)(sumMz / validMag) : 0.0f;

  initQuatFromAccelAndMag(avgAx, avgAy, avgAz, avgMx, avgMy, avgMz, initWithMag);
  bootMillis = millis();
}

// 360° Jiroskop Ölçek Faktörü Kalibrasyonu:
// MPU-9250/6500 çiplerindeki +-3%..8% fabrika ölçek hatasını ölçüp Flash'a kaydeder!
void calibrateGyroScale360() {
  Serial.println("=================================================================");
  Serial.println("[360 DERECE JIROSKOP OLCEK KALIBRASYONU]");
  Serial.println("1) Tufegi duz bir referans cizgisine (masa kenari) koyun.");
  Serial.println("2) Ilk 1.5 sn sabit tutun -> hazir mesaji gelince yatayda TAM 1 TUR (360 derece) cevirin.");
  Serial.println("3) Tam ayni cizgiye gelince 1.5 sn sabit tutun (iptal icin 'x').");
  Serial.println("=================================================================");

  // 1) 1.5 sn başlangıç sabitliği ve anlık sıfır noktası ölçümü
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

  // PCB tüfek üzerinde açılı monte edilmişse (ör. >7° eğim), 360° yatay dönüş 2 eksene bölünür;
  // tek ekseni 360/domVal ile ölçeklemek yanlış çarpan üretir. Toplam 3D vektör açısını raporla!
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

  const char* axisName = "Y";
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

bool initAK8963() {
  writeI2CReg(mpuAddr, 0x6A, 0x00); // USER_CTRL: I2C Master kapat
  writeI2CReg(mpuAddr, 0x37, 0x02); // INT_PIN_CFG: BYPASS_EN = 1
  delay(10);

  uint8_t wia = readI2CReg(AK8963_ADDR, AK8963_WIA_REG);
  if (wia != 0x48) {
    return false;
  }

  writeI2CReg(AK8963_ADDR, AK8963_CNTL2_REG, 0x01); // Soft Reset
  delay(20);
  writeI2CReg(AK8963_ADDR, AK8963_CNTL1_REG, 0x00); // Power down
  delay(10);
  writeI2CReg(AK8963_ADDR, AK8963_CNTL1_REG, 0x0F); // Fuse ROM erişim modu
  delay(10);

  Wire.beginTransmission(AK8963_ADDR);
  Wire.write(AK8963_ASAX_REG);
  if (Wire.endTransmission(false) == 0 && Wire.requestFrom((uint16_t)AK8963_ADDR, (uint8_t)3, true) == 3) {
    uint8_t asaX = Wire.read();
    uint8_t asaY = Wire.read();
    uint8_t asaZ = Wire.read();
    magAsaX = ((asaX - 128) * 0.5f / 128.0f) + 1.0f;
    magAsaY = ((asaY - 128) * 0.5f / 128.0f) + 1.0f;
    magAsaZ = ((asaZ - 128) * 0.5f / 128.0f) + 1.0f;
  }

  writeI2CReg(AK8963_ADDR, AK8963_CNTL1_REG, 0x00); // Power down
  delay(10);
  writeI2CReg(AK8963_ADDR, AK8963_CNTL1_REG, 0x16); // 16-bit + 100 Hz sürekli ölçüm (Mode 2)
  delay(10);
  return true;
}

void configureMpuRegisters(bool doSoftReset) {
  if (doSoftReset) {
    writeI2CReg(mpuAddr, 0x6B, 0x80); // Cihaz sıfırlama
    vTaskDelay(msToTicksSafe(50));
  }
  writeI2CReg(mpuAddr, 0x6B, 0x01); // PLL saat kaynağı (X Gyro)
  vTaskDelay(msToTicksSafe(15));

  writeI2CReg(mpuAddr, 0x19, 0x00); // 1 kHz dahili örnekleme
  writeI2CReg(mpuAddr, 0x1A, 0x02); // Gyro DLPF = 92 Hz (~3.9 ms gecikme)
  writeI2CReg(mpuAddr, 0x1B, 0x10); // Jiroskop: +-1000 deg/s
  writeI2CReg(mpuAddr, 0x1C, 0x08); // İvmeölçer: +-4g
  writeI2CReg(mpuAddr, 0x1D, 0x03); // İvmeölçer DLPF = 41 Hz
}

void initSensors() {
  Wire.begin(I2C_SDA_PIN, I2C_SCL_PIN);
  Wire.setClock(I2C_CLOCK_HZ);
  Wire.setTimeOut(10);

  Wire.beginTransmission(0x68);
  if (Wire.endTransmission() == 0) {
    mpuAddr = 0x68;
  } else {
    Wire.beginTransmission(0x69);
    if (Wire.endTransmission() == 0) {
      mpuAddr = 0x69;
    } else {
      Serial.println("[HATA] MPU-9250/6500 bulunamadi! I2C kablolarini kontrol edin.");
      return;
    }
  }

  configureMpuRegisters(true);

  uint8_t whoAmI = readI2CReg(mpuAddr, 0x75);
  hasMagnetometer = initAK8963();
  loadGyroCalFromFlash();
  loadMagCalFromFlash();

  Serial.println("============================================================");
  Serial.printf("Sensor Adresi : 0x%02X | WHO_AM_I: 0x%02X\n", mpuAddr, whoAmI);
  Serial.printf("Donanim       : %s\n", hasMagnetometer ? "MPU-9250 (AK8963 Algilandi)" : "MPU-6500");
  Serial.println("Mimari        : Dual-Core (Core 1: 1000Hz Bortz-Exp IMU | Core 0: Wi-Fi/UDP)");
  Serial.printf("Flash Olcek   : X:%.4f  Y:%.4f  Z:%.4f\n", gyroScaleX, gyroScaleY, gyroScaleZ);
  if (hasFlashGyroCal) {
    Serial.printf("Flash Termal  : Ref:%.1fC | Egim(LSB/C): %.2f, %.2f, %.2f\n",
                  biasRefTempC, thermalKx, thermalKy, thermalKz);
  }
  Serial.println("============================================================");

  calibrateGyroAndPose(true);
}

// Koning Telafili Exact Lie-Group Quaternion Exponential + Dinamik İvme Korumalı AHRS
void updateAHRSExact(
    float gx, float gy, float gz,
    float ax, float ay, float az,
    float mx, float my, float mz,
    bool useMagYaw,
    float dt,
    float beta) {

  if (useMagYaw) {
    float magNorm = sqrtf(mx * mx + my * my + mz * mz);
    bool fieldOk = (magNorm > 12.0f) && (magNorm < 115.0f)
                && (fabsf(magNorm - magRefFieldUt) <= magRefFieldUt * 0.55f);
    magAnomalyActive = !fieldOk;

    if (fieldOk) {
      float invM = 1.0f / magNorm;
      float hx, hy, hz;
      rotateVecByQuat(q0, q1, q2, q3, mx * invM, my * invM, mz * invM, hx, hy, hz);
      float hLen = hypotf(hx, hy);
      if (hLen > 0.18f) {
        float yawErr = atan2f(-hy, hx);
        yawBiasEarthZ += KI_MAG_YAW * yawErr * dt;
        yawBiasEarthZ = fmaxf(-0.08f, fminf(0.08f, yawBiasEarthZ));

        float earthYawCorr = KP_MAG_YAW * yawErr + yawBiasEarthZ;
        float corrX, corrY, corrZ;
        rotateVecByQuat(q0, -q1, -q2, -q3, 0.0f, 0.0f, earthYawCorr, corrX, corrY, corrZ);
        gx += corrX;
        gy += corrY;
        gz += corrZ;
      }
    }
  } else {
    magAnomalyActive = false;
  }

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

// --- CORE 1 YÜKSEK ÖNCELİKLİ 1000 Hz IMU GÖREVİ (SIFIR WI-FI / UDP / SERIAL BLOKAJI) ---
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
  uint32_t lastMagUs = lastImuUs;
  uint32_t maxDtUsLocal = 0;
  uint32_t samplesLocal = 0;
  uint32_t satLocal = 0;
  uint32_t i2cErrLocal = 0;
  uint32_t pwrResetLocal = 0;
  uint32_t consecI2cErr = 0;
  uint32_t healthCheckTick = 0;
  uint32_t lastStatResetMs = millis();

  for (;;) {
    // 1) Seri porttan istenen kalibrasyon komutlarını güvenli şekilde Core 1'de yürüt (I2C çakışması sıfır)
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
      } else if (req == CAL_MAG) {
        nineDofEnabled = true;
        calibrateMagnetometer();
      } else if (req == CAL_CLEAR_ALL) {
        clearAllCalFlash();
      }

      portENTER_CRITICAL(&stateMux);
      sharedState.calBusy = false;
      portEXIT_CRITICAL(&stateMux);

      lastImuUs = micros();
      lastMagUs = lastImuUs;
#if (configTICK_RATE_HZ >= 1000)
      lastWakeTick = xTaskGetTickCount();
#endif
      continue;
    }

    // 2) Deterministik 1000 Hz (1 ms) bekleme -> Idle1 asla aç kalmaz, WDT güvenli!
#if (configTICK_RATE_HZ >= 1000)
    vTaskDelayUntil(&lastWakeTick, 1);
#else
    ulTaskNotifyTake(pdTRUE, pdMS_TO_TICKS(5));
#endif

    uint32_t nowUs = micros();

    // 3) İsteğe bağlı 9-DOF açıksa 100 Hz AK8963 oku
    if (nineDofEnabled && hasMagnetometer && (nowUs - lastMagUs >= MAG_INTERVAL_US)) {
      lastMagUs = nowUs;
      float rMx, rMy, rMz;
      if (readAlignedMagUt(rMx, rMy, rMz)) {
        calMx = (rMx - magOffX) * magScaleX;
        calMy = (rMy - magOffY) * magScaleY;
        calMz = (rMz - magOffZ) * magScaleZ;
        magSampleValid = true;
      }
    }

    // 4) 1000 Hz MPU-9250 Okuma + Gevşek Kablo (I2C/Brownout) Koruması + 2 Kademeli ZUPT + Bortz Exact AHRS
    if (readRawMPU()) {
      bool justRecoveredI2c = (consecI2cErr > 0);
      consecI2cErr = 0;

      // Gevşek VCC/GND jumper temassızlığında MPU-9250 uyku veya +-250 deg/s moduna dönerse otomatik kurtar!
      healthCheckTick++;
      if (justRecoveredI2c || healthCheckTick >= 2000) {
        healthCheckTick = 0;
        uint8_t gyroCfg = 0, pwrMgmt = 0;
        if (readI2CRegSafe(mpuAddr, 0x1B, gyroCfg) && readI2CRegSafe(mpuAddr, 0x6B, pwrMgmt)) {
          if (gyroCfg != 0x10 || (pwrMgmt & 0x40) != 0) {
            configureMpuRegisters(false);
            hasPrevGyro = false;
            pwrResetLocal++;
            Serial.println("[UYARI] MPU-9250 guc kesintisi (gevsek VCC/GND kablosu) algilandi -> +-1000 deg/s yeniden kuruldu!");
            lastImuUs = micros();
            continue;
          }
        }
      }

      uint32_t dtUs = nowUs - lastImuUs;
      lastImuUs = nowUs;

      // Tekil 1-2 ms I2C takılmasında açı kaybını önlemek için gerçek dt kullanılır; uzun kopmada (>25 ms) taşma korunur
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
      smoothAccMag += (accMag - smoothAccMag) * 0.04f; // 1000 Hz uyumlu (~6 Hz)
      float accDelta = fabsf(accMag - smoothAccMag);
      float accErr1g = fabsf(accMag - 1.0f);

      // Sıcaklık telafili anlık efektif ofset
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

      // El titremesini süzüp yalnız sabit DC termal kaymayı gösteren ~0.35 Hz LPF
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
          // Artık hatayı tRef tabanlı gxOffset/gyOffset/gzOffset'e işle
          float resX = (float)rawGx - effOffX;
          float resY = (float)rawGy - effOffY;
          float resZ = (float)rawGz - effOffZ;
          gxOffset = fmaxf(baseGxOffset - maxRawSpan, fminf(baseGxOffset + maxRawSpan, gxOffset + resX * DESK_LEARN_ALPHA));
          gyOffset = fmaxf(baseGyOffset - maxRawSpan, fminf(baseGyOffset + maxRawSpan, gyOffset + resY * DESK_LEARN_ALPHA));
          gzOffset = fmaxf(baseGzOffset - maxRawSpan, fminf(baseGzOffset + maxRawSpan, gzOffset + resZ * DESK_LEARN_ALPHA));

          zuptAnchorGx += ((float)rawGx - zuptAnchorGx) * 0.005f;
          zuptAnchorGy += ((float)rawGy - zuptAnchorGy) * 0.005f;
          zuptAnchorGz += ((float)rawGz - zuptAnchorGz) * 0.005f;

          // Tüfek masada 4 sn+ tamamen hareketsiz ve sıcaklık >=3C değiştiyse termal eğimi öğren & kaydet (en sık 2 dk'da bir)
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

      // Güncel telafili ofset + 360° kalibre ölçek çarpanı ile gerçek açısal hız
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

      // Masa kilidinde kalan mikro-gürültüyü tamamen sıfırla
      if (activeZuptTier == 2 && gyroMag < (0.55f * DEG_TO_RAD_F)) {
        gx = gy = gz = 0.0f;
        gxDeg = gyDeg = gzDeg = 0.0f;
        gyroMag = 0.0f;
      }

      // Hareket/Merkezkaç kapısı: hızlı sallarken beta = 0.0f -> yerçekimi vektörü bozulmaz, Yaw'a hata sızmaz!
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

      bool useMagNow = nineDofEnabled && hasMagnetometer && hasMagCal && magSampleValid;
      updateAHRSExact(gx, gy, gz, ax, ay, az, calMx, calMy, calMz, useMagNow, dt, activeBeta);

      // Paylaşımlı anlık görüntüyü Core 0 için atomik güncelle (< 1 us)
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
      sharedState.magAnomaly = magAnomalyActive;
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

// --- CORE 0 AĞ, UDP VE SERİ PORT GÖREVİ ---
void sendUDPPacket(bool forceTriggerHigh, const SharedImuSnapshot& snap) {
  if (WiFi.status() != WL_CONNECTED) return;

  packet.header        = 0xAA;
  packet.qw            = snap.qw;
  packet.qx            = snap.qx;
  packet.qy            = snap.qy;
  packet.qz            = snap.qz;
  packet.triggerState  = forceTriggerHigh ? 1 : ((digitalRead(TRIGGER_PIN) == LOW) ? 1 : 0);
  packet.shotCount     = interruptCounter;
  packet.shoulderState = shoulderState;

  udp.beginPacket(targetIp, UDP_PORT);
  udp.write((const uint8_t*)&packet, sizeof(packet));
  udp.endPacket();
}

void connectBestWifi() {
  WiFi.mode(WIFI_STA);
  WiFi.disconnect(true);
  vTaskDelay(pdMS_TO_TICKS(80));
  WiFi.setAutoReconnect(true);

  bool hotspotConfigured = (strcmp(HOTSPOT_SSID, "TELEFON_HOTSPOT_ADI") != 0) && (strlen(HOTSPOT_SSID) > 0);
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

  Serial.printf("Baglaniliyor -> %s (%s)\n", chosenSsid, useHotspot ? "Telefon Hotspot" : "Ev Wi-Fi / Yayin");
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
    IPAddress bcast = WiFi.broadcastIP();
    targetIp = (bcast != IPAddress(0, 0, 0, 0)) ? bcast : HOME_PC_IP;
  }

  Serial.println("\n[OK] Wi-Fi baglandi! ESP32 IP: " + WiFi.localIP().toString()
                 + " -> Hedef UDP IP: " + targetIp.toString() + ":" + String(UDP_PORT));
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
    } else if (cmd == '6') {
      nineDofEnabled = false;
      Serial.println("[MOD] 6-DOF + 2 Kademeli Akilli ZUPT aktif (Pusula kapali).");
    } else if (cmd == '9') {
      if (hasMagnetometer) {
        nineDofEnabled = true;
        Serial.printf("[MOD] 9-DOF acildi (%s).\n", hasMagCal ? "Flash kalibresi hazir" : "Kalibre etmek icin 'm' gonderin");
      } else {
        Serial.println("[UYARI] Bu modulde manyetometre yok (MPU-6500).");
      }
    } else if (cmd == 'm' || cmd == 'M') {
      cancelCalFlag = false;
      pendingCal = CAL_MAG;
    } else if (cmd == 'c' || cmd == 'C') {
      pendingCal = CAL_CLEAR_ALL;
    }
  }
}

void netTaskFn(void* param) {
  (void)param;

  connectBestWifi();
  Serial.println("[KOMUTLAR] 'g': Sabit Masa Kalibresi | 's': 360 Derece Olcek Kalibresi | 'r': Olcek Sifirla | '6'/'9': 6/9-DOF | 'm': Pusula | 'c': Flash Temizle");

  uint32_t lastUdpUs = micros();
  uint32_t lastDebugMs = millis();

  for (;;) {
    uint32_t nowUs = micros();

    // 1) Tetik Bırakma Sıçrama Koruması (25 ms kesintisiz HIGH)
    if (!triggerArmed) {
      if (digitalRead(TRIGGER_PIN) == HIGH) {
        if (highStateStartUs == 0) {
          highStateStartUs = nowUs;
        } else if (nowUs - highStateStartUs >= RELEASE_DEBOUNCE_US) {
          triggerArmed = true;
          highStateStartUs = 0;
        }
      } else {
        highStateStartUs = 0;
      }
    }

    // 2) Omuz sensörü sıçrama sonrası durum eşitleme (30 ms debounce sonrası)
    if (nowUs - lastShoulderIrqUs >= SHOULDER_DEBOUNCE_US) {
      uint8_t actualShoulder = (digitalRead(SHOULDER_PIN) == LOW) ? 1 : 0;
      if (actualShoulder != shoulderState) {
        shoulderState = actualShoulder;
        shoulderEventFlag = true;
      }
    }

    // 3) 200 Hz UDP Göndericisi (Acil tetik/omuz olayları anında gönderilir)
    bool timeForRegularUdp  = (nowUs - lastUdpUs >= UDP_INTERVAL_US);
    bool urgentEventPending = (triggerEventFlag || shoulderEventFlag) && (nowUs - lastUdpUs >= MIN_UDP_GAP_US);

    if (urgentEventPending || timeForRegularUdp) {
      bool isTriggerShot = triggerEventFlag;
      triggerEventFlag   = false;
      shoulderEventFlag  = false;
      lastUdpUs          = nowUs;

      SharedImuSnapshot snap;
      portENTER_CRITICAL(&stateMux);
      snap = sharedState;
      portEXIT_CRITICAL(&stateMux);

      sendUDPPacket(isTriggerShot, snap);
    }

    // 4) Seri komutlar ve 1 Hz teşhis çıktısı
    handleSerialCommands();

    uint32_t nowMs = millis();
    if (nowMs - lastDebugMs >= DEBUG_INTERVAL_MS) {
      lastDebugMs = nowMs;

      SharedImuSnapshot snap;
      portENTER_CRITICAL(&stateMux);
      snap = sharedState;
      portEXIT_CRITICAL(&stateMux);

      if (!snap.calBusy) {
        uint8_t liveTrig = (digitalRead(TRIGGER_PIN) == LOW) ? 1 : 0;
        const char* modeTag;
        if (nineDofEnabled) {
          modeTag = (hasMagnetometer && hasMagCal)
              ? (snap.magAnomaly ? "9DOF-MAG_BOZUK" : "9DOF-AKTIF")
              : "9DOF-KALIBSIZ";
        } else {
          modeTag = (snap.zuptTier == 2) ? "6DOF-SABIT"
                  : (snap.zuptTier == 1 ? "6DOF-NISAN" : "6DOF-HAREKET");
        }

        Serial.printf("[%s] %luHz maxDt:%.1fms | Temp:%.1fC | G(%+.2f,%+.2f,%+.2f) | Sc(%.3f,%.3f,%.3f) | T:%u O:%u A:%lu",
                      modeTag,
                      (unsigned long)snap.imuSamplesInSec,
                      snap.maxDtUs * 0.001f,
                      snap.tempC,
                      snap.gxDegS, snap.gyDegS, snap.gzDegS,
                      snap.scaleX, snap.scaleY, snap.scaleZ,
                      liveTrig, shoulderState, (unsigned long)interruptCounter);
        if (snap.satCount > 0) {
          Serial.printf(" | SAT:%lu", (unsigned long)snap.satCount);
        }
        if (snap.i2cErrCount > 0) {
          Serial.printf(" | I2C_ERR:%lu", (unsigned long)snap.i2cErrCount);
        }
        if (snap.pwrResetCount > 0) {
          Serial.printf(" | PWR_RST:%lu", (unsigned long)snap.pwrResetCount);
        }
        Serial.println();
      }
    }

    vTaskDelay(msToTicksSafe(1));
  }
}

void setup() {
  Serial.begin(115200);
  delay(300);

  pinMode(TRIGGER_PIN, INPUT_PULLUP);
  pinMode(SHOULDER_PIN, INPUT_PULLUP);

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

  triggerArmed = (digitalRead(TRIGGER_PIN) == HIGH);
  attachInterrupt(digitalPinToInterrupt(TRIGGER_PIN), onTriggerPress, FALLING);

  shoulderState = (digitalRead(SHOULDER_PIN) == LOW) ? 1 : 0;
  attachInterrupt(digitalPinToInterrupt(SHOULDER_PIN), onShoulderChange, CHANGE);

  // Core 1: İzole 1000 Hz IMU Görevi (Yüksek Öncelik = 6)
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

  // Core 0: Wi-Fi + 200 Hz UDP + Serial Görevi (Öncelik = 2)
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
  // Tüm iş Core 1 (IMU) ve Core 0 (Wi-Fi/UDP) görevlerine bölündü
  vTaskDelay(pdMS_TO_TICKS(1000));
}
