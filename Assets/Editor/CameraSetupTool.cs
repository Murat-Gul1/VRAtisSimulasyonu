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
        private const string AutoSetupVersionKey = "VRAtis_Camera_V6_Done";

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
                // Natural right-handed ready stance: eye/camera rests slightly left-above the buttstock side
                Vector3 riflePos = rifleCtrl.transform.position;
                mainCam.transform.position = new Vector3(riflePos.x - 0.22f, riflePos.y + 0.18f, riflePos.z - 0.56f);
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
                SerializedObject trackerSo = new SerializedObject(headTracker);
                trackerSo.FindProperty("rifleOrientationController").objectReferenceValue = rifleCtrl;
                trackerSo.ApplyModifiedPropertiesWithoutUndo();

                SerializedObject rifleSo = new SerializedObject(rifleCtrl);
                rifleSo.FindProperty("headCameraTransform").objectReferenceValue = mainCam.transform;
                rifleSo.ApplyModifiedPropertiesWithoutUndo();

                WeaponAimController aimCtrl = rifleCtrl.GetComponent<WeaponAimController>();
                if (aimCtrl == null)
                    aimCtrl = Undo.AddComponent<WeaponAimController>(rifleCtrl.gameObject);

                Transform redDotAnchor = FindDeepChild(rifleCtrl.transform, "RedDotAnchor");
                RedDotSightController redDotSight = rifleCtrl.GetComponentInChildren<RedDotSightController>(true);

                SerializedObject aimSo = new SerializedObject(aimCtrl);
                aimSo.FindProperty("playerRifleTransform").objectReferenceValue = rifleCtrl.transform;
                aimSo.FindProperty("redDotAnchor").objectReferenceValue = redDotAnchor;
                aimSo.FindProperty("redDotSight").objectReferenceValue = redDotSight;
                aimSo.FindProperty("mainCameraTransform").objectReferenceValue = mainCam.transform;
                aimSo.FindProperty("headTracker").objectReferenceValue = headTracker;
                aimSo.FindProperty("stereoRig").objectReferenceValue = stereoRig;
                aimSo.ApplyModifiedPropertiesWithoutUndo();

                if (redDotSight != null)
                {
                    SerializedObject dotSo = new SerializedObject(redDotSight);
                    dotSo.FindProperty("dotColor").colorValue = new Color(3.6f, 0.16f, 0.08f, 1f);
                    dotSo.FindProperty("alwaysKeepReticleOn").boolValue = false;
                    dotSo.FindProperty("reticlePower").floatValue = 0f;
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
