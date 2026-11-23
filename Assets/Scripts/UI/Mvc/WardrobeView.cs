using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FPS.UI
{
    public sealed class WardrobeView : LobbyPageView
    {
        [Serializable] public struct Card
        {
            public Button button;
            public RawImage icon;
            public TMP_Text name, badge;
            public Image border;
        }
        [Serializable] public struct ItemImages { public Texture2D[] items; }
        public Button[] slots, colors;
        private Transform[] slotMarkers;
        public Card[] cards;
        public ItemImages[] thumbnails;
        public Texture2D paletteAtlas;
        public TMP_Text heading, selectedName, inner, outer, detailTitle, feedback, removeText, dragHint;
        public RawImage innerIcon, outerIcon;
        public Button save, undo, detail, closeDetail, remove, fullBody, firstPerson;
        public GameObject details;
        public WardrobePreview preview;
        public ClothingSlot SelectedSlot { get; private set; } = ClothingSlot.Jacket;
        private CharacterAppearanceData current, saved;
        private bool editable;
        public event Action<CharacterAppearanceData> Changed;
        public event Action UndoRequested;
        public static readonly Color Ink=new Color32(21,46,88,255), Blue=new Color32(31,103,210,255),
            Paper=new Color32(241,247,255,255), Yellow=new Color32(255,220,60,255);
        protected override void Awake()
        {
            base.Awake();
            slotMarkers=new Transform[slots.Length];
            for(int i=0;i<slots.Length;i++) slotMarkers[i]=slots[i].transform.Find("Selected");
            for(int i=0;i<slots.Length;i++) { var index=(ClothingSlot)i; slots[i].onClick.AddListener(()=>SelectSlot(index)); }
            for(int i=0;i<cards.Length;i++) { int index=i; cards[i].button.onClick.AddListener(()=>Pick(index+(WardrobeItems.Optional(SelectedSlot)?1:0))); }
            for(int i=0;i<colors.Length;i++) { int index=i; colors[i].onClick.AddListener(()=>Recolor(index)); }
            detail.onClick.AddListener(()=>{ details.SetActive(true); Paint(); });
            closeDetail.onClick.AddListener(()=>details.SetActive(false));
            remove.onClick.AddListener(()=>Pick(0)); undo.onClick.AddListener(()=>UndoRequested?.Invoke());
            fullBody.onClick.AddListener(()=>Mode(false)); firstPerson.onClick.AddListener(()=>Mode(true));
        }
        private void OnEnable() { SelectedSlot=ClothingSlot.Jacket; if(details) details.SetActive(false); }
        public bool CloseDetails()
        { if (!details.activeSelf) return false; details.SetActive(false); return true; }
        public void RenderDraft(CustomizationModel draft,PlayerProfileData profile,GameObject station,string message,bool allow,bool blocked)
        {
            current=draft.Appearance; saved=profile!=null?profile.appearance:CharacterAppearanceData.Default;
            editable=allow && !blocked; SetInputEnabled(!blocked);
            feedback.text=message; save.interactable=editable && draft.IsDirty;
            undo.interactable=editable && draft.IsDirty;
            preview.Bind(station); preview.Apply(current); Paint();
        }
        public void SelectSlot(ClothingSlot value)
        { SelectedSlot=value; details.SetActive(false); Paint(); }
        public void Mode(bool first)
        { preview.SetMode(first); Paint(); }
        public void Pick(int item)
        {
            if (!editable) return;
            if(item==WardrobeItems.Item(current,SelectedSlot)) return;
            int color=item==WardrobeItems.Item(saved,SelectedSlot)?WardrobeItems.ColorId(saved,SelectedSlot):0;
            var a=WardrobeItems.Select(current,SelectedSlot,item,color);
            if (!a.Equals(current)) Changed?.Invoke(a);
        }
        private void Recolor(int value)
        {
            if (!editable) return;
            Changed?.Invoke(WardrobeItems.Select(current,SelectedSlot,WardrobeItems.Item(current,SelectedSlot),value));
        }
        private void Paint()
        {
            int slot=(int)SelectedSlot, selected=WardrobeItems.Item(current,SelectedSlot);
            int equipped=WardrobeItems.Item(saved,SelectedSlot), color=WardrobeItems.ColorId(current,SelectedSlot);
            string[] names=WardrobeItems.Names[slot]; heading.text=WardrobeItems.Slots[slot];
            for(int i=0;i<slots.Length;i++)
            {
                Highlight(slots[i],i==slot,Paper,Ink,new Color32(25,62,136,255),Color.white);
                slotMarkers[i].gameObject.SetActive(i==slot);
            }
            for(int i=0;i<cards.Length;i++)
            {
                int id=i+(WardrobeItems.Optional(SelectedSlot)?1:0);
                var card=cards[i]; card.button.gameObject.SetActive(id<names.Length);
                if(id>=names.Length) continue;
                card.name.text=names[id]; card.icon.texture=thumbnails[slot].items[id];
                card.icon.uvRect=new Rect(0,0,1,1);
                if(id==selected && color!=0) UseSample(card.icon,SelectedSlot,id,color);
                card.icon.gameObject.SetActive(card.icon.texture!=null);
                card.border.color=id==selected?Blue:new Color32(195,213,238,255);
                bool trying=id==selected && (id!=equipped || color!=WardrobeItems.ColorId(saved,SelectedSlot));
                card.badge.text=trying?"试穿中":id==equipped?"已穿戴":"";
                card.badge.transform.parent.gameObject.SetActive(card.badge.text.Length>0);
                card.badge.transform.parent.GetComponent<Image>().color=trying?Blue:new Color32(83,104,137,255);
                card.button.interactable=editable;
            }
            selectedName.text=WardrobeItems.Optional(SelectedSlot) && selected==0?"未穿戴":names[selected];
            innerIcon.texture=thumbnails[(int)ClothingSlot.Top].items[WardrobeItems.Item(current,ClothingSlot.Top)];
            outerIcon.texture=thumbnails[(int)ClothingSlot.Jacket].items[WardrobeItems.Item(current,ClothingSlot.Jacket)];
            ShowLayerColor(innerIcon,ClothingSlot.Top); ShowLayerColor(outerIcon,ClothingSlot.Jacket);
            outerIcon.gameObject.SetActive(outerIcon.texture!=null);
            inner.text=ItemName(ClothingSlot.Top); outer.text=WardrobeItems.Item(current,ClothingSlot.Jacket)==0?"无外套":ItemName(ClothingSlot.Jacket);
            bool empty=WardrobeItems.Optional(SelectedSlot)&&selected==0;
            remove.gameObject.SetActive(WardrobeItems.Optional(SelectedSlot)&&!empty); removeText.text=names[0]; remove.interactable=editable;
            detail.interactable=!empty;
            if(empty) details.SetActive(false);
            detailTitle.text=names[selected]+"  /  配色";
            for(int i=0;i<colors.Length;i++)
            {
                colors[i].gameObject.SetActive(i<(slot==0?4:5)); colors[i].interactable=editable;
                if(empty || !details.activeSelf) continue;
                var label=colors[i].GetComponentInChildren<TMP_Text>(); label.text=i==color?"已选择":"配色 "+(i+1);
                colors[i].image.color=i==color?new Color32(188,213,255,255):Paper;
                UseSample(colors[i].GetComponentInChildren<RawImage>(),SelectedSlot,selected,i);
            }
            Highlight(fullBody,!preview.FirstPerson,Blue,Color.white,Paper,Ink);
            Highlight(firstPerson,preview.FirstPerson,Blue,Color.white,Paper,Ink);
            dragHint.text=preview.FirstPerson?"拖动查看":"拖动旋转";
        }
        private void ShowLayerColor(RawImage icon,ClothingSlot slot)
        {
            int dye=WardrobeItems.ColorId(current,slot);
            icon.uvRect=new Rect(0,0,1,1);
            if(icon.texture!=null && dye!=0) UseSample(icon,slot,WardrobeItems.Item(current,slot),dye);
        }
        private void UseSample(RawImage image,ClothingSlot slot,int item,int dye)
        {
            int rows=WardrobeItems.PaletteRow(ClothingSlot.Accessory,1)+1;
            image.texture=paletteAtlas;
            image.uvRect=new Rect(dye/5f,(float)WardrobeItems.PaletteRow(slot,item)/rows,.2f,1f/rows);
        }
        private string ItemName(ClothingSlot s)=>WardrobeItems.Names[(int)s][WardrobeItems.Item(current,s)];
        private static void Highlight(Button b,bool on,Color active,Color text,Color idle,Color inactiveText)
        { b.image.color=on?active:idle; b.GetComponentInChildren<TMP_Text>().color=on?text:inactiveText; }
    }
}
