using UnityEngine;

public enum ClothingSlot { Hair, Hat, Top, Jacket, Bottom, Shoes, Gloves, Accessory }

public static class WardrobeItems
{
    // Two bits per slot. A marker distinguishes an explicitly edited outfit from a legacy preset.
    public const int Marker = 1 << 16;
    public static readonly string[] Slots = { "发型", "帽子", "上装", "外套", "下装", "鞋靴", "手套", "配饰" };
    public static readonly string[][] Names = {
        new[] { "侧丸长发", "齐肩短发" }, new[] { "摘下帽子", "贝雷帽" },
        new[] { "修身上衣", "运动上衣" }, new[] { "脱下外套", "飞行夹克", "机能马甲", "短袖夹克" },
        new[] { "百褶短裙", "运动短裤" }, new[] { "系带长靴", "运动鞋" },
        new[] { "摘下手套", "训练手套" }, new[] { "取下配饰", "腰包与护膝" } };
    public static int PaletteRow(ClothingSlot slot,int item)
    {
        int row=item-(Optional(slot)?1:0);
        for(int i=0;i<(int)slot;i++) row+=Names[i].Length-(Optional((ClothingSlot)i)?1:0);
        return row;
    }
    public static readonly Color[] Tints = { Color.white, new Color(.43f,.65f,.95f), new Color(.98f,.61f,.73f), new Color(.55f,.85f,.72f), new Color(.48f,.51f,.59f) };
    public static bool Optional(ClothingSlot slot) => slot == ClothingSlot.Hat || slot == ClothingSlot.Jacket || slot == ClothingSlot.Gloves || slot == ClothingSlot.Accessory;
    public static int Item(CharacterAppearanceData a, ClothingSlot slot)
    {
        if (a.wardrobeItems != 0) return (a.wardrobeItems >> (2 * (int)slot)) & 3;
        switch (slot)
        {
            case ClothingSlot.Hat: return a.hat ? 1 : 0;
            case ClothingSlot.Accessory: return a.accessories && a.outfitId != 2 ? 1 : 0;
            case ClothingSlot.Jacket: return a.outfitId == 0 ? 1 : 0;
            case ClothingSlot.Top: case ClothingSlot.Bottom: case ClothingSlot.Shoes: return a.outfitId == 2 ? 1 : 0;
            default: return 0;
        }
    }
    public static int ColorId(CharacterAppearanceData a, ClothingSlot slot)
    {
        if (a.wardrobeItems != 0) return (a.wardrobeColors >> (3 * (int)slot)) & 7;
        if (slot == ClothingSlot.Hair) return a.hairColorId;
        if (slot == ClothingSlot.Gloves || slot == ClothingSlot.Accessory || a.outfitId == 2 && slot != ClothingSlot.Hat) return 0;
        return a.clothColorId;
    }
    public static bool SameClothing(CharacterAppearanceData a, CharacterAppearanceData b)
    {
        for(int i=0;i<Slots.Length;i++)
        {
            var slot=(ClothingSlot)i;
            if(Item(a,slot)!=Item(b,slot) || ColorId(a,slot)!=ColorId(b,slot)) return false;
        }
        return true;
    }
    public static bool Valid(int items, int colors)
    {
        if (items == 0) return colors == 0;
        if ((items >> 16) != 1 || (colors >> 24) != 0) return false;
        for (int i = 0; i < Slots.Length; i++)
            if (((items >> (2*i)) & 3) >= Names[i].Length || ((colors >> (3*i)) & 7) >= (i == 0 ? 4 : 5)) return false;
        return true;
    }
    public static CharacterAppearanceData Select(CharacterAppearanceData a, ClothingSlot slot, int item, int color)
    {
        if ((uint)slot >= Slots.Length || (uint)item >= Names[(int)slot].Length || (uint)color >= (slot == ClothingSlot.Hair ? 4 : 5)) return a;
        if (Item(a,slot) == item && ColorId(a,slot) == color) return a;
        if (a.wardrobeItems == 0)
        {
            int items = Marker, colors = 0;
            for (int i = 0; i < Slots.Length; i++)
            {
                items |= Item(a,(ClothingSlot)i) << (2*i);
                colors |= ColorId(a,(ClothingSlot)i) << (3*i);
            }
            a.wardrobeItems = items; a.wardrobeColors = colors;
        }
        int ix = (int)slot;
        a.wardrobeItems = (a.wardrobeItems & ~(3 << (2*ix))) | (item << (2*ix));
        a.wardrobeColors = (a.wardrobeColors & ~(7 << (3*ix))) | (color << (3*ix));
        return a;
    }
}