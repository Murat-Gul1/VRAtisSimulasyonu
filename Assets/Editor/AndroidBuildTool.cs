using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace VRAtis.EditorTools
{
    public static class AndroidBuildTool
    {
        public const string DefaultApkRelativePath = "Builds/VRAtisSimulasyonu.apk";

        [MenuItem("VRAtis/Android/1. Android Ayarlarını Uygula")]
        public static void ApplyAndroidSettings()
        {
            PlayerSettings.companyName = "VRAtis";
            PlayerSettings.productName = "VRAtisSimulasyonu";
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.vratis.simulasyon");
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeRight = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;

            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
            PlayerSettings.Android.forceInternetPermission = true;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

            AssetDatabase.SaveAssets();
            Debug.Log("[AndroidBuildTool] Android VR ayarları uygulandı (LandscapeLeft, Internet İzni, IL2CPP, ARM64).");
        }

        [MenuItem("VRAtis/Android/2. Android APK Derle")]
        public static bool BuildApk()
        {
            ApplyAndroidSettings();

            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            {
                bool switched = EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
                if (!switched)
                {
                    Debug.LogError("[AndroidBuildTool] Android platformuna geçiş başarısız oldu.");
                    return false;
                }
            }

            List<string> scenes = new List<string>();
            foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
            {
                if (scene.enabled)
                    scenes.Add(scene.path);
            }

            if (scenes.Count == 0)
            {
                Debug.LogError("[AndroidBuildTool] Build Settings içinde aktif sahne bulunamadı.");
                return false;
            }

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string apkFullPath = Path.Combine(projectRoot, DefaultApkRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(apkFullPath));

            BuildPlayerOptions options = new BuildPlayerOptions
            {
                scenes = scenes.ToArray(),
                locationPathName = apkFullPath,
                target = BuildTarget.Android,
                options = BuildOptions.None
            };

            Debug.Log($"[AndroidBuildTool] APK derleme başlatılıyor -> {apkFullPath}");
            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;

            if (summary.result == BuildResult.Succeeded)
            {
                double mb = summary.totalSize / (1024.0 * 1024.0);
                Debug.Log($"[AndroidBuildTool] APK başarıyla oluşturuldu: {apkFullPath} ({mb:F1} MB, {summary.totalTime.TotalSeconds:F1} sn)");
                EditorUtility.RevealInFinder(apkFullPath);
                return true;
            }

            Debug.LogError($"[AndroidBuildTool] APK derleme başarısız: {summary.result} (Hatalar: {summary.totalErrors})");
            return false;
        }

        [MenuItem("VRAtis/Android/3. APK Derle ve ADB ile Telefona Kur")]
        public static void BuildAndInstallViaAdb()
        {
            if (!BuildApk())
                return;

            InstallExistingApkViaAdb();
        }

        [MenuItem("VRAtis/Android/4. Mevcut APK'yı Telefona Kur (ADB)")]
        public static void InstallExistingApkViaAdb()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string apkFullPath = Path.Combine(projectRoot, DefaultApkRelativePath);
            if (!File.Exists(apkFullPath))
            {
                Debug.LogError($"[AndroidBuildTool] APK bulunamadı: {apkFullPath}. Önce 'Android APK Derle' çalıştırın.");
                return;
            }

            string adbPath = FindAdbPath();
            if (string.IsNullOrEmpty(adbPath))
            {
                Debug.LogError("[AndroidBuildTool] adb.exe bulunamadı.");
                return;
            }

            if (!RunProcess(adbPath, $"install -r \"{apkFullPath}\"", out string stdout, out string stderr))
            {
                Debug.LogError($"[AndroidBuildTool] ADB kurulum hatası:\n{stdout}\n{stderr}");
                return;
            }

            Debug.Log($"[AndroidBuildTool] APK telefona kuruldu!\n{stdout}");
            RunProcess(adbPath, "shell monkey -p com.vratis.simulasyon -c android.intent.category.LAUNCHER 1", out _, out _);
        }

        private static string FindAdbPath()
        {
            string localSdkAdb = Path.Combine(
                global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.LocalApplicationData),
                @"Android\Sdk\platform-tools\adb.exe");
            if (File.Exists(localSdkAdb))
                return localSdkAdb;

            string unitySdkAdb = Path.Combine(
                EditorApplication.applicationContentsPath,
                @"PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe");
            if (File.Exists(unitySdkAdb))
                return unitySdkAdb;

            return null;
        }

        private static bool RunProcess(string exe, string args, out string stdout, out string stderr)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (Process p = Process.Start(psi))
            {
                stdout = p.StandardOutput.ReadToEnd();
                stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode == 0;
            }
        }
    }
}
