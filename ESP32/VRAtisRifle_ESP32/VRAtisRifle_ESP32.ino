#include <Preferences.h>
#include <WiFi.h>
#include <WiFiUdp.h>
#include <Wire.h>
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

// --- ZAMANLAMA SABİTLERİ (500 Hz Gyro+Accel / 100 Hz Mag / 200 Hz UDP) ---
const uint32_t IMU_INTERVAL_US        = 2000;  // 500 Hz (2 ms)
const uint32_t MAG_INTERVAL_US        = 10000; // 100 Hz (10 ms)
const uint32_t UDP_INTERVAL_US        = 5000;  // 200 Hz (5 ms)
const uint32_t MIN_UDP_GAP_US         = 2000;  // Wi-Fi TX koruması (2 ms)
const uint32_t MIN_SHOT_INTERVAL_US   = 35000; // Tetik ark koruması (35 ms)
const uint32_t RELEASE_DEBOUNCE_US    = 25000; // Tetik bırakma kararlılığı (25 ms)
const uint32_t SHOULDER_DEBOUNCE_US   = 30000; // Omuz sensörü sıçrama koruması (30 ms)
const uint32_t DEBUG_INTERVAL_MS      = 1000;  // 1 Hz durum çıktısı

// --- SENSÖR ÖLÇEK SABİTLERİ ---
constexpr float DEG_TO_RAD_F   = PI / 180.0f;
constexpr float RAD_TO_DEG_F   = 180.0f / PI;
constexpr float GYRO_RAD_SCALE = DEG_TO_RAD_F / 32.8f;       // +-1000 deg/s (FS_SEL=2)
constexpr float ACCEL_SCALE    = 1.0f / 8192.0f;             // +-4g (AFS_SEL=1)
constexpr float MAG_UT_SCALE   = 4912.0f / 32760.0f;         // AK8963 16-bit (~0.15 uT/LSB)

// --- 6-DOF / İSTEĞE BAĞLI 9-DOF FÜZYON VE KORUMALI ZUPT SABİTLERİ ---
const float WARMUP_BETA              = 1.25f;
const float STEADY_BETA              = 0.032f;
const float FAST_MOVE_BETA           = 0.008f;
const float KP_MAG_YAW               = 0.15f;  // Yalnız '9' komutuyla 9-DOF açılırsa yumuşak pusula kazancı
const float KI_MAG_YAW               = 0.02f;
const float ZUPT_ACCEL_DELTA_G       = 0.015f; // İvme değişim toleransı (fabrika 0.95g-1.05g ofsetinden bağımsız)
const float ZUPT_GYRO_DEV_DEG_S      = 0.65f;  // Sabitlik penceresi içi max salınım (elde/masada durmayı yakalar)
const float ZUPT_MAX_DRIFT_DEG_S     = 1.25f;  // Yavaş nişan gezdirmeyi sabitlik sanmasını önleyen hız sınırı
const float ZUPT_MAX_BOOT_SPAN_DEG_S = 3.50f;  // Başlangıç kalibresine göre maksimum canlı ofset kayma sınırı
const int   ZUPT_STILL_SAMPLES_REQ   = 150;    // ~300 ms kesintisiz hareketsizlik -> canlı sıfır noktası kilidi
const float ZUPT_LEARN_ALPHA         = 0.025f; // Hareketsizken termal sapmayı ~0.3 sn içinde sıfırlar
const uint32_t WARMUP_MS             = 2000;

Preferences prefs;
WiFiUDP udp;

uint8_t mpuAddr        = 0x68;
bool nineDofEnabled    = false; // Varsayılan: 6-DOF + Korumalı ZUPT (harekette manyetik bozulma yapmaz)
bool hasMagnetometer   = false;
bool hasMagCal         = false;
bool magAnomalyActive  = false;

// Unity'ye giden 23 byte'lık UDP paketi
struct __attribute__((packed)) RifleQuaternionPacket {
  uint8_t header;         // 0xAA doğrulama baytı
  float qw, qx, qy, qz;   // 3D Dönüş Verisi (Mevcut sağ-sol / yukarı-aşağı / roll eksenleriyle %100 uyumlu)
  uint8_t triggerState;   // 1 = Tetik Basılı, 0 = Serbest
  uint32_t shotCount;     // Toplam tetik basma sayacı
  uint8_t shoulderState;  // 1 = Omuzlandı (Red Dot Açık), 0 = Serbest (Red Dot Kapalı)
};

