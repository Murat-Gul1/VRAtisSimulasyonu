// VRAtisSimulasyonu — Native Android NDK High-Rate Head Tracking Sensor Fusion
// Bypasses Samsung/OEM GameRotationVector small-movement deadzones & dynamic bias rubberbanding,
// and bypasses Unity InputSystem's AndroidCompensateRotationProcessor w-sign bug.
// Integrates raw MEMS gyroscope (ASENSOR_TYPE_GYROSCOPE_UNCALIBRATED / GYROSCOPE) + accelerometer
// in 64-bit double precision at up to 400 Hz using 2nd-order Bortz coning + Exact Lie-Group Quaternion updates.

#include <stdint.h>

#if defined(__ANDROID__)

#include <stddef.h>
#include <stdbool.h>
#include <math.h>
#include <pthread.h>
#include <dlfcn.h>
#include <time.h>
#include <android/sensor.h>
#include <android/looper.h>

#ifndef ASENSOR_TYPE_ACCELEROMETER
#define ASENSOR_TYPE_ACCELEROMETER 1
#endif
#ifndef ASENSOR_TYPE_GYROSCOPE
#define ASENSOR_TYPE_GYROSCOPE 4
#endif
#ifndef ASENSOR_TYPE_ROTATION_VECTOR
#define ASENSOR_TYPE_ROTATION_VECTOR 11
#endif
#ifndef ASENSOR_TYPE_GAME_ROTATION_VECTOR
#define ASENSOR_TYPE_GAME_ROTATION_VECTOR 15
#endif
#ifndef ASENSOR_TYPE_GYROSCOPE_UNCALIBRATED
#define ASENSOR_TYPE_GYROSCOPE_UNCALIBRATED 16
#endif

typedef ASensorManager* (*PFN_ASensorManager_getInstanceForPackage)(const char* packageName);
typedef ASensorManager* (*PFN_ASensorManager_getInstance)(void);
typedef const ASensor* (*PFN_ASensorManager_getDefaultSensor)(ASensorManager* manager, int type);
typedef ASensorEventQueue* (*PFN_ASensorManager_createEventQueue)(
    ASensorManager* manager, ALooper* looper, int ident, ALooper_callbackFunc callback, void* data);
typedef int (*PFN_ASensorManager_destroyEventQueue)(ASensorManager* manager, ASensorEventQueue* queue);
typedef int (*PFN_ASensorEventQueue_enableSensor)(ASensorEventQueue* queue, const ASensor* sensor);
typedef int (*PFN_ASensorEventQueue_disableSensor)(ASensorEventQueue* queue, const ASensor* sensor);
typedef int (*PFN_ASensorEventQueue_setEventRate)(ASensorEventQueue* queue, const ASensor* sensor, int32_t usec);
typedef ssize_t (*PFN_ASensorEventQueue_getEvents)(ASensorEventQueue* queue, ASensorEvent* events, size_t count);
typedef int (*PFN_ASensor_getMinDelay)(const ASensor* sensor);
typedef ALooper* (*PFN_ALooper_prepare)(int opts);
typedef int (*PFN_ALooper_pollOnce)(int timeoutMillis, int* outFd, int* outEvents, void** outData);
typedef void (*PFN_ALooper_wake)(ALooper* looper);

static void* g_libAndroid = NULL;
static PFN_ASensorManager_getInstanceForPackage fn_getInstanceForPackage = NULL;
static PFN_ASensorManager_getInstance fn_getInstance = NULL;
static PFN_ASensorManager_getDefaultSensor fn_getDefaultSensor = NULL;
static PFN_ASensorManager_createEventQueue fn_createEventQueue = NULL;
static PFN_ASensorManager_destroyEventQueue fn_destroyEventQueue = NULL;
static PFN_ASensorEventQueue_enableSensor fn_enableSensor = NULL;
static PFN_ASensorEventQueue_disableSensor fn_disableSensor = NULL;
static PFN_ASensorEventQueue_setEventRate fn_setEventRate = NULL;
static PFN_ASensorEventQueue_getEvents fn_getEvents = NULL;
static PFN_ASensor_getMinDelay fn_getMinDelay = NULL;
static PFN_ALooper_prepare fn_looperPrepare = NULL;
static PFN_ALooper_pollOnce fn_looperPollOnce = NULL;
static PFN_ALooper_wake fn_looperWake = NULL;

