using UnityEngine;

namespace FPS.Settings
{
    /// <summary>Attach to out-of-match music or UI audio sources; world sounds retain their own mix.</summary>
    [DisallowMultipleComponent]
    public sealed class SettingsAudioChannel : MonoBehaviour
    {
        public AudioSource source;
        public bool music = true;
        private float originalVolume;
        private bool configured;
        public void Configure(AudioSource audioSource, bool isMusic)
        {
            if (!configured || source != audioSource) originalVolume = audioSource.volume;
            source = audioSource; music = isMusic; configured = true;
            Apply(GameSettingsService.Current);
        }
        private void Awake()
        {
            if (source == null) source = GetComponent<AudioSource>();
            if (source != null) { originalVolume = source.volume; configured = true; }
        }
        private void OnEnable() { GameSettingsService.Changed += Apply; Apply(GameSettingsService.Current); }
        private void OnDisable() { GameSettingsService.Changed -= Apply; if (source != null && configured) source.volume = originalVolume; }
        private void Apply(GameSettingsData data) { if (source != null && configured) source.volume = originalVolume * (music ? data.music : data.uiSfx) / 100f; }
    }
}
