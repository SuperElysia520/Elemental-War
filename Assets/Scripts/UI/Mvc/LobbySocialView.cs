using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections;
using FPS.Settings;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using static FPS.UI.LobbySocialStyle;

namespace FPS.UI
{
    public sealed class LobbySocialView : MonoBehaviour
    {
        private enum FriendsTab { Friends, Requests, Search, Blocked }
        private sealed class Player
        {
            public string Id, Uid, Name, Signature;
            public byte Avatar;
        }
        private sealed class Contact
        {
            public Player Player;
            public string Relation, Presence, Preview;
            public int Unread;
            public long Activity;
        }
        private sealed class Invite
        {
            public string MapName, State;
            public int Members;
        }
        private sealed class Line
        {
            public string Id, Sender, Name, Text;
            public byte Avatar;
            public long Time;
            public Invite Invite;
        }
        private sealed class Snapshot
        {
            public Contact[] Contacts = Array.Empty<Contact>();
            public Line[] Messages = Array.Empty<Line>();
            public string PartyId;
            public int Requests, Unread, Invites;
        }
        private sealed class SendReply { public Snapshot Snapshot; }

        private AccountClient account;
        private PartyLobbyClient party;
        private LobbySocialStyle style;
        private RectTransform screen, stage, navigation, detail, threadHeader;
        private TextMeshProUGUI feedback, badgeFriends, badgeChat, friendCount, composerCount, headingCount;
        private TMP_InputField search, composer;
        private ScrollRect list, transcript;
        private UnityEngine.UI.Button send;
        private bool chat, team, pending, requesting, loaded, searched;
        private float nextSync;
        private FriendsTab tab;
        private string selectedFriend, selectedChat, searchTerm = "", listKey, threadKey, detailKey, messageKey, renderedTarget, syncError;
        private string draftNonce, draftTarget, draftText, confirmCommand;
        private string sessionId;
        private Snapshot state = new Snapshot();
        private Contact[] results = Array.Empty<Contact>();
        private static readonly Color Green = new Color32(125, 238, 104, 255);
        private static readonly Color Gray = new Color32(186, 215, 231, 255);
        public bool IsOpen => screen != null && screen.gameObject.activeSelf;

        internal void Initialize(AccountClient accountClient, PartyLobbyClient partyClient, UnifiedLobbyView home,
            PlayerAvatarCatalog avatars, Font heading)
        {
            account = accountClient;
            party = partyClient;
            style = new LobbySocialStyle(home.playerName.font, heading, avatars);
            foreach (var binding in home.buttons)
            {
                if (binding.action == LobbyAction.Friends) badgeFriends = HomeBadge(binding.button.transform);
                if (binding.action == LobbyAction.Chat) badgeChat = HomeBadge(binding.button.transform);
            }
            screen = Rect(transform, "LobbySocial", 0, 0, 0, 0);
            screen.gameObject.SetActive(false);
            var canvas = screen.gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 80;
            screen.gameObject.AddComponent<GraphicRaycaster>();
            var background = Shape(Rect(screen, "Background", 0, 0, 0, 0), Blue, 0);
            Fill(background.rectTransform);
            background.gradient = background.verticalGradient = true;
            background.endColor = new Color32(7, 87, 161, 255);
            background.raycastTarget = true;
            stage = Rect(screen, "Stage", 0, 0, 1920, 1080);
            stage.anchorMin = stage.anchorMax = stage.pivot = new Vector2(.5f, .5f);
            stage.anchoredPosition = Vector2.zero;
            account.ProfileChanged += ProfileChanged;
            ProfileChanged(account.Profile);
        }

        private void OnDestroy()
        {
            if (account != null) account.ProfileChanged -= ProfileChanged;
        }

        private void ProfileChanged(PlayerProfileData profile)
        {
            if (sessionId == profile?.accountId) return;
            sessionId = profile?.accountId;
            StopAllCoroutines();
            requesting = pending = loaded = searched = false;
            state = new Snapshot();
            results = Array.Empty<Contact>();
            selectedFriend = selectedChat = draftNonce = draftTarget = draftText = renderedTarget = null;
            searchTerm = "";
            syncError = null;
            tab = FriendsTab.Friends;
            nextSync = 0;
            Close();
            UpdateBadges();
        }

