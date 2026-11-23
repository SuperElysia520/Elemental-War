using UnityEngine;
using UnityEngine.Video;

namespace FPS.Settings
{
    /// <summary>Menu video audio may bypass AudioListener; apply master and music gains to direct tracks too.</summary>
    [RequireComponent(typeof(VideoPlayer))]
    public sealed class SettingsVideoAudioChannel : MonoBehaviour
    {
        private VideoPlayer player;
        private float[] authoredVolume;
        private bool[] authoredMute;
        private void Awake() { player=GetComponent<VideoPlayer>(); }
        private void OnEnable()
        {
            player.prepareCompleted+=Prepared;GameSettingsService.Changed+=Apply;
            if(player.isPrepared)Apply(GameSettingsService.Current);
        }
        private void OnDisable()
        {
            player.prepareCompleted-=Prepared;GameSettingsService.Changed-=Apply;
            if(authoredVolume==null||!player.isPrepared||player.audioOutputMode!=VideoAudioOutputMode.Direct)return;
            for(ushort i=0;i<authoredVolume.Length&&i<player.audioTrackCount;i++)
            {player.SetDirectAudioVolume(i,authoredVolume[i]);player.SetDirectAudioMute(i,authoredMute[i]);}
        }
        private void Prepared(VideoPlayer _) { Apply(GameSettingsService.Current); }
        private void Apply(GameSettingsData data)
        {
            if(!player.isPrepared)return;
            int tracks=Mathf.Min(player.audioTrackCount,player.controlledAudioTrackCount);
            if(player.audioOutputMode==VideoAudioOutputMode.Direct)
            {
                if(authoredVolume==null||authoredVolume.Length!=tracks)
                {
                    authoredVolume=new float[tracks];authoredMute=new bool[tracks];
                    for(ushort i=0;i<tracks;i++){authoredVolume[i]=player.GetDirectAudioVolume(i);authoredMute[i]=player.GetDirectAudioMute(i);}
                }
                for(ushort i=0;i<tracks;i++)
                {
                    player.SetDirectAudioVolume(i,authoredVolume[i]*data.music*data.master/10000f);
                    player.SetDirectAudioMute(i,data.mute||authoredMute[i]);
                }
            }
            else if(player.audioOutputMode==VideoAudioOutputMode.AudioSource)
                for(ushort i=0;i<tracks;i++)
                {
                    var audio=player.GetTargetAudioSource(i);
                    if(audio!=null&&audio.GetComponent<SettingsAudioChannel>()==null)audio.gameObject.AddComponent<SettingsAudioChannel>().Configure(audio,true);
                }
        }
    }
}
