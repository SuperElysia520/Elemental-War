using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FPS.Settings
{
    /// <summary>Local, versioned settings. Only Apply writes the player's saved configuration.</summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class GameSettingsService : MonoBehaviour
    {
        public const string PreferenceKey = "FPS.Settings.v1";
        public static event Action<GameSettingsData> Changed;
        private static GameSettingsData current;
        private static GameSettingsService instance;
        private AudioSource uiAudio;
        private AudioClip click;
        public static GameSettingsData Current
        {
            get
            {
                if (current == null) current = ReadSaved();
                return current;
            }
        }
        public static string LastSaved => PlayerPrefs.GetString(PreferenceKey + ".time", "");
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ClearStatics() { instance = null; current = null; Changed = null; }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            if (DedicatedServerBootstrap.IsDedicatedServerProcess || instance != null) return;
            var go = new GameObject("GameSettings");
            DontDestroyOnLoad(go); instance = go.AddComponent<GameSettingsService>();
        }
        private void Awake()
        {
            if (instance != null && instance != this) { Destroy(gameObject); return; }
            instance = this;
            ApplyRuntime(Current, true);
            SceneManager.sceneLoaded += SceneLoaded;
            var canvasObject = new GameObject("ConfiguredCrosshairCanvas", typeof(RectTransform), typeof(Canvas), typeof(UnityEngine.UI.CanvasScaler), typeof(SettingsGameplayCrosshair));
            canvasObject.transform.SetParent(transform, false);
            var canvas = canvasObject.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 5;
            var scaler = canvasObject.GetComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = 0.5f;
            uiAudio = gameObject.AddComponent<AudioSource>(); uiAudio.playOnAwake = false; uiAudio.spatialBlend = 0;
        }
        public static GameSettingsData ReadSaved()
        {
            var result = GameSettingsData.Defaults();
            try
            {
                string json = PlayerPrefs.GetString(PreferenceKey, "");
                if (!string.IsNullOrEmpty(json)) JsonUtility.FromJsonOverwrite(json, result);
                result.Sanitize();
            }
            catch (Exception e) { Debug.LogWarning("[Settings] 本地设置无法读取，使用默认值。" + e.Message); result = GameSettingsData.Defaults(); }
            return result;
        }
        public static bool Save(GameSettingsData draft, out string error)
        {
            var next = draft.Copy(); next.Sanitize();
            string previous = PlayerPrefs.GetString(PreferenceKey, ""), previousTime = LastSaved;
            var before = Current.Copy();
            try
            {
                ApplyRuntime(next, true);
                PlayerPrefs.SetString(PreferenceKey, JsonUtility.ToJson(next));
                PlayerPrefs.SetString(PreferenceKey + ".time", DateTime.Now.ToString("HH:mm:ss"));
                PlayerPrefs.Save();
                current = next;
                Changed?.Invoke(current);
                error = ""; return true;
            }
            catch (Exception e)
            {
                current = before; ApplyRuntime(before, true);
                if (previous.Length > 0) PlayerPrefs.SetString(PreferenceKey, previous); else PlayerPrefs.DeleteKey(PreferenceKey);
                if (previousTime.Length > 0) PlayerPrefs.SetString(PreferenceKey + ".time", previousTime); else PlayerPrefs.DeleteKey(PreferenceKey + ".time");
                error = "设置保存失败：" + e.Message; return false;
            }
        }
        private static void ApplyRuntime(GameSettingsData data, bool display)
        {
            if (DedicatedServerBootstrap.IsDedicatedServerProcess) return;
            QualitySettings.SetQualityLevel(data.quality, true);
            // Quality presets also set vSync. The user's independent vSync choice takes precedence.
            QualitySettings.vSyncCount = data.vsync ? 1 : 0;
            Application.targetFrameRate = data.fpsCap;
            AudioListener.volume = data.mute ? 0 : data.master / 100f;
            if (display) ApplyDisplay(data);
        }
        public static void ApplyDisplay(GameSettingsData data)
        {
            if (DedicatedServerBootstrap.IsDedicatedServerProcess) return;
            if (!GameSettingsData.TryResolution(data.resolution, out int width, out int height)) return;
            var mode = data.displayMode == 0 ? FullScreenMode.ExclusiveFullScreen : data.displayMode == 1 ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;
            // Changing desktop resolution in an embedded Game view is neither meaningful nor safe.
            if (!Application.isEditor) Screen.SetResolution(width, height, mode);
        }
        private void SceneLoaded(Scene scene, LoadSceneMode mode)
        {
            bool menu = scene.name == "Login" || scene.name == "GameLobby";
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var t in root.GetComponentsInChildren<RectTransform>(true))
                    if (t.name == "CrossHair" || t.name == "CrossHairPoint")
                        foreach (var graphic in t.GetComponentsInChildren<UnityEngine.UI.Graphic>(true)) graphic.enabled = false;
                if (menu)
                {
                    foreach (var audio in root.GetComponentsInChildren<AudioSource>(true))
                        if (audio.loop && audio.spatialBlend < 0.1f && audio.GetComponent<SettingsAudioChannel>() == null)
                            audio.gameObject.AddComponent<SettingsAudioChannel>().Configure(audio, true);
                    foreach(var video in root.GetComponentsInChildren<UnityEngine.Video.VideoPlayer>(true))
                        if(video.GetComponent<SettingsVideoAudioChannel>()==null)video.gameObject.AddComponent<SettingsVideoAudioChannel>();
                }
            }
        }
        public static void PlayUiClick()
        {
            if (instance == null || instance.uiAudio == null || Current.uiSfx <= 0) return;
            if (instance.click == null)
            {
                const int samples = 1000;
                var wave = new float[samples];
                for (int i = 0; i < samples; i++) wave[i] = Mathf.Sin(i * 0.31f) * Mathf.Exp(-i / 140f) * 0.1f;
                instance.click = AudioClip.Create("Settings UI Click", samples, 1, 44100, false);
                instance.click.SetData(wave, 0);
            }
            instance.uiAudio.PlayOneShot(instance.click, Current.uiSfx / 100f);
        }
        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= SceneLoaded;
            if (click != null) Destroy(click);
            if (instance == this) instance = null;
        }
    }
}