        internal void Open(bool openChat)
        {
            if (chat != openChat) searchTerm = "";
            chat = openChat;
            if (chat) team = false;
            screen.gameObject.SetActive(true);
            Rebuild();
            nextSync = 0;
        }

        internal void Close()
        {
            if (!IsOpen) return;
            EventSystem.current?.SetSelectedGameObject(null);
            screen.gameObject.SetActive(false);
            nextSync = 0;
        }

        private void Update()
        {
            if (screen == null || !account.IsAuthenticated) return;
            if (IsOpen)
            {
                float scale = Mathf.Min(Screen.width / 1920f, Screen.height / 1080f);
                stage.localScale = Vector3.one * scale;
            }
            if (!requesting && !pending && Time.unscaledTime >= nextSync) StartCoroutine(Sync());
        }

        private string Channel => IsOpen && chat ? team ? "@team" : selectedChat : null;
        private float SyncInterval => IsOpen ? (chat && account.HasDirectLobby ? 1f : 5f) : 18f;

        private IEnumerator Sync()
        {
            requesting = true;
            nextSync = Time.unscaledTime + SyncInterval;
            string target = Channel;
            yield return account.SocialRequest(Args("sync", target), (body, error) =>
            {
                if (!string.IsNullOrEmpty(error))
                {
                    syncError = error;
                    if (IsOpen && !pending) Report(error);
                    return;
                }
                if (!TryRead(body, out Snapshot next))
                {
                    syncError = "社交数据暂时无法读取，请稍后重试。";
                    if (IsOpen && !pending) Report(syncError);
                    return;
                }
                bool firstLoad = !loaded;
                if (IsOpen && !pending && (firstLoad || feedback.text == syncError)) Report("");
                syncError = null;
                loaded = true;
                state = next;
                renderedTarget = target;
                UpdateBadges();
                if (!IsOpen) return;
                if (chat && !team && !state.Contacts.Any(c => c.Relation == "friend" && c.Player.Id == selectedChat))
                {
                    selectedChat = Conversations().FirstOrDefault()?.Player.Id;
                    if (selectedChat != null) nextSync = 0;
                }
                Render();
            });
            requesting = false;
        }

        private static Dictionary<string, object> Args(string command, string target = null, string text = null, string nonce = null)
        {
            var args = new Dictionary<string, object> { { "command", command } };
            if (target != null) args.Add("target", target);
            if (text != null) args.Add("text", text);
            if (nonce != null) args.Add("nonce", nonce);
            return args;
        }

        private static bool TryRead<T>(string body, out T value) where T : class
        {
            value = null;
            try { if (!string.IsNullOrEmpty(body)) value = JsonConvert.DeserializeObject<T>(body); }
            catch (JsonException) { }
            return value != null;
        }

        private void Run(string command, string target = null, string text = null, string nonce = null, Action<string> done = null)
        {
            if (!IsOpen || pending) return;
            pending = true;
            Report("正在处理…");
            RefreshComposer();
            StartCoroutine(RunCommand(Args(command, target, text, nonce), done));
        }

        private IEnumerator RunCommand(Dictionary<string, object> args, Action<string> done)
        {
            while (requesting) yield return null;
            bool refreshed = false;
            yield return account.SocialRequest(args, (body, error) =>
            {
                if (!string.IsNullOrEmpty(error)) { Report(error); return; }
                Report("");
                done?.Invoke(body);
                string command = (string)args["command"];
                if (command == "invite" || command == "accept-invite") party.RefreshParty();
                if ((command == "send" || command == "team-send") && IsOpen &&
                    Channel == (command == "team-send" ? "@team" : (string)args["target"]) &&
                    TryRead(body, out SendReply sent) && sent.Snapshot != null)
                {
                    state = sent.Snapshot;
                    renderedTarget = Channel;
                    loaded = refreshed = true;
                    UpdateBadges();
                    Render();
                }
            });
            pending = false;
            nextSync = refreshed ? Time.unscaledTime + SyncInterval : 0;
            if (IsOpen) RefreshComposer();
        }