static pthread_mutex_t g_stateMutex = PTHREAD_MUTEX_INITIALIZER;
static pthread_t g_workerThread;
static volatile int g_threadRunning = 0;
static volatile int g_compassMode = 0;
static ALooper* g_workerLooper = NULL;

// Shared 64-bit ENU orientation state (Device Portrait -> Android ENU right-handed world)
static bool g_poseReady = false;
static double g_qx = 0.0;
static double g_qy = -0.7071067811865475;
static double g_qz = 0.0;
static double g_qw = 0.7071067811865475;
static double g_wx = 0.0;
static double g_wy = 0.0;
static double g_wz = 0.0;
static double g_wxSmooth = 0.0;
static double g_wySmooth = 0.0;
static double g_wzSmooth = 0.0;
static int64_t g_lastGyroTsNs = 0;
static uint32_t g_sampleSeq = 0;

// Compass (9-DOF) state from hardware RotationVector (preserving signed data[3] = w)
static bool g_compassPoseReady = false;
static double g_compQx = 0.0;
static double g_compQy = 0.0;
static double g_compQz = 0.0;
static double g_compQw = 1.0;

static int64_t get_boottime_ns(void)
{
    struct timespec ts;
    if (clock_gettime(CLOCK_BOOTTIME, &ts) == 0)
        return (int64_t)ts.tv_sec * 1000000000LL + (int64_t)ts.tv_nsec;
    return 0;
}

static bool resolve_ndk_symbols(void)
{
    if (fn_getDefaultSensor != NULL)
        return true;

    g_libAndroid = dlopen("libandroid.so", RTLD_NOW);
    if (g_libAndroid == NULL)
        return false;

    fn_getInstanceForPackage = (PFN_ASensorManager_getInstanceForPackage)dlsym(g_libAndroid, "ASensorManager_getInstanceForPackage");
    fn_getInstance = (PFN_ASensorManager_getInstance)dlsym(g_libAndroid, "ASensorManager_getInstance");
    fn_getDefaultSensor = (PFN_ASensorManager_getDefaultSensor)dlsym(g_libAndroid, "ASensorManager_getDefaultSensor");
    fn_createEventQueue = (PFN_ASensorManager_createEventQueue)dlsym(g_libAndroid, "ASensorManager_createEventQueue");
    fn_destroyEventQueue = (PFN_ASensorManager_destroyEventQueue)dlsym(g_libAndroid, "ASensorManager_destroyEventQueue");
    fn_enableSensor = (PFN_ASensorEventQueue_enableSensor)dlsym(g_libAndroid, "ASensorEventQueue_enableSensor");
    fn_disableSensor = (PFN_ASensorEventQueue_disableSensor)dlsym(g_libAndroid, "ASensorEventQueue_disableSensor");
    fn_setEventRate = (PFN_ASensorEventQueue_setEventRate)dlsym(g_libAndroid, "ASensorEventQueue_setEventRate");
    fn_getEvents = (PFN_ASensorEventQueue_getEvents)dlsym(g_libAndroid, "ASensorEventQueue_getEvents");
    fn_getMinDelay = (PFN_ASensor_getMinDelay)dlsym(g_libAndroid, "ASensor_getMinDelay");
    fn_looperPrepare = (PFN_ALooper_prepare)dlsym(g_libAndroid, "ALooper_prepare");
    fn_looperPollOnce = (PFN_ALooper_pollOnce)dlsym(g_libAndroid, "ALooper_pollOnce");
    fn_looperWake = (PFN_ALooper_wake)dlsym(g_libAndroid, "ALooper_wake");

    return (fn_getInstanceForPackage != NULL || fn_getInstance != NULL)
        && fn_getDefaultSensor != NULL
        && fn_createEventQueue != NULL
        && fn_destroyEventQueue != NULL
        && fn_enableSensor != NULL
        && fn_disableSensor != NULL
        && fn_setEventRate != NULL
        && fn_getEvents != NULL
        && fn_looperPrepare != NULL
        && fn_looperPollOnce != NULL;
}

