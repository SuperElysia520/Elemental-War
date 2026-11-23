using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

// Interpolate along the existing garment triangles so a cropped or split piece has a continuous hem.
internal static class WardrobeMeshCut
{
    public static Mesh Clip(Mesh source,float[] distance)
    {
        var verts=source.vertices.ToList(); var normals=source.normals.ToList();
        var tangent=source.tangents.ToList(); var colors=source.colors.ToList(); var bones=source.boneWeights.ToList();
        var uv=new List<Vector4>[8];
        for(int i=0;i<uv.Length;i++){ uv[i]=new List<Vector4>(); source.GetUVs(i,uv[i]); }
        var edges=new Dictionary<long,int>();
        int Intersection(int a,int b)
        {
            long key=((long)Mathf.Min(a,b)<<32) | (uint)Mathf.Max(a,b);
            if(edges.TryGetValue(key,out var existing)) return existing;
            float t=distance[a]/(distance[a]-distance[b]); int index=verts.Count; edges.Add(key,index);
            verts.Add(Vector3.Lerp(verts[a],verts[b],t));
            if(normals.Count>0) normals.Add(Vector3.Lerp(normals[a],normals[b],t).normalized);
            if(tangent.Count>0) tangent.Add(Vector4.Lerp(tangent[a],tangent[b],t));
            if(colors.Count>0) colors.Add(Color.Lerp(colors[a],colors[b],t));
            foreach(var channel in uv) if(channel.Count>0) channel.Add(Vector4.Lerp(channel[a],channel[b],t));
            bones.Add(Blend(bones[a],bones[b],t)); return index;
        }
        var mesh=new Mesh{indexFormat=IndexFormat.UInt32};
        var sections=new List<int>[source.subMeshCount]; var polygon=new int[4];
        for(int sub=0;sub<sections.Length;sub++)
        {
            var tris=source.GetTriangles(sub); var kept=sections[sub]=new List<int>();
            for(int f=0;f<tris.Length;f+=3)
            {
                int n=0;
                for(int j=0;j<3;j++)
                {
                    int a=tris[f+j],b=tris[f+(j+1)%3];
                    if(distance[a]>=0) polygon[n++]=a;
                    if(distance[a]*distance[b]<0) polygon[n++]=Intersection(a,b);
                }
                for(int j=2;j<n;j++) { kept.Add(polygon[0]); kept.Add(polygon[j-1]); kept.Add(polygon[j]); }
            }
        }
        mesh.SetVertices(verts); if(normals.Count>0)mesh.SetNormals(normals); if(tangent.Count>0)mesh.SetTangents(tangent);
        if(colors.Count>0)mesh.SetColors(colors);
        for(int i=0;i<uv.Length;i++) if(uv[i].Count>0)mesh.SetUVs(i,uv[i]);
        mesh.boneWeights=bones.ToArray(); mesh.bindposes=source.bindposes; mesh.subMeshCount=sections.Length;
        for(int i=0;i<sections.Length;i++)mesh.SetTriangles(sections[i],i,false);
        mesh.bounds=source.bounds; return mesh;
    }
    // A whole source body may contribute just two hands. Do not skin thousands of discarded vertices per player.
    public static Mesh Compact(Mesh source)
    {
        if(source.blendShapeCount!=0) return source;
        var ids=source.triangles.Distinct().OrderBy(index=>index).ToArray();
        if(ids.Length==source.vertexCount) return source;
        var remap=new int[source.vertexCount];
        for(int i=0;i<ids.Length;i++) remap[ids[i]]=i;
        T[] Used<T>(T[] values) => values.Length==0?values:ids.Select(i=>values[i]).ToArray();
        var result=new Mesh{indexFormat=ids.Length>65535?IndexFormat.UInt32:IndexFormat.UInt16};
        result.vertices=Used(source.vertices); result.normals=Used(source.normals); result.tangents=Used(source.tangents);
        result.colors32=Used(source.colors32);
        for(int channel=0;channel<8;channel++)
        {
            var uv=new List<Vector4>(); source.GetUVs(channel,uv);
            if(uv.Count>0) result.SetUVs(channel,ids.Select(i=>uv[i]).ToList());
        }
        result.boneWeights=Used(source.boneWeights); result.bindposes=source.bindposes;
        result.subMeshCount=source.subMeshCount;
        for(int sub=0;sub<source.subMeshCount;sub++) result.SetTriangles(source.GetTriangles(sub).Select(i=>remap[i]).ToArray(),sub,false);
        result.bounds=source.bounds;
        Object.DestroyImmediate(source);
        return result;
    }
    private static BoneWeight Blend(BoneWeight a,BoneWeight b,float t)
    {
        var weights=new Dictionary<int,float>();
        void Add(int bone,float weight) { if(weight>0){weights.TryGetValue(bone,out float old);weights[bone]=old+weight;} }
        void Mix(BoneWeight w,float f)
        { Add(w.boneIndex0,w.weight0*f); Add(w.boneIndex1,w.weight1*f); Add(w.boneIndex2,w.weight2*f); Add(w.boneIndex3,w.weight3*f); }
        Mix(a,1-t);Mix(b,t);
        var ordered=weights.OrderByDescending(v=>v.Value).Take(4).ToArray(); float sum=ordered.Sum(v=>v.Value);
        var result=new BoneWeight();
        if(ordered.Length>0){result.boneIndex0=ordered[0].Key;result.weight0=ordered[0].Value/sum;}
        if(ordered.Length>1){result.boneIndex1=ordered[1].Key;result.weight1=ordered[1].Value/sum;}
        if(ordered.Length>2){result.boneIndex2=ordered[2].Key;result.weight2=ordered[2].Value/sum;}
        if(ordered.Length>3){result.boneIndex3=ordered[3].Key;result.weight3=ordered[3].Value/sum;}
        return result;
    }
}