        private void Rebuild()
        {
            EventSystem.current?.SetSelectedGameObject(null);
            Clear(stage);
            listKey = threadKey = detailKey = messageKey = null;
            list = transcript = null;
            search = composer = null;
            send = null;
            confirmCommand = null;
            var header = Box(stage, "Header", 0, 0, 1920, 96, Header, 0);
            style.Button(header, "‹  返回大厅", 34, 18, 265, 60, Close, size: 28);
            Box(header, "Divider", 324, 22, 2, 52, Rim, 0);
            style.Heading(header, chat ? "聊天" : "好友", 370, 8, 300, 76, 50);
            Icon(header, chat ? UnifiedLobbyIcon.Symbol.Chat : UnifiedLobbyIcon.Symbol.Team, 1810, 23, 52);
            var footer = Box(stage, "Footer", 0, 980, 1920, 100, Header, 0);
            style.Label(footer, "Esc 返回", 44, 16, 225, 62, 25);
            feedback = style.Label(footer, pending ? "正在处理…" : syncError ?? (loaded ? "" : "正在连接社交服务…"), 320, 16, 1535, 62);
            navigation = Rect(stage, "Navigation", 34, 144, 292, 798);
            if (chat) BuildChat(); else BuildFriends();
            Render();
        }

        private void BuildFriends()
        {
            var panel = Box(stage, "Friends", 350, 128, 928, 818, Panel, 24, true);
            string[] titles = { "我的好友", "好友申请", "添加好友", "黑名单" };
            style.Heading(panel, titles[(int)tab], 34, 16, 260, 63);
            headingCount = style.Label(panel, "", 300, 20, 165, 55, 21, SettingsPalette.Secondary);
            if (tab == FriendsTab.Friends || tab == FriendsTab.Search)
            {
                search = style.Input(panel, "搜索好友昵称 / UID", 498, 24, tab == FriendsTab.Search ? 270 : 390, 50, 64);
                search.SetTextWithoutNotify(searchTerm);
                search.onValueChanged.AddListener(value => { searchTerm = value; if (tab == FriendsTab.Friends) RenderList(); });
                if (tab == FriendsTab.Search)
                {
                    style.Button(panel, "搜索", 780, 24, 108, 50, Search, true);
                    search.onSubmit.AddListener(_ => Search());
                }
            }
            Box(panel, "Divider", 32, 92, 864, 1, Rim, 0);
            list = Scroll(panel, 23, 109, 882, 685);
            detail = Box(stage, "Player Card", 1300, 128, 586, 818, Panel, 24, true);
        }

        private void BuildChat()
        {
            var recents = Box(stage, "Conversations", 314, 128, 418, 818, Panel, 24, true);
            style.Heading(recents, team ? "当前队伍" : "最近会话", 26, 16, 360, 60);
            if (!team)
            {
                search = style.Input(recents, "搜索会话", 22, 87, 374, 52, 64);
                search.SetTextWithoutNotify(searchTerm);
                search.onValueChanged.AddListener(value => { searchTerm = value; RenderList(); });
            }
            list = Scroll(recents, 14, team ? 92 : 155, 390, team ? 696 : 632);
            var panel = Box(stage, "Chat", 752, 128, 1134, 818, Panel, 24, true);
            threadHeader = Rect(panel, "Conversation Header", 25, 16, 1084, 85);
            Box(panel, "Divider", 25, 107, 1084, 1, Rim, 0);
            transcript = Scroll(panel, 25, 121, 1084, 564);
            Box(panel, "Divider", 25, 694, 1084, 1, Rim, 0);
            composer = style.Input(panel, "输入消息…", 25, 714, 903, 55, 200);
            composer.onValueChanged.AddListener(_ => RefreshComposer());
            composer.onSubmit.AddListener(_ => Send());
            composerCount = style.Label(panel, "", 25, 773, 170, 31, 17, SettingsPalette.Secondary);
            style.Label(panel, "Enter 发送", 850, 773, 259, 31, 17, SettingsPalette.Secondary, TextAlignmentOptions.MidlineRight);
            send = style.Button(panel, "发送", 947, 714, 162, 55, Send, true, size: 28);
        }

        private void Render()
        {
            UpdateNavigation();
            RenderList();
            if (chat) { RenderHeader(); RenderTranscript(); RefreshComposer(); }
            else RenderDetail();
        }

