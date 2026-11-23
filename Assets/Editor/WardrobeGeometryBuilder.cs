using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

internal static class WardrobeGeometryBuilder
{
    internal const string Folder = "Assets/Resources/Customization";
    public static WardrobeGeometry Build(GameObject rig)
    {
        var animator = rig.GetComponent<Animator>();
        var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        var shoulders = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        var elbow = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
        var hand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
        var right = animator.GetBoneTransform(HumanBodyBones.RightHand);
        Vector3 Local(Transform t) => rig.transform.InverseTransformPoint(t.position);
        var waist = Local(hips).y + .028f;
        var dress = Skin(rig,"Dress/Rifle_Dress"); var jacket = Skin(rig,"Jakcket/Rifle_Jacket");
        var body = Skin(rig,"Dress/Body_Full"); var hair = Skin(rig,"Rifle_Hair");
        string path = Folder + "/WardrobeGeometry.asset";
        var set = AssetDatabase.LoadAssetAtPath<WardrobeGeometry>(path);
        if (!set) { set = ScriptableObject.CreateInstance<WardrobeGeometry>(); AssetDatabase.CreateAsset(set,path); }
        // Preserve subasset local IDs on regeneration so scene and prefab references remain stable.
        Mesh Store(Mesh mesh, string name)
        {
            mesh = WardrobeMeshCut.Compact(mesh);
            mesh.name = name;
            var existing = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().FirstOrDefault(x=>x.name == name);
            if (existing) { EditorUtility.CopySerialized(mesh,existing); Object.DestroyImmediate(mesh); return existing; }
            AssetDatabase.AddObjectToAsset(mesh,set); return mesh;
        }
        var dressPoints = Posed(rig,dress);
        set.bodice = Store(WardrobeMeshCut.Clip(dress.sharedMesh,dressPoints.Select(p=>p.y-waist).ToArray()),"Bodice");
        set.skirt = Store(WardrobeMeshCut.Clip(dress.sharedMesh,dressPoints.Select(p=>waist-p.y).ToArray()),"Skirt");
        var arm = Local(shoulders); var forearm = Local(elbow);
        float shoulderWidth = Mathf.Abs(arm.x), elbowWidth = Mathf.Abs(forearm.x);
        var jacketPoints = Posed(rig,jacket);
        Mesh Sleeves(float limit) => WardrobeMeshCut.Clip(jacket.sharedMesh,jacketPoints.Select(p=>limit-Mathf.Abs(p.x)).ToArray());
        set.vest = Store(Sleeves(Mathf.Lerp(shoulderWidth,elbowWidth,.23f)),"UtilityVest");
        set.shortJacket = Store(Sleeves(Mathf.Lerp(shoulderWidth,elbowWidth,.75f)),"ShortSleeves");

        Mesh bob = Object.Instantiate(hair.sharedMesh); var hp = Posed(rig,hair); var hv = bob.vertices;
        var weights = bob.boneWeights; var bones = hair.bones; var bind = bob.bindposes;
        // Bring each strand tip up towards the shoulders in posed space before returning it to bind space.
        float shoulder = arm.y;
        for (int i=0;i<hv.Length;i++)
        {
            if (hp[i].y >= shoulder) continue;
            var weight = weights[i];
            var matrix = new Matrix4x4();
            void Add(int index,float w)
            {
                if (w <= 0) return;
                var m = bones[index].localToWorldMatrix * bind[index];
                for (int j=0;j<16;j++) matrix[j] += m[j]*w;
            }
            Add(weight.boneIndex0,weight.weight0); Add(weight.boneIndex1,weight.weight1);
            Add(weight.boneIndex2,weight.weight2); Add(weight.boneIndex3,weight.weight3);
            float t = Mathf.Clamp01((shoulder-hp[i].y)/.53f);
            var delta = new Vector3(-hp[i].x*.69f*t, (shoulder-hp[i].y)*.78f, -hp[i].z*.40f*t);
            hv[i] += matrix.inverse.MultiplyVector(rig.transform.TransformVector(delta));
        }
        bob.vertices=hv; bob.RecalculateNormals(); bob.RecalculateBounds(); set.shortHair=Store(bob,"ShoulderLengthHair");
        var bw = body.sharedMesh.boneWeights; var boneList = body.bones;
        bool[] handBones = boneList.Select(b=> b && (b == hand || b.IsChildOf(hand) || b == right || b.IsChildOf(right))).ToArray();
        bool Hand(int i)
        {
            var b=bw[i];
            return (handBones[b.boneIndex0]?b.weight0:0)+(handBones[b.boneIndex1]?b.weight1:0)+
                (handBones[b.boneIndex2]?b.weight2:0)+(handBones[b.boneIndex3]?b.weight3:0) > .38f;
        }
        Mesh glove=Subset(body.sharedMesh,(a,b,c)=>Hand(a)&&Hand(b)&&Hand(c));
        var gv=glove.vertices; var norms=glove.normals; float thickness=glove.bounds.size.magnitude*.0013f;
        for(int i=0;i<gv.Length;i++) gv[i]+=norms[i]*thickness;
        glove.vertices=gv; set.gloves=Store(glove,"TrainingGloves");
        var sportsTop=Skin(rig,"Sportswear/Sportswear_Top");
        var topPoints=Posed(rig,sportsTop);
        set.layeredSportTop=Store(WardrobeMeshCut.Clip(sportsTop.sharedMesh,
            topPoints.Select(p=>shoulderWidth*.96f-Mathf.Abs(p.x)).ToArray()),"SportUnderJacket");
        var armMeshes=new List<WardrobeGeometry.ArmMesh>();
        void AddArmMesh(SkinnedMeshRenderer skin,Mesh mesh,string name)
        {
            Transform l=animator.GetBoneTransform(HumanBodyBones.LeftUpperArm), r=animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
            bool[] armBones=skin.bones.Select(b=>b && (b==l || b.IsChildOf(l) || b==r || b.IsChildOf(r))).ToArray();
            var influence=mesh.boneWeights;
            bool Arm(int index)
            {
                var b=influence[index];
                return (armBones[b.boneIndex0]?b.weight0:0)+(armBones[b.boneIndex1]?b.weight1:0)+
                    (armBones[b.boneIndex2]?b.weight2:0)+(armBones[b.boneIndex3]?b.weight3:0)>.48f;
            }
            var arms=Subset(mesh,(a,b,c)=>Arm(a)||Arm(b)||Arm(c),false);
            armMeshes.Add(new WardrobeGeometry.ArmMesh{source=mesh,arms=Store(arms,name+"Arms"),renderer=skin.name});
        }
        AddArmMesh(body,body.sharedMesh,"InnerBody");
        foreach(string item in new[]{"Jakcket/Body_Jacke","Sportswear/Body_Sportswea","Sportswear/Sportswear_Top"})
        {
            var source=Skin(rig,item); AddArmMesh(source,source.sharedMesh,source.name);
        }
        AddArmMesh(sportsTop,set.layeredSportTop,"SportUnderJacket");
        AddArmMesh(jacket,jacket.sharedMesh,"Jacket"); AddArmMesh(jacket,set.shortJacket,"ShortJacket"); AddArmMesh(jacket,set.vest,"Vest");
        set.armMeshes=armMeshes.ToArray();
        if (!set.gloveMaterial)
        {
            set.gloveMaterial=new Material(Shader.Find("Universal Render Pipeline/Lit")){name="TrainingGloveMaterial",color=new Color(.24f,.24f,.29f)};
            AssetDatabase.AddObjectToAsset(set.gloveMaterial,set);
        }
        set.gloveMaterial.shader=Shader.Find("Universal Render Pipeline/Lit");
        set.gloveMaterial.SetColor("_BaseColor",new Color(.18f,.17f,.23f));
        set.gloveMaterial.SetFloat("_Smoothness",.28f);
        EditorUtility.SetDirty(set.gloveMaterial);
        EditorUtility.SetDirty(set); AssetDatabase.SaveAssetIfDirty(set);
        return set;
    }
    private static SkinnedMeshRenderer Skin(GameObject go,string path) => go.transform.Find(path).GetComponent<SkinnedMeshRenderer>();
    private static Vector3[] Posed(GameObject rig,SkinnedMeshRenderer s)
    {
        var baked=new Mesh(); s.BakeMesh(baked); var verts=baked.vertices; Object.DestroyImmediate(baked);
        var m=rig.transform.worldToLocalMatrix*s.transform.localToWorldMatrix;
        for(int i=0;i<verts.Length;i++) verts[i]=m.MultiplyPoint3x4(verts[i]);
        return verts;
    }
    private static Mesh Subset(Mesh source, Func<int,int,int,bool> include,bool requireGeometry=true)
    {
        var mesh=Object.Instantiate(source);
        for(int sub=0;sub<source.subMeshCount;sub++)
        {
            var tris=source.GetTriangles(sub); var kept=new List<int>(tris.Length);
            for(int i=0;i<tris.Length;i+=3)
                if(include(tris[i],tris[i+1],tris[i+2])) { kept.Add(tris[i]); kept.Add(tris[i+1]); kept.Add(tris[i+2]); }
            mesh.SetTriangles(kept,sub,false);
        }
        if(requireGeometry && mesh.triangles.Length==0) throw new InvalidOperationException("Wardrobe empty cut from "+source.name);
        return mesh;
    }
}