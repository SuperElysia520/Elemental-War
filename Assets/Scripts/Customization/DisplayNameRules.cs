/// <summary>Rules for newly chosen player names; account login usernames are separate.</summary>
public static class DisplayNameRules
{
    public const int MaxLength = 12;
    public const string Hint = "名称最多6个汉字或12个英文字母/数字，混用时汉字计2，其余计1，总计不超过12。";
    public const string Error = "请输入中文、英文字母或数字组成的名称；汉字计2，字母和数字计1，总计1–12。";

    public static bool IsValid(string name) => TryGetLength(name, out int length) && length > 0 && length <= MaxLength;

    public static bool TryGetLength(string name, out int length)
    {
        length = 0;
        if (name == null) return false;
        for (int i = 0; i < name.Length; i++)
        {
            int code = name[i];
            if (char.IsHighSurrogate(name[i]))
            {
                if (i + 1 >= name.Length || !char.IsLowSurrogate(name[i + 1])) return false;
                code = char.ConvertToUtf32(name[i], name[++i]);
            }
            if ((code >= 'A' && code <= 'Z') || (code >= 'a' && code <= 'z') || (code >= '0' && code <= '9')) length++;
            else if ((code >= 0x3400 && code <= 0x4DBF) || (code >= 0x4E00 && code <= 0x9FFF) ||
                     (code >= 0xF900 && code <= 0xFAFF) || (code >= 0x20000 && code <= 0x2FFFF) ||
                     (code >= 0x30000 && code <= 0x3FFFF)) length += 2;
            else return false;
        }
        return true;
    }
}