        private void UpdateNavigation()
        {
            if (navigation.childCount > 0)
            {
                if (!chat) friendCount.text = "好友  " + state.Contacts.Count(c => c.Relation == "friend") + " / 100";
                var emblem = navigation.Find("Requests Badge");
                if (emblem != null) SetBadge(emblem.GetComponentInChildren<TextMeshProUGUI>(true), state.Requests);
                return;
            }
            if (chat)
            {
                NavButton("好友私聊", UnifiedLobbyIcon.Symbol.Chat, 0, 254, () => SetTeam(false), !team);
                NavButton("队伍聊天", UnifiedLobbyIcon.Symbol.Team, 110, 254, () => SetTeam(true), team);
                style.Button(navigation, "好友列表", 0, 238, 254, 60, () => Open(false));
                if (account.Profile != null)
                {
                    style.Avatar(navigation, account.Profile.avatarId, 0, 710, 70);
                    style.Label(navigation, account.Profile.DisplayName, 86, 710, 160, 36, 24);
                    style.Label(navigation, "在线", 86, 746, 160, 32, 20, SettingsPalette.Secondary);
                }
            }
            else
            {
                string[] tabs = { "我的好友", "好友申请", "添加好友", "黑名单" };
                var icons = new[] { UnifiedLobbyIcon.Symbol.Team, UnifiedLobbyIcon.Symbol.Mail, UnifiedLobbyIcon.Symbol.AddPerson, UnifiedLobbyIcon.Symbol.Block };
                for (int i = 0; i < tabs.Length; i++)
                {
                    var choice = (FriendsTab)i;
                    NavButton(tabs[i], icons[i], i * 108, 292, () => SetTab(choice), choice == tab);
                }
                SetBadge(Badge(navigation, "Requests Badge", 245, 112), state.Requests);
                style.Button(navigation, "打开聊天", 0, 466, 292, 58, () => Open(true));
                friendCount = style.Label(navigation, "好友  " + state.Contacts.Count(c => c.Relation == "friend") + " / 100", 12, 733, 278, 55);
            }
        }

        private void NavButton(string caption, UnifiedLobbyIcon.Symbol symbol, int y, int width, Action open, bool selected)
        {
            var button = style.Button(navigation, caption, 0, y, width, 88, open, selected, size: 31);
            var label = button.GetComponentInChildren<TextMeshProUGUI>();
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.rectTransform.anchoredPosition = new Vector2(84, 0);
            label.rectTransform.sizeDelta = new Vector2(width - 90, 88);
            Icon(button.transform, symbol, 28, 27, 34, selected ? SettingsPalette.Navy : SettingsPalette.White);
        }

        private void SetTab(FriendsTab next)
        {
            if (tab == next) return;
            tab = next; selectedFriend = null; searchTerm = ""; searched = false;
            results = Array.Empty<Contact>();
            Rebuild();
        }

        private void SetTeam(bool value)
        {
            if (team == value) return;
            team = value;
            searchTerm = "";
            Rebuild();
            nextSync = 0;
        }

        private IEnumerable<Contact> Conversations() => state.Contacts.Where(c => c.Relation == "friend")
            .OrderByDescending(c => c.Activity).ThenBy(c => c.Player.Name, StringComparer.Ordinal);