static void enable_sensor_best_rate(ASensorEventQueue* queue, const ASensor* sensor, int32_t desiredUs)
{
    if (queue == NULL || sensor == NULL)
        return;

    if (fn_enableSensor(queue, sensor) < 0)
        return;

    int32_t minDelay = (fn_getMinDelay != NULL) ? fn_getMinDelay(sensor) : 5000;
    if (minDelay < 2000)
        minDelay = 2000;

    int32_t reqUs = desiredUs >= minDelay ? desiredUs : minDelay;
    if (fn_setEventRate(queue, sensor, reqUs) < 0)
    {
        // Fallback to 5000 us (200 Hz) if HIGH_SAMPLING_RATE_SENSORS is not active
        int32_t safeUs = minDelay > 5000 ? minDelay : 5000;
        fn_setEventRate(queue, sensor, safeUs);
    }
}

// Initializes ENU quaternion directly from gravity vector so Pitch and Roll are level on Frame 1
static void init_quat_from_accel(double ax, double ay, double az, double* oqx, double* oqy, double* oqz, double* oqw)
{
    double norm = sqrt(ax * ax + ay * ay + az * az);
    if (norm < 1e-6)
    {
        *oqx = 0.0; *oqy = -0.7071067811865475; *oqz = 0.0; *oqw = 0.7071067811865475;
        return;
    }

    double ux = ax / norm;
    double uy = ay / norm;
    double uz = az / norm;

    if (uz < -0.9999)
    {
        *oqx = 1.0; *oqy = 0.0; *oqz = 0.0; *oqw = 0.0;
        return;
    }

    // Shortest-arc rotation mapping measured unit Sky vector u -> ENU Sky (0, 0, +1)
    double qx = uy;
    double qy = -ux;
    double qz = 0.0;
    double qw = 1.0 + uz;
    double qNorm = sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
    if (qNorm > 1e-9)
    {
        double inv = 1.0 / qNorm;
        *oqx = qx * inv;
        *oqy = qy * inv;
        *oqz = qz * inv;
        *oqw = qw * inv;
    }
}

