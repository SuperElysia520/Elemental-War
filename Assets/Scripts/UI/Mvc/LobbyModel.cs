using System;
using System.Collections.Generic;

namespace FPS.UI
{
    public enum LobbyPage { MainMenu, Home, Party, Rooms, CustomRoom, Customization, Profile, Settings, Wardrobe }
    public enum LobbyAction
    {
        Back, Multiplayer, Pve, CustomPvp, Character, Clothes, Loadout, Profile,
        CreateParty, Rooms, RefreshRooms, Primary, LeaveParty, Exit, Placeholder,
        SaveProfile, ReloadProfile, CreateRoom, JoinRoom, RefreshCustomRooms, StartRoom, LeaveRoom,
        SelectRed, SelectBlue, SelectCharacter, ProfileOverview, ProfileInformation, Avatar, CloseAvatarPicker, Settings,
        Training, Friends, Chat
    }

    /// <summary>Scene-local navigation state. Account and party data remain owned by their services.</summary>
    public sealed class LobbyModel
    {
        private readonly Stack<LobbyPage> history = new Stack<LobbyPage>();
        public LobbyPage Page { get; private set; } = LobbyPage.Home;
        public string Status { get; set; } = "";
        public bool IsBusy { get; set; }
        public bool ShowProfileInformation { get; set; }
        public bool ShowAvatarPicker { get; set; }
        public LobbyGameMode CustomMode { get; set; } = LobbyGameMode.PVP;
        public LanDiscovery.RoomEntry SelectedRoom { get; set; }
        public event Action Changed;

        public void Navigate(LobbyPage page, bool remember = true)
        {
            page = Normalize(page);
            if (Page == page) return;
            if (remember) history.Push(Page);
            Page = page;
            Changed?.Invoke();
        }
        public void Reset(LobbyPage page)
        {
            history.Clear(); Page = Normalize(page); Changed?.Invoke();
        }
        public void Back()
        {
            Page = history.Count > 0 ? Normalize(history.Pop()) : LobbyPage.Home;
            Changed?.Invoke();
        }
        public void Notify() => Changed?.Invoke();
        // Keep serialized enum ordinals compatible with older prefabs and callers.
        // The initial menu and party are now states of the same homepage.
        private static LobbyPage Normalize(LobbyPage page) => page == LobbyPage.MainMenu || page == LobbyPage.Party ? LobbyPage.Home : page;
    }

    public sealed class CustomizationModel
    {
        public CharacterAppearanceData Appearance { get; private set; } = CharacterAppearanceData.Default;
        public PvpLoadoutData Loadout { get; private set; } = PvpLoadoutData.Default;
        private CharacterAppearanceData savedAppearance;
        private PvpLoadoutData savedLoadout;
        public CharacterAppearanceData SavedAppearance => savedAppearance;
        public void Dress(CharacterAppearanceData appearance) => Appearance = WardrobeItems.SameClothing(appearance,savedAppearance) ? savedAppearance : appearance.Sanitized();
        public bool IsDirty => !Appearance.Equals(savedAppearance) || !Loadout.Equals(savedLoadout);
        public void Load(PlayerProfileData profile)
        {
            Appearance = (profile != null ? profile.appearance : CharacterAppearanceData.Default).Sanitized();
            Loadout = (profile != null ? profile.loadout : PvpLoadoutData.Default).Sanitized();
            savedAppearance = Appearance; savedLoadout = Loadout;
        }
        public void Change(int choice, int direction)
        {
            var appearance = Appearance; var loadout = Loadout;
            switch (choice)
            {
                case 0: appearance.outfitId = Cycle(appearance.outfitId, direction, 3); break;
                case 1: appearance.faceId = Cycle(appearance.faceId, direction, 5); break;
                case 2: if (appearance.outfitId != 2) appearance.clothColorId = Cycle(appearance.clothColorId, direction, 5); break;
                case 3: appearance.hairColorId = Cycle(appearance.hairColorId, direction, 4); break;
                case 4: appearance.hat = !appearance.hat; break;
                case 5: if (appearance.outfitId != 2) appearance.accessories = !appearance.accessories; break;
                case 6: loadout.primaryWeaponId = (byte)(loadout.primaryWeaponId == 0 ? 2 : 0); break;
                case 7: loadout.primarySightId = Cycle(loadout.primarySightId, direction, 6); break;
                case 8: loadout.sidearmSightId = (byte)(loadout.sidearmSightId == 0 ? 3 : 0); break;
            }
            Appearance = appearance.Sanitized(); Loadout = loadout.Sanitized();
        }
        public string Value(int choice)
        {
            string[] outfits = { "外套裙装", "裙装", "运动装" };
            string[] sights = { "铁瞄", "红点 1", "红点 2", "红点 3", "全息瞄具", "8 倍镜" };
            switch (choice)
            {
                case 0: return outfits[Appearance.outfitId];
                case 1: return "预设 " + (Appearance.faceId + 1);
                case 2: return Appearance.outfitId == 2 ? "运动装原色" : "配色 " + (Appearance.clothColorId + 1);
                case 3: return "发色 " + (Appearance.hairColorId + 1);
                case 4: return Appearance.hat ? "佩戴" : "不佩戴";
                case 5: return Appearance.outfitId == 2 ? "此套装不适用" : Appearance.accessories ? "佩戴" : "不佩戴";
                case 6: return Loadout.primaryWeaponId == 2 ? "狙击枪" : "突击步枪";
                case 7: return sights[Loadout.primarySightId];
                default: return sights[Loadout.sidearmSightId];
            }
        }
        private static byte Cycle(byte value, int step, int count) => (byte)((value + step + count) % count);
    }
}