        private Contact[] VisibleContacts()
        {
            IEnumerable<Contact> contacts;
            if (chat) contacts = Conversations();
            else if (tab == FriendsTab.Search)
                contacts = results.Select(c => state.Contacts.FirstOrDefault(current => current.Player.Id == c.Player.Id) ?? c);
            else
            {
                contacts = state.Contacts.Where(c => tab == FriendsTab.Friends ? c.Relation == "friend" :
                    tab == FriendsTab.Blocked ? c.Relation == "blocked" : c.Relation == "incoming" || c.Relation == "outgoing");
                contacts = contacts.OrderBy(c => c.Presence == "offline").ThenBy(c => c.Relation == "outgoing").ThenBy(c => c.Player.Name, StringComparer.Ordinal);
            }
            string filter = searchTerm.Trim();
            if ((chat || tab == FriendsTab.Friends) && filter.Length > 0)
                contacts = contacts.Where(c => c.Player.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 || c.Player.Uid.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
            return contacts.ToArray();
        }

        private void RenderList()
        {
            if (chat && team) { RenderMembers(); return; }
            Contact[] contacts = VisibleContacts();
            if (!chat)
            {
                if (tab == FriendsTab.Friends)
                {
                    var friends = state.Contacts.Where(c => c.Relation == "friend").ToArray();
                    headingCount.text = "在线 " + friends.Count(c => c.Presence != "offline") + " / " + friends.Length;
                }
                if (!contacts.Any(c => c.Player.Id == selectedFriend))
                {
                    selectedFriend = contacts.FirstOrDefault()?.Player.Id;
                    confirmCommand = null;
                }
            }
            string selection = chat ? selectedChat : selectedFriend;
            string key = loaded.ToString() + searched + selection + JsonConvert.SerializeObject(contacts);
            if (listKey == key) { if (!chat) RenderDetail(); return; }
            listKey = key;
            float position = list.content.childCount > 0 ? list.verticalNormalizedPosition : 1;
            Clear(list.content);
            if (contacts.Length == 0)
            {
                string empty = !loaded ? "正在读取…" : chat ? "暂无会话，添加好友后可私聊。" :
                    tab == FriendsTab.Requests ? "暂无好友申请。" : tab == FriendsTab.Search ? searched ? "未找到该玩家。" : "输入昵称或 UID 查找玩家。" :
                    tab == FriendsTab.Blocked ? "黑名单为空。" : "暂无符合条件的好友。";
                style.Label(list.content, empty, 18, 19, list.content.rect.width - 36, 55, 21, SettingsPalette.Secondary);
            }
            for (int i = 0; i < contacts.Length; i++) RenderContact(contacts[i], i, selection);
            SizeContent(list, Mathf.Max(90, contacts.Length * 108), Mathf.Clamp01(position));
            if (!chat) RenderDetail();
        }

        private void RenderContact(Contact contact, int index, string selection)
        {
            Player player = contact.Player;
            bool selected = player.Id == selection;
            float width = chat ? 388 : 877;
            var row = Box(list.content, "Player Row", 0, index * 108, width, 98, selected ? Selected : Row, 17, selected);
            Click(row, () =>
            {
                if (chat && selectedChat != player.Id) { selectedChat = player.Id; nextSync = 0; }
                if (!chat) { selectedFriend = player.Id; confirmCommand = null; }
                Render();
            });
            if (selected) Box(row, "Selection", 4, 12, 6, 74, SettingsPalette.Gold, 3);
            style.Avatar(row, player.Avatar, 22, 12, 74);
            style.Label(row, player.Name, 114, 10, chat ? 158 : 365, 42, 27);
            string hint = chat && !string.IsNullOrEmpty(contact.Preview) ? contact.Preview :
                contact.Relation == "incoming" ? "请求添加你为好友" : contact.Relation == "outgoing" ? "等待对方回应" :
                contact.Relation == "blocked" ? "已屏蔽" : Presence(contact);
            Box(row, "Presence", 114, 65, 13, 13, PresenceColor(contact), 7);
            style.Label(row, hint, 138, 50, chat ? 194 : 395, 40, 19, SettingsPalette.Secondary);
            if (chat)
            {
                if (contact.Activity > 0)
                    style.Label(row, TimeLabel(contact.Activity), 274, 11, 98, 34, 16, SettingsPalette.Secondary, TextAlignmentOptions.MidlineRight);
                if (contact.Unread > 0) SetBadge(Badge(row, "Unread Badge", 341, 55), contact.Unread);
            }
            else if (tab == FriendsTab.Friends)
            {
                style.Button(row, "聊天", 545, 24, 142, 50, () => OpenPrivate(player.Id));
                style.Button(row, "邀请", 706, 24, 145, 50, () => SendInvite(player.Id), true, CanInvite(contact));
            }
        }

        private void RenderMembers()
        {
            var members = party.Party?.members ?? Array.Empty<PartyMemberData>();
            string key = state.PartyId + string.Join("|", members.Select(m => m.profile.accountId + m.profile.DisplayName + m.profile.avatarId));
            if (listKey == key) return;
            listKey = key;
            Clear(list.content);
            if (members.Length == 0) style.Label(list.content, "加入队伍后即可聊天。", 16, 8, 356, 52, 21);
            for (int i = 0; i < members.Length; i++)
            {
                var member = members[i].profile;
                var row = Box(list.content, "Member", 0, i * 107, 388, 95, Row);
                style.Avatar(row, member.avatarId, 12, 11, 72);
                style.Label(row, member.DisplayName, 100, 9, 275, 42, 25);
                style.Label(row, party.Party.ownerAccountId == member.accountId ? "队长" : "队员", 100, 51, 260, 32, 18, SettingsPalette.Secondary);
            }
            SizeContent(list, Mathf.Max(90, members.Length * 107), 1);
        }

        private void RenderDetail()
        {
            if (detail == null || chat) return;
            Contact contact = VisibleContacts().FirstOrDefault(c => c.Player.Id == selectedFriend);
            string key = confirmCommand + JsonConvert.SerializeObject(contact);
            if (key == detailKey) return;
            detailKey = key;
            Clear(detail, keepOutline: true);
            if (contact == null)
            {
                style.Heading(detail, "好友资料", 34, 22, 450, 62);
                style.Label(detail, "选择一名玩家查看资料。", 34, 108, 515, 51, 24);
                return;
            }
            Player p = contact.Player;
            style.Avatar(detail, p.Avatar, 32, 31, 152);
            style.Label(detail, p.Name, 216, 31, 322, 60, 41);
            style.Label(detail, "UID: " + p.Uid, 216, 99, 329, 40, 21);
            Box(detail, "Presence", 216, 153, 16, 16, PresenceColor(contact), 8);
            style.Label(detail, Presence(contact), 244, 141, 304, 40, 22);
            Box(detail, "Divider", 32, 217, 522, 1, Rim, 0);
            var signature = style.Label(detail, p.Signature, 32, 244, 522, 122, 23, SettingsPalette.Secondary, TextAlignmentOptions.TopLeft);
            signature.enableWordWrapping = true;
            string id = p.Id;
            switch (contact.Relation)
            {
                case "friend":
                    style.Button(detail, "邀请组队", 32, 483, 522, 78, () => SendInvite(id), true, CanInvite(contact), 32);
                    style.Button(detail, "发送消息", 32, 579, 522, 74, () => OpenPrivate(id), size: 28);
                    style.Button(detail, "移除好友", 32, 696, 247, 54, () => Confirm("remove"));
                    style.Button(detail, "加入黑名单", 303, 696, 251, 54, () => Confirm("block"));
                    break;
                case "incoming":
                    style.Button(detail, "接受申请", 32, 525, 522, 75, () => Run("accept", id), true, size: 29);
                    style.Button(detail, "拒绝申请", 32, 622, 522, 66, () => Run("reject", id), size: 26);
                    break;
                case "outgoing":
                    style.Label(detail, "等待对方回应", 32, 515, 522, 50, 26);
                    style.Button(detail, "撤回申请", 32, 594, 522, 66, () => Run("reject", id));
                    break;
                case "blocked":
                    style.Button(detail, "解除屏蔽", 32, 563, 522, 70, () => Run("unblock", id), true, size: 28);
                    break;
                default:
                    style.Button(detail, "发送好友申请", 32, 545, 522, 75, () => Run("request", id), true, size: 30);
                    break;
            }
            if (confirmCommand != null)
            {
                string command = confirmCommand;
                style.Button(detail, command == "remove" ? "确认移除好友" : "确认加入黑名单", 32, 378, 335, 58,
                    () => Run(command, id, done: _ => { confirmCommand = null; if (IsOpen && !chat) RenderDetail(); }), true);
                style.Button(detail, "取消", 389, 378, 165, 58, () => Confirm(null));
            }
        }

        private void Confirm(string command) { confirmCommand = command; RenderDetail(); }

        private void RenderHeader()
        {
            var contact = state.Contacts.FirstOrDefault(c => c.Relation == "friend" && c.Player.Id == selectedChat);
            string key = team + state.PartyId + JsonConvert.SerializeObject(contact);
            if (threadKey == key) return;
            threadKey = key;
            Clear(threadHeader);
            if (team)
            {
                style.Heading(threadHeader, "队伍聊天", 14, 1, 712, 48);
                style.Label(threadHeader, string.IsNullOrEmpty(state.PartyId) ? "当前没有队伍" : "仅当前队伍成员可见", 14, 49, 712, 32, 19, SettingsPalette.Secondary);
            }
            else if (contact != null)
            {
                style.Avatar(threadHeader, contact.Player.Avatar, 5, 4, 76);
                style.Label(threadHeader, contact.Player.Name, 105, 1, 548, 47, 34);
                Box(threadHeader, "Presence", 105, 59, 14, 14, PresenceColor(contact), 7);
                style.Label(threadHeader, Presence(contact), 130, 47, 490, 40, 22, SettingsPalette.Secondary);
                style.Button(threadHeader, "查看资料", 679, 15, 179, 54, () => OpenFriend(contact.Player.Id));
                style.Button(threadHeader, "邀请组队", 879, 15, 200, 54, () => SendInvite(contact.Player.Id), true, CanInvite(contact));
            }
            else style.Heading(threadHeader, "选择好友开始私聊", 14, 10, 710, 56, 32);
        }

        private void RenderTranscript()
        {
            string target = Channel;
            Line[] lines = target != null && target == renderedTarget ? state.Messages : Array.Empty<Line>();
            string key = target + "/" + state.PartyId + JsonConvert.SerializeObject(lines);
            if (messageKey == key) return;
            bool sameThread = messageKey != null && messageKey.StartsWith(target + "/" + state.PartyId + "[", StringComparison.Ordinal);
            float position = sameThread ? transcript.verticalNormalizedPosition : 0;
            messageKey = key;
            Clear(transcript.content);
            float y = 10;
            if (lines.Length == 0)
            {
                string empty = target != renderedTarget && target != null ? "正在读取消息…" :
                    team && string.IsNullOrEmpty(state.PartyId) ? "加入队伍后即可聊天。" : "暂无消息。";
                style.Label(transcript.content, empty, 10, 10, 1055, 55, 22, SettingsPalette.Secondary, TextAlignmentOptions.Center);
            }
            DateTime lastDate = DateTime.MinValue;
            foreach (Line line in lines)
            {
                DateTime date = DateTimeOffset.FromUnixTimeMilliseconds(line.Time).LocalDateTime.Date;
                if (lastDate != date)
                {
                    style.Label(transcript.content, date == DateTime.Today ? "今天" : date.ToString("MM-dd"), 0, y, 1080, 32, 18,
                        SettingsPalette.Secondary, TextAlignmentOptions.Center);
                    lastDate = date;
                    y += 40;
                }
                y += RenderLine(line, y) + 10;
            }
            SizeContent(transcript, Mathf.Max(564, y), position < .08f ? 0 : Mathf.Clamp01(position));
        }

        private float RenderLine(Line line, float y)
        {
            bool own = line.Sender == sessionId;
            style.Avatar(transcript.content, line.Avatar, own ? 1014 : 6, y + 19, 56);
            style.Label(transcript.content, (own ? "" : line.Name + "  ") + TimeLabel(line.Time),
                79, y, 920, 23, 16, SettingsPalette.Secondary, own ? TextAlignmentOptions.MidlineRight : TextAlignmentOptions.MidlineLeft);
            if (line.Invite != null) return InviteCard(line, y + 26, own) + 26;
            var label = style.Label(transcript.content, line.Text, 0, 0, 615, 55, 23,
                own ? SettingsPalette.Navy : SettingsPalette.White, TextAlignmentOptions.TopLeft);
            label.enableWordWrapping = true;
            label.overflowMode = TextOverflowModes.Overflow;
            Vector2 preferred = label.GetPreferredValues(line.Text, 615, 0);
            float width = Mathf.Clamp(preferred.x + 40, 110, 655);
            float height = Mathf.Max(50, preferred.y + 25);
            var bubble = Box(transcript.content, "Message", own ? 995 - width : 80, y + 27, width, height,
                own ? SettingsPalette.Gold : Row, 14, !own);
            label.rectTransform.SetParent(bubble, false);
            label.rectTransform.anchoredPosition = new Vector2(19, -11);
            label.rectTransform.sizeDelta = new Vector2(width - 38, height - 20);
            return height + 27;
        }

        private float InviteCard(Line line, float y, bool own)
        {
            Invite invite = line.Invite;
            var card = Box(transcript.content, "Team Invitation", own ? 390 : 80, y, 605, 217, Row, 17, true);
            Icon(card, UnifiedLobbyIcon.Symbol.Team, 23, 16, 34);
            style.Heading(card, "组队邀请", 72, 8, 497, 50, 29);
            Box(card, "Divider", 22, 61, 561, 1, Rim, 0);
            style.Label(card, "团队对抗 · " + invite.MapName + "    " + invite.Members + " / 5", 25, 72, 548, 47);
            if (!own && invite.State == "pending")
            {
                style.Button(card, "接受邀请", 22, 141, 271, 52,
                    () => Run("accept-invite", line.Sender, nonce: line.Id, done: _ => Report("已加入队伍。")), true);
                style.Button(card, "暂不加入", 311, 141, 271, 52, () => Run("decline-invite", line.Sender, nonce: line.Id));
            }
            else style.Label(card, invite.State == "accepted" ? "已接受" : invite.State == "declined" ? "已拒绝" :
                invite.State == "pending" ? "等待对方回应" : "邀请已失效", 25, 140, 545, 48, 22, SettingsPalette.Secondary);
            return 217;
        }

        private void Search()
        {
            string query = searchTerm.Trim();
            if (query.Length == 0) { Report("请输入好友昵称或 UID。"); return; }
            Run("search", text: query, done: body =>
            {
                if (!TryRead(body, out Contact[] found)) { Report("搜索结果暂时无法读取。"); return; }
                if (tab != FriendsTab.Search || searchTerm.Trim() != query) return;
                searched = true; results = found; listKey = null;
                if (IsOpen && !chat) RenderList();
            });
        }

        private void OpenPrivate(string id) { selectedChat = id; searchTerm = ""; Open(true); }
        private void OpenFriend(string id) { selectedFriend = id; tab = FriendsTab.Friends; searchTerm = ""; Open(false); }
        private bool CanInvite(Contact contact) => contact.Presence == "online";
        private void SendInvite(string id) => Run("invite", id, nonce: Guid.NewGuid().ToString("N"), done: _ => Report("组队邀请已发送。"));

        private void Send()
        {
            if (composer == null || !send.interactable) return;
            string target = team ? state.PartyId : selectedChat;
            string message = composer.text.Trim();
            if (draftNonce == null || draftTarget != target || draftText != message)
            {
                draftNonce = Guid.NewGuid().ToString("N");
                draftTarget = target; draftText = message;
            }
            var input = composer;
            Run(team ? "team-send" : "send", target, message, draftNonce, _ =>
            {
                draftNonce = null;
                if (input != null && input == composer && input.text.Trim() == message) input.text = "";
            });
        }

        private void RefreshComposer()
        {
            if (send == null || composer == null) return;
            bool available = team ? !string.IsNullOrEmpty(state.PartyId) : state.Contacts.Any(c => c.Relation == "friend" && c.Player.Id == selectedChat);
            composer.interactable = available;
            send.interactable = !pending && available && !string.IsNullOrWhiteSpace(composer.text);
            composerCount.text = composer.text.Length + " / 200";
        }

        private void Report(string message) { if (IsOpen && feedback != null) feedback.text = message; }
        private void UpdateBadges()
        {
            SetBadge(badgeFriends, state.Requests);
            SetBadge(badgeChat, Math.Max(state.Unread, state.Invites));
        }
        private TextMeshProUGUI HomeBadge(Transform parent)
        {
            var badge = Badge(parent, "Social Notification", 0, 0);
            var panel = (RectTransform)badge.transform.parent;
            panel.anchorMin = panel.anchorMax = Vector2.one;
            panel.anchoredPosition = new Vector2(-27, 6);
            return badge;
        }
        private TextMeshProUGUI Badge(Transform parent, string name, float x, float y)
        {
            var badge = Box(parent, name, x, y, 39, 35, Red, 18, true);
            return style.Label(badge, "", 0, 0, 39, 35, 17, Color.white, TextAlignmentOptions.Center);
        }
        private static void SetBadge(TextMeshProUGUI badge, int count)
        {
            if (badge == null) return;
            badge.text = count > 99 ? "99+" : count.ToString();
            badge.transform.parent.gameObject.SetActive(count > 0);
        }
        private static string Presence(Contact contact) => contact.Presence == "online" ? "在线 · 大厅" :
            contact.Presence == "party" ? "在线 · 组队中" : contact.Presence == "playing" ? "游戏中 · 团队对抗" : "离线";
        private static Color PresenceColor(Contact contact) => contact.Presence == "offline" ? Gray :
            contact.Presence == "playing" ? SettingsPalette.Gold : Green;
        private static string TimeLabel(long unix) => DateTimeOffset.FromUnixTimeMilliseconds(unix).ToLocalTime().ToString("HH:mm");
        private static void Clear(Transform root, bool keepOutline = false)
        {
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                var child = root.GetChild(i);
                if (keepOutline && child.name == "Outline") continue;
                child.gameObject.SetActive(false);
                child.SetParent(null, false);
                Destroy(child.gameObject);
            }
        }
    }
}
