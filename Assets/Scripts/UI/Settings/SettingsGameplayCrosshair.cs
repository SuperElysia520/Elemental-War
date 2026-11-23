using UnityEngine;

namespace FPS.Settings
{
    public sealed class SettingsGameplayCrosshair : MonoBehaviour
    {
        private SettingsCrosshairGraphic graphic;
        private GameSettingsData settings;
        private void Awake()
        {
            var go=new GameObject("Crosshair",typeof(RectTransform),typeof(SettingsCrosshairGraphic));go.transform.SetParent(transform,false);
            var rect=go.GetComponent<RectTransform>();rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(.5f,.5f);rect.sizeDelta=new Vector2(100,100);
            graphic=go.GetComponent<SettingsCrosshairGraphic>();graphic.raycastTarget=false;
        }
        private void OnEnable() { GameSettingsService.Changed+=Apply;Apply(GameSettingsService.Current); }
        private void OnDisable() { GameSettingsService.Changed-=Apply; }
        private void Apply(GameSettingsData data) { settings=data; if(graphic!=null) graphic.Configure(data); }
        private void LateUpdate()
        {
            var full=PvpFullBodyController.LocalPlayer;
            var legacy=PlayerController.instance;
            bool visible=false;float spread=0;
            if(full!=null && full.IsSpawned)
            {
                var health=full.GetComponent<PvpPlayerCombat>();
                visible=full.LocalControlsEnabled && full.AimFraction<.7f && (health==null||!health.IsDead.Value);
                spread=full.CrosshairActivity;
            }
            else if(legacy!=null && legacy.enabled && legacy.currentPlayerModel!=null)
            { visible=!legacy.isAiming && Cursor.lockState==CursorLockMode.Locked;spread=legacy.moveInput.magnitude*4+(legacy.isFire?5:0); }
            graphic.enabled=visible;
            if(visible) graphic.SetSpread(settings.x_gap+(settings.x_dynamic?spread:0));
        }
    }
}