RifleQuaternionPacket packet;

// Tetik & Omuz Kesme Değişkenleri
volatile bool triggerEventFlag        = false;
volatile bool triggerArmed            = true;
volatile uint32_t interruptCounter    = 0;
volatile uint32_t lastTriggerPressUs  = 0;
uint32_t highStateStartUs             = 0;

volatile bool shoulderEventFlag       = false;
volatile uint8_t shoulderState        = 0;
volatile uint32_t lastShoulderIrqUs   = 0;

// Ham sensör verileri
int16_t rawAx = 0, rawAy = 0, rawAz = 0;
int16_t rawGx = 0, rawGy = 0, rawGz = 0;
int16_t rawMx = 0, rawMy = 0, rawMz = 0;

// Kalibrasyon ve Korumalı ZUPT durumları
float bootGxOffset = 0.0f, bootGyOffset = 0.0f, bootGzOffset = 0.0f;
float gxOffset = 0.0f, gyOffset = 0.0f, gzOffset = 0.0f;
float zuptAnchorGx = 0.0f, zuptAnchorGy = 0.0f, zuptAnchorGz = 0.0f;
float smoothAccMag = 1.0f;
int zuptStillCount = 0;

// Manyetometre fabrika ASA çarpanı + Hard/Soft-Iron kalibrasyonu (MPU gövde eksenlerinde)
float magAsaX = 1.0f, magAsaY = 1.0f, magAsaZ = 1.0f;
float magOffX = 0.0f, magOffY = 0.0f, magOffZ = 0.0f;
float magScaleX = 1.0f, magScaleY = 1.0f, magScaleZ = 1.0f;
float magRefFieldUt = 45.0f;

// Arka planda otomatik 3D pusula küre yakalama (ilk kullanımda bile 360° çevirince otomatik öğrenir)
float autoMinX = 1e6f, autoMinY = 1e6f, autoMinZ = 1e6f;
float autoMaxX = -1e6f, autoMaxY = -1e6f, autoMaxZ = -1e6f;

// Güncel kalibreli manyetometre değeri (uT, MPU gyro/accel eksenlerine hizalı)
float calMx = 0.0f, calMy = 0.0f, calMz = 0.0f;
bool magSampleValid = false;

// Kuaterniyon & 9-DOF Yaw integral durumu
float q0 = 1.0f, q1 = 0.0f, q2 = 0.0f, q3 = 0.0f;
float yawBiasEarthZ = 0.0f;

uint32_t bootMillis      = 0;
uint32_t lastImuMicros   = 0;
uint32_t lastMagMicros   = 0;
uint32_t lastUdpMicros   = 0;
uint32_t lastDebugMillis = 0;

