#include <WiFi.h>
#include <WiFiUdp.h>
#include <Wire.h>
#include <esp_wifi.h>
#include <math.h>
#include <string.h>

// --- AĞ AYARLARI (Önce Telefon Hotspot'u arar; bulamazsa Ev Wi-Fi + PC IP'ye bağlanır) ---
const char* HOTSPOT_SSID     = "YOUR_HOTSPOT_SSID";     // Galaxy A26 Hotspot adın
const char* HOTSPOT_PASSWORD = "YOUR_WIFI_PASSWORD";         // Galaxy A26 Hotspot şifren

const char* HOME_WIFI_SSID   = "YOUR_HOME_WIFI_SSID";                     // PC testi için ev modemi
const char* HOME_WIFI_PASS   = "YOUR_WIFI_PASSWORD";
const IPAddress HOME_PC_IP(192, 168, 1, 105);             // PC yerel IP adresi

const uint16_t UDP_PORT      = 4210;
IPAddress targetIp           = HOME_PC_IP;

// --- PİN VE DONANIM TANIMLAMALARI ---
#define I2C_SDA_PIN  8
#define I2C_SCL_PIN  9
#define TRIGGER_PIN  15     // Beyaz kablo (Sinyal) -> GPIO 15, Kırmızı kablo -> GND
#define I2C_CLOCK_HZ 400000 // 400 kHz Fast-Mode I2C

// --- ZAMANLAMA SABİTLERİ (500 Hz IMU / 200 Hz UDP Kesintisiz Akış) ---
const uint32_t IMU_INTERVAL_US      = 2000;  // 500 Hz (2 ms) IMU okuma ve füzyon
const uint32_t UDP_INTERVAL_US      = 5000;  // 200 Hz (5 ms) düzenli UDP akışı
const uint32_t MIN_UDP_GAP_US       = 2000;  // Wi-Fi TX tamponunun taşmasını önleyen min paket aralığı (2 ms)
const uint32_t MIN_SHOT_INTERVAL_US = 35000; // Tetik ark/sıçrama koruması: iki atış arası min 35 ms
const uint32_t RELEASE_DEBOUNCE_US  = 25000; // Tetiği bırakma kararlılık süresi (25 ms)
const uint32_t DEBUG_INTERVAL_MS    = 1000;  // 1 Hz hafif durum çıktısı

// --- SENSÖR ÖLÇEK SABİTLERİ ---
constexpr float DEG_TO_RAD_F   = PI / 180.0f;
constexpr float GYRO_RAD_SCALE = DEG_TO_RAD_F / 32.8f;
constexpr float ACCEL_SCALE    = 1.0f / 8192.0f;

// --- HAREKETSİZLİK VE ADAPTİF MADGWICK SABİTLERİ ---
const float WARMUP_BETA          = 0.80f;                    // İlk 1 sn hızlı yerçekimi kilidi
const float STEADY_BETA          = 0.035f;                   // Normal nişan alırken kararlı ufuk kilidi
const float FAST_MOVE_BETA       = 0.008f;                   // Hızlı savurmada doğrusal ivme sapmasını önler
const float STILL_GYRO_THRESH    = 0.30f * DEG_TO_RAD_F;     // 0.30 deg/s altı tam sabit kabul edilir
const float BIAS_LEARN_ALPHA     = 0.0005f;                  // Sabit dururken canlı gyro sıfırlama hızı
const uint32_t WARMUP_MS         = 1000;

uint8_t mpuAddr = 0x68;
WiFiUDP udp;

struct __attribute__((packed)) RifleQuaternionPacket {
  uint8_t header;        // 0xAA doğrulama baytı
  float qw, qx, qy, qz;  // 3D Dönüş Verisi
  uint8_t triggerState;  // 1 = Basılı, 0 = Serbest
  uint32_t shotCount;    // Toplam tetik basma sayacı
};

RifleQuaternionPacket packet;

volatile bool triggerEventFlag        = false;
volatile bool triggerArmed            = true;
volatile uint32_t interruptCounter    = 0;
volatile uint32_t lastTriggerPressUs  = 0;
uint32_t highStateStartUs             = 0;

float q0 = 1.0f, q1 = 0.0f, q2 = 0.0f, q3 = 0.0f;
float gxOffset = 0.0f, gyOffset = 0.0f, gzOffset = 0.0f;
int16_t rawAx = 0, rawAy = 0, rawAz = 0;
int16_t rawGx = 0, rawGy = 0, rawGz = 0;

uint32_t bootMillis      = 0;
uint32_t lastImuMicros   = 0;
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

void writeI2CReg(uint8_t devAddr, uint8_t reg, uint8_t val) {
  Wire.beginTransmission(devAddr);
  Wire.write(reg);
  Wire.write(val);
  Wire.endTransmission();
}

