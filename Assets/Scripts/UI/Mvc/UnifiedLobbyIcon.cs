using UnityEngine;
using UnityEngine.UI;

namespace FPS.UI
{
    /// <summary>Resolution-independent, tintable lobby symbols; no icon-font dependency.</summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class UnifiedLobbyIcon : MaskableGraphic
    {
        public enum Symbol { Person, Team, Crosshair, Rooms, Shirt, Rifle, Mail, Settings, Exit, Chevron, Chat, AddPerson, Block }
        public Symbol symbol;
        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            switch (symbol)
            {
                case Symbol.Person: Person(mesh, .5f, .29f, .18f); break;
                case Symbol.Team: Person(mesh, .35f, .30f, .15f); Person(mesh, .73f, .4f, .12f); break;
                case Symbol.Crosshair:
                    Ring(mesh,.5f,.5f,.30f,.055f); Disc(mesh,.5f,.5f,.065f);
                    Line(mesh,.5f,.04f,.5f,.30f,.065f); Line(mesh,.5f,.70f,.5f,.96f,.065f);
                    Line(mesh,.04f,.5f,.30f,.5f,.065f); Line(mesh,.70f,.5f,.96f,.5f,.065f); break;
                case Symbol.Rooms:
                    Box(mesh,.10f,.19f,.80f,.65f,.055f);
                    for(int i=0;i<3;i++){Rect(mesh,.22f,.32f+i*.15f,.075f,.055f);Rect(mesh,.38f,.32f+i*.15f,.39f,.055f);}break;
                case Symbol.Shirt:
                    Poly(mesh,new[]{new Vector2(.33f,.14f),new Vector2(.17f,.19f),new Vector2(.02f,.42f),new Vector2(.20f,.53f),new Vector2(.29f,.40f),new Vector2(.29f,.89f),new Vector2(.71f,.89f),new Vector2(.71f,.4f),new Vector2(.8f,.53f),new Vector2(.98f,.42f),new Vector2(.83f,.19f),new Vector2(.67f,.14f),new Vector2(.59f,.27f),new Vector2(.41f,.27f)});break;
                case Symbol.Rifle:
                    Poly(mesh,new[]{new Vector2(.04f,.55f),new Vector2(.28f,.37f),new Vector2(.62f,.37f),new Vector2(.62f,.30f),new Vector2(.72f,.30f),new Vector2(.72f,.38f),new Vector2(.98f,.38f),new Vector2(.98f,.45f),new Vector2(.7f,.47f),new Vector2(.64f,.53f),new Vector2(.60f,.76f),new Vector2(.48f,.73f),new Vector2(.50f,.54f),new Vector2(.40f,.54f),new Vector2(.32f,.70f),new Vector2(.24f,.67f),new Vector2(.26f,.53f),new Vector2(.08f,.68f)});break;
                case Symbol.Mail:
                    Box(mesh,.08f,.20f,.84f,.62f,.06f);Line(mesh,.1f,.25f,.5f,.57f,.06f);Line(mesh,.5f,.57f,.9f,.25f,.06f);break;
                case Symbol.Settings:
                    Ring(mesh,.5f,.5f,.27f,.09f);
                    for(int i=0;i<8;i++){float a=i*Mathf.PI/4;Line(mesh,.5f+Mathf.Cos(a)*.28f,.5f+Mathf.Sin(a)*.28f,.5f+Mathf.Cos(a)*.43f,.5f+Mathf.Sin(a)*.43f,.13f);}break;
                case Symbol.Exit:
                    Line(mesh,.22f,.18f,.22f,.85f,.065f);Line(mesh,.22f,.18f,.58f,.18f,.065f);Line(mesh,.22f,.85f,.58f,.85f,.065f);
                    Line(mesh,.42f,.5f,.94f,.5f,.065f);Line(mesh,.74f,.3f,.94f,.5f,.065f);Line(mesh,.74f,.7f,.94f,.5f,.065f);break;
                case Symbol.Chevron:Line(mesh,.3f,.18f,.65f,.5f,.07f);Line(mesh,.65f,.5f,.3f,.82f,.07f);break;
                case Symbol.Chat:
                    Box(mesh,.08f,.17f,.84f,.58f,.06f);Line(mesh,.20f,.73f,.20f,.94f,.06f);Line(mesh,.20f,.94f,.41f,.75f,.06f);
                    Disc(mesh,.29f,.46f,.045f);Disc(mesh,.50f,.46f,.045f);Disc(mesh,.71f,.46f,.045f);break;
                case Symbol.AddPerson:
                    Person(mesh,.34f,.29f,.17f);
                    Line(mesh,.59f,.49f,.95f,.49f,.075f);Line(mesh,.77f,.31f,.77f,.67f,.075f);break;
                case Symbol.Block:
                    Ring(mesh,.5f,.5f,.36f,.07f);Line(mesh,.25f,.75f,.75f,.25f,.07f);break;
            }
        }
        private Vector2 P(Vector2 p){var r=rectTransform.rect;return new Vector2(r.xMin+p.x*r.width,r.yMax-p.y*r.height);}
        private void Poly(VertexHelper mesh,Vector2[] points)
        {
            int start=mesh.currentVertCount;var center=Vector2.zero;foreach(var p in points)center+=p;center/=points.Length;Add(mesh,center);
            foreach(var p in points)Add(mesh,p);
            for(int i=0;i<points.Length;i++)mesh.AddTriangle(start,start+i+1,start+(i+1)%points.Length+1);
        }
        private void Add(VertexHelper mesh,Vector2 p){var v=UIVertex.simpleVert;v.position=P(p);v.color=color;mesh.AddVert(v);}
        private void Rect(VertexHelper mesh,float x,float y,float w,float h)=>Poly(mesh,new[]{new Vector2(x,y),new Vector2(x+w,y),new Vector2(x+w,y+h),new Vector2(x,y+h)});
        private void Line(VertexHelper mesh,float x,float y,float xx,float yy,float width)
        {var a=new Vector2(x,y);var b=new Vector2(xx,yy);var n=new Vector2(-(b-a).y,(b-a).x).normalized*width*.5f;Poly(mesh,new[]{a+n,b+n,b-n,a-n});}
        private void Box(VertexHelper mesh,float x,float y,float w,float h,float s){Line(mesh,x,y,x+w,y,s);Line(mesh,x+w,y,x+w,y+h,s);Line(mesh,x+w,y+h,x,y+h,s);Line(mesh,x,y+h,x,y,s);}
        private void Disc(VertexHelper mesh,float x,float y,float radius)
        {var points=new Vector2[40];for(int i=0;i<40;i++){float a=i*Mathf.PI*2/40;points[i]=new Vector2(x+Mathf.Cos(a)*radius,y+Mathf.Sin(a)*radius);}Poly(mesh,points);}
        private void Ring(VertexHelper mesh,float x,float y,float radius,float s)
        {for(int i=0;i<48;i++){float a=i*Mathf.PI*2/48,b=(i+1)*Mathf.PI*2/48;Line(mesh,x+Mathf.Cos(a)*radius,y+Mathf.Sin(a)*radius,x+Mathf.Cos(b)*radius,y+Mathf.Sin(b)*radius,s);}}
        private void Person(VertexHelper mesh,float x,float y,float radius){Disc(mesh,x,y,radius);Rect(mesh,x-radius*1.25f,y+radius*1.4f,radius*2.5f,radius*1.55f);}
    }
}
