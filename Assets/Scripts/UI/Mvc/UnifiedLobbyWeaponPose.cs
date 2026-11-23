using UnityEngine;

namespace FPS.UI
{
    /// <summary>Presentation-only weapon and shared-skeleton hand IK; never fires or handles input.</summary>
    [ExecuteAlways]
    public sealed class UnifiedLobbyWeaponPose : MonoBehaviour
    {
        public Animator animator;
        public PvpWeaponModel weapon;
        public GameObject riflePrefab, sniperPrefab;
        public byte displayedWeapon;
        public Vector3 rightRotation = new Vector3(275.18954f,253.31927f,175.65051f);
        public Vector3 leftRotation = new Vector3(274.75546f,3.406072f,284.76218f);
        private AccountClient account;
        private void OnEnable()
        {
            if(!Application.isPlaying)return;
            account=AccountClient.EnsureInstance();account.ProfileChanged+=Apply;Apply(account.Profile);
        }
        private void OnDisable(){if(account!=null)account.ProfileChanged-=Apply;account=null;}
        private void Apply(PlayerProfileData profile)
        {
            byte id=profile!=null&&profile.loadout.primaryWeaponId==2?(byte)2:(byte)0;
            if(weapon!=null&&id==displayedWeapon){weapon.SelectSight(profile!=null?profile.loadout.primarySightId:(byte)0);return;}
            var prefab=id==2?sniperPrefab:riflePrefab;if(prefab==null)return;
            if(weapon!=null)Destroy(weapon.gameObject);
            var go=Instantiate(prefab,transform,false);go.name="LobbyWeapon";SetLayer(go.transform,gameObject.layer);
            weapon=go.GetComponent<PvpWeaponModel>();displayedWeapon=id;weapon.SelectSight(profile!=null?profile.loadout.primarySightId:(byte)0);
        }
        private void OnAnimatorIK(int layer)
        {
            if(layer!=0||animator==null||!animator.isHuman||weapon==null||weapon.rightGrip==null||weapon.leftGrip==null)return;
            weapon.transform.rotation=transform.rotation*Quaternion.Euler(25,-65,0);
            weapon.transform.position+=transform.TransformPoint(new Vector3(.20f,1.15f,.20f))-weapon.rightGrip.position;
            animator.SetIKPositionWeight(AvatarIKGoal.RightHand,1);animator.SetIKRotationWeight(AvatarIKGoal.RightHand,1);
            animator.SetIKPosition(AvatarIKGoal.RightHand,weapon.rightGrip.position);animator.SetIKRotation(AvatarIKGoal.RightHand,weapon.rightGrip.rotation*Quaternion.Euler(rightRotation));
            animator.SetIKPositionWeight(AvatarIKGoal.LeftHand,1);animator.SetIKRotationWeight(AvatarIKGoal.LeftHand,1);
            animator.SetIKPosition(AvatarIKGoal.LeftHand,weapon.leftGrip.position);animator.SetIKRotation(AvatarIKGoal.LeftHand,weapon.leftGrip.rotation*Quaternion.Euler(leftRotation));
        }
        private static void SetLayer(Transform item,int layer){item.gameObject.layer=layer;foreach(Transform child in item)SetLayer(child,layer);}
    }
}
