using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.EventSystems;

namespace FPS.UI
{
    public sealed class WardrobePreview : MonoBehaviour, IDragHandler
    {
        public UnityEngine.UI.RawImage image;
        public RuntimeAnimatorController idle;
        public AnimationClip rifleIdle;
        public GameObject riflePrefab;
        public float eyeHeight, fieldOfView;
        private GameObject stage;
        private RifleGirlAppearance avatar;
        private GameObject rifle;
        private AnimatorOverrideController holding;
        private Camera lens;
        private RenderTexture target;
        private CharacterAppearanceData applied;
        private bool appliedOnce;
        private float yaw, pitch;
        private WardrobeGeometry geometry;
        private readonly Dictionary<SkinnedMeshRenderer,Mesh> fittedMeshes=new Dictionary<SkinnedMeshRenderer,Mesh>();
        private readonly List<SkinnedMeshRenderer> hidden=new List<SkinnedMeshRenderer>();
        public bool FirstPerson { get; private set; }
        public RifleGirlAppearance Avatar => avatar;
        public void Bind(GameObject station)
        {
            if (stage != null) return;
            stage = new GameObject("Wardrobe fitting stage");
            stage.transform.SetParent(station.transform,false);
            geometry=Resources.Load<WardrobeGeometry>("Customization/WardrobeGeometry");
            var rig=Instantiate(Resources.Load<GameObject>(RifleGirlAppearance.ResourcePath),stage.transform,false);
            avatar=rig.GetComponent<RifleGirlAppearance>();
            var anim = avatar.CharacterAnimator;
            anim.applyRootMotion = false;
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            holding = new AnimatorOverrideController(idle);
            holding[idle.animationClips[0].name] = rifleIdle;
            var socket = anim.GetBoneTransform(HumanBodyBones.RightHand).Find("Hand_R_Socket");
            rifle = Instantiate(riflePrefab, socket, false);
            // The original rifle's first constraint source is this socket. Direct parenting also
            // makes its support handle follow the animated wrist during the humanoid IK pass.
            var mount = rifle.GetComponent<ParentConstraint>();
            mount.enabled = false;
            rifle.transform.SetLocalPositionAndRotation(mount.GetTranslationOffset(0), Quaternion.Euler(mount.GetRotationOffset(0)));
            rig.AddComponent<WardrobeHandPose>().Bind(anim, rifle.transform.Find("Left_Handle"));
            foreach(Transform t in stage.GetComponentsInChildren<Transform>(true)) t.gameObject.layer=30;
            lens=new GameObject("Fitting camera",typeof(Camera)).GetComponent<Camera>();
            lens.transform.SetParent(stage.transform,false); lens.cullingMask=1<<30;
            lens.clearFlags=CameraClearFlags.Skybox; lens.farClipPlane=120; lens.nearClipPlane=.035f;
            Resize(); SetMode(false);
        }
        public void Apply(CharacterAppearanceData data)
        {
            if (avatar != null && (!appliedOnce || !data.Equals(applied)))
            {
                ClearArmPreview(); avatar.Apply(data);
                if (FirstPerson) FitArms();
                applied=data; appliedOnce=true;
            }
        }
        public void SetMode(bool first)
        {
            FirstPerson=first; yaw=pitch=0;
            if (!avatar) return;
            ClearArmPreview();
            rifle.SetActive(first);
            avatar.SetLocalFirstPerson(first);
            if(first) FitArms();
            Frame();
            var anim = avatar.CharacterAnimator;
            anim.runtimeAnimatorController = first ? holding : idle;
            anim.Update(0);
        }
        // Only the preview camera looks across the avatar's collar when the mouse pans.
        // Keep the skinned garment sleeves and hands from this exact fitting, including legacy outfits.
        private void FitArms()
        {
            foreach(var skin in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if(skin.name=="WardrobeGloves") continue;
                Mesh mesh=skin.sharedMesh;
                int i=System.Array.FindIndex(geometry.armMeshes,m=>m.source==mesh);
                if(i<0 && skin.name.StartsWith("Body_"))
                    i=System.Array.FindIndex(geometry.armMeshes,m=>m.renderer==skin.name);
                if(i>=0) { fittedMeshes.Add(skin,mesh); skin.sharedMesh=geometry.armMeshes[i].arms; }
                else if(!skin.forceRenderingOff){skin.forceRenderingOff=true; hidden.Add(skin);}
            }
        }
        private void ClearArmPreview()
        {
            foreach(var entry in fittedMeshes) if(entry.Key) entry.Key.sharedMesh=entry.Value;
            foreach(var skin in hidden) if(skin) skin.forceRenderingOff=false;
            fittedMeshes.Clear(); hidden.Clear();
        }
        private void Frame()
        {
            avatar.transform.localRotation=Quaternion.Euler(0,FirstPerson?0:180+yaw,0);
            lens.fieldOfView=FirstPerson?2f*Mathf.Atan(Mathf.Tan(fieldOfView*Mathf.Deg2Rad*.5f)*1.5f)*Mathf.Rad2Deg:32;
            if (FirstPerson)
            {
                lens.transform.localPosition=Vector3.up*eyeHeight+new Vector3(.008f,-.10f,.01f);
                lens.transform.localRotation=Quaternion.Euler(23+pitch,6+yaw,0);
            }
            else
            {
                float height=eyeHeight*1.13f;
                float distance=height*.77f/Mathf.Tan(16*Mathf.Deg2Rad);
                lens.transform.localPosition=new Vector3(0,height*.54f,-distance);
                lens.transform.localRotation=Quaternion.identity;
            }
        }
        public void OnDrag(PointerEventData e)
        {
            if(!lens) return;
            float unit=180f/Mathf.Max(1,image.rectTransform.rect.width*image.canvas.scaleFactor);
            yaw=FirstPerson?Mathf.Clamp(yaw+e.delta.x*unit,-24,24):yaw-e.delta.x*unit;
            if(FirstPerson) pitch=Mathf.Clamp(pitch-e.delta.y*unit,-15,15);
            Frame();
        }
        private void Resize()
        {
            if(!lens) return;
            Rect rect=image.rectTransform.rect; float aspect=rect.width/Mathf.Max(1,rect.height);
            if(aspect<.1f) aspect=.9f;
            int h=Mathf.Clamp(Mathf.RoundToInt(rect.height*image.canvas.scaleFactor),320,1200);
            int w=Mathf.Clamp(Mathf.RoundToInt(h*aspect),192,2048);
            if(target && target.width==w && target.height==h) return;
            ReleaseTarget();
            target=new RenderTexture(w,h,24){antiAliasing=2,name="Wardrobe fitting preview"};
            target.Create(); lens.targetTexture=target; image.texture=target;
        }
        private void OnRectTransformDimensionsChange() { if(isActiveAndEnabled) Resize(); }
        private void ReleaseTarget()
        {
            if(!target) return;
            if(lens) lens.targetTexture=null;
            image.texture=null; target.Release(); Destroy(target); target=null;
        }
        private void OnDisable()
        {
            ClearArmPreview();
            if(stage) { stage.SetActive(false); Destroy(stage); }
            if (holding) Destroy(holding);
            holding=null; rifle=null;
            stage=null; avatar=null; appliedOnce=false; ReleaseTarget(); lens=null; FirstPerson=false;
        }
    }
}