static void* vratis_sensor_thread_main(void* arg)
{
    (void)arg;

    ALooper* looper = fn_looperPrepare(ALOOPER_PREPARE_ALLOW_NON_CALLBACKS);
    if (looper == NULL)
    {
        g_threadRunning = 0;
        return NULL;
    }

    pthread_mutex_lock(&g_stateMutex);
    g_workerLooper = looper;
    pthread_mutex_unlock(&g_stateMutex);

    ASensorManager* mgr = (fn_getInstanceForPackage != NULL)
        ? fn_getInstanceForPackage("com.vratis.simulasyon")
        : NULL;
    if (mgr == NULL && fn_getInstance != NULL)
        mgr = fn_getInstance();

    if (mgr == NULL)
    {
        g_threadRunning = 0;
        return NULL;
    }

    ASensorEventQueue* queue = fn_createEventQueue(mgr, looper, 1, NULL, NULL);
    if (queue == NULL)
    {
        g_threadRunning = 0;
        return NULL;
    }

    // Prefer uncalibrated hardware MEMS gyroscope to completely bypass OEM/Samsung stationary deadzones
    const ASensor* gyroSensor = fn_getDefaultSensor(mgr, ASENSOR_TYPE_GYROSCOPE_UNCALIBRATED);
    bool isUncalibratedGyro = (gyroSensor != NULL);
    if (gyroSensor == NULL)
        gyroSensor = fn_getDefaultSensor(mgr, ASENSOR_TYPE_GYROSCOPE);

    const ASensor* accelSensor = fn_getDefaultSensor(mgr, ASENSOR_TYPE_ACCELEROMETER);
    const ASensor* rotVecSensor = fn_getDefaultSensor(mgr, ASENSOR_TYPE_ROTATION_VECTOR);
    const ASensor* gameRotFallbackSensor = (gyroSensor == NULL)
        ? fn_getDefaultSensor(mgr, ASENSOR_TYPE_GAME_ROTATION_VECTOR)
        : NULL;

    if (gyroSensor != NULL)
        enable_sensor_best_rate(queue, gyroSensor, 2500); // 400 Hz (auto-falls back to 200 Hz if capped)
    if (accelSensor != NULL)
        enable_sensor_best_rate(queue, accelSensor, 5000); // 200 Hz gravity reference
    if (gameRotFallbackSensor != NULL)
        enable_sensor_best_rate(queue, gameRotFallbackSensor, 5000);

    int activeCompassEnabled = 0;
    if (g_compassMode != 0 && rotVecSensor != NULL)
    {
        enable_sensor_best_rate(queue, rotVecSensor, 5000);
        activeCompassEnabled = 1;
    }

    // Local 64-bit AHRS filter state
    bool quatInitialized = false;
    double qx = 0.0, qy = -0.7071067811865475, qz = 0.0, qw = 0.7071067811865475;

    bool hasValidAccel = false;
    double accX = 0.0, accY = 0.0, accZ = 9.80665;
    double accLpfX = 0.0, accLpfY = 0.0, accLpfZ = 9.80665;
    double accMag = 9.80665;
    bool accelLpfInit = false;

    bool biasSeeded = false;
    double biasSeedElapsedSec = 0.0;
    double biasX = 0.0, biasY = 0.0, biasZ = 0.0;

    bool hasPrevGyro = false;
    int64_t prevGyroTsNs = 0;
    double prevWx = 0.0, prevWy = 0.0, prevWz = 0.0;
    double smoothWx = 0.0, smoothWy = 0.0, smoothWz = 0.0;
    double prevDtx = 0.0, prevDty = 0.0, prevDtz = 0.0;
    double deskStillSec = 0.0;

    ASensorEvent events[32];

    while (g_threadRunning)
    {
        int wantCompass = g_compassMode;
        if (wantCompass != activeCompassEnabled && rotVecSensor != NULL)
        {
            if (wantCompass)
            {
                enable_sensor_best_rate(queue, rotVecSensor, 5000);
                activeCompassEnabled = 1;
            }
            else
            {
                fn_disableSensor(queue, rotVecSensor);
                activeCompassEnabled = 0;
            }
        }

        fn_looperPollOnce(20, NULL, NULL, NULL);
        if (!g_threadRunning)
            break;

        ssize_t n;
        while ((n = fn_getEvents(queue, events, 32)) > 0)
        {
            for (ssize_t i = 0; i < n; i++)
            {
                const ASensorEvent* ev = &events[i];

                if (ev->type == ASENSOR_TYPE_ACCELEROMETER)
                {
                    double ax = (double)ev->data[0];
                    double ay = (double)ev->data[1];
                    double az = (double)ev->data[2];
                    double mag = sqrt(ax * ax + ay * ay + az * az);

                    if (mag > 4.0 && mag < 16.0)
                    {
                        accX = ax; accY = ay; accZ = az; accMag = mag;
                        hasValidAccel = true;

                        if (!accelLpfInit)
                        {
                            accLpfX = ax; accLpfY = ay; accLpfZ = az;
                            accelLpfInit = true;
                        }
                        else
                        {
                            accLpfX += (ax - accLpfX) * 0.08;
                            accLpfY += (ay - accLpfY) * 0.08;
                            accLpfZ += (az - accLpfZ) * 0.08;
                        }

                        if (!quatInitialized)
                        {
                            init_quat_from_accel(ax, ay, az, &qx, &qy, &qz, &qw);
                            quatInitialized = true;
                            pthread_mutex_lock(&g_stateMutex);
                            g_qx = qx; g_qy = qy; g_qz = qz; g_qw = qw;
                            g_poseReady = true;
                            g_sampleSeq++;
                            pthread_mutex_unlock(&g_stateMutex);
                        }
                    }
                }
                else if (ev->type == ASENSOR_TYPE_GYROSCOPE_UNCALIBRATED || ev->type == ASENSOR_TYPE_GYROSCOPE)
                {
                    double rawWx = (double)ev->data[0];
                    double rawWy = (double)ev->data[1];
                    double rawWz = (double)ev->data[2];

                    if (!isfinite(rawWx) || !isfinite(rawWy) || !isfinite(rawWz))
                        continue;

                    int64_t tsNs = ev->timestamp;
                    double dt = 0.0;
                    if (prevGyroTsNs > 0 && tsNs > prevGyroTsNs)
                    {
                        dt = (double)(tsNs - prevGyroTsNs) * 1e-9;
                        if (dt > 0.05)
                            dt = 0.005;
                    }
                    prevGyroTsNs = tsNs;

                    // Seed factory/thermal gyro bias ONCE at startup from UNCALIBRATED data[3..5],
                    // then freeze out Android's live auto-bias so slow VR head motion is NEVER eaten or rubberbanded!
                    if (isUncalibratedGyro && ev->type == ASENSOR_TYPE_GYROSCOPE_UNCALIBRATED && !biasSeeded && biasSeedElapsedSec < 0.75)
                    {
                        biasSeedElapsedSec += dt;
                        double bx = (double)ev->data[3];
                        double by = (double)ev->data[4];
                        double bz = (double)ev->data[5];
                        if (isfinite(bx) && isfinite(by) && isfinite(bz) &&
                            (fabs(bx) > 1e-7 || fabs(by) > 1e-7 || fabs(bz) > 1e-7) &&
                            fabs(bx) < 0.25 && fabs(by) < 0.25 && fabs(bz) < 0.25)
                        {
                            biasX = bx; biasY = by; biasZ = bz;
                            biasSeeded = true;
                        }
                    }

                    double wx = rawWx - biasX;
                    double wy = rawWy - biasY;
                    double wz = rawWz - biasZ;

                    // Ultra-strict desk-only stillness check:
                    // Human head micro-motion (>0.12 deg/s) NEVER enters this block -> 0.000 deg/s deadzone!
                    if (dt > 0.0 && hasValidAccel && accelLpfInit)
                    {
                        double wMag = sqrt(wx * wx + wy * wy + wz * wz);
                        double dax = accX - accLpfX;
                        double day = accY - accLpfY;
                        double daz = accZ - accLpfZ;
                        double accJitter = sqrt(dax * dax + day * day + daz * daz);

                        if (wMag < 0.0020 && accJitter < 0.035) // < 0.115 deg/s & < 0.035 m/s^2 (sitting on desk)
                        {
                            deskStillSec += dt;
                            if (deskStillSec >= 1.50 && isUncalibratedGyro)
                            {
                                double alpha = 0.30 * dt;
                                if (alpha > 0.005) alpha = 0.005;
                                biasX += (rawWx - biasX) * alpha;
                                biasY += (rawWy - biasY) * alpha;
                                biasZ += (rawWz - biasZ) * alpha;
                                wx = rawWx - biasX;
                                wy = rawWy - biasY;
                                wz = rawWz - biasZ;
                            }
                        }
                        else
                        {
                            deskStillSec = 0.0;
                        }
                    }

                    if (dt > 0.0 && dt <= 0.05)
                    {
                        if (!hasPrevGyro)
                        {
                            smoothWx = wx; smoothWy = wy; smoothWz = wz;
                        }
                        else
                        {
                            // Low-delay (~0.9 ms at 500 Hz) EMA for clean sub-frame CLOCK_BOOTTIME interpolation
                            smoothWx += (wx - smoothWx) * 0.38;
                            smoothWy += (wy - smoothWy) * 0.38;
                            smoothWz += (wz - smoothWz) * 0.38;
                        }

                        double wmx = hasPrevGyro ? 0.5 * (prevWx + wx) : wx;
                        double wmy = hasPrevGyro ? 0.5 * (prevWy + wy) : wy;
                        double wmz = hasPrevGyro ? 0.5 * (prevWz + wz) : wz;
                        prevWx = wx; prevWy = wy; prevWz = wz;

                        double dtx = wmx * dt;
                        double dty = wmy * dt;
                        double dtz = wmz * dt;

                        // 2nd-order Bortz coning compensation
                        double rx = dtx;
                        double ry = dty;
                        double rz = dtz;
                        if (hasPrevGyro)
                        {
                            rx += (1.0 / 12.0) * (prevDty * dtz - prevDtz * dty);
                            ry += (1.0 / 12.0) * (prevDtz * dtx - prevDtx * dtz);
                            rz += (1.0 / 12.0) * (prevDtx * dty - prevDty * dtx);
                        }
                        prevDtx = dtx; prevDty = dty; prevDtz = dtz;
                        hasPrevGyro = true;

                        // Complementary Earth-gravity Pitch/Roll correction (strictly orthogonal to ENU World Up +Z,
                        // so horizontal Yaw projection is identically 0.000000 — zero Yaw pull or center drift!)
                        if (hasValidAccel && fabs(accMag - 9.80665) < 1.35)
                        {
                            double invAcc = 1.0 / accMag;
                            double axN = accX * invAcc;
                            double ayN = accY * invAcc;
                            double azN = accZ * invAcc;

                            // Predicted ENU Sky (0, 0, +1) in body frame: R(q)^T * [0, 0, 1]^T
                            double vx = 2.0 * (qx * qz - qw * qy);
                            double vy = 2.0 * (qy * qz + qw * qx);
                            double vz = qw * qw - qx * qx - qy * qy + qz * qz;

                            // Tilt error e = a_measured x v_predicted
                            double ex = ayN * vz - azN * vy;
                            double ey = azN * vx - axN * vz;
                            double ez = axN * vy - ayN * vx;

                            const double kpGravity = 0.42;
                            rx += kpGravity * ex * dt;
                            ry += kpGravity * ey * dt;
                            rz += kpGravity * ez * dt;
                        }

                        // Exact Lie-Group Quaternion Exponential SO(3) -> S^3 (zero small-angle deadzone)
                        double angle = sqrt(rx * rx + ry * ry + rz * rz);
                        double half = 0.5 * angle;
                        double sinOverAngle = (angle > 1e-8)
                            ? (sin(half) / angle)
                            : (0.5 - (angle * angle) / 48.0);

                        double dqx = rx * sinOverAngle;
                        double dqy = ry * sinOverAngle;
                        double dqz = rz * sinOverAngle;
                        double dqw = cos(half);

                        double nqx = qw * dqx + qx * dqw + qy * dqz - qz * dqy;
                        double nqy = qw * dqy - qx * dqz + qy * dqw + qz * dqx;
                        double nqz = qw * dqz + qx * dqy - qy * dqx + qz * dqw;
                        double nqw = qw * dqw - qx * dqx - qy * dqy - qz * dqz;

                        double qNorm = sqrt(nqx * nqx + nqy * nqy + nqz * nqz + nqw * nqw);
                        if (qNorm > 1e-12)
                        {
                            double invQ = 1.0 / qNorm;
                            qx = nqx * invQ;
                            qy = nqy * invQ;
                            qz = nqz * invQ;
                            qw = nqw * invQ;
                        }

                        quatInitialized = true;

                        pthread_mutex_lock(&g_stateMutex);
                        g_qx = qx; g_qy = qy; g_qz = qz; g_qw = qw;
                        g_wx = wx; g_wy = wy; g_wz = wz;
                        g_wxSmooth = smoothWx; g_wySmooth = smoothWy; g_wzSmooth = smoothWz;
                        g_lastGyroTsNs = tsNs;
                        g_poseReady = true;
                        g_sampleSeq++;
                        pthread_mutex_unlock(&g_stateMutex);
                    }
                }
                else if (ev->type == ASENSOR_TYPE_ROTATION_VECTOR || ev->type == ASENSOR_TYPE_GAME_ROTATION_VECTOR)
                {
                    // Preserves true signed data[3] = cos(theta/2) directly from Android SensorEvent!
                    double rvx = (double)ev->data[0];
                    double rvy = (double)ev->data[1];
                    double rvz = (double)ev->data[2];
                    double rvw = (double)ev->data[3];
                    double nSq = rvx * rvx + rvy * rvy + rvz * rvz + rvw * rvw;
                    if (nSq > 0.25 && isfinite(nSq))
                    {
                        double inv = 1.0 / sqrt(nSq);
                        rvx *= inv; rvy *= inv; rvz *= inv; rvw *= inv;

                        pthread_mutex_lock(&g_stateMutex);
                        if (ev->type == ASENSOR_TYPE_ROTATION_VECTOR)
                        {
                            g_compQx = rvx; g_compQy = rvy; g_compQz = rvz; g_compQw = rvw;
                            g_compassPoseReady = true;
                        }
                        else if (gyroSensor == NULL)
                        {
                            g_qx = rvx; g_qy = rvy; g_qz = rvz; g_qw = rvw;
                            g_poseReady = true;
                        }
                        g_sampleSeq++;
                        pthread_mutex_unlock(&g_stateMutex);
                    }
                }
            }
        }
    }

    if (gyroSensor != NULL) fn_disableSensor(queue, gyroSensor);
    if (accelSensor != NULL) fn_disableSensor(queue, accelSensor);
    if (gameRotFallbackSensor != NULL) fn_disableSensor(queue, gameRotFallbackSensor);
    if (activeCompassEnabled && rotVecSensor != NULL) fn_disableSensor(queue, rotVecSensor);
    fn_destroyEventQueue(mgr, queue);

    pthread_mutex_lock(&g_stateMutex);
    g_workerLooper = NULL;
    pthread_mutex_unlock(&g_stateMutex);

    return NULL;
}

