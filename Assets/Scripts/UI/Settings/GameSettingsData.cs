using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace FPS.Settings
{
    [Serializable]
    public sealed class GameSettingsData
    {
        public int version = 1;
        public int displayMode = 1;
        public string resolution = "1920 × 1080";
        public int fpsCap = 144;
        public bool vsync;
        public int quality = 2;
        public float master = 78, music = 62, uiSfx = 70;
        public bool mute;
        public float sens = 1, ads = 0.85f;
        public int aimMode, sprintMode, crouchMode;
        public string kb_forward = "<Keyboard>/w", kb_back = "<Keyboard>/s";
        public string kb_left = "<Keyboard>/a", kb_right = "<Keyboard>/d";
        public string kb_jump = "<Keyboard>/space", kb_crouch = "<Keyboard>/leftCtrl", kb_sprint = "<Keyboard>/leftShift";
        public string kb_fire = "<Mouse>/leftButton", kb_aim = "<Mouse>/rightButton";
        public string kb_reload = "<Keyboard>/r", kb_switch = "<Keyboard>/q";
        public int x_style = 3, x_color;
        public float x_len = 7, x_gap = 4, x_thick = 2;
        public bool x_dot = true, x_outline = true, x_dynamic = true;

        public GameSettingsData Copy() => JsonUtility.FromJson<GameSettingsData>(JsonUtility.ToJson(this));
        public static GameSettingsData Defaults()
        {
            var data = new GameSettingsData();
            var desktop = Screen.currentResolution;
            if (desktop.width >= 800 && desktop.height >= 600) data.resolution = ResolutionName(desktop.width, desktop.height);
            data.quality = Mathf.Clamp(2, 0, Mathf.Max(0, QualitySettings.names.Length - 1));
            return data;
        }
        public static string ResolutionName(int width, int height) => width + " × " + height;
        public static bool TryResolution(string text, out int width, out int height)
        {
            width = height = 0;
            var parts = (text ?? "").Split('×');
            return parts.Length == 2 && int.TryParse(parts[0].Trim(), out width) && int.TryParse(parts[1].Trim(), out height)
                && width >= 800 && height >= 600 && width <= 16384 && height <= 16384;
        }
        public void Sanitize()
        {
            var defaults = new GameSettingsData();
            version = 1; displayMode = Mathf.Clamp(displayMode, 0, 2);
            if (!TryResolution(resolution, out _, out _)) resolution = Defaults().resolution;
            if (!new[] { 60, 120, 144, 240, -1 }.Contains(fpsCap)) fpsCap = 144;
            quality = Mathf.Clamp(quality, 0, Mathf.Max(0, QualitySettings.names.Length - 1));
            master = Clamp(master, 0, 100, 78); music = Clamp(music, 0, 100, 62); uiSfx = Clamp(uiSfx, 0, 100, 70);
            sens = Clamp(sens, 0.1f, 5, 1); ads = Clamp(ads, 0.1f, 2, 0.85f);
            aimMode = Mathf.Clamp(aimMode, 0, 1); sprintMode = Mathf.Clamp(sprintMode, 0, 1); crouchMode = Mathf.Clamp(crouchMode, 0, 1);
            x_style = Mathf.Clamp(x_style, 0, 3); x_color = Mathf.Clamp(x_color, 0, 4);
            x_len = Clamp(x_len, 2, 20, 7); x_gap = Clamp(x_gap, 0, 16, 4); x_thick = Clamp(x_thick, 1, 6, 2);
            var used = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var def in SettingsSchema.Rows.Where(r => r.kind == SettingKind.Key))
            {
                string path = (string)def.Get(this);
                if (!SettingsPlayerInput.ValidBinding(path) || used.Contains(path)) path = (string)def.Get(defaults);
                if (used.Contains(path))
                    path = SettingsPlayerInput.AvailableFallback(used);
                def.Set(this, path); used.Add(path);
            }
        }
        private static float Clamp(float value, float min, float max, float fallback)
            => float.IsNaN(value) || float.IsInfinity(value) ? fallback : Mathf.Clamp(value, min, max);
        public int Differences(GameSettingsData other) => SettingsSchema.Rows.Count(row => row.kind != SettingKind.Info && !Equals(row.Get(this), row.Get(other)));
        public bool DisplayEquals(GameSettingsData other) => displayMode == other.displayMode && resolution == other.resolution;
    }

    public enum SettingKind { Segment, Select, Slider, Toggle, Key, Info }
    public sealed class SettingDefinition
    {
        public readonly string id, label, english, hint;
        public readonly int tab, section;
        public readonly SettingKind kind;
        public readonly string[] options;
        public readonly float min, max, step;
        public readonly string unit;
        private readonly FieldInfo field;
        public SettingDefinition(int tab, int section, string id, string label, string english, SettingKind kind,
            string[] options = null, string hint = "", float min = 0, float max = 100, float step = 1, string unit = "")
        {
            this.tab = tab; this.section = section; this.id = id; this.label = label; this.english = english;
            this.kind = kind; this.options = options; this.hint = hint; this.min = min; this.max = max; this.step = step; this.unit = unit;
            field = typeof(GameSettingsData).GetField(id);
        }
        public object Get(GameSettingsData data) => field != null ? field.GetValue(data) : "系统默认";
        public void Set(GameSettingsData data, object value)
        {
            if (field == null) return;
            field.SetValue(data, Convert.ChangeType(value, field.FieldType, CultureInfo.InvariantCulture));
        }
        public string Format(GameSettingsData data) => kind == SettingKind.Key ? SettingsPlayerInput.DisplayName((string)Get(data))
            : kind == SettingKind.Slider ? Convert.ToSingle(Get(data)).ToString(step < 1 ? "0.00" : "0", CultureInfo.InvariantCulture) + (unit.Length > 0 ? " <size=10>" + unit + "</size>" : "") : Get(data).ToString();
    }

    public static class SettingsSchema
    {
        public static readonly string[] Titles = { "画面", "音频", "操作", "按键", "准心" };
        public static readonly string[] English = { "GRAPHICS", "AUDIO", "CONTROLS", "KEY BINDINGS", "CROSSHAIR" };
        public static readonly string[] Subtitles = { "显示输出与画质", "主输出与界面声音", "灵敏度与动作模式", "点击键位可改键", "样式与实时预览" };
        public static readonly string[][] Sections = { new[] { "显示输出|DISPLAY OUTPUT", "画质|QUALITY" }, new[] { "主输出|MASTER", "局外与界面|OUT-OF-MATCH" }, new[] { "灵敏度|SENSITIVITY", "动作模式|ACTION MODE" }, new[] { "移动|MOVEMENT", "战斗|COMBAT" }, new[] { "准心参数|CROSSHAIR" } };
        public static string[] QualityNames => QualitySettings.names.Select((name, index) => index == 0 ? "流畅" : index == 1 ? "均衡" : index == 2 ? "高" : name).ToArray();
        public static string[] Resolutions => Screen.resolutions.Select(r => GameSettingsData.ResolutionName(r.width, r.height))
            .Concat(new[] { GameSettingsData.Defaults().resolution, "1280 × 720", "1600 × 900", "1920 × 1080", "2560 × 1440", "3840 × 2160" }).Distinct().ToArray();
        public static readonly SettingDefinition[] Rows =
        {
            new SettingDefinition(0,0,"displayMode","显示模式","DISPLAY MODE",SettingKind.Segment,new[]{"全屏","无边框","窗口"}),
            new SettingDefinition(0,0,"resolution","分辨率","RESOLUTION",SettingKind.Select),
            new SettingDefinition(0,0,"fpsCap","帧率上限","FRAME RATE LIMIT",SettingKind.Segment,new[]{"60","120","144","240","无限制"}),
            new SettingDefinition(0,0,"vsync","垂直同步","VERTICAL SYNC",SettingKind.Toggle,hint:"开启后由显示器刷新率决定帧率上限。"),
            new SettingDefinition(0,1,"quality","画面质量","GRAPHICS QUALITY",SettingKind.Segment),
            new SettingDefinition(1,0,"master","主音量","MASTER VOLUME",SettingKind.Slider,unit:"%"),
            new SettingDefinition(1,0,"device","输出设备","OUTPUT DEVICE",SettingKind.Info,hint:"跟随 Windows 当前默认输出设备。"),
            new SettingDefinition(1,0,"mute","全部静音","MUTE ALL",SettingKind.Toggle),
            new SettingDefinition(1,1,"music","局外音乐","MENU MUSIC",SettingKind.Slider,hint:"大厅、菜单和结算界面的音乐音量。",unit:"%"),
            new SettingDefinition(1,1,"uiSfx","界面音效","UI SOUND",SettingKind.Slider,unit:"%"),
            new SettingDefinition(2,0,"sens","鼠标灵敏度","MOUSE SENSITIVITY",SettingKind.Slider,min:0.1f,max:5,step:0.05f),
            new SettingDefinition(2,0,"ads","开镜灵敏度倍率","ADS MULTIPLIER",SettingKind.Slider,min:0.1f,max:2,step:0.05f,hint:"开镜时在基础鼠标灵敏度上乘以此倍率。"),
            new SettingDefinition(2,1,"aimMode","瞄准模式","AIM MODE",SettingKind.Segment,new[]{"按住","切换"}),
            new SettingDefinition(2,1,"sprintMode","疾跑模式","SPRINT MODE",SettingKind.Segment,new[]{"按住","切换"}),
            new SettingDefinition(2,1,"crouchMode","蹲下模式","CROUCH MODE",SettingKind.Segment,new[]{"按住","切换"}),
            new SettingDefinition(3,0,"kb_forward","前进","FORWARD",SettingKind.Key),
            new SettingDefinition(3,0,"kb_back","后退","BACK",SettingKind.Key),
            new SettingDefinition(3,0,"kb_left","左移","STRAFE LEFT",SettingKind.Key),
            new SettingDefinition(3,0,"kb_right","右移","STRAFE RIGHT",SettingKind.Key),
            new SettingDefinition(3,0,"kb_jump","跳跃","JUMP",SettingKind.Key),
            new SettingDefinition(3,0,"kb_crouch","蹲下","CROUCH",SettingKind.Key),
            new SettingDefinition(3,0,"kb_sprint","疾跑","SPRINT",SettingKind.Key),
            new SettingDefinition(3,1,"kb_fire","开火","FIRE",SettingKind.Key),
            new SettingDefinition(3,1,"kb_aim","瞄准","AIM",SettingKind.Key),
            new SettingDefinition(3,1,"kb_reload","换弹","RELOAD",SettingKind.Key),
            new SettingDefinition(3,1,"kb_switch","切换武器","SWITCH WEAPON",SettingKind.Key,hint:"PVP 主、副武器切换；数字 1 / 2 与滚轮仍可用。"),
            new SettingDefinition(4,0,"x_style","准心样式","STYLE",SettingKind.Segment,new[]{"十字","点","圆环","十字+点"}),
            new SettingDefinition(4,0,"x_color","准心颜色","COLOR",SettingKind.Select,new[]{"青色","白色","品红","荧光绿","琥珀"}),
            new SettingDefinition(4,0,"x_len","准心长度","LENGTH",SettingKind.Slider,min:2,max:20,unit:"px"),
            new SettingDefinition(4,0,"x_gap","中心间隙","GAP",SettingKind.Slider,min:0,max:16,unit:"px"),
            new SettingDefinition(4,0,"x_thick","线条粗细","THICKNESS",SettingKind.Slider,min:1,max:6,unit:"px"),
            new SettingDefinition(4,0,"x_dot","中心点","CENTER DOT",SettingKind.Toggle),
            new SettingDefinition(4,0,"x_outline","黑色描边","OUTLINE",SettingKind.Toggle,hint:"亮背景下更容易辨认准心。"),
            new SettingDefinition(4,0,"x_dynamic","动态扩散","DYNAMIC SPREAD",SettingKind.Toggle,hint:"移动、射击时显示准心扩散。")
        };
        public static SettingDefinition Find(string id) => Array.Find(Rows, r => r.id == id);
    }
}
