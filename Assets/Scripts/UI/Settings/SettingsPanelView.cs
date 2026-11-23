using System;
using System.Linq;
using FPS.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace FPS.Settings
{
    /// <summary>Authored uGUI view. Draft values are isolated until Apply succeeds.</summary>
    [DefaultExecutionOrder(-30)]
    public sealed class SettingsPanelView : LobbyPageView
    {
        public GameObject[] tabPages = new GameObject[0];
        public UnityEngine.UI.Button[] tabButtons = new UnityEngine.UI.Button[0], quickButtons = new UnityEngine.UI.Button[0];
        public SettingsShape[] tabHighlights = new SettingsShape[0], quickHighlights = new SettingsShape[0];
        public UnityEngine.UI.Text[] tabLabels = new UnityEngine.UI.Text[0], tabEnglish = new UnityEngine.UI.Text[0];
        public UnityEngine.UI.ScrollRect[] scrollViews = new UnityEngine.UI.ScrollRect[0];
        public UnityEngine.UI.Text headerPlayerName;
        public UnityEngine.UI.Text titleText, titleEnglish, titleNumber, watermarkText, watermarkNumber, profileText;
        public UnityEngine.UI.Text saveStatus, saveTime, feedbackText, previewDescription, previewMode;
        public UnityEngine.UI.Button applyButton, cancelButton, resetButton, backButton;
        public SettingsCrosshairGraphic preview, zoomPreview;
        public GameObject feedback, captureBlocker, modal;
        public UnityEngine.UI.Text modalTitle, modalBody, modalConfirmLabel;
        public UnityEngine.UI.Button modalConfirm, modalCancel;
        public SettingsRowView[] rows = new SettingsRowView[0];
        public event Action CloseRequested;
        public GameSettingsData Draft { get; private set; }
        public int SelectedTab { get; private set; }
        public bool HasUnsavedChanges => Draft != null && Draft.Differences(saved)>0;
        public bool IsCapturing => captureId != null;
        public int InputConsumedFrame { get; private set; } = -1;
        private GameSettingsData saved;
        private string captureId;
        private int captureFrame;
        private int modalOpenedFrame = -1;
        private float feedbackUntil, displayDeadline;
        private Action confirmAction;
        private bool displayPreview;
        private static readonly Color Cyan = SettingsPalette.Gold;
        private static readonly Color Dim = SettingsPalette.White;
        protected override void Awake()
        {
            base.Awake();
            if(rows.Length==0)rows=GetComponentsInChildren<SettingsRowView>(true);
            foreach(var row in rows)row.Bind(this);
            for(int i=0;i<tabButtons.Length;i++){int tab=i;tabButtons[i].onClick.AddListener(()=>SelectTab(tab));}
            for(int i=0;i<quickButtons.Length;i++){int quality=i;quickButtons[i].onClick.AddListener(()=>Change("quality",quality));}
            applyButton.onClick.AddListener(Apply);
            cancelButton.onClick.AddListener(Revert);
            resetButton.onClick.AddListener(RequestReset);
            backButton.onClick.AddListener(RequestClose);
            modalConfirm.onClick.AddListener(Confirm);
            modalCancel.onClick.AddListener(CancelModal);
        }
        private void OnEnable()
        {
            saved=GameSettingsService.Current.Copy();Draft=saved.Copy();
            if(rows==null||rows.Length==0)return;
            captureId=null;displayPreview=false;confirmAction=null;
            modal.SetActive(false);captureBlocker.SetActive(false);feedback.SetActive(false);
            SelectTab(0);Refresh();
        }
        private void OnDisable()
        {
            if(displayPreview&&saved!=null)GameSettingsService.ApplyDisplay(saved);
            displayPreview=false;captureId=null;
        }
        public override void Render(PlayerProfileData profile,PlayerAvatarCatalog catalog,string message,bool busy)
        { base.Render(profile,catalog,"",busy); if(headerPlayerName!=null)headerPlayerName.text=profile!=null?profile.DisplayName:"PLAYER"; }
        private void Update()
        {
            if(feedback.activeSelf&&Time.unscaledTime>feedbackUntil)feedback.SetActive(false);
            if(displayPreview)
            {
                int seconds=Mathf.Max(0,Mathf.CeilToInt(displayDeadline-Time.unscaledTime));
                modalBody.text="请确认画面显示正常。"+seconds+" 秒后将自动恢复之前的显示设置。";
                if(seconds==0)CancelModal();
            }
            var keyboard=Keyboard.current;
            if(captureId!=null)
            {
                if(Time.frameCount<=captureFrame)return;
                if(keyboard!=null)
                {
                    if(keyboard.escapeKey.wasPressedThisFrame){EndCapture();InputConsumedFrame=Time.frameCount;return;}
                    foreach(var key in keyboard.allKeys)
                        if(key!=null&&key.wasPressedThisFrame){CaptureBinding("<Keyboard>/"+key.name);return;}
                }
                var mouse=Mouse.current;
                if(mouse!=null)
                {
                    if(mouse.leftButton.wasPressedThisFrame)CaptureBinding("<Mouse>/leftButton");
                    else if(mouse.rightButton.wasPressedThisFrame)CaptureBinding("<Mouse>/rightButton");
                    else if(mouse.middleButton.wasPressedThisFrame)CaptureBinding("<Mouse>/middleButton");
                    else if(mouse.forwardButton.wasPressedThisFrame)CaptureBinding("<Mouse>/forwardButton");
                    else if(mouse.backButton.wasPressedThisFrame)CaptureBinding("<Mouse>/backButton");
                }
                return;
            }
            if(keyboard!=null&&keyboard.escapeKey.wasPressedThisFrame)
            {
                if(modal.activeSelf){CancelModal();InputConsumedFrame=Time.frameCount;return;}
                foreach(var row in rows)if(row.dropdown!=null&&row.dropdown.IsExpanded)
                {row.dropdown.Hide();InputConsumedFrame=Time.frameCount;return;}
            }
            if(modal.activeSelf)return;
            if (keyboard != null && EventSystem.current != null && tabButtons.Any(b => b.gameObject == EventSystem.current.currentSelectedGameObject))
            {
                if (keyboard.downArrowKey.wasPressedThisFrame) { SelectTab((SelectedTab + 1) % 5); tabButtons[SelectedTab].Select(); }
                else if (keyboard.upArrowKey.wasPressedThisFrame) { SelectTab((SelectedTab + 4) % 5); tabButtons[SelectedTab].Select(); }
            }
            if(keyboard!=null&&keyboard.enterKey.wasPressedThisFrame&&(keyboard.ctrlKey.isPressed||keyboard.leftMetaKey.isPressed||keyboard.rightMetaKey.isPressed))Apply();
        }
        public void SelectTab(int index)
        {
            index=Mathf.Clamp(index,0,4);EndCapture();
            foreach(var row in rows)if(row.dropdown!=null)row.dropdown.Hide();
            SelectedTab=index;
            for(int i=0;i<tabPages.Length;i++)
            {
                bool on=i==index;tabPages[i].SetActive(on);
                tabHighlights[i].color=on?Cyan:SettingsPalette.Tab;
                var edge=tabButtons[i].transform.Find("ActiveEdge");if(edge!=null)edge.gameObject.SetActive(false);
                var icon=tabButtons[i].transform.Find("Icon")?.GetComponent<UnityEngine.UI.Graphic>();if(icon!=null)icon.color=on?SettingsPalette.Navy:Color.white;
                tabLabels[i].color=on?SettingsPalette.Navy:Dim;tabEnglish[i].color=SettingsPalette.Secondary;
            }
            // Preload all category titles together so switching tabs does not introduce
            // new large Chinese glyphs midway through a legacy font-atlas rebuild.
            if(titleText.font!=null)titleText.font.RequestCharactersInTexture(string.Concat(SettingsSchema.Titles)+"设置",titleText.fontSize,titleText.fontStyle);
            titleText.text=SettingsSchema.Titles[index]+"设置";titleText.cachedTextGenerator.Invalidate();titleText.SetAllDirty();
            if(titleEnglish!=null)titleEnglish.text=SettingsSchema.Subtitles[index];
            titleNumber.text=(index+1).ToString("00");watermarkText.text=SettingsSchema.English[index];watermarkNumber.text=(index+1).ToString("00");
            watermarkText.gameObject.SetActive(false);watermarkNumber.gameObject.SetActive(false);
            Canvas.ForceUpdateCanvases();
            if(scrollViews[index]!=null)
            {
                var scroll=scrollViews[index];scroll.StopMovement();scroll.verticalNormalizedPosition=1;
                scroll.verticalScrollbar.gameObject.SetActive(scroll.content.rect.height>scroll.viewport.rect.height+.5f);
            }
        }
        public void Change(string id,object value)
        {
            if(Draft==null||modal.activeSelf)return;
            SettingsSchema.Find(id)?.Set(Draft,value);Refresh();GameSettingsService.PlayUiClick();
        }
        public void Refresh()
        {
            if(Draft==null||saved==null)return;
            foreach(var row in rows)row.Refresh(Draft,row.settingId==captureId);
            int changed=Draft.Differences(saved);
            saveStatus.text=changed==0?"配置已同步":changed+" 项未保存";
            saveStatus.color=changed==0?SettingsPalette.White:Cyan;
            saveTime.text="上次保存 · "+(GameSettingsService.LastSaved.Length>0?GameSettingsService.LastSaved:"--:--:--");
            applyButton.interactable=changed>0;cancelButton.interactable=changed>0;
            var names=SettingsSchema.QualityNames;profileText.text=names[Mathf.Clamp(Draft.quality,0,names.Length-1)];
            for(int i=0;i<quickHighlights.Length;i++)
            {
                bool selected=i==Draft.quality;quickHighlights[i].color=selected?Cyan:SettingsPalette.Control;
                var label=quickButtons[i].GetComponentInChildren<UnityEngine.UI.Text>();if(label!=null)label.color=selected?SettingsPalette.Navy:SettingsPalette.White;
            }
            if(preview!=null)preview.Configure(Draft);if(zoomPreview!=null)zoomPreview.Configure(Draft);
            if(previewDescription!=null)previewDescription.text=SettingsSchema.Find("x_style").options[Draft.x_style]+" · "+SettingsSchema.Find("x_color").options[Draft.x_color]+" · 长"+Draft.x_len+" 隙"+Draft.x_gap+" 粗"+Draft.x_thick;
            if(previewMode!=null)previewMode.text=Draft.x_dynamic?"动态扩散":"静态准心";
        }
        private void LateUpdate()
        {
            if(SelectedTab!=4||Draft==null)return;
            float spread=Draft.x_dynamic?(1-Mathf.Cos(Time.unscaledTime*3.3f))*2.5f:0;
            preview.SetSpread(Draft.x_gap+spread);zoomPreview.SetSpread(Draft.x_gap+spread);
        }
        public void Apply()
        {
            if(!HasUnsavedChanges||captureId!=null||modal.activeSelf)return;
            Draft.Sanitize();
            if(!Draft.DisplayEquals(saved))
            {
                GameSettingsService.ApplyDisplay(Draft);displayPreview=true;displayDeadline=Time.unscaledTime+15;
                OpenModal("保留新的显示设置？","", "保留并应用",Commit);return;
            }
            Commit();
        }
        private void Commit()
        {
            displayPreview=false;
            if(GameSettingsService.Save(Draft,out string error))
            { saved=GameSettingsService.Current.Copy();Draft=saved.Copy();Notify("配置已保存 // CONFIG SAVED"); }
            else Notify(error);
            Refresh();
        }
        public void Revert()
        { if(modal.activeSelf)return;EndCapture();Draft=saved.Copy();Refresh();Notify("更改已还原 // REVERTED"); }
        public void RequestReset()
        { if(modal.activeSelf)return;OpenModal("恢复默认设置？","所有设置将恢复默认值。点击“应用”后保存，账号数据不受影响。","恢复默认",()=>{Draft=GameSettingsData.Defaults();Refresh();}); }
        public void RequestClose()
        {
            if(InputConsumedFrame==Time.frameCount)return;
            if(captureId!=null){EndCapture();return;}
            if(modal.activeSelf){CancelModal();return;}
            foreach(var row in rows)if(row.dropdown!=null&&row.dropdown.IsExpanded){row.dropdown.Hide();return;}
            if(HasUnsavedChanges)OpenModal("放弃尚未保存的更改？","离开后将使用上次保存的设置。","放弃并返回",()=>{Draft=saved.Copy();CloseRequested?.Invoke();});
            else CloseRequested?.Invoke();
        }
        private void OpenModal(string title,string body,string confirm,Action action)
        {
            EndCapture();modalTitle.text=title;modalBody.text=body;modalConfirmLabel.text=confirm;confirmAction=action;
            modalOpenedFrame=Time.frameCount;
            modal.SetActive(true);modalConfirm.Select();
        }
        private void Confirm()
        {
            // The Enter press that opened this dialog must not also accept it through EventSystem Submit.
            if(Time.frameCount<=modalOpenedFrame)return;
            var action=confirmAction;confirmAction=null;modal.SetActive(false);action?.Invoke();
        }
        public void CancelModal()
        {
            if(displayPreview){GameSettingsService.ApplyDisplay(saved);displayPreview=false;Draft.displayMode=saved.displayMode;Draft.resolution=saved.resolution;Refresh();Notify("已恢复之前的显示设置");}
            modal.SetActive(false);confirmAction=null;
        }
        public void StartCapture(string id)
        {
            if(modal.activeSelf)return;
            captureId=id;captureFrame=Time.frameCount;captureBlocker.SetActive(true);
            Refresh();Notify("请按下新的键位，ESC 取消");
        }
        private void EndCapture()
        { if(captureId==null)return;captureId=null;captureBlocker.SetActive(false);Refresh(); }
        public void CaptureBinding(string path)
        {
            if(captureId==null)return;
            InputConsumedFrame=Time.frameCount;
            if(!SettingsPlayerInput.ValidBinding(path)){Notify("此键为系统保留或固定操作键，请选择其他键位。");return;}
            var target=SettingsSchema.Find(captureId);string previous=(string)target.Get(Draft);
            var conflict=SettingsSchema.Rows.FirstOrDefault(r=>r.kind==SettingKind.Key&&r.id!=target.id&&(string)r.Get(Draft)==path);
            EndCapture();
            if(conflict!=null)
                OpenModal("按键已被占用",SettingsPlayerInput.DisplayName(path)+" 已绑定“"+conflict.label+"”，是否与“"+target.label+"”交换？","交换按键",()=>{conflict.Set(Draft,previous);target.Set(Draft,path);Refresh();});
            else{target.Set(Draft,path);Refresh();Notify("“"+target.label+"”已绑定 "+SettingsPlayerInput.DisplayName(path));}
        }
        private void Notify(string text) { feedbackText.text=text;feedback.SetActive(true);feedbackUntil=Time.unscaledTime+3; }
#if UNITY_EDITOR
        public void InitializeEditorPreview()
        {
            saved=GameSettingsData.Defaults();Draft=saved.Copy();
            SelectTab(0);Refresh();
        }
#endif
    }
}
