using UnityEngine;
using UnityEngine.UI;

namespace FPS.Settings
{
    /// <summary>Editable uGUI panels, rounded controls and line icons.</summary>
    [AddComponentMenu("UI/Settings/Shape")]
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class SettingsShape : MaskableGraphic
    {
        public enum ShapeKind { Panel, Slanted, Diamond, Hexagon, Ring, DashedLine, Grid, Monitor, Audio, Mouse, Keyboard, Crosshair, RoundedPanel, Chevron }
        public ShapeKind shape;
        public float bevel = 0, stroke = 1;
        public bool outline;
        public Color endColor = Color.white;
        public bool gradient;
        public bool verticalGradient;
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear(); var r = rectTransform.rect;
            if (shape == ShapeKind.Chevron)
            {
                var center = new Vector2(r.center.x, r.yMin + r.height * .28f);
                Line(vh, new Vector2(r.xMin + r.width * .15f, r.yMax - r.height * .28f), center, stroke);
                Line(vh, center, new Vector2(r.xMax - r.width * .15f, r.yMax - r.height * .28f), stroke);
                return;
            }
            if (shape == ShapeKind.Ring)
            {
                const int n = 96;
                for (int i = 0; i < n; i++)
                {
                    float a = i * Mathf.PI * 2 / n, b = (i + 1) * Mathf.PI * 2 / n;
                    Vector2 p = r.center + Vector2.Scale(new Vector2(Mathf.Cos(a), Mathf.Sin(a)), r.size * 0.5f);
                    Vector2 q = r.center + Vector2.Scale(new Vector2(Mathf.Cos(b), Mathf.Sin(b)), r.size * 0.5f);
                    Line(vh, p, q, stroke);
                }
                return;
            }
            if (shape == ShapeKind.DashedLine)
            { for (float x = r.xMin; x < r.xMax; x += 8) Quad(vh, new Rect(x, r.center.y, Mathf.Min(2, r.xMax - x), stroke)); return; }
            if (shape == ShapeKind.Grid)
            {
                for (float x = r.xMin; x < r.xMax; x += 44) Quad(vh, new Rect(x, r.yMin, stroke, r.height));
                for (float y = r.yMin; y < r.yMax; y += 44) Quad(vh, new Rect(r.xMin, y, r.width, stroke));
                return;
            }
            if (shape == ShapeKind.RoundedPanel)
            {
                float radius = Mathf.Clamp(bevel, 0, Mathf.Min(r.width, r.height) * .5f);
                var corners = new[] {
                    new Vector2(r.xMax-radius,r.yMax-radius), new Vector2(r.xMax-radius,r.yMin+radius),
                    new Vector2(r.xMin+radius,r.yMin+radius), new Vector2(r.xMin+radius,r.yMax-radius) };
                const int segments = 10;
                var rounded = new Vector2[4 * (segments + 1)];
                for (int c = 0; c < 4; c++)
                    for (int s = 0; s <= segments; s++)
                    {
                        float angle = (90 - c * 90 - s * 90f / segments) * Mathf.Deg2Rad;
                        rounded[c * (segments + 1) + s] = corners[c] + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
                    }
                if (outline)
                    for (int i = 0; i < rounded.Length; i++) Line(vh, rounded[i], rounded[(i+1)%rounded.Length], stroke);
                else
                {
                    Add(vh, r.center);
                    foreach (var point in rounded) Add(vh, point);
                    for (int i = 0; i < rounded.Length; i++) vh.AddTriangle(0, i+1, (i+1)%rounded.Length+1);
                }
                return;
            }
            if (shape >= ShapeKind.Monitor) { Icon(vh, r); return; }
            Vector2[] points;
            if (shape == ShapeKind.Diamond) points = new[] { new Vector2(r.center.x, r.yMax), new Vector2(r.xMax, r.center.y), new Vector2(r.center.x, r.yMin), new Vector2(r.xMin, r.center.y) };
            else if (shape == ShapeKind.Hexagon) points = new[] { new Vector2(r.center.x,r.yMax),new Vector2(r.xMax,r.yMax-r.height*0.25f),new Vector2(r.xMax,r.yMin+r.height*0.25f),new Vector2(r.center.x,r.yMin),new Vector2(r.xMin,r.yMin+r.height*0.25f),new Vector2(r.xMin,r.yMax-r.height*0.25f) };
            else if (shape == ShapeKind.Slanted) points = new[] { new Vector2(r.xMin+bevel,r.yMax), new Vector2(r.xMax,r.yMax), new Vector2(r.xMax-bevel,r.yMin), new Vector2(r.xMin,r.yMin) };
            else points = new[] { new Vector2(r.xMin,r.yMax),new Vector2(r.xMax,r.yMax),new Vector2(r.xMax,r.yMin+bevel),new Vector2(r.xMax-bevel,r.yMin),new Vector2(r.xMin,r.yMin) };
            if (outline) for (int i = 0; i < points.Length; i++) Line(vh, points[i], points[(i + 1) % points.Length], stroke);
            else
            {
                Add(vh, r.center);
                for (int i = 0; i < points.Length; i++) Add(vh, points[i]);
                for (int i = 0; i < points.Length; i++) vh.AddTriangle(0, i + 1, (i + 1) % points.Length + 1);
            }
        }
        private void Icon(VertexHelper vh, Rect r)
        {
            Vector2 P(float x, float y) => new Vector2(r.xMin + x * r.width, r.yMax - y * r.height);
            void L(float x,float y,float xx,float yy) => Line(vh,P(x,y),P(xx,yy),stroke);
            void Box(float x,float y,float w,float h) { L(x,y,x+w,y); L(x+w,y,x+w,y+h); L(x+w,y+h,x,y+h); L(x,y+h,x,y); }
            if (shape == ShapeKind.Monitor)
            { Box(.1f,.18f,.8f,.52f); L(.5f,.7f,.5f,.87f); L(.34f,.87f,.66f,.87f); L(.25f,.53f,.4f,.4f); L(.4f,.4f,.53f,.5f); L(.53f,.5f,.77f,.3f); }
            else if (shape == ShapeKind.Audio)
            { for (int i=0;i<5;i++) { float h = i==2?.72f:i==1||i==3?.48f:.22f; L(.15f+i*.175f,.5f-h/2,.15f+i*.175f,.5f+h/2); } }
            else if (shape == ShapeKind.Mouse)
            { Box(.26f,.08f,.48f,.84f); L(.5f,.08f,.5f,.36f); L(.5f,.48f,.5f,.58f); }
            else if (shape == ShapeKind.Keyboard)
            { Box(.06f,.28f,.88f,.5f); for(int i=0;i<4;i++) { L(.18f+i*.2f,.45f,.25f+i*.2f,.45f); L(.18f+i*.2f,.59f,.25f+i*.2f,.59f); } L(.3f,.7f,.7f,.7f); }
            else
            { L(.5f,0,.5f,.3f); L(.5f,.7f,.5f,1); L(0,.5f,.3f,.5f); L(.7f,.5f,1,.5f); Box(.3f,.3f,.4f,.4f); }
        }
        private void Quad(VertexHelper vh, Rect r)
        { int start=vh.currentVertCount; Add(vh,new Vector2(r.xMin,r.yMin));Add(vh,new Vector2(r.xMin,r.yMax));Add(vh,new Vector2(r.xMax,r.yMax));Add(vh,new Vector2(r.xMax,r.yMin));vh.AddTriangle(start,start+1,start+2);vh.AddTriangle(start,start+2,start+3); }
        private void Line(VertexHelper vh, Vector2 a, Vector2 b,float width)
        {
            Vector2 n = new Vector2(-(b-a).y,(b-a).x).normalized * width * .5f;
            int start=vh.currentVertCount;Add(vh,a-n);Add(vh,a+n);Add(vh,b+n);Add(vh,b-n);vh.AddTriangle(start,start+1,start+2);vh.AddTriangle(start,start+2,start+3);
        }
        private void Add(VertexHelper vh,Vector2 p)
        { var r=rectTransform.rect; float t=verticalGradient?Mathf.InverseLerp(r.yMax,r.yMin,p.y):Mathf.InverseLerp(r.xMin,r.xMax,p.x);vh.AddVert(p,gradient?Color.Lerp(color,endColor,t):color,Vector2.zero); }
    }
}
