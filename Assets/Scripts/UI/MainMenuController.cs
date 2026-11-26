using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace VRAtis.UI
{
    /// <summary>
    /// Controls the startup Main Menu screen, letting the player launch either
    /// Story Mode (Warband-style top-down world map) or Training Mode (existing shooting range).
    /// </summary>
    public class MainMenuController : MonoBehaviour
    {
        public const string MainMenuSceneName = "MainMenu";
        public const string TrainingSceneName = "SampleScene";
        public const string StoryWorldMapSceneName = "StoryWorldMap";

        [Header("Sahne İsimleri")]
        [SerializeField] private string storyModeSceneName = StoryWorldMapSceneName;
        [SerializeField] private string trainingModeSceneName = TrainingSceneName;

        [Header("Buton Referansları")]
        [SerializeField] private UnityEngine.UI.Button storyModeCardButton;
        [SerializeField] private UnityEngine.UI.Button storyModeButton;
        [SerializeField] private UnityEngine.UI.Button trainingModeCardButton;
        [SerializeField] private UnityEngine.UI.Button trainingModeButton;
        [SerializeField] private UnityEngine.UI.Button quitButton;

        [Header("Yükleme Ekranı")]
        [SerializeField] private GameObject loadingOverlay;
        [SerializeField] private UnityEngine.UI.Text loadingStatusText;

        [Header("Ses (İsteğe Bağlı)")]
        [SerializeField] private AudioSource uiAudioSource;
        [SerializeField] private AudioClip buttonClickClip;

        private bool _isLoadingScene;

        public bool IsLoadingScene => _isLoadingScene;

        private void Awake()
        {
            Time.timeScale = 1f;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            BindButton(storyModeCardButton, StartStoryMode);
            BindButton(storyModeButton, StartStoryMode);
            BindButton(trainingModeCardButton, StartTrainingMode);
            BindButton(trainingModeButton, StartTrainingMode);
            BindButton(quitButton, QuitGame);

            SetLoadingOverlay(false, string.Empty);
        }

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            if (!_isLoadingScene && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
                QuitGame();
#endif
        }

        public void StartStoryMode()
        {
            LoadModeScene(storyModeSceneName, "Hikaye Modu yükleniyor...");
        }

        public void StartTrainingMode()
        {
            LoadModeScene(trainingModeSceneName, "Antrenman Modu yükleniyor...");
        }

        public void QuitGame()
        {
            if (_isLoadingScene)
                return;

            PlayClickSound();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        public void SetLoadingOverlay(bool visible, string statusMessage)
        {
            if (loadingOverlay != null)
                loadingOverlay.SetActive(visible);

            if (loadingStatusText != null && !string.IsNullOrEmpty(statusMessage))
                loadingStatusText.text = statusMessage;
        }

        private void LoadModeScene(string sceneName, string statusMessage)
        {
            if (_isLoadingScene || string.IsNullOrEmpty(sceneName))
                return;

            if (!Application.CanStreamedLevelBeLoaded(sceneName))
            {
                Debug.LogError($"[MainMenuController] '{sceneName}' sahnesi Build Settings içinde bulunamadı.");
                return;
            }

            PlayClickSound();
            StartCoroutine(LoadSceneAsyncRoutine(sceneName, statusMessage));
        }

        private IEnumerator LoadSceneAsyncRoutine(string sceneName, string statusMessage)
        {
            _isLoadingScene = true;
            SetButtonsInteractable(false);
            SetLoadingOverlay(true, statusMessage);

            // Allow one frame for the loading overlay to render before heavy scene load begins
            yield return null;

            AsyncOperation loadOp = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
            if (loadOp == null)
            {
                _isLoadingScene = false;
                SetButtonsInteractable(true);
                SetLoadingOverlay(false, string.Empty);
                yield break;
            }

            while (!loadOp.isDone)
                yield return null;
        }

        private void SetButtonsInteractable(bool interactable)
        {
            if (storyModeCardButton != null) storyModeCardButton.interactable = interactable;
            if (storyModeButton != null) storyModeButton.interactable = interactable;
            if (trainingModeCardButton != null) trainingModeCardButton.interactable = interactable;
            if (trainingModeButton != null) trainingModeButton.interactable = interactable;
            if (quitButton != null) quitButton.interactable = interactable;
        }

        private void PlayClickSound()
        {
            if (uiAudioSource != null && buttonClickClip != null)
                uiAudioSource.PlayOneShot(buttonClickClip);
        }

        private static void BindButton(UnityEngine.UI.Button button, UnityEngine.Events.UnityAction action)
        {
            if (button == null)
                return;

            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(action);
        }
    }
}
