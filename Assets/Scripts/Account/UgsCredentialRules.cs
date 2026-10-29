using System;

/// <summary>Matches the Username & Password provider; never normalizes passwords.</summary>
public static class UgsCredentialRules
{
    public const string UsernameHelp = "用户名需 3–20 位：英文、数字或 . - @ _。";
    public const string PasswordHelp = "密码需 8–30 位，大小写/数字/符号缺一不可。";

    public static bool IsValidUsername(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 3 || value.Length > 20) return false;
        foreach (char c in value)
            if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') &&
                c != '.' && c != '-' && c != '@' && c != '_') return false;
        return true;
    }

    public static bool IsValidPassword(string value)
    {
        if (value == null || value.Length < 8 || value.Length > 30) return false;
        bool upper = false, lower = false, digit = false, symbol = false;
        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) return false;
            if (c >= 'A' && c <= 'Z') upper = true;
            else if (c >= 'a' && c <= 'z') lower = true;
            else if (c >= '0' && c <= '9') digit = true;
            else symbol = true;
        }
        return upper && lower && digit && symbol;
    }
}