void IRAM_ATTR onTriggerPress() {
  uint32_t now = micros();
  if (triggerArmed && (now - lastTriggerPressUs >= MIN_SHOT_INTERVAL_US)) {
    triggerArmed = false;
    lastTriggerPressUs = now;
    interruptCounter++;
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

void writeI2CReg(uint8_t devAddr, uint8_t reg, uint8_t val) {
  Wire.beginTransmission(devAddr);
  Wire.write(reg);
  Wire.write(val);
  Wire.endTransmission();
}

uint8_t readI2CReg(uint8_t devAddr, uint8_t reg) {
  Wire.beginTransmission(devAddr);
  Wire.write(reg);
  if (Wire.endTransmission(false) != 0) return 0;
  if (Wire.requestFrom((uint16_t)devAddr, (uint8_t)1, true) != 1) return 0;
  return Wire.read();
}

bool readRawMPU() {
  Wire.beginTransmission(mpuAddr);
  Wire.write(0x3B);
  if (Wire.endTransmission(false) != 0) return false;
  if (Wire.requestFrom((uint16_t)mpuAddr, (uint8_t)14, true) != 14) return false;

  rawAx = (int16_t)((Wire.read() << 8) | Wire.read());
  rawAy = (int16_t)((Wire.read() << 8) | Wire.read());
  rawAz = (int16_t)((Wire.read() << 8) | Wire.read());
  Wire.read(); Wire.read(); // Sıcaklık baytlarını atla
  rawGx = (int16_t)((Wire.read() << 8) | Wire.read());
  rawGy = (int16_t)((Wire.read() << 8) | Wire.read());
  rawGz = (int16_t)((Wire.read() << 8) | Wire.read());
  return true;
}

// AK8963 okur ve ham değerleri InvenSense MPU-9250 datasheet'ine göre
// MPU ivme/jiroskop eksenlerine (mx_mpu = mag_y, my_mpu = mag_x, mz_mpu = -mag_z) hizalar.
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
  uint8_t st2 = Wire.read(); // ST2 okunmadan AK8963 sonraki ölçüme geçmez

  if (st2 & 0x08) return false; // Manyetik taşma (HOFL) varsa bu kareyi atla

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
      // Sol taraftan Earth +Z yatay dönüşü: q = qz * q
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

void clearMagCalFlash() {
  if (prefs.begin("vratis_imu", false)) {
    prefs.clear();
    prefs.end();
  }
  hasMagCal = false;
  magOffX = magOffY = magOffZ = 0.0f;
  magScaleX = magScaleY = magScaleZ = 1.0f;
  autoMinX = autoMinY = autoMinZ = 1e6f;
  autoMaxX = autoMaxY = autoMaxZ = -1e6f;
  Serial.println("[BILGI] Pusula kalibrasyonu temizlendi (tufegi her yone cevirince yeniden ogrenecek).");
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

  while (millis() - startMs < 10000) {
    float mx, my, mz;
    if (readAlignedMagUt(mx, my, mz)) {
      if (fabsf(mx) < 600.0f && fabsf(my) < 600.0f && fabsf(mz) < 600.0f) {
        if (mx < minX) minX = mx; if (mx > maxX) maxX = mx;
        if (my < minY) minY = my; if (my > maxY) maxY = my;
        if (mz < minZ) minZ = mz; if (mz > maxZ) maxZ = mz;
      }
    }
    delay(10);
  }

  if (!finalizeMagSphere(minX, maxX, minY, maxY, minZ, maxZ)) {
    Serial.println("[HATA] Yeterli donus alinamadi; tufegi tum eksenlerde biraz daha cevirip 'm' ile tekrar deneyin.");
  }
}

void calibrateGyroAndPose() {
  Serial.println("Jiroskop ve yercekimi kalibre ediliyor (2 sn sabit tutun)...");
  double sumGx = 0, sumGy = 0, sumGz = 0;
  double sumAx = 0, sumAy = 0, sumAz = 0;
  double sumMx = 0, sumMy = 0, sumMz = 0;
  int validImu = 0;
  int validMag = 0;

  for (int i = 0; i < 1000; i++) {
    if (readRawMPU()) {
      sumGx += rawGx;
      sumGy += rawGy;
      sumGz += rawGz;
      sumAx += rawAx;
      sumAy += rawAy;
      sumAz += rawAz;
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
    delay(2);
  }

  if (validImu > 0) {
    bootGxOffset = gxOffset = (float)(sumGx / validImu);
    bootGyOffset = gyOffset = (float)(sumGy / validImu);
    bootGzOffset = gzOffset = (float)(sumGz / validImu);
    zuptAnchorGx = gxOffset;
    zuptAnchorGy = gyOffset;
    zuptAnchorGz = gzOffset;
    zuptStillCount = 0;

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
    Serial.printf("[OK] Baslangic kalibrasyonu tamam (%d ornek, %s)\n",
                  validImu, initWithMag ? "9-DOF Aktif" : "6-DOF + Korumali ZUPT Aktif");
  } else {
    Serial.println("[HATA] IMU verisi okunamadi!");
  }
}

bool initAK8963() {
  // I2C Bypass aç (ESP32 doğrudan 0x0C adresindeki AK8963'e erişsin)
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
  writeI2CReg(AK8963_ADDR, AK8963_CNTL1_REG, 0x16); // 16-bit çözünürlük + 100 Hz sürekli ölçüm (Mode 2)
  delay(10);
  return true;
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

  writeI2CReg(mpuAddr, 0x6B, 0x80); // Cihaz sıfırlama
  delay(50);
  writeI2CReg(mpuAddr, 0x6B, 0x01); // PLL saat kaynağı (X Gyro)
  delay(25);

  writeI2CReg(mpuAddr, 0x19, 0x00); // 1 kHz dahili örnekleme
  writeI2CReg(mpuAddr, 0x1A, 0x02); // Gyro DLPF = 92 Hz (~3.9 ms gecikme)
  writeI2CReg(mpuAddr, 0x1B, 0x10); // Jiroskop: +-1000 deg/s
  writeI2CReg(mpuAddr, 0x1C, 0x08); // İvmeölçer: +-4g
  writeI2CReg(mpuAddr, 0x1D, 0x03); // İvmeölçer DLPF = 41 Hz

  uint8_t whoAmI = readI2CReg(mpuAddr, 0x75);
  hasMagnetometer = initAK8963();
  loadMagCalFromFlash();

  Serial.println("==========================================");
  Serial.printf("Sensor Adresi : 0x%02X | WHO_AM_I: 0x%02X\n", mpuAddr, whoAmI);
  Serial.printf("Donanim       : %s\n", hasMagnetometer ? "MPU-9250 (AK8963 Algilandi)" : "MPU-6500");
  Serial.println("Aktif Mod     : 6-DOF + Korumali ZUPT (Varsayilan, manyetik bozulma kapali)");
  if (hasMagnetometer && hasMagCal) {
    Serial.println("Pusula Kaydi  : Flash'ta Hazir ('9' komutuyla istenirse acilabilir)");
  }
  Serial.println("==========================================");

  calibrateGyroAndPose();
}

// Ayrıştırılmış 9-DOF Füzyon:
// - İvmeölçer + Jiroskop: Yalnız yerçekimiyle Pitch & Roll'u kilitler (oda metallerinin dikey eğimi bozmasını önler).
// - AK8963 Manyetometre: Yalnız Dünya Yatay +Z ekseninde (Yaw) pusula düzeltmesi uygular ve kaymayı sıfırlar.
void updateAHRS9DOF(
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
        yawBiasEarthZ = fmaxf(-0.08f, fminf(0.08f, yawBiasEarthZ)); // Max +-4.5 deg/s anti-windup

        float earthYawCorr = KP_MAG_YAW * yawErr + yawBiasEarthZ;
        // Dünya +Z düzeltmesini q_inv ile sensör gövdesine dönüştür -> Pitch & Roll sıfır etkilenir!
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

  float qDot1 = 0.5f * (-q1 * gx - q2 * gy - q3 * gz);
  float qDot2 = 0.5f * (q0 * gx + q2 * gz - q3 * gy);
  float qDot3 = 0.5f * (q0 * gy - q1 * gz + q3 * gx);
  float qDot4 = 0.5f * (q0 * gz + q1 * gy - q2 * gx);

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
      qDot1 -= beta * s0 * recipNorm;
      qDot2 -= beta * s1 * recipNorm;
      qDot3 -= beta * s2 * recipNorm;
      qDot4 -= beta * s3 * recipNorm;
    }
  }

  q0 += qDot1 * dt;
  q1 += qDot2 * dt;
  q2 += qDot3 * dt;
  q3 += qDot4 * dt;

  float qNormSq = q0 * q0 + q1 * q1 + q2 * q2 + q3 * q3;
  if (qNormSq > 1e-12f) {
    float recipNorm = 1.0f / sqrtf(qNormSq);
    q0 *= recipNorm;
    q1 *= recipNorm;
    q2 *= recipNorm;
    q3 *= recipNorm;
  }
}

void sendUDPPacket(bool forceTriggerHigh) {
  if (WiFi.status() != WL_CONNECTED) return;

  packet.header        = 0xAA;
  packet.qw            = q0;
  packet.qx            = q1;
  packet.qy            = q2;
  packet.qz            = q3;
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
  delay(80);
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
    delay(200);
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
    if (cmd == 'g' || cmd == 'G') {
      calibrateGyroAndPose();
    } else if (cmd == '6') {
      nineDofEnabled = false;
      yawBiasEarthZ = 0.0f;
      magAnomalyActive = false;
      Serial.println("[MOD] 6-DOF + Korumali ZUPT aktif (Pusula kapali).");
    } else if (cmd == '9') {
      if (hasMagnetometer) {
        nineDofEnabled = true;
        Serial.printf("[MOD] 9-DOF acildi (%s).\n", hasMagCal ? "Flash kalibresi hazir" : "Kalibre etmek icin 'm' gonderin");
      } else {
        Serial.println("[UYARI] Bu modulde manyetometre yok (MPU-6500).");
      }
    } else if (cmd == 'm' || cmd == 'M') {
      nineDofEnabled = true;
      calibrateMagnetometer();
    } else if (cmd == 'c' || cmd == 'C') {
      clearMagCalFlash();
    }
  }
}