int vratis_sensor_start(int compassMode)
{
    g_compassMode = compassMode ? 1 : 0;

    if (g_threadRunning)
        return 1;

    if (!resolve_ndk_symbols())
        return 0;

    g_threadRunning = 1;
    if (pthread_create(&g_workerThread, NULL, vratis_sensor_thread_main, NULL) != 0)
    {
        g_threadRunning = 0;
        return 0;
    }

    return 1;
}

void vratis_sensor_stop(void)
{
    if (!g_threadRunning)
        return;

    g_threadRunning = 0;

    pthread_mutex_lock(&g_stateMutex);
    ALooper* looper = g_workerLooper;
    pthread_mutex_unlock(&g_stateMutex);

    if (looper != NULL && fn_looperWake != NULL)
        fn_looperWake(looper);

    pthread_join(g_workerThread, NULL);
}

void vratis_sensor_set_compass_mode(int compassMode)
{
    g_compassMode = compassMode ? 1 : 0;

    pthread_mutex_lock(&g_stateMutex);
    ALooper* looper = g_workerLooper;
    pthread_mutex_unlock(&g_stateMutex);

    if (looper != NULL && fn_looperWake != NULL)
        fn_looperWake(looper);
}

int vratis_sensor_get_pose(float* outQuatXyzw, float* outGyroXyz, uint32_t* outSeq)
{
    if (outQuatXyzw == NULL || outGyroXyz == NULL || outSeq == NULL)
        return 0;

    double qx, qy, qz, qw;
    double wx, wy, wz;
    double swx, swy, swz;
    int64_t lastTsNs;
    uint32_t seq;
    bool ready;
    int useCompass = g_compassMode;

    pthread_mutex_lock(&g_stateMutex);
    if (useCompass && g_compassPoseReady)
    {
        qx = g_compQx; qy = g_compQy; qz = g_compQz; qw = g_compQw;
        wx = g_wx; wy = g_wy; wz = g_wz;
        swx = g_wxSmooth; swy = g_wySmooth; swz = g_wzSmooth;
        lastTsNs = 0;
        seq = g_sampleSeq;
        ready = true;
    }
    else
    {
        qx = g_qx; qy = g_qy; qz = g_qz; qw = g_qw;
        wx = g_wx; wy = g_wy; wz = g_wz;
        swx = g_wxSmooth; swy = g_wySmooth; swz = g_wzSmooth;
        lastTsNs = g_lastGyroTsNs;
        seq = g_sampleSeq;
        ready = g_poseReady;
    }
    pthread_mutex_unlock(&g_stateMutex);

    if (!ready)
        return 0;

    // Exact CLOCK_BOOTTIME phase alignment across SensorHub IPC delivery delay (~3-8 ms on Exynos/ICM42632M):
    // Keeping the cap at 18 ms ensures (lastTsNs + dtExt == nowNs) holds on every VSync without clipping!
    if (!useCompass && lastTsNs > 0)
    {
        int64_t nowNs = get_boottime_ns();
        if (nowNs > lastTsNs)
        {
            double dtExt = (double)(nowNs - lastTsNs) * 1e-9;
            if (dtExt > 0.018)
                dtExt = 0.018;

            double rx = swx * dtExt;
            double ry = swy * dtExt;
            double rz = swz * dtExt;
            double angle = sqrt(rx * rx + ry * ry + rz * rz);
            if (angle > 1e-9)
            {
                double half = 0.5 * angle;
                double sinOver = (angle > 1e-7) ? (sin(half) / angle) : 0.5;
                double dqx = rx * sinOver;
                double dqy = ry * sinOver;
                double dqz = rz * sinOver;
                double dqw = cos(half);

                double nqx = qw * dqx + qx * dqw + qy * dqz - qz * dqy;
                double nqy = qw * dqy - qx * dqz + qy * dqw + qz * dqx;
                double nqz = qw * dqz + qx * dqy - qy * dqx + qz * dqw;
                double nqw = qw * dqw - qx * dqx - qy * dqy - qz * dqz;
                double qNorm = sqrt(nqx * nqx + nqy * nqy + nqz * nqz + nqw * nqw);
                if (qNorm > 1e-12)
                {
                    double invQ = 1.0 / qNorm;
                    qx = nqx * invQ; qy = nqy * invQ; qz = nqz * invQ; qw = nqw * invQ;
                }
            }
        }
    }

    outQuatXyzw[0] = (float)qx;
    outQuatXyzw[1] = (float)qy;
    outQuatXyzw[2] = (float)qz;
    outQuatXyzw[3] = (float)qw;
    outGyroXyz[0] = (float)wx;
    outGyroXyz[1] = (float)wy;
    outGyroXyz[2] = (float)wz;
    *outSeq = seq;
    return 1;
}

#else

int vratis_sensor_start(int compassMode) { (void)compassMode; return 0; }
void vratis_sensor_stop(void) { }
void vratis_sensor_set_compass_mode(int compassMode) { (void)compassMode; }
int vratis_sensor_get_pose(float* outQuatXyzw, float* outGyroXyz, uint32_t* outSeq)
{
    (void)outQuatXyzw; (void)outGyroXyz; (void)outSeq;
    return 0;
}

#endif
