using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FPS.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

/// <summary>Real login/lobby scenes and uGUI interactions, isolated only at the cloud boundary.</summary>
[InitializeOnLoad]
public static class UnifiedLobbyValidation
{
    private const string Prefix="FPS.UnifiedLobbyValidation.";
    private static readonly FieldInfo Factory=typeof(AccountClient).GetField("UgsGatewayFactory",BindingFlags.Static|BindingFlags.NonPublic);
    private static readonly Stack<IEnumerator> steps=new Stack<IEnumerator>();
    private static Gateway fake;
    private static object previousFactory;
    private static Report report;
    private static bool Running=>SessionState.GetBool(Prefix+"running",false);
    private static string Stage{get=>SessionState.GetString(Prefix+"stage","");set=>SessionState.SetString(Prefix+"stage",value);}
    [Serializable]private sealed class Report
    {
        public bool success,restored;
        public string scope="Real Login/GameLobby, real buttons and account/party controllers; offline cloud gateway, no production requests.";
        public List<string> checks=new List<string>(),errors=new List<string>(),baselineErrors=new List<string>();
    }
    static UnifiedLobbyValidation()
    {
        EditorApplication.update+=Tick;
        EditorApplication.playModeStateChanged+=state=>{if(Running&&state==PlayModeStateChange.EnteredPlayMode)Stage="entered";};
        Application.logMessageReceived+=Log;
        if(Running&&Stage=="testing")EditorApplication.delayCall+=()=>Finish("Scripts changed during verification.");
    }
    [MenuItem("Tools/FPS/Lobby/Validate Unified Homepage Runtime (Offline)")]
    public static void Run()
    {
        if(Running||EditorApplication.isPlayingOrWillChangePlaymode)throw new InvalidOperationException("Stop Play Mode first.");
        UnifiedLobbyBuilder.Validate();
        var scene=SceneManager.GetActiveScene();
        if(scene.isDirty)throw new InvalidOperationException("Save the open scene before running lobby validation.");
        report=new Report();Store();
        SessionState.SetString(Prefix+"scene",scene.path);
        SessionState.SetString(Prefix+"playStart",AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene));
        SessionState.SetString(Prefix+"environment",Environment.GetEnvironmentVariable("FPS_ACCOUNT_BACKEND")??"");
        SessionState.SetBool(Prefix+"hadEnvironment",Environment.GetEnvironmentVariable("FPS_ACCOUNT_BACKEND")!=null);
        SessionState.SetBool(Prefix+"background",Application.runInBackground);
        SessionState.SetString(Prefix+"started",DateTime.UtcNow.Ticks.ToString());
        SessionState.SetBool(Prefix+"running",true);Stage="starting";
        Environment.SetEnvironmentVariable("FPS_ACCOUNT_BACKEND","UnityGamingServices");
        EditorSceneManager.playModeStartScene=AssetDatabase.LoadAssetAtPath<SceneAsset>("Assets/Scenes/Login.unity");
        EditorApplication.isPaused=false;EditorApplication.isPlaying=true;
    }
    private static void Tick()
    {
        if(!Running)return;
        if(EditorApplication.isPlaying)EditorApplication.QueuePlayerLoopUpdate();
        if(Stage=="stopping"&&!EditorApplication.isPlayingOrWillChangePlaymode&&!EditorApplication.isCompiling){Restore();return;}
        if(long.TryParse(SessionState.GetString(Prefix+"started","0"),out long started)&&DateTime.UtcNow.Ticks-started>TimeSpan.FromSeconds(150).Ticks){Finish("Verification timed out.");return;}
        if(Stage=="entered")
        {
            Stage="testing";previousFactory=Factory.GetValue(null);fake=new Gateway();Factory.SetValue(null,(Func<IUgsAccountGateway>)(()=>fake));
            Application.runInBackground=true;
            foreach(var window in Resources.FindObjectsOfTypeAll<EditorWindow>())if(window.GetType().Name=="GameView"){window.Focus();break;}
            steps.Clear();steps.Push(Probe());
        }
        if(Stage!="testing"||steps.Count==0)return;
        try
        {
            var current=steps.Peek();
            if(!current.MoveNext()){steps.Pop();if(steps.Count==0)Finish(null);}
            else if(current.Current is IEnumerator nested)steps.Push(nested);
        }
        catch(Exception error){Finish(error.ToString());}
    }
    private static IEnumerator Probe()
    {
        yield return Wait(()=>ObjectFind<LoginSceneController>()!=null,"Login startup");
        var login=ObjectFind<LoginSceneController>();login.enterGameAfterLogin=false;login.Login("ui_validation","Validation9!Pass");
        yield return Wait(()=>!login.IsBusy,"Offline login");
        Check(AccountClient.Instance.HasSession,"Login establishes the real account session");
        login.EnterGame();yield return Wait(()=>SceneManager.GetActiveScene().name=="GameLobby"&&ObjectFind<LobbyUIRoot>()?.Controller!=null,"Unified homepage after login");
        var root=ObjectFind<LobbyUIRoot>();var controller=root.Controller;var view=root.homeView;var party=PartyLobbyClient.Instance;
        LobbyManager.Instance.dedicatedServerAddress="127.0.0.1";
        Check(controller.Model.Page==LobbyPage.Home&&root.pages.Count(p=>p.gameObject.activeInHierarchy)==1,"Login lands directly on one unified homepage");
        Check(fake.Loads==1,"Initial profile is read exactly once in Login");
        Check(root.pages.All(p=>p.page!=LobbyPage.MainMenu&&p.page!=LobbyPage.Party),"No extra initial menu or party page remains");
        Check(view.playerName.text=="初始旅行者"&&view.uid.text.Contains(AccountUid.First.ToString()),"Header displays the authenticated nickname and UID");
        Check(!view.squadCard.activeInHierarchy&&view.matchCard.activeInHierarchy&&view.createPartyButton.IsInteractable()&&MatchCardAtTop(view),"Before creating a party the match card fills the top of the right column");
        Check(view.memberNames.All(t=>string.IsNullOrEmpty(t.text)),"An uncreated squad does not present the local player as a party member");
        Check(!view.buttons.Any(b=>b.action==LobbyAction.Pve)&&!view.GetComponentsInChildren<TMPro.TMP_Text>(true).Any(t=>t.text.Contains("单机")),"Single-player UI and binding are absent");
        Check(root.homeCharacter.activeInHierarchy&&root.unifiedEnvironment.activeInHierarchy,"Account character and 3D training station are visible");
        int frame=Time.frameCount;yield return Wait(()=>Time.frameCount>frame+5,"Advancing game frames");
        Check(Time.frameCount>frame+5,"Actual game frames advance during verification");
        // A cold font import can leave the first few rendered frames during the lobby fade.
        yield return Wait(()=>view.buttons.Where(b=>b.button.gameObject.activeInHierarchy).All(b=>PointerHits(b.button)),"Homepage finishes entering and all visible buttons receive pointer input");
        foreach(var binding in view.buttons.Where(b=>b.button.gameObject.activeInHierarchy))Check(PointerHits(binding.button),"Pointer reaches "+binding.button.name);
        yield return Capture("runtime-homepage");
        Click(view,LobbyAction.Friends);
        var social=ObjectFind<LobbySocialView>();
        yield return Wait(()=>social.IsOpen&&social.GetComponentsInChildren<TMPro.TMP_Text>().Any(t=>t.text=="星野"),"Friends entry and server data");
        social.GetComponentsInChildren<UnityEngine.UI.Button>().Single(button=>button.name=="Player Row"&&
            button.GetComponentsInChildren<TMPro.TMP_Text>().Any(text=>text.text=="星野")).onClick.Invoke();
        yield return Wait(()=>PointerHits(SocialButton(social,"发送消息")),"Selected friend's controls reach the real EventSystem");
        Check(PointerHits(SocialButton(social,"发送消息")),"The friends panel receives pointer input from the real EventSystem");
        yield return Capture("runtime-friends");
        SocialButton(social,"发送消息").onClick.Invoke();
        yield return Wait(()=>social.GetComponentsInChildren<TMPro.TMP_Text>().Any(t=>t.text=="来一起玩吗？"),"Private conversation contents");
        yield return Capture("runtime-chat");
        var composer=social.GetComponentsInChildren<TMPro.TMP_InputField>().Last();
        composer.text="测试私聊";
        SocialButton(social,"发送").onClick.Invoke();
        yield return Wait(()=>fake.SocialText=="测试私聊","Chat composer submits through the authenticated gateway");
        Check(PointerHits(SocialButton(social,"接受邀请")),"The invitation card exposes a clickable accept action");
        SocialButton(social,"接受邀请").onClick.Invoke();
        yield return Wait(()=>fake.InviteAccepted&&party.Party?.id=="unified-offline-party","Accepted invitation refreshes the lobby party");
        Check(view.squadCard.activeInHierarchy&&MatchCardBelowSquad(view),"Accepting an invitation displays the squad and moves the match card below it");
        SocialButton(social,"队伍聊天").onClick.Invoke();
        yield return Wait(()=>fake.SocialChannel=="@team"&&social.GetComponentsInChildren<TMPro.TMP_InputField>().Last().interactable,
            "Team channel requests a scoped transcript");
        composer=social.GetComponentsInChildren<TMPro.TMP_InputField>().Last();
        composer.text="测试队聊";
        SocialButton(social,"发送").onClick.Invoke();
        yield return Wait(()=>fake.SocialTeamText=="测试队聊","Team composer sends through the current party");
        controller.Back();
        Check(!social.IsOpen&&controller.Model.Page==LobbyPage.Home&&!controller.IsModalOpen,"Escape closes social UI and retains the lobby");
        Click(view,LobbyAction.LeaveParty);
        yield return Wait(()=>!party.IsBusy&&party.Party==null,"Leave invitation test party");
        Click(view,LobbyAction.Chat);
        Check(social.IsOpen&&SocialButton(social,"队伍聊天")!=null,"The existing chat entry opens the chat screen");
        controller.Back();
        Click(view,LobbyAction.Training);Check(controller.IsModalOpen,"Unavailable training range displays honest feedback");
        root.messageDialog.confirmButton.onClick.Invoke();
        Click(view,LobbyAction.Settings);Check(controller.Model.Page==LobbyPage.Settings&&!view.gameObject.activeSelf,"Settings opens from the unified homepage");
        var settings=root.pages.OfType<FPS.Settings.SettingsPanelView>().Single();
        Check(settings.rows.Length==34,"All 34 existing settings controls remain intact");settings.backButton.onClick.Invoke();
        Check(controller.Model.Page==LobbyPage.Home&&view.gameObject.activeInHierarchy,"Settings returns directly to the same homepage");
        Check(!view.buttons.Any(b=>b.action==LobbyAction.Character),"No character entry in the home dock");
        yield return VerifyWardrobe(root);
        Click(view,LobbyAction.Loadout);yield return Wait(()=>root.customizationView.preview.image.texture!=null,"Weapon preview");
        Check(controller.Model.Page==LobbyPage.Customization,"Weapons have their own page");controller.Back();
        yield return VerifyProfile(root);
        Check(controller.Model.Page==LobbyPage.Home,"Profile returns directly home");
        int queuesBeforeCreate=fake.Queues;
        Click(view,LobbyAction.CreateParty);
        yield return Wait(()=>!party.IsBusy&&party.Party!=null,"Create a party without matchmaking");
        Check(view.squadCard.activeInHierarchy&&view.matchCard.activeInHierarchy&&fake.Queues==queuesBeforeCreate&&MatchCardBelowSquad(view),"Confirmed creation shows the squad above the match card with a gap and does not automatically queue");
        Check(view.memberNames[0].text==AccountClient.Instance.Profile.DisplayName&&view.teamCount.text.Contains("1/5"),"The created squad displays its confirmed member and count");
        Check(PointerHits(view.leaveButton),"The squad card's leave action receives actual pointer input");
        yield return Capture("runtime-party-created");
        Click(view,LobbyAction.LeaveParty);yield return Wait(()=>!party.IsBusy&&party.Party==null,"Dissolve the manually created squad");
        Check(!view.squadCard.activeInHierarchy&&view.matchCard.activeInHierarchy&&view.createPartyButton.IsInteractable()&&MatchCardAtTop(view),"Dissolving a squad moves the match card back to the top and restores creation");
        int createsBeforeMatching=fake.Creates,queuesBeforeMatching=fake.Queues;
        var gate=fake.CreateGate=new TaskCompletionSource<UgsPvpReply>();Click(view,LobbyAction.Primary);Click(view,LobbyAction.Primary);
        Check(fake.Creates==createsBeforeMatching+1&&party.IsBusy&&controller.Model.Page==LobbyPage.Home&&!view.squadCard.activeInHierarchy&&MatchCardAtTop(view),"Pending creation keeps the match card at the top and repeated clicks submit one request");
        gate.SetResult(Reply("{\"code\":\"validation_failure\",\"message\":\"离线验证建队失败\"}",503));
        yield return Wait(()=>!party.IsBusy,"Rejected party creation");
        Check(party.Party==null&&view.status.text.Contains("失败")&&view.primaryButton.interactable&&!view.squadCard.activeInHierarchy&&MatchCardAtTop(view),"Failed creation retains the match card at the top with retry enabled");
        Click(view,LobbyAction.Primary);yield return Wait(()=>!party.IsBusy&&party.Party?.state=="Matching","One-click matchmaking");
        Check(fake.Creates==createsBeforeMatching+2&&fake.Queues==queuesBeforeMatching+1&&controller.Model.Page==LobbyPage.Home&&view.squadCard.activeInHierarchy,"One click creates a confirmed party, reveals the squad and queues on the same homepage");
        Check(view.primaryLabel.text=="取消匹配"&&view.leaveButton.gameObject.activeSelf,"Matching state updates primary and leave controls");
        yield return Capture("runtime-matching");
        Click(view,LobbyAction.Primary);yield return Wait(()=>!party.IsBusy&&party.Party?.state=="Waiting","Cancel matching");
        Check(view.primaryLabel.text=="开始匹配","Cancelling restores the start action on the same screen");
        Click(view,LobbyAction.LeaveParty);yield return Wait(()=>!party.IsBusy&&party.Party==null,"Leave own party");
        Check(!view.squadCard.activeInHierarchy&&view.matchCard.activeInHierarchy&&MatchCardAtTop(view),"Leaving a party hides the squad card and returns the match card to the top");
        Click(view,LobbyAction.Rooms);yield return Wait(()=>root.roomsView.content.childCount>0,"Room list");
        Check(controller.Model.Page==LobbyPage.Rooms&&party.BrowseRooms,"Room list still browses real room cards");
        root.roomsView.content.GetChild(0).GetComponent<RoomEntryUI>().joinButton.onClick.Invoke();
        yield return Wait(()=>!party.IsBusy&&party.Party!=null&&controller.Model.Page==LobbyPage.Home,"Join room on unified homepage");
        Check(!party.IsOwner&&!party.BrowseRooms&&view.primaryLabel.text=="准备"&&view.squadCard.activeInHierarchy&&MatchCardBelowSquad(view),"Joining a party stacks the match card below the squad and shows the ready action");
        Click(view,LobbyAction.Primary);yield return Wait(()=>!party.IsBusy&&party.LocalReady,"Ready toggle");
        Check(view.primaryLabel.text=="取消准备","Member ready action updates the unified primary button");
        Click(view,LobbyAction.LeaveParty);yield return Wait(()=>!party.IsBusy&&party.Party==null,"Leave joined party");
        Click(view,LobbyAction.CustomPvp);Check(controller.Model.Page==LobbyPage.CustomRoom,"Custom PvP room flow remains accessible");controller.Back();
        Check(controller.Model.Page==LobbyPage.Home,"Custom rooms return directly home");
        controller.Execute(LobbyAction.Pve);Check(controller.Model.Page==LobbyPage.Home&&!controller.IsModalOpen,"Legacy single-player intent cannot start PvE");
        controller.Back();Check(controller.IsModalOpen,"Escape on the homepage requests exit confirmation");root.confirmDialog.cancelButton.onClick.Invoke();
        Check(controller.Model.Page==LobbyPage.Home&&!controller.IsModalOpen,"Cancelling exit retains the homepage");
        int loads=fake.Loads;SceneManager.LoadScene("GameLobby");
        yield return Wait(()=>ObjectFind<LobbyUIRoot>()?.Controller!=null,"Lobby scene reentry");root=ObjectFind<LobbyUIRoot>();
        Check(root.Controller.Model.Page==LobbyPage.Home&&fake.Loads==loads,"Returning from gameplay reconstructs the homepage without rereading the cloud profile");
        yield return Capture("runtime-homepage-final");
    }
    private static IEnumerator VerifyWardrobe(LobbyUIRoot root)
    {
        var controller=root.Controller;
        Click(root.homeView,LobbyAction.Clothes);
        var w=root.Wardrobe;
        yield return Wait(()=>w.preview.image.texture!=null,"Open authored wardrobe");
        var legacy=controller.Draft.Appearance;
        var savedLoadout=controller.Draft.Loadout;
        Check(controller.Model.Page==LobbyPage.Wardrobe && w.slots.Length==8 && !w.details.activeSelf,"Eight independent slots open with color detail collapsed");
        Check(PointerHits(w.slots[(int)ClothingSlot.Hair]) && PointerHits(w.cards[0].button) && PointerHits(w.firstPerson),"Wardrobe slots, item cards and perspective switch receive real pointer input");
        w.slots[(int)ClothingSlot.Hair].onClick.Invoke(); w.cards[1].button.onClick.Invoke();
        Check(WardrobeItems.Item(controller.Draft.Appearance,ClothingSlot.Hat)==WardrobeItems.Item(legacy,ClothingSlot.Hat),"Hair style preserves hat");
        w.SelectSlot(ClothingSlot.Hat); w.Pick(0);
        Check(WardrobeItems.Item(controller.Draft.Appearance,ClothingSlot.Hair)==1,"Removing hat preserves hairstyle");
        w.undo.onClick.Invoke();
        Check(controller.Draft.Appearance.Equals(legacy),"Undo restores the original legacy outfit");
        w.SelectSlot(ClothingSlot.Top); w.Pick(1);
        w.SelectSlot(ClothingSlot.Jacket); w.Pick(0);
        Check(WardrobeItems.Item(controller.Draft.Appearance,ClothingSlot.Top)==1,"Removing jacket preserves inner top");
        w.Pick(1); w.detail.onClick.Invoke(); w.colors[2].onClick.Invoke();
        Check(w.details.activeSelf && WardrobeItems.ColorId(controller.Draft.Appearance,ClothingSlot.Jacket)==2 &&
            WardrobeItems.ColorId(controller.Draft.Appearance,ClothingSlot.Top)==WardrobeItems.ColorId(legacy,ClothingSlot.Top),"Item detail color affects only the jacket");
        yield return Capture("wardrobe-v3-detail");
        controller.Back(); Check(!w.details.activeSelf && controller.Model.Page==LobbyPage.Wardrobe,"Escape closes detail first");
        w.detail.onClick.Invoke(); w.colors[0].onClick.Invoke(); w.closeDetail.onClick.Invoke(); w.SelectSlot(ClothingSlot.Gloves); w.Pick(1); w.SelectSlot(ClothingSlot.Jacket);
        yield return Capture("wardrobe-v3-full-body");
        w.firstPerson.onClick.Invoke();
        Check(w.preview.FirstPerson && w.preview.Avatar.IsLocalFirstPerson,"First person uses current shared skeleton");
        yield return Capture("wardrobe-v3-first-person");
        var animator = w.preview.Avatar.CharacterAnimator;
        var socket = animator.GetBoneTransform(HumanBodyBones.RightHand).Find("Hand_R_Socket");
        var rifle = socket.GetComponentInChildren<UnityEngine.Animations.ParentConstraint>();
        Check(rifle && rifle.transform.parent == socket && rifle.transform.localPosition.sqrMagnitude < .000001f
            && Quaternion.Angle(rifle.transform.localRotation, Quaternion.identity) < .1f,
            "Wardrobe mounts the pack rifle on the animated right hand");
        Check(animator.GetCurrentAnimatorClipInfo(0).Any(c => c.clip == w.preview.rifleIdle),
            "First-person fitting plays the rifle grip animation on the garment skeleton");
        var leftHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
        var handle = rifle.transform.Find("Left_Handle");
        w.SelectSlot(ClothingSlot.Gloves); w.Pick(0);
        w.SelectSlot(ClothingSlot.Top); w.Pick(0);
        w.SelectSlot(ClothingSlot.Jacket); w.Pick(2);
        yield return Capture("wardrobe-bound-rifle-vest");
        float gripDistance = Vector3.Distance(leftHand.position, handle.position);
        Check(gripDistance < .03f, $"Bare support hand stays on the authored handle ({gripDistance:F3} m)");
        var fittingCamera = animator.transform.parent.GetComponentInChildren<Camera>();
        var supportOnScreen = fittingCamera.WorldToViewportPoint(leftHand.position);
        var gripOnScreen = fittingCamera.WorldToViewportPoint(socket.position);
        var clearArea = new Rect(.05f,.14f,.9f,.8f);
        Check(clearArea.Contains(gripOnScreen) && clearArea.Contains(supportOnScreen),
            $"Both hands clear the wardrobe controls (right {gripOnScreen:F2}, left {supportOnScreen:F2})");
        w.preview.OnDrag(new PointerEventData(EventSystem.current) { delta = new Vector2(35, -15) });
        yield return null;
        Check(Vector3.Distance(leftHand.position, handle.position) < .03f && rifle.transform.parent == socket,
            "Looking around keeps both hands attached to the animated rifle");
        w.Mode(false); w.Mode(true);
        w.SelectSlot(ClothingSlot.Top); w.Pick(1);
        w.SelectSlot(ClothingSlot.Gloves); w.Pick(1);
        w.SelectSlot(ClothingSlot.Jacket);
        w.cards[2].button.onClick.Invoke(); w.SelectSlot(ClothingSlot.Gloves);
        Check(w.preview.FirstPerson && WardrobeItems.Item(w.preview.Avatar.Current,ClothingSlot.Jacket)==3,"First-person view updates while switching garments");
        yield return Capture("wardrobe-v3-gloves");
        w.SelectSlot(ClothingSlot.Jacket); w.cards[0].button.onClick.Invoke();
        var worn=controller.Draft.Appearance;
        Check(WireRoundTrip(worn).Equals(worn),"PvP network serialization preserves garment and dye choices");
        w.fullBody.onClick.Invoke();
        Check(!rifle.gameObject.activeInHierarchy && animator.GetCurrentAnimatorClipInfo(0).All(c => c.clip != w.preview.rifleIdle),
            "Full-body mode hides the fitting rifle and restores the standing animation");
        Check(!w.preview.Avatar.IsLocalFirstPerson && w.preview.Avatar.Current.Equals(worn),"Returning to full body preserves fitting");
        w.save.onClick.Invoke(); yield return Wait(()=>!controller.Draft.IsDirty,"Wardrobe save");
        controller.Back(); yield return null;
        Check(root.homeCharacter.GetComponentInChildren<RifleGirlAppearance>().Current.Equals(worn),"Home shows the saved wardrobe");
        Click(root.homeView,LobbyAction.Clothes);
        Check(controller.Draft.Appearance.Equals(worn) && controller.Draft.Loadout.Equals(savedLoadout),"Opening again restores the outfit and preserves weapon loadout");
        w.SelectSlot(ClothingSlot.Hat); w.Pick(0); controller.Back();
        Check(controller.IsModalOpen,"Unsaved fitting asks before returning");
        root.confirmDialog.confirmButton.onClick.Invoke();
        Check(controller.Model.Page==LobbyPage.Home,"Discard returns to the homepage");
    }
    private static CharacterAppearanceData WireRoundTrip(CharacterAppearanceData appearance)
    {
        using(var writer=new Unity.Netcode.FastBufferWriter(64,Unity.Collections.Allocator.Temp))
        {
            writer.WriteNetworkSerializable(appearance);
            using(var reader=new Unity.Netcode.FastBufferReader(writer,Unity.Collections.Allocator.Temp))
            {
                reader.ReadNetworkSerializable(out CharacterAppearanceData restored);
                return restored;
            }
        }
    }
    private static IEnumerator VerifyProfile(LobbyUIRoot root)
    {
        var view = root.pages.OfType<ProfilePanelView>().Single();
        var camera = root.unifiedEnvironment.GetComponentInChildren<Camera>();
        var appearance = root.homeCharacter.GetComponentInChildren<RifleGirlAppearance>();
        var account = AccountClient.Instance;
        var homePosition = camera.transform.localPosition;
        var homeRotation = camera.transform.localRotation;
        float homeFov = camera.fieldOfView;
        int loads = fake.Loads;
        Click(root.homeView, LobbyAction.Profile);
        yield return Wait(() => !account.IsBusy && fake.Loads > loads, "Profile refresh");
        Check(view.gameObject.activeInHierarchy && view.overview.activeInHierarchy && !view.information.activeSelf,
            "The account portrait opens the profile overview and requests fresh data");
        Check(view.overviewName.text == account.Profile.DisplayName && view.overviewUid.text == "UID " + account.Profile.uid
            && view.overviewAvatar.image.sprite == root.avatars.Get(account.Profile.avatarId)
            && view.genderIcon.sprite == view.femaleIcon && view.signatureInput.text == account.Profile.personal.signature,
            "The profile overview renders the signed-in account's identity");
        Check(view.matchesText.text == "64" && view.winsText.text == "39" && view.killsText.text == "320"
            && view.deathsText.text == "160" && view.kdText.text == "2.00" && view.headshotRateText.text == "25.0%",
            "All six career metrics are derived from the refreshed account record");
        var head = root.homeCharacter.GetComponentInChildren<Animator>().GetBoneTransform(HumanBodyBones.Head);
        var screen = camera.WorldToViewportPoint(head.position);
        Check(appearance.gameObject.activeInHierarchy && appearance.Current.Equals(account.Profile.appearance)
            && screen.x > .62f && screen.x < .94f && screen.y > .5f && screen.y < .97f && screen.z > 0,
            "The dressed character's head falls in the right-hand portrait area");
        Check(PointerHits(view.informationButton), "The information tab accepts pointer input");
        yield return Capture("runtime-profile-overview");
        Click(view, LobbyAction.ProfileInformation);
        yield return Capture("runtime-profile-information");
        Check(view.information.activeInHierarchy && PointerHits(view.editNameButton), "Profile settings and rename still respond to input");
        Click(view, LobbyAction.Avatar);
        var closePicker = view.buttons.Single(b => b.action == LobbyAction.CloseAvatarPicker).button;
        yield return Wait(() => PointerHits(closePicker), "Pointer over the avatar picker close button");
        Check(view.avatarPicker.activeInHierarchy, "The avatar picker opens above the profile and can be closed");
        view.choiceButtons[1].onClick.Invoke();
        yield return Wait(() => !account.IsBusy && !view.avatarPicker.activeSelf, "Account avatar save");
        Click(view, LobbyAction.ProfileOverview);
        Check(account.Profile.avatarId == 1 && view.overviewAvatar.image.sprite == root.avatars.Get(1),
            "Saving a new account avatar updates the overview");
        view.signatureInput.text = "下一场见";
        view.signatureInput.onEndEdit.Invoke(view.signatureInput.text);
        yield return Wait(() => !account.IsBusy && account.Profile.personal.signature == "下一场见", "Account signature save");
        var hat = appearance.transform.Find("Rifle_Hat").gameObject;
        var hair = appearance.transform.Find("Rifle_Hair").GetComponent<Renderer>();
        var defaultHair = hair.sharedMaterial;
        var outfit = CharacterAppearanceData.Default;
        outfit.outfitId = 1; outfit.faceId = 2; outfit.clothColorId = 3; outfit.hairColorId = 2;
        outfit.hat = false; outfit.accessories = false;
        fake.Profile.appearance = outfit;
        fake.Profile.career = new PlayerCareerRecord();
        account.RefreshProfile();
        yield return Wait(() => !account.IsBusy && appearance.Current.Equals(outfit), "Refreshed outfit in the open profile");
        var palette = new SerializedObject(appearance);
        var cloth = appearance.transform.Find("Dress/Rifle_Dress").GetComponent<Renderer>();
        Check(appearance.transform.Find("Dress").gameObject.activeInHierarchy && !appearance.transform.Find("Jakcket").gameObject.activeSelf
            && appearance.transform.Find("Face/Face_03").gameObject.activeInHierarchy && !hat.activeSelf
            && !appearance.transform.Find("Acc").gameObject.activeSelf && hair.sharedMaterial != defaultHair
            && cloth.sharedMaterial == palette.FindProperty("clothMaterials").GetArrayElementAtIndex(outfit.clothColorId).objectReferenceValue,
            "The visible outfit, face, hat, accessories and mesh materials follow the confirmed account changes");
        Check(view.kdText.text == "0.00" && view.headshotRateText.text == "0.0%" && view.matchesText.text == "0",
            "A second cloud response updates the visible portrait and safely clears all-zero statistics");
        yield return Capture("runtime-profile-outfit");
        root.Controller.Back();
        Check(camera.transform.localPosition == homePosition && Quaternion.Angle(camera.transform.localRotation, homeRotation) < .02f
            && Mathf.Abs(camera.fieldOfView-homeFov) < .01f, "Returning restores the homepage camera framing");
    }

    private static bool MatchCardAtTop(UnifiedLobbyView view)
    {
        Canvas.ForceUpdateCanvases();
        return Mathf.Abs(((RectTransform)view.matchCard.transform).anchoredPosition.y)<.5f;
    }
    private static bool MatchCardBelowSquad(UnifiedLobbyView view)
    {
        Canvas.ForceUpdateCanvases();
        var squad=(RectTransform)view.squadCard.transform;var match=(RectTransform)view.matchCard.transform;
        return Mathf.Abs(squad.anchoredPosition.y)<.5f&&match.anchoredPosition.y<squad.anchoredPosition.y-squad.rect.height;
    }
    private static bool PointerHits(UnityEngine.UI.Button button)
    {
        Canvas.ForceUpdateCanvases();var rect=(RectTransform)button.transform;var canvas=button.GetComponentInParent<Canvas>();
        var eventData=new PointerEventData(EventSystem.current){position=RectTransformUtility.WorldToScreenPoint(canvas.renderMode==RenderMode.ScreenSpaceOverlay?null:canvas.worldCamera,rect.TransformPoint(rect.rect.center))};
        var hits=new List<RaycastResult>();EventSystem.current.RaycastAll(eventData,hits);
        return hits.Count>0&&ExecuteEvents.GetEventHandler<IPointerClickHandler>(hits[0].gameObject)==button.gameObject;
    }
    private static T ObjectFind<T>()where T:UnityEngine.Object=>UnityEngine.Object.FindObjectOfType<T>(true);
    private static UnityEngine.UI.Button SocialButton(LobbySocialView view,string name)
        =>view.GetComponentsInChildren<UnityEngine.UI.Button>().First(button=>button.name==name);
    private static void Click(LobbyPageView view,LobbyAction action)=>view.buttons.First(b=>b.action==action).button.onClick.Invoke();
    private static IEnumerator Wait(Func<bool> condition,string name){double end=EditorApplication.timeSinceStartup+18;while(!condition()){if(EditorApplication.timeSinceStartup>end)throw new TimeoutException(name);yield return null;}}
    private static IEnumerator Capture(string name)
    {int frame=Time.frameCount;double end=EditorApplication.timeSinceStartup+.8;while(Time.frameCount<frame+3||EditorApplication.timeSinceStartup<end)yield return null;ScreenCapture.CaptureScreenshot(Path.GetFullPath(UnifiedLobbyBuilder.Output+"/"+name+".png"));yield return null;yield return null;}
    private static void Check(bool condition,string description){if(!condition)throw new InvalidOperationException(description);Read().checks.Add(description);Store();}
    private static void Finish(string error)
    {
        if(!Running||Stage=="stopping")return;if(error!=null)Read().errors.Add(error);
        Factory.SetValue(null,previousFactory);steps.Clear();Stage="stopping";Store();EditorApplication.isPlaying=false;
    }
    private static void Restore()
    {
        Environment.SetEnvironmentVariable("FPS_ACCOUNT_BACKEND",SessionState.GetBool(Prefix+"hadEnvironment",false)?SessionState.GetString(Prefix+"environment",""):null);
        Application.runInBackground=SessionState.GetBool(Prefix+"background",false);
        string play=SessionState.GetString(Prefix+"playStart","");EditorSceneManager.playModeStartScene=string.IsNullOrEmpty(play)?null:AssetDatabase.LoadAssetAtPath<SceneAsset>(play);
        Read().restored=SceneManager.GetActiveScene().path==SessionState.GetString(Prefix+"scene","");
        Read().success=Read().restored&&Read().errors.Count==0;Store();
        File.WriteAllText(UnifiedLobbyBuilder.Output+"/runtime-validation.json",JsonUtility.ToJson(Read(),true));SessionState.SetBool(Prefix+"running",false);Stage="done";
        Debug.Log("[Unified Lobby Validation] "+(Read().success?"PASS":"FAIL")+" "+Read().checks.Count+" checks.");
    }
    private static void Log(string message,string trace,LogType type)
    {
        if(!Running||Stage=="stopping"||(type!=LogType.Error&&type!=LogType.Exception&&type!=LogType.Assert))return;
        bool baseline=trace.Contains("Unity.VisualScripting.Codebase.IncludeInSettings")||trace.Contains("Unity.VisualScripting.XmlDocumentation.BackgroundWork")||message.Contains("Failed to late initialize plugin module 'VisualScripting.Core.BoltCoreConfiguration'");
        string detail=baseline?"Existing Visual Scripting initialization issue":message.Split('\n')[0]+"\n"+trace;
        var messages=baseline?Read().baselineErrors:Read().errors;if(!messages.Contains(detail))messages.Add(detail);Store();
    }
    private static Report Read()=>report??(report=JsonUtility.FromJson<Report>(SessionState.GetString(Prefix+"report","{}")));
    private static void Store()=>SessionState.SetString(Prefix+"report",JsonUtility.ToJson(Read()));
    private static UgsPvpReply Reply(string body,int status=200)=>new UgsPvpReply{ok=true,state="done",body=body,status=status};
    // JsonUtility materializes null nested classes, including the admission ticket.
    private static PlayerProfileData Clone(PlayerProfileData profile)=>new PlayerProfileData
    {
        accountId=profile.accountId,uid=profile.uid,username=profile.username,avatarId=profile.avatarId,
        appearance=profile.appearance,loadout=profile.loadout,revision=profile.revision,personalRevision=profile.personalRevision,
        career=profile.career==null?null:new PlayerCareerRecord{matches=profile.career.matches,wins=profile.career.wins,
            kills=profile.career.kills,deaths=profile.career.deaths,headshotKills=profile.career.headshotKills},
        personal=profile.personal==null?null:new PersonalProfileData{displayName=profile.personal.displayName,signature=profile.personal.signature,gender=profile.personal.gender}
    };
    private sealed class Gateway:IUgsAccountGateway,IUgsPvpGateway,IUgsPersonalProfileGateway
    {
        public bool IsSignedIn{get;private set;}
        public string PlayerId=>IsSignedIn?"unified-offline-player":"";
        public int PublicUid=>Profile.uid;
        public bool HasLegacyUid=>false;
        public string AccessToken=>"offline-validation";
        public event Action SessionExpired;
        public int Loads,Creates,Queues;
        public string SocialText,SocialTeamText,SocialChannel;
        public bool InviteAccepted;
        public TaskCompletionSource<UgsPvpReply>CreateGate;
        public PlayerProfileData Profile=new PlayerProfileData{accountId="unified-offline-player",uid=AccountUid.First,username="ui_validation",avatarId=0,appearance=CharacterAppearanceData.Default,loadout=PvpLoadoutData.Default,
            personal=new PersonalProfileData{displayName="初始旅行者",gender=2,signature="今天也要赢！"},
            career=new PlayerCareerRecord{matches=64,wins=39,kills=320,deaths=160,headshotKills=80}};
        private PartyData party;
        public Task InitializeAsync()=>Task.CompletedTask;
        public Task SignUpAsync(string username,string password){IsSignedIn=true;return Task.CompletedTask;}
        public Task SignInAsync(string username,string password){IsSignedIn=true;return Task.CompletedTask;}
        public void SignOut(){IsSignedIn=false;}
        public Task<PlayerProfileData>LoadProfileAsync(string username){Loads++;return Task.FromResult(Clone(Profile));}
        public Task<PlayerProfileData>SaveProfileAsync(string username,int revision,CharacterAppearanceData appearance,PvpLoadoutData loadout){Profile.appearance=appearance;Profile.loadout=loadout;Profile.revision++;return Task.FromResult(Clone(Profile));}
        public Task<PlayerProfileData>SaveAvatarAsync(string username,int revision,byte avatarId){Profile.avatarId=avatarId;Profile.revision++;return Task.FromResult(Clone(Profile));}
        public Task<PlayerProfileData>SavePersonalProfileAsync(string username,int revision,PersonalProfileData personal){Profile.personal=personal;Profile.personalRevision++;return Task.FromResult(Clone(Profile));}
        public Task<UgsPvpReply>ExecutePvpAsync(string operation,string username,Dictionary<string,object>args)
        {
            if(operation=="social")
            {
                string command=(string)args["command"];
                if(command=="send")SocialText=(string)args["text"];
                if(command=="team-send"&&(string)args["target"]==party?.id)SocialTeamText=(string)args["text"];
                if(command=="accept-invite"&&(string)args["nonce"]=="12de0000000000000000000000000000")
                { InviteAccepted=true;party=Party(false); }
                if(command!="sync")return Task.FromResult(Reply("{\"ok\":true}"));
                SocialChannel=args.TryGetValue("target",out object target)?(string)target:null;
                var contacts=new[]
                {
                    new {player=new {id="friend-1",uid="111100028",name="星野",avatar=23,signature="今天也一起并肩作战！"},relation="friend",presence="online",unread=0,preview="邀请你加入队伍",activity=1795000000004L},
                    new {player=new {id="friend-2",uid="111100029",name="小雨",avatar=6,signature=""},relation="friend",presence="party",unread=2,preview="下局一起吗？",activity=1795000000002L},
                    new {player=new {id="friend-3",uid="111100031",name="柚子",avatar=9,signature=""},relation="friend",presence="offline",unread=0,preview="明天见～",activity=1795000000000L}
                };
                object[] messages=SocialChannel=="friend-1"?new object[]
                {
                    new {id="message-1",sender="friend-1",name="星野",avatar=23,text="来一起玩吗？",time=1795000000000L},
                    new {id="message-2",sender=PlayerId,name=Profile.DisplayName,avatar=0,text="好，马上来。",time=1795000000001L},
                    new {id="12de0000000000000000000000000000",sender="friend-1",name="星野",avatar=23,text="邀请你加入队伍",time=1795000000004L,
                        invite=new {mapName="测试地图1",members=2,state=InviteAccepted?"accepted":"pending"}}
                }:Array.Empty<object>();
                return Task.FromResult(Reply(Newtonsoft.Json.JsonConvert.SerializeObject(new {contacts,messages,partyId=party?.id??""})));
            }
            if(operation=="create"){Creates++;if(CreateGate!=null){var gate=CreateGate;CreateGate=null;return gate.Task;}party=Party(true);}
            if(operation=="rooms")return Task.FromResult(Reply(JsonUtility.ToJson(new PartyRoomListData{rooms=new[]{Party(false)}})));
            if(operation=="join")party=Party(false);
            if(operation=="ready"&&party!=null)foreach(var member in party.members)if(member.profile.accountId==PlayerId)member.ready=!member.ready;
            if(operation=="queue"&&party!=null){Queues++;party.state="Matching";}
            if(operation=="cancel"&&party!=null)party.state="Waiting";
            if(operation=="leave")party=null;
            return Task.FromResult(Reply(JsonUtility.ToJson(new PartyStateData{party=party})));
        }
        private PartyData Party(bool owner)=>new PartyData{id="unified-offline-party",name="团队对抗",mapName="测试地图1",state="Waiting",ownerAccountId=owner?PlayerId:"offline-owner",members=new[]{new PartyMemberData{profile=Clone(Profile),ready=owner}}};
        public void Dispose(){IsSignedIn=false;SessionExpired=null;}
    }
}