void setup() {
  Serial.begin(115200);
  delay(300);

  pinMode(TRIGGER_PIN, INPUT_PULLUP);
  pinMode(SHOULDER_PIN, INPUT_PULLUP);

  initSensors();

  connectBestWifi();
  Serial.println("[KOMUTLAR] 'g': Jiroskop Sifirla | '6': Saf 6-DOF (Varsayilan) | '9': 9-DOF Ac | 'm': Pusula Kalibre | 'c': Kaydi Sil");

  triggerArmed = (digitalRead(TRIGGER_PIN) == HIGH);
  attachInterrupt(digitalPinToInterrupt(TRIGGER_PIN), onTriggerPress, FALLING);

  shoulderState = (digitalRead(SHOULDER_PIN) == LOW) ? 1 : 0;
  attachInterrupt(digitalPinToInterrupt(SHOULDER_PIN), onShoulderChange, CHANGE);

  bootMillis     = millis();
  uint32_t nowUs = micros();
  lastImuMicros  = nowUs;
  lastMagMicros  = nowUs;
  lastUdpMicros  = nowUs;
}

void loop() {
  uint32_t nowUs = micros();

  // 1. ÖNCELİK: İsteğe bağlı 9-DOF açıksa 100 Hz AK8963 Manyetometre Okuma (otomatik bozuk küre kaydetmez)
  if (nineDofEnabled && hasMagnetometer && (nowUs - lastMagMicros >= MAG_INTERVAL_US)) {
    lastMagMicros = nowUs;
    float rMx, rMy, rMz;
    if (readAlignedMagUt(rMx, rMy, rMz)) {
      calMx = (rMx - magOffX) * magScaleX;
      calMy = (rMy - magOffY) * magScaleY;
      calMz = (rMz - magOffZ) * magScaleZ;
      magSampleValid = true;
    }
  }

  // 2. ÖNCELİK: 500 Hz Gyro+Accel Okuma + Korumalı ZUPT + Madgwick Füzyon
  if (nowUs - lastImuMicros >= IMU_INTERVAL_US) {
    float dt = (nowUs - lastImuMicros) * 1e-6f;
    lastImuMicros = nowUs;
    if (dt > 0.02f) dt = 0.002f;

    if (readRawMPU()) {
      float ax = rawAx * ACCEL_SCALE;
      float ay = rawAy * ACCEL_SCALE;
      float az = rawAz * ACCEL_SCALE;
      float accMag = sqrtf(ax * ax + ay * ay + az * az);
      smoothAccMag += (accMag - smoothAccMag) * 0.08f;
      bool accelStill = (accMag > 0.85f && accMag < 1.15f)
                     && (fabsf(accMag - smoothAccMag) < ZUPT_ACCEL_DELTA_G);

      if (zuptStillCount == 0) {
        zuptAnchorGx = rawGx;
        zuptAnchorGy = rawGy;
        zuptAnchorGz = rawGz;
      }

      // Pencere içi değişim (elde/masada sabitliği yakalar) + güncel ofsete uzaklık (yavaş taramayı korur)
      float devX = (rawGx - zuptAnchorGx) * (1.0f / 32.8f);
      float devY = (rawGy - zuptAnchorGy) * (1.0f / 32.8f);
      float devZ = (rawGz - zuptAnchorGz) * (1.0f / 32.8f);
      float devDegPerSec = sqrtf(devX * devX + devY * devY + devZ * devZ);

      float offX = (rawGx - gxOffset) * (1.0f / 32.8f);
      float offY = (rawGy - gyOffset) * (1.0f / 32.8f);
      float offZ = (rawGz - gzOffset) * (1.0f / 32.8f);
      float rateFromOffsetDegS = sqrtf(offX * offX + offY * offY + offZ * offZ);

      if (accelStill && devDegPerSec < ZUPT_GYRO_DEV_DEG_S && rateFromOffsetDegS < ZUPT_MAX_DRIFT_DEG_S) {
        if (zuptStillCount < ZUPT_STILL_SAMPLES_REQ) {
          zuptStillCount++;
        } else {
          float maxRawSpan = ZUPT_MAX_BOOT_SPAN_DEG_S * 32.8f;
          gxOffset = fmaxf(bootGxOffset - maxRawSpan, fminf(bootGxOffset + maxRawSpan, gxOffset + (rawGx - gxOffset) * ZUPT_LEARN_ALPHA));
          gyOffset = fmaxf(bootGyOffset - maxRawSpan, fminf(bootGyOffset + maxRawSpan, gyOffset + (rawGy - gyOffset) * ZUPT_LEARN_ALPHA));
          gzOffset = fmaxf(bootGzOffset - maxRawSpan, fminf(bootGzOffset + maxRawSpan, gzOffset + (rawGz - gzOffset) * ZUPT_LEARN_ALPHA));
          zuptAnchorGx += (rawGx - zuptAnchorGx) * 0.01f;
          zuptAnchorGy += (rawGy - zuptAnchorGy) * 0.01f;
          zuptAnchorGz += (rawGz - zuptAnchorGz) * 0.01f;
        }
      } else {
        zuptStillCount = 0;
      }

      float gx = (rawGx - gxOffset) * GYRO_RAD_SCALE;
      float gy = (rawGy - gyOffset) * GYRO_RAD_SCALE;
      float gz = (rawGz - gzOffset) * GYRO_RAD_SCALE;
      float gyroMag = sqrtf(gx * gx + gy * gy + gz * gz);

      // Tüfek tamamen sabit dururken kalan mikro-gürültüyü sıfırla
      if (zuptStillCount >= ZUPT_STILL_SAMPLES_REQ && gyroMag < (0.55f * DEG_TO_RAD_F)) {
        gx = 0.0f;
        gy = 0.0f;
        gz = 0.0f;
      }

      float activeBeta;
      if (millis() - bootMillis < WARMUP_MS) {
        activeBeta = WARMUP_BETA;
      } else if (fabsf(accMag - smoothAccMag) > 0.08f || fabsf(accMag - 1.0f) > 0.15f || gyroMag > 1.5f) {
        activeBeta = FAST_MOVE_BETA;
      } else {
        activeBeta = STEADY_BETA;
      }

      bool useMagNow = nineDofEnabled && hasMagnetometer && hasMagCal && magSampleValid;
      updateAHRS9DOF(gx, gy, gz, ax, ay, az, calMx, calMy, calMz, useMagNow, dt, activeBeta);
    }
  }

  // 3. ÖNCELİK: Tetik Bırakma Sıçrama Koruması (25 ms kesintisiz HIGH)
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

  // Omuz sensörü sıçrama sonrası durum eşitleme (30 ms debounce sonrası)
  if (nowUs - lastShoulderIrqUs >= SHOULDER_DEBOUNCE_US) {
    uint8_t actualShoulder = (digitalRead(SHOULDER_PIN) == LOW) ? 1 : 0;
    if (actualShoulder != shoulderState) {
      shoulderState = actualShoulder;
      shoulderEventFlag = true;
    }
  }

  // 4. ÖNCELİK: Birleştirilmiş UDP Göndericisi
  bool timeForRegularUdp  = (nowUs - lastUdpMicros >= UDP_INTERVAL_US);
  bool urgentEventPending = (triggerEventFlag || shoulderEventFlag) && (nowUs - lastUdpMicros >= MIN_UDP_GAP_US);

  if (urgentEventPending || timeForRegularUdp) {
    bool isTriggerShot = triggerEventFlag;
    triggerEventFlag   = false;
    shoulderEventFlag  = false;
    lastUdpMicros      = nowUs;
    sendUDPPacket(isTriggerShot);
  }

  // 5. ÖNCELİK: Seri Komutlar ve Durum Raporu
  handleSerialCommands();

  uint32_t nowMs = millis();
  if (nowMs - lastDebugMillis >= DEBUG_INTERVAL_MS) {
    lastDebugMillis = nowMs;
    uint8_t liveTrig = (digitalRead(TRIGGER_PIN) == LOW) ? 1 : 0;
    bool zuptLocked = (zuptStillCount >= ZUPT_STILL_SAMPLES_REQ);
    const char* modeTag = nineDofEnabled
        ? (hasMagnetometer && hasMagCal ? (magAnomalyActive ? "9DOF-MAG_BOZUK" : "9DOF-AKTIF") : "9DOF-KALIBSIZ")
        : (zuptLocked ? "6DOF-KILITLI" : "6DOF-HAREKET");
    Serial.printf("[%s] Q(%.2f,%.2f,%.2f,%.2f) | T:%u O:%u A:%lu | Zupt:%s\n",
                  modeTag, q0, q1, q2, q3, liveTrig, shoulderState, (unsigned long)interruptCounter,
                  zuptLocked ? "SABIT" : "AKTIF");
  }
}
