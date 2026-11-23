using System;
using UnityEngine;

namespace FPS.Settings
{
    public sealed class SettingsRowView : MonoBehaviour
    {
        public string settingId;
        public UnityEngine.UI.Button[] choices = new UnityEngine.UI.Button[0];
        public SettingsShape[] choiceBackgrounds = new SettingsShape[0];
        public UnityEngine.UI.Text[] choiceLabels = new UnityEngine.UI.Text[0];
        public UnityEngine.UI.Slider slider;
        public UnityEngine.UI.Toggle toggle;
        public SettingsDropdown dropdown;
        public UnityEngine.UI.Button keyButton;
        public UnityEngine.UI.Text valueText;
        public RectTransform toggleKnob;
        public SettingsShape toggleTrack;
        private SettingsPanelView owner;
        private SettingDefinition definition;
        private static readonly Color Cyan = SettingsPalette.Gold;
        private static readonly Color Muted = SettingsPalette.White;
        private static readonly Color Dark = SettingsPalette.Navy;
        public void Bind(SettingsPanelView panel)
        {
            owner = panel; definition = SettingsSchema.Find(settingId);
            for (int i=0;i<choices.Length;i++)
            {
                int index=i; choices[i].onClick.AddListener(()=>SetChoice(index));
            }
            if(slider!=null) slider.onValueChanged.AddListener(v=>
            { float step=definition.step;owner.Change(settingId,Mathf.Round(v/step)*step); });
            if(toggle!=null) toggle.onValueChanged.AddListener(v=>owner.Change(settingId,v));
            if(dropdown!=null)
            {
                dropdown.ClearOptions();dropdown.AddOptions(new System.Collections.Generic.List<string>(settingId=="resolution"?SettingsSchema.Resolutions:definition.options));
                dropdown.onValueChanged.AddListener(i=>owner.Change(settingId,settingId=="resolution"?(object)dropdown.options[i].text:i));
            }
            if(keyButton!=null)keyButton.onClick.AddListener(()=>owner.StartCapture(settingId));
        }
        private void SetChoice(int index)
        {
            object value=settingId=="fpsCap"?new[]{60,120,144,240,-1}[index]:index;
            owner.Change(settingId,value);
        }
        public void Refresh(GameSettingsData data,bool capturing=false)
        {
            if(definition==null)definition=SettingsSchema.Find(settingId);
            if(definition==null)return;
            if(choices.Length>0)
            {
                int selected=settingId=="fpsCap"?Array.IndexOf(new[]{60,120,144,240,-1},data.fpsCap):Convert.ToInt32(definition.Get(data));
                for(int i=0;i<choices.Length;i++)
                {
                    bool on=i==selected;
                    choiceBackgrounds[i].color=on?Cyan:new Color(0,0,0,0);
                    choiceLabels[i].color=on?Dark:Muted;
                }
            }
            if(slider!=null) slider.SetValueWithoutNotify(Convert.ToSingle(definition.Get(data)));
            if(toggle!=null)
            {
                bool on=(bool)definition.Get(data);toggle.SetIsOnWithoutNotify(on);
                toggleTrack.color=on?Cyan:SettingsPalette.Control;
                float diameter=toggleKnob.rect.height;
                float inset=diameter*.5f+5;
                toggleKnob.anchoredPosition=new Vector2(on?toggleTrack.rectTransform.rect.width-inset:inset,-toggleTrack.rectTransform.rect.height*.5f);
                toggleKnob.GetComponent<SettingsShape>().color=SettingsPalette.White;
                valueText.text=on?"开启":"关闭";valueText.color=Muted;
            }
            else if(valueText!=null)valueText.text=capturing?"按下按键…":definition.Format(data);
            if(dropdown!=null)
            {
                int index=settingId=="resolution"?dropdown.options.FindIndex(o=>o.text==data.resolution):Convert.ToInt32(definition.Get(data));
                if(index<0&&settingId=="resolution") { dropdown.options.Add(new UnityEngine.UI.Dropdown.OptionData(data.resolution));index=dropdown.options.Count-1; }
                dropdown.SetValueWithoutNotify(Mathf.Max(0,index));dropdown.RefreshShownValue();
            }
            if(keyButton!=null)
            { keyButton.targetGraphic.color=capturing?Cyan:SettingsPalette.Control;valueText.color=capturing?Dark:SettingsPalette.White; }
        }
    }
}
