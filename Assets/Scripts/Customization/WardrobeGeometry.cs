using UnityEngine;

// Shared assets generated from the project's original rig. All parts use its original bind poses.
public sealed class WardrobeGeometry : ScriptableObject
{
    public Mesh shortHair, bodice, skirt, vest, shortJacket, gloves, layeredSportTop;
    public Material gloveMaterial;
    [System.Serializable] public struct ArmMesh { public Mesh source, arms; public string renderer; }
    public ArmMesh[] armMeshes;
}

internal sealed class WardrobeRenderer
{
    private readonly SkinnedMeshRenderer hair, dress, jacket, sportsTop, shorts, boots, sneakers, hat, body;
    private readonly SkinnedMeshRenderer skirt, top, gloves;
    private readonly Renderer[] accessories;
    private readonly Mesh longHair, longJacket, originalSportTop;
    private readonly Material sportsTopMaterial, shortsMaterial, sneakersMaterial;
    private readonly WardrobeGeometry geometry;
    private readonly MaterialPropertyBlock tint = new MaterialPropertyBlock();
    private readonly GameObject[] roots;
    private readonly Transform host;

    public WardrobeRenderer(Transform host, GameObject[] roots, GameObject accessory, WardrobeGeometry geometry)
    {
        this.geometry = geometry; this.host = host; this.roots = roots;
        hair = Skin("Rifle_Hair"); hat = Skin("Rifle_Hat");
        jacket = Skin("Jakcket/Rifle_Jacket"); dress = Skin("Dress/Rifle_Dress");
        sportsTop = Skin("Sportswear/Sportswear_Top"); shorts = Skin("Sportswear/Sportswear_Pants");
        boots = Skin("Dress/Rifle_Boots"); sneakers = Skin("Sportswear/Sportswear_Shoes");
        body = Skin("Dress/Body_Full");
        accessories = accessory.GetComponentsInChildren<Renderer>(true);
        longHair = hair.sharedMesh; longJacket = jacket.sharedMesh; originalSportTop = sportsTop.sharedMesh;
        sportsTopMaterial = sportsTop.sharedMaterial; shortsMaterial = shorts.sharedMaterial; sneakersMaterial = sneakers.sharedMaterial;
        top = Part(dress, geometry.bodice, "WardrobeBodice"); skirt = Part(dress, geometry.skirt, "WardrobeSkirt");
        gloves = Part(body, geometry.gloves, "WardrobeGloves"); gloves.sharedMaterial = geometry.gloveMaterial;
    }
    private SkinnedMeshRenderer Skin(string path) => host.Find(path).GetComponent<SkinnedMeshRenderer>();
    private static SkinnedMeshRenderer Part(SkinnedMeshRenderer source, Mesh mesh, string name)
    {
        var go = new GameObject(name); go.layer = source.gameObject.layer;
        var t = go.transform; t.SetParent(source.transform, false);
        var r = go.AddComponent<SkinnedMeshRenderer>();
        r.sharedMesh = mesh; r.bones = source.bones; r.rootBone = source.rootBone;
        r.sharedMaterial = source.sharedMaterial; r.localBounds = source.localBounds;
        r.updateWhenOffscreen = source.updateWhenOffscreen; r.enabled = false;
        return r;
    }
    public Renderer Thumbnail(ClothingSlot slot, int item)
    {
        switch (slot)
        {
            case ClothingSlot.Hair: return hair;
            case ClothingSlot.Hat: return hat;
            case ClothingSlot.Top: return item == 0 ? top : sportsTop;
            case ClothingSlot.Jacket: return jacket;
            case ClothingSlot.Bottom: return item == 0 ? skirt : shorts;
            case ClothingSlot.Shoes: return item == 0 ? boots : sneakers;
            case ClothingSlot.Gloves: return gloves;
            default: return System.Array.Find(accessories, r => r.name == "Rifle_Belt") ?? accessories[0];
        }
    }
    public void Apply(CharacterAppearanceData a, Material[] cloth, Material[] hairColors)
    {
        int I(ClothingSlot s) => WardrobeItems.Item(a,s);
        int C(ClothingSlot s) => WardrobeItems.ColorId(a,s);
        foreach (var root in roots) root.SetActive(true);
        Skin("Jakcket/Body_Jacke").enabled = false; Skin("Sportswear/Body_Sportswea").enabled = false;
        Skin("Jakcket/Rifle_Dress (1)").enabled = false; Skin("Jakcket/Rifle_Boots (1)").enabled = false;
        dress.enabled = false;
        body.enabled = true;
        hair.sharedMesh = I(ClothingSlot.Hair) == 0 ? longHair : geometry.shortHair;
        hair.sharedMaterial = hairColors[C(ClothingSlot.Hair)];
        hat.gameObject.SetActive(I(ClothingSlot.Hat) != 0); hat.sharedMaterial = cloth[C(ClothingSlot.Hat)];
        int outer = I(ClothingSlot.Jacket);
        jacket.enabled = outer != 0;
        jacket.sharedMesh = outer == 2 ? geometry.vest : outer == 3 ? geometry.shortJacket : longJacket;
        jacket.sharedMaterial = cloth[C(ClothingSlot.Jacket)];
        top.enabled = I(ClothingSlot.Top) == 0; sportsTop.enabled = !top.enabled;
        sportsTop.sharedMesh = outer == 1 || outer == 3 ? geometry.layeredSportTop : originalSportTop;
        top.sharedMaterial = cloth[C(ClothingSlot.Top)]; Tint(sportsTop,C(ClothingSlot.Top));
        skirt.enabled = I(ClothingSlot.Bottom) == 0; shorts.enabled = !skirt.enabled;
        skirt.sharedMaterial = cloth[C(ClothingSlot.Bottom)]; Tint(shorts,C(ClothingSlot.Bottom));
        boots.enabled = I(ClothingSlot.Shoes) == 0; sneakers.enabled = !boots.enabled;
        boots.sharedMaterial = cloth[C(ClothingSlot.Shoes)]; Tint(sneakers,C(ClothingSlot.Shoes));
        gloves.enabled = I(ClothingSlot.Gloves) != 0; Tint(gloves,C(ClothingSlot.Gloves));
        foreach (var r in accessories)
        {
            if(r.name=="Rifle_Belt") r.enabled=!sportsTop.enabled;
            Tint(r,C(ClothingSlot.Accessory));
        }
    }
    private void Tint(Renderer r, int color)
    {
        if (color == 0) { r.SetPropertyBlock(null); return; }
        tint.Clear(); var c = WardrobeItems.Tints[color];
        tint.SetColor("_BaseColor",c); tint.SetColor("_Color",c);
        r.SetPropertyBlock(tint);
    }
    public void Restore()
    {
        foreach (var root in roots)
            foreach (var r in root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) r.enabled = true;
        skirt.enabled = top.enabled = gloves.enabled = false;
        hair.sharedMesh = longHair; jacket.sharedMesh = longJacket;
        sportsTop.sharedMesh = originalSportTop;
        sportsTop.sharedMaterial = sportsTopMaterial; shorts.sharedMaterial = shortsMaterial; sneakers.sharedMaterial = sneakersMaterial;
        foreach (var r in new Renderer[] { sportsTop, shorts, sneakers, gloves }) r.SetPropertyBlock(null);
        foreach (var r in accessories) { r.enabled=true; r.SetPropertyBlock(null); }
    }
}