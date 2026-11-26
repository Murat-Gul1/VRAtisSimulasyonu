using UnityEngine;
using UnityEngine.SceneManagement;
using VRAtis.UI;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace VRAtis.WorldMap
{
    /// <summary>
    /// Minimal HUD controller for the Story Mode top-down world map scene.
    /// Provides a clean return-to-menu button, camera reset button, and lightweight coordinate readout.
    /// </summary>
    public class StoryMapHudController : MonoBehaviour
    {
        [Header("Referanslar")]
        [SerializeField] private WorldMapCameraController mapCamera;
        [SerializeField] private UnityEngine.UI.Button backToMenuButton;
        [SerializeField] private UnityEngine.UI.Button resetCameraButton;
        [SerializeField] private UnityEngine.UI.Text coordinatesText;

        [Header("Ses (İsteğe Bağlı)")]
        [SerializeField] private AudioSource uiAudioSource;
        [SerializeField] private AudioClip buttonClickClip;

        private int _lastCoordX = int.MinValue;
        private int _lastCoordZ = int.MinValue;
        private bool _isReturningToMenu;

        private void Awake()
        {
            if (mapCamera == null)
                mapCamera = FindAnyObjectByType<WorldMapCameraController>();

            if (backToMenuButton != null)
            {
                backToMenuButton.onClick.RemoveAllListeners();
                backToMenuButton.onClick.AddListener(ReturnToMainMenu);
            }

            if (resetCameraButton != null)
            {
                resetCameraButton.onClick.RemoveAllListeners();
                resetCameraButton.onClick.AddListener(ResetCameraView);
            }
        }

        private void Start()
        {
            RefreshCoordinatesText(force: true);
        }

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            if (!_isReturningToMenu && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
                ReturnToMainMenu();
#endif

            RefreshCoordinatesText(force: false);
        }

        public void ReturnToMainMenu()
        {
            if (_isReturningToMenu)
                return;

            if (!Application.CanStreamedLevelBeLoaded(MainMenuController.MainMenuSceneName))
            {
                Debug.LogError($"[StoryMapHudController] '{MainMenuController.MainMenuSceneName}' sahnesi Build Settings içinde bulunamadı.");
                return;
            }

            _isReturningToMenu = true;
            PlayClickSound();
            SceneManager.LoadScene(MainMenuController.MainMenuSceneName, LoadSceneMode.Single);
        }

        public void ResetCameraView()
        {
            PlayClickSound();
            if (mapCamera != null)
                mapCamera.ResetView();
        }

        private void RefreshCoordinatesText(bool force)
        {
            if (coordinatesText == null || mapCamera == null)
                return;

            Vector3 pos = mapCamera.FocusPosition;
            int x = Mathf.RoundToInt(pos.x);
            int z = Mathf.RoundToInt(pos.z);

            if (!force && x == _lastCoordX && z == _lastCoordZ)
                return;

            _lastCoordX = x;
            _lastCoordZ = z;
            coordinatesText.text = $"KONUM: {x}, {z}";
        }

        private void PlayClickSound()
        {
            if (uiAudioSource != null && buttonClickClip != null)
                uiAudioSource.PlayOneShot(buttonClickClip);
        }
    }
}
