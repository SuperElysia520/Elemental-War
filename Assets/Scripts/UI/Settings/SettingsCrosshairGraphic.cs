using UnityEngine;
using UnityEngine.UI;

namespace FPS.Settings
{
    [AddComponentMenu("UI/Settings/Crosshair")]
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class SettingsCrosshairGraphic : MaskableGraphic
    {
        public int style = 3;
        public float length = 7, gap = 4, thickness = 2;
        public bool centerDot = true, blackOutline = true;
        public static readonly Color[] Palette = { new Color32(60,232,255,255),new Color32(234,246,255,255),new Color32(255,63,158,255),new Color32(125,255,155,255),new Color32(255,196,107,255) };
        public void Configure(GameSettingsData data)
        {
            style = data.x_style; length = data.x_len; gap = data.x_gap; thickness = data.x_thick;
            centerDot = data.x_dot; blackOutline = data.x_outline; color = Palette[data.x_color]; SetVerticesDirty();
        }
        public void SetSpread(float value) { if (Mathf.Abs(gap-value)<.05f) return; gap=value; SetVerticesDirty(); }
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear(); Vector2 c=rectTransform.rect.center;
            if (style==2)
            {
                if (blackOutline) Ring(vh,c,gap+length,thickness+2,new Color(0,0,0,.8f));
                Ring(vh,c,gap+length,thickness,color);
            }
            else if (style!=1)
            {
                Arm(vh,new Rect(c.x-thickness*.5f,c.y+gap,thickness,length));
                Arm(vh,new Rect(c.x-thickness*.5f,c.y-gap-length,thickness,length));
                Arm(vh,new Rect(c.x+gap,c.y-thickness*.5f,length,thickness));
                Arm(vh,new Rect(c.x-gap-length,c.y-thickness*.5f,length,thickness));
            }
            if (centerDot && style!=0 && style!=2) Arm(vh,new Rect(c.x-(thickness+1)*.5f,c.y-(thickness+1)*.5f,thickness+1,thickness+1));
        }
        private void Arm(VertexHelper vh,Rect r)
        {
            if (blackOutline) Quad(vh,new Rect(r.x-1,r.y-1,r.width+2,r.height+2),new Color(0,0,0,.8f));
            Quad(vh,r,color);
        }
        private static void Quad(VertexHelper vh,Rect r,Color c)
        { int i=vh.currentVertCount;vh.AddVert(new Vector2(r.xMin,r.yMin),c,Vector2.zero);vh.AddVert(new Vector2(r.xMin,r.yMax),c,Vector2.zero);vh.AddVert(new Vector2(r.xMax,r.yMax),c,Vector2.zero);vh.AddVert(new Vector2(r.xMax,r.yMin),c,Vector2.zero);vh.AddTriangle(i,i+1,i+2);vh.AddTriangle(i,i+2,i+3); }
        private static void Ring(VertexHelper vh,Vector2 center,float radius,float width,Color c)
        {
            for(int k=0;k<96;k++)
            {
                float a=k*Mathf.PI*2/96,b=(k+1)*Mathf.PI*2/96;
                Vector2 u=new Vector2(Mathf.Cos(a),Mathf.Sin(a)),v=new Vector2(Mathf.Cos(b),Mathf.Sin(b));
                int i=vh.currentVertCount;
                vh.AddVert(center+u*(radius-width*.5f),c,Vector2.zero);vh.AddVert(center+u*(radius+width*.5f),c,Vector2.zero);
                vh.AddVert(center+v*(radius+width*.5f),c,Vector2.zero);vh.AddVert(center+v*(radius-width*.5f),c,Vector2.zero);
                vh.AddTriangle(i,i+1,i+2);vh.AddTriangle(i,i+2,i+3);
            }
        }
    }
}
