#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FPS.Settings;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

/// <summary>Runs only in the isolated, offline settings validation scene created by the editor tool.</summary>
public sealed class SettingsRuntimeValidationProbe : MonoBehaviour
{
    [Serializable] private sealed class Report { public bool success; public List<string> checks=new List<string>();public List<string> errors=new List<string>(); }
    private readonly Report report=new Report();
    private Keyboard keyboard, previousKeyboard;
    private SettingsPlayerInput configured;
    private MyInputSystem source;
    private bool background;
    private IEnumerator Start()
    {
        background=Application.runInBackground;Application.runInBackground=true;
        Application.logMessageReceived+=ObserveError;
        var steps=Steps();
        while(true)
        {
            object value=null;bool moved;
            try{moved=steps.MoveNext();if(moved)value=steps.Current;}
            catch(Exception e){report.errors.Add(e.ToString());break;}
            if(!moved)break;
            yield return value;
        }
        configured?.Dispose();source?.Dispose();
        if(keyboard!=null)InputSystem.RemoveDevice(keyboard);
        previousKeyboard?.MakeCurrent();
        Application.logMessageReceived-=ObserveError;
        report.success=report.errors.Count==0;
        File.WriteAllText("tmp/Settings20261116/runtime-validation.json",JsonUtility.ToJson(report,true));
        Application.runInBackground=background;
        UnityEditor.EditorApplication.delayCall+=()=>UnityEditor.EditorApplication.ExitPlaymode();
    }
    private IEnumerator Steps()
    {
        yield return null;yield return null;
        var view=FindObjectOfType<SettingsPanelView>();
        Check(view!=null&&view.rows.Length==34,"Settings prefab opens as real uGUI in Play Mode.");
        Check(view.GetComponentsInChildren<TMPro.TMP_Text>(true).Length==0&&view.GetComponentsInChildren<TMPro.TMP_Dropdown>(true).Length==0,"Settings contains no TMP text or TMP dropdown components, including hidden dialogs and option templates.");
        Check(view.GetComponentsInChildren<UnityEngine.UI.Text>(true).All(t=>t.font!=null),"Every legacy Text has an actual Font source.");
        var original=GameSettingsService.Current.Copy();float originalVolume=AudioListener.volume;
        var masterSlider=view.rows.First(r=>r.settingId=="master").slider;masterSlider.value=19;
        Check(view.Draft.master==19&&view.HasUnsavedChanges,"The authored volume slider still changes the settings draft through its event.");view.Revert();
        var syncToggle=view.rows.First(r=>r.settingId=="vsync").toggle;syncToggle.isOn=!original.vsync;
        Check(view.Draft.vsync!=original.vsync,"The authored toggle still changes the settings draft through its event.");view.Revert();
        view.Change("master",17f);
        Check(view.HasUnsavedChanges&&Mathf.Approximately(AudioListener.volume,originalVolume),"Editing a draft does not change saved audio.");
        view.cancelButton.onClick.Invoke();Check(!view.HasUnsavedChanges&&view.Draft.master==original.master,"Cancel restores the saved snapshot.");
        view.Change("master",42f);view.Change("mute",false);view.Change("fpsCap",60);view.Change("vsync",false);view.Change("quality",0);
        view.Change("sens",1.7f);view.Change("ads",.65f);view.Change("aimMode",1);view.Change("sprintMode",1);view.Change("crouchMode",1);
        view.Change("kb_forward","<Keyboard>/f");view.applyButton.onClick.Invoke();
        Check(!view.HasUnsavedChanges&&GameSettingsService.Current.master==42,"Apply commits the draft.");
        Check(Mathf.Approximately(AudioListener.volume,.42f)&&Application.targetFrameRate==60&&QualitySettings.vSyncCount==0&&QualitySettings.GetQualityLevel()==0,"Master volume, quality, frame cap and vSync take effect.");
        var loaded=GameSettingsService.ReadSaved();Check(loaded.Differences(GameSettingsService.Current)==0,"Settings survive a fresh read of persisted JSON.");
        view.StartCapture("kb_forward");view.CaptureBinding("<Keyboard>/s");
        Check(view.modal.activeSelf&&view.modalBody.text.Contains("后退"),"Conflicting keys present an explicit swap dialog.");
        yield return null;
        view.modalConfirm.onClick.Invoke();Check(view.Draft.kb_forward=="<Keyboard>/s"&&view.Draft.kb_back=="<Keyboard>/f","Confirm swaps both actions without duplicate bindings.");
        view.Revert();
        view.StartCapture("kb_forward");view.CaptureBinding("<Keyboard>/v");Check(view.IsCapturing,"Fixed weapon-mode key cannot silently conflict with a rebind.");
        yield return null;
        view.RequestClose();Check(!view.IsCapturing,"Back cancels key capture before closing the settings.");
        view.RequestReset();Check(view.modal.activeSelf,"Restore defaults requires confirmation.");yield return null;view.modalConfirm.onClick.Invoke();Check(view.HasUnsavedChanges&&GameSettingsService.Current.master==42,"Restore defaults remains a draft until Apply.");view.Revert();
        yield return null;
        bool closed=false;view.CloseRequested+=()=>closed=true;view.Change("master",41f);view.RequestClose();Check(view.modal.activeSelf&&!closed,"Leaving with unsaved changes asks before discarding.");view.modalCancel.onClick.Invoke();Check(view.HasUnsavedChanges&&!closed,"Cancelling discard keeps the draft.");view.Revert();
        view.Change("displayMode",(view.Draft.displayMode+1)%3);view.Apply();Check(view.modal.activeSelf,"Display changes use a safety confirmation.");view.modalConfirm.onClick.Invoke();Check(view.modal.activeSelf,"The key press that opens display confirmation cannot also accept it.");view.CancelModal();Check(view.Draft.DisplayEquals(GameSettingsService.Current),"Rejecting display changes restores the prior mode.");
        var musicRoot=new GameObject("ValidationMusic",typeof(AudioSource));var audio=musicRoot.GetComponent<AudioSource>();audio.volume=.8f;
        var channel=musicRoot.AddComponent<SettingsAudioChannel>();channel.Configure(audio,true);
        Check(Mathf.Abs(audio.volume-.8f*GameSettingsService.Current.music/100)<.001f,"Menu music gain applies once, preserving authored source volume.");
        view.Change("music",25f);view.Apply();Check(Mathf.Abs(audio.volume-.2f)<.001f,"Applying menu music volume updates an already-playing audio channel.");Destroy(musicRoot);
        source=new MyInputSystem();configured=new SettingsPlayerInput(source);source.Enable();configured.Enable();
        Check(source.Player.Move.bindings.Any(b=>b.effectivePath=="<Keyboard>/f"),"New player input instances load saved movement bindings.");
        previousKeyboard=Keyboard.current;keyboard=InputSystem.AddDevice<Keyboard>();
        KeyState(Key.F);Check(source.Player.Move.ReadValue<Vector2>().y>.9f,"Rebound forward key produces actual movement input.");
        KeyState();configured.Tick(true);
        // Use keyboard aiming to exercise the same hold/toggle reader without a mouse event.
        var sample=GameSettingsService.Current.Copy();sample.kb_aim="<Keyboard>/t";sample.kb_sprint="<Keyboard>/e";sample.kb_crouch="<Keyboard>/c";
        Check(GameSettingsService.Save(sample,out _),"Gameplay key changes can be saved while an input instance exists.");
        KeyState(Key.T);configured.Tick(true);Check(configured.Aim,"Toggle aim activates on key-down.");
        KeyState();configured.Tick(true);Check(configured.Aim,"Toggle aim stays active after key release.");
        KeyState(Key.T);configured.Tick(true);Check(!configured.Aim,"A second press releases toggle aim.");
        KeyState(Key.E);configured.Tick(true);KeyState();configured.Tick(true);Check(configured.Sprint,"Toggle sprint persists after release.");
        KeyState(Key.C);configured.Tick(true);KeyState();configured.Tick(true);Check(configured.Crouch,"Toggle crouch persists after release.");
        configured.Tick(false);Check(!configured.Aim&&!configured.Sprint&&!configured.Crouch,"Losing player control clears all toggle latches.");
        sample.aimMode=0;Check(GameSettingsService.Save(sample,out _),"Hold-mode change applies to live input.");
        KeyState(Key.T);configured.Tick(true);Check(configured.Aim,"Hold aim activates while pressed.");KeyState();configured.Tick(true);Check(!configured.Aim,"Hold aim ends on release.");
        var capsuleRoot=new GameObject("ValidationCapsule",typeof(CharacterController));var capsule=capsuleRoot.GetComponent<CharacterController>();capsule.height=2;capsule.center=new Vector3(0,1,0);capsule.radius=.3f;
        var crouch=new SettingsCrouch(capsule);crouch.Apply(true);Check(crouch.IsCrouching&&capsule.height<2&&crouch.EyeDrop>0,"Crouching reduces capsule and camera height.");
        var ceiling=GameObject.CreatePrimitive(PrimitiveType.Cube);ceiling.transform.position=new Vector3(0,1.8f,0);ceiling.transform.localScale=new Vector3(2,.2f,2);Physics.SyncTransforms();crouch.Apply(false);Check(crouch.IsCrouching,"A low ceiling prevents the player from standing into geometry.");
        Destroy(ceiling);yield return null;Physics.SyncTransforms();crouch.Apply(false);Check(!crouch.IsCrouching&&Mathf.Approximately(capsule.height,2),"The player can stand once the ceiling clears.");Destroy(capsuleRoot);
        view.gameObject.SetActive(false);view.gameObject.SetActive(true);
        Check(view.Draft.Differences(GameSettingsService.Current)==0,"Reopening settings loads the latest saved configuration.");
        for(int tab=0;tab<5;tab++)
        {
            view.SelectTab(tab);yield return null;Canvas.ForceUpdateCanvases();
            Check(view.tabPages.Count(p=>p.activeSelf)==1,"Only the selected page is active: "+SettingsSchema.English[tab]);
            Check(view.scrollViews[tab].viewport.rect.width>0&&view.scrollViews[tab].viewport.rect.height>0,"Viewport has visible bounds: "+SettingsSchema.English[tab]);
            yield return new WaitForEndOfFrame();
            yield return null;yield return new WaitForEndOfFrame();
            string page=SettingsSchema.English[tab].ToLowerInvariant().Replace(" ","-");
            ScreenCapture.CaptureScreenshot("tmp/SettingsLegacy20261118/runtime-"+page+".png");
            File.AppendAllText("tmp/SettingsLegacy20261118/runtime-typography.txt",page+": "+view.titleText.text+"; visible characters="+view.titleText.cachedTextGenerator.characterCountVisible+"\n");
            yield return null;
        }
        view.SelectTab(3);yield return null;
        var keys=view.scrollViews[3];Check(keys.content.rect.height>keys.viewport.rect.height,"The full list of key bindings is scrollable.");keys.verticalNormalizedPosition=0;yield return null;
        var switchRow=view.rows.First(r=>r.settingId=="kb_switch");Check(switchRow.gameObject.activeInHierarchy,"The final weapon-switch binding remains accessible.");
        view.SelectTab(0);yield return null;
        var displayRow=view.rows.First(r=>r.settingId=="displayMode");var button=displayRow.choices[2];
        var point=RectTransformUtility.WorldToScreenPoint(null,button.GetComponent<RectTransform>().TransformPoint(button.GetComponent<RectTransform>().rect.center));
        var hits=new List<RaycastResult>();EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current){position=point},hits);
        Check(hits.Count>0&&ExecuteEvents.GetEventHandler<IPointerClickHandler>(hits[0].gameObject)==button.gameObject,"The visible settings button is reached by the real EventSystem raycast.");
        var dropdown=view.rows.First(r=>r.settingId=="resolution").dropdown;dropdown.Show();yield return null;Check(dropdown.IsExpanded,"Resolution dropdown creates a native interactive option list.");
        string selectedResolution=dropdown.options.First(o=>o.text!=view.Draft.resolution).text;
        var option=dropdown.transform.Find("Dropdown List").GetComponentsInChildren<UnityEngine.UI.Toggle>().First(t=>t.GetComponentInChildren<UnityEngine.UI.Text>().text==selectedResolution);
        option.isOn=true;yield return null;
        Check(view.Draft.resolution==selectedResolution,"Selecting an actual legacy dropdown option updates the resolution draft.");
        yield return new WaitForSecondsRealtime(.2f);view.Revert();
        dropdown.Show();yield return null;view.RequestClose();
        yield return new WaitForSecondsRealtime(.2f);
        Check(!dropdown.IsExpanded,"Back closes the legacy dropdown before navigating away.");
        view.SelectTab(4);view.Change("x_style",2);view.Change("x_color",2);view.Change("x_len",13f);
        Check(view.preview.style==2&&view.zoomPreview.length==13&&view.preview.color==SettingsCrosshairGraphic.Palette[2],"Crosshair controls update both 1:1 and zoomed previews.");
        view.Apply();var gameplay=FindObjectOfType<SettingsGameplayCrosshair>();Check(gameplay!=null&&gameplay.GetComponentInChildren<SettingsCrosshairGraphic>().style==2,"Saved crosshair style reaches the persistent gameplay renderer.");
        view.SelectTab(0);yield return new WaitForEndOfFrame();
        ScreenCapture.CaptureScreenshot("tmp/Settings20261116/runtime-gameview.png");yield return null;yield return null;
        Check(FindObjectOfType<FPS.UI.LobbyUIRoot>()==null&&FindObjectOfType<Unity.Netcode.NetworkManager>()==null,"Validation remains isolated from accounts, matchmaking and network services.");
    }
    private void KeyState(params Key[] keys)
    {InputSystem.QueueStateEvent(keyboard,new KeyboardState(keys));InputSystem.Update();}
    private void Check(bool condition,string description)
    {if(condition)report.checks.Add(description);else report.errors.Add(description);}
    private void ObserveError(string message,string stackTrace,LogType type)
    {if((type==LogType.Exception||type==LogType.Error)&&stackTrace.Contains("FPS.Settings."))report.errors.Add(message+"\n"+stackTrace);}
}
#endif