bool readRawMPU() {
  Wire.beginTransmission(mpuAddr);
  Wire.write(0x3B);
  if (Wire.endTransmission(false) != 0) return false;
  if (Wire.requestFrom((uint16_t)mpuAddr, (uint8_t)14, true) != 14) return false;

  rawAx = (int16_t)((Wire.read() << 8) | Wire.read());
  rawAy = (int16_t)((Wire.read() << 8) | Wire.read());
  rawAz = (int16_t)((Wire.read() << 8) | Wire.read());
  Wire.read(); Wire.read();
  rawGx = (int16_t)((Wire.read() << 8) | Wire.read());
  rawGy = (int16_t)((Wire.read() << 8) | Wire.read());
  rawGz = (int16_t)((Wire.read() << 8) | Wire.read());
  return true;
}

void calibrateGyro() {
  Serial.println("Jiroskop kalibre ediliyor, lutfen tufegi 2 saniye tamamen sabit tutun...");
  long sumX = 0, sumY = 0, sumZ = 0;
  int validSamples = 0;

  for (int i = 0; i < 1000; i++) {
    if (readRawMPU()) {
      sumX += rawGx;
      sumY += rawGy;
      sumZ += rawGz;
      validSamples++;
    }
    delay(2);
  }

  if (validSamples > 0) {
    gxOffset = sumX / (float)validSamples;
    gyOffset = sumY / (float)validSamples;
    gzOffset = sumZ / (float)validSamples;
    Serial.printf("[OK] Jiroskop kalibrasyonu tamamlandi! (%d ornek)\n", validSamples);
  } else {
    Serial.println("[HATA] Jiroskop verisi alinamadi!");
  }
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

  writeI2CReg(mpuAddr, 0x6B, 0x01);
  delay(50);

  writeI2CReg(mpuAddr, 0x19, 0x00); // 1 kHz dahili örnekleme
  writeI2CReg(mpuAddr, 0x1A, 0x02); // Gyro DLPF = 92 Hz (~3.9 ms gecikme)
  writeI2CReg(mpuAddr, 0x1B, 0x10); // Jiroskop: +-1000 deg/s
  writeI2CReg(mpuAddr, 0x1C, 0x08); // İvmeölçer: +-4g
  writeI2CReg(mpuAddr, 0x1D, 0x03); // İvmeölçer DLPF = 41 Hz

  Serial.println("==========================================");
  Serial.printf("Sensor Adresi : 0x%02X\n", mpuAddr);
  Serial.println("Mod           : SAF 6-DOF YUKSEK HIZ (500 Hz IMU, Manyetik Geri-Cekme Yok)");
  Serial.println("==========================================");

  calibrateGyro();
}

void updateMadgwick6DOF(float gx, float gy, float gz, float ax, float ay, float az, float dt, float beta) {
  float recipNorm;
  float s0, s1, s2, s3;
  float qDot1, qDot2, qDot3, qDot4;
  float _2q0, _2q1, _2q2, _2q3, _4q0, _4q1, _4q2, _8q1, _8q2, q0q0, q1q1, q2q2, q3q3;

  qDot1 = 0.5f * (-q1 * gx - q2 * gy - q3 * gz);
  qDot2 = 0.5f * (q0 * gx + q2 * gz - q3 * gy);
  qDot3 = 0.5f * (q0 * gy - q1 * gz + q3 * gx);
  qDot4 = 0.5f * (q0 * gz + q1 * gy - q2 * gx);

  float accNormSq = ax * ax + ay * ay + az * az;
  if (accNormSq > 1e-12f) {
    recipNorm = 1.0f / sqrtf(accNormSq);
    ax *= recipNorm; ay *= recipNorm; az *= recipNorm;

    _2q0 = 2.0f * q0; _2q1 = 2.0f * q1; _2q2 = 2.0f * q2; _2q3 = 2.0f * q3;
    _4q0 = 4.0f * q0; _4q1 = 4.0f * q1; _4q2 = 4.0f * q2;
    _8q1 = 8.0f * q1; _8q2 = 8.0f * q2;
    q0q0 = q0 * q0; q1q1 = q1 * q1; q2q2 = q2 * q2; q3q3 = q3 * q3;

    s0 = _4q0 * q2q2 + _2q2 * ax + _4q0 * q1q1 - _2q1 * ay;
    s1 = _4q1 * q3q3 - _2q3 * ax + 4.0f * q0q0 * q1 - _2q0 * ay - _4q1 + _8q1 * q1q1 + _8q1 * q2q2 + _4q1 * az;
    s2 = 4.0f * q0q0 * q2 + _2q0 * ax + _4q2 * q3q3 - _2q3 * ay - _4q2 + _8q2 * q1q1 + _8q2 * q2q2 + _4q2 * az;
    s3 = 4.0f * q1q1 * q3 - _2q1 * ax + 4.0f * q2q2 * q3 - _2q2 * ay;

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
    recipNorm = 1.0f / sqrtf(qNormSq);
    q0 *= recipNorm;
    q1 *= recipNorm;
    q2 *= recipNorm;
    q3 *= recipNorm;
  }
}

