using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRAtis.Hardware;
using VRAtis.Weapons;

namespace VRAtis.EditorTools
{
    [InitializeOnLoad]
    public static class CameraSetupTool
    {
        private const string AutoSetupVersionKey = "VRAtis_Camera_V15_Done";

        static CameraSetupTool()
        {
            EditorApplication.delayCall += TryAutoSetupOnce;
        }

        private static void TryAutoSetupOnce()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || Application.isBatchMode)
                return;

            Camera mainCam = Camera.main;
            if (mainCam == null)
                return;

            if (mainCam.GetComponent<HeadTrackingCameraController>() != null &&
                mainCam.GetComponent<CardboardStereoRig>() != null &&
                Object.FindAnyObjectByType<WeaponAimController>() != null &&
                Object.FindAnyObjectByType<OpticalRifleTracker>() != null &&
                EditorPrefs.GetBool(AutoSetupVersionKey, false))
                return;

            SetupMainCamera();
        }

        [MenuItem("VRAtis/Kamera Kurulumu Yap")]
        public static void SetupMainCamera()
        {
            Camera mainCam = Camera.main;
            if (mainCam == null)
            {
                Debug.LogError("[CameraSetupTool] Sahnede MainCamera etiketli kamera bulunamadı.");
                return;
            }

            Undo.RecordObject(mainCam.transform, "Main Camera Ready Framing");
            Undo.RecordObject(mainCam, "Main Camera Optics");

            RifleOrientationController rifleCtrl = Object.FindAnyObjectByType<RifleOrientationController>();
            if (rifleCtrl != null)
            {
                // Natural right-handed shouldered stance: eye/camera rests just left-above the receiver (8 cm left, 14 cm up, 25 cm back)
                Vector3 riflePos = rifleCtrl.transform.position;
                mainCam.transform.position = new Vector3(riflePos.x - 0.08f, riflePos.y + 0.14f, riflePos.z - 0.25f);
                mainCam.transform.rotation = Quaternion.identity;
            }

            mainCam.nearClipPlane = 0.04f;
            mainCam.fieldOfView = 64f;

            HeadTrackingCameraController headTracker = mainCam.GetComponent<HeadTrackingCameraController>();
            if (headTracker == null)
                headTracker = Undo.AddComponent<HeadTrackingCameraController>(mainCam.gameObject);

            CardboardStereoRig stereoRig = mainCam.GetComponent<CardboardStereoRig>();
            if (stereoRig == null)
                stereoRig = Undo.AddComponent<CardboardStereoRig>(mainCam.gameObject);

            SerializedObject stereoSo = new SerializedObject(stereoRig);
            stereoSo.FindProperty("lensCenterSpacingMm").floatValue = 66f;
            stereoSo.FindProperty("fallbackPhoneWidthMm").floatValue = 154f;
            stereoSo.FindProperty("virtualEyeIpdMm").floatValue = 45f;
            stereoSo.FindProperty("vrVerticalFov").floatValue = 68f;
            stereoSo.FindProperty("allowLiveCalibrationButtons").boolValue = false;
            stereoSo.ApplyModifiedPropertiesWithoutUndo();

            Esp32RifleReceiver receiver = Object.FindAnyObjectByType<Esp32RifleReceiver>();
            if (receiver != null)
            {
                SerializedObject recvSo = new SerializedObject(receiver);
                recvSo.FindProperty("showDiagnosticsHud").boolValue = false;
                recvSo.ApplyModifiedPropertiesWithoutUndo();
            }

            if (rifleCtrl != null)
            {
                OpticalRifleTracker opticalTracker = rifleCtrl.GetComponent<OpticalRifleTracker>();
                if (opticalTracker == null)
                    opticalTracker = Undo.AddComponent<OpticalRifleTracker>(rifleCtrl.gameObject);

                SerializedObject trackerSo = new SerializedObject(headTracker);
                trackerSo.FindProperty("rifleOrientationController").objectReferenceValue = rifleCtrl;
                trackerSo.FindProperty("opticalTracker").objectReferenceValue = opticalTracker;
                trackerSo.FindProperty("requestedSensorRateHz").floatValue = 200f;
                trackerSo.FindProperty("minSmoothingSpeed").floatValue = 32f;
                trackerSo.FindProperty("maxSmoothingSpeed").floatValue = 125f;
                trackerSo.FindProperty("fastTurnSpeedDegPerSec").floatValue = 45f;
                trackerSo.ApplyModifiedPropertiesWithoutUndo();

                SerializedObject rifleSo = new SerializedObject(rifleCtrl);
                rifleSo.FindProperty("headCameraTransform").objectReferenceValue = mainCam.transform;
                rifleSo.ApplyModifiedPropertiesWithoutUndo();

                WeaponAimController aimCtrl = rifleCtrl.GetComponent<WeaponAimController>();
                if (aimCtrl == null)
                    aimCtrl = Undo.AddComponent<WeaponAimController>(rifleCtrl.gameObject);

                Transform redDotAnchor = FindDeepChild(rifleCtrl.transform, "RedDotAnchor");
                RedDotSightController redDotSight = rifleCtrl.GetComponentInChildren<RedDotSightController>(true);

                SerializedObject optSo = new SerializedObject(opticalTracker);
                optSo.FindProperty("headCameraTransform").objectReferenceValue = mainCam.transform;
                optSo.FindProperty("weaponAimController").objectReferenceValue = aimCtrl;
                optSo.FindProperty("redDotSight").objectReferenceValue = redDotSight;
                optSo.FindProperty("redDotAnchor").objectReferenceValue = redDotAnchor;
                optSo.FindProperty("rifleReceiver").objectReferenceValue = receiver;
                optSo.FindProperty("ledSpacingMeters").floatValue = 0.06f;
                optSo.FindProperty("ledOffsetFromPivotMeters").vector3Value = Vector3.zero;
                optSo.FindProperty("readyLocalOffsetMeters").vector3Value = new Vector3(0.08f, -0.14f, 0.25f);
                optSo.FindProperty("zDepthScale").floatValue = 0.45f;
                optSo.FindProperty("minColorDominance").intValue = 105;
                optSo.FindProperty("minPixelBrightness").intValue = 72;
                optSo.FindProperty("requestedWidth").intValue = 320;
                optSo.FindProperty("requestedHeight").intValue = 240;
                optSo.FindProperty("minPositionSmoothSpeed").floatValue = 12f;
                optSo.FindProperty("maxPositionSmoothSpeed").floatValue = 38f;
                optSo.ApplyModifiedPropertiesWithoutUndo();

                SerializedObject aimSo = new SerializedObject(aimCtrl);
                aimSo.FindProperty("playerRifleTransform").objectReferenceValue = rifleCtrl.transform;
                aimSo.FindProperty("redDotAnchor").objectReferenceValue = redDotAnchor;
                aimSo.FindProperty("redDotSight").objectReferenceValue = redDotSight;
                aimSo.FindProperty("mainCameraTransform").objectReferenceValue = mainCam.transform;
                aimSo.FindProperty("headTracker").objectReferenceValue = headTracker;
                aimSo.FindProperty("stereoRig").objectReferenceValue = stereoRig;
                aimSo.FindProperty("opticalTracker").objectReferenceValue = opticalTracker;
                aimSo.FindProperty("rifleReceiver").objectReferenceValue = receiver;
                aimSo.ApplyModifiedPropertiesWithoutUndo();

                if (redDotSight != null)
                {
                    SerializedObject dotSo = new SerializedObject(redDotSight);
                    dotSo.FindProperty("dotColor").colorValue = new Color(3.6f, 0.16f, 0.08f, 1f);
                    dotSo.FindProperty("alwaysKeepReticleOn").boolValue = true;
                    dotSo.FindProperty("reticlePower").floatValue = 1f;
                    dotSo.ApplyModifiedPropertiesWithoutUndo();
                }
            }

            EditorUtility.SetDirty(mainCam.gameObject);
            EditorSceneManager.MarkSceneDirty(mainCam.gameObject.scene);
            EditorSceneManager.SaveOpenScenes();
            EditorPrefs.SetBool(AutoSetupVersionKey, true);
            Debug.Log("[CameraSetupTool] Main Camera, CardboardStereoRig (66mm/154mm) ve WeaponAimController (Q / Red Dot kamera yaklaşması) kuruldu!");
        }

        private static Transform FindDeepChild(Transform parent, string targetName)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.name == targetName)
                    return child;
                Transform nested = FindDeepChild(child, targetName);
                if (nested != null)
                    return nested;
            }
            return null;
        }
    }
}