void sendUDPPacket(bool forceTriggerHigh) {
  if (WiFi.status() != WL_CONNECTED) return;

  packet.header       = 0xAA;
  packet.qw           = q0;
  packet.qx           = q1;
  packet.qy           = q2;
  packet.qz           = q3;
  packet.triggerState = forceTriggerHigh ? 1 : ((digitalRead(TRIGGER_PIN) == LOW) ? 1 : 0);
  packet.shotCount    = interruptCounter;

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
    if (hotspotConfigured && ssid == HOTSPOT_SSID)
      hotspotFound = true;
    if (ssid == HOME_WIFI_SSID)
      homeFound = true;
  }
  WiFi.scanDelete();

  bool useHotspot = hotspotFound || (hotspotConfigured && !homeFound);
  const char* chosenSsid = useHotspot ? HOTSPOT_SSID : HOME_WIFI_SSID;
  const char* chosenPass = useHotspot ? HOTSPOT_PASSWORD : HOME_WIFI_PASS;

  Serial.printf("Baglaniliyor -> %s (%s)\n", chosenSsid, useHotspot ? "Telefon Hotspot" : "Ev Wi-Fi / PC");
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
    // Ev Wi-Fi ağında alt ağ yayın adresine (192.168.1.255) göndererek
    // hem PC'nin hem de telefondaki VR uygulamasının aynı anda paket almasını sağlar
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
      calibrateGyro();
    }
  }
}

void setup() {
  Serial.begin(115200);
  delay(300);

  pinMode(TRIGGER_PIN, INPUT_PULLUP);

  initSensors();

  connectBestWifi();
  Serial.println("[KOMUT] 'g': Jiroskop Sifirlama");

  triggerArmed = (digitalRead(TRIGGER_PIN) == HIGH);
  attachInterrupt(digitalPinToInterrupt(TRIGGER_PIN), onTriggerPress, FALLING);

  bootMillis    = millis();
  uint32_t nowUs = micros();
  lastImuMicros = nowUs;
  lastUdpMicros = nowUs;
}

void loop() {
  uint32_t nowUs = micros();

  // 1. ÖNCELİK: 500 Hz (2 ms) IMU Okuma ve 6-DoF Füzyon
  if (nowUs - lastImuMicros >= IMU_INTERVAL_US) {
    float dt = (nowUs - lastImuMicros) * 1e-6f;
    lastImuMicros = nowUs;

    if (dt > 0.02f) dt = 0.002f;

    if (readRawMPU()) {
      float gx = (rawGx - gxOffset) * GYRO_RAD_SCALE;
      float gy = (rawGy - gyOffset) * GYRO_RAD_SCALE;
      float gz = (rawGz - gzOffset) * GYRO_RAD_SCALE;
      float ax = rawAx * ACCEL_SCALE;
      float ay = rawAy * ACCEL_SCALE;
      float az = rawAz * ACCEL_SCALE;

      float accMag = sqrtf(ax * ax + ay * ay + az * az);
      float gyroMag = sqrtf(gx * gx + gy * gy + gz * gz);

      if (gyroMag < STILL_GYRO_THRESH && fabsf(accMag - 1.0f) < 0.04f) {
        gxOffset = (1.0f - BIAS_LEARN_ALPHA) * gxOffset + BIAS_LEARN_ALPHA * rawGx;
        gyOffset = (1.0f - BIAS_LEARN_ALPHA) * gyOffset + BIAS_LEARN_ALPHA * rawGy;
        gzOffset = (1.0f - BIAS_LEARN_ALPHA) * gzOffset + BIAS_LEARN_ALPHA * rawGz;
        gx = 0.0f;
        gy = 0.0f;
        gz = 0.0f;
      }

      float activeBeta;
      if (millis() - bootMillis < WARMUP_MS) {
        activeBeta = WARMUP_BETA;
      } else if (fabsf(accMag - 1.0f) > 0.12f || gyroMag > 1.5f) {
        activeBeta = FAST_MOVE_BETA;
      } else {
        activeBeta = STEADY_BETA;
      }

      updateMadgwick6DOF(gx, gy, gz, ax, ay, az, dt, activeBeta);
    }
  }

  // 2. ÖNCELİK: Tetik Bırakma Sıçrama Koruması (25 ms kesintisiz HIGH)
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

  // 3. ÖNCELİK: Birleştirilmiş UDP Göndericisi
  bool timeForRegularUdp = (nowUs - lastUdpMicros >= UDP_INTERVAL_US);
  bool canSendTriggerNow = triggerEventFlag && (nowUs - lastUdpMicros >= MIN_UDP_GAP_US);

  if (canSendTriggerNow || timeForRegularUdp) {
    bool isTriggerShot = triggerEventFlag;
    triggerEventFlag = false;
    lastUdpMicros = nowUs;
    sendUDPPacket(isTriggerShot);
  }

  // 4. ÖNCELİK: Seri Komutlar ve Hafif Durum Raporu
  handleSerialCommands();

  uint32_t nowMs = millis();
  if (nowMs - lastDebugMillis >= DEBUG_INTERVAL_MS) {
    lastDebugMillis = nowMs;
    uint8_t liveTrig = (digitalRead(TRIGGER_PIN) == LOW) ? 1 : 0;
    Serial.printf("6DOF Quat -> W:%.2f X:%.2f Y:%.2f Z:%.2f | Tetik:%u | Atis:%lu\n",
                  q0, q1, q2, q3, liveTrig, (unsigned long)interruptCounter);
  }
}
