using UnityEngine;

namespace FPS.UI
{
    public sealed class WardrobeHandPose : MonoBehaviour
    {
        private Animator animator;
        private Transform leftHandle;
        public void Bind(Animator owner, Transform supportHandle)
        {
            animator = owner;
            leftHandle = supportHandle;
        }
        private void OnAnimatorIK(int layer)
        {
            if (layer != 0 || !leftHandle) return;
            if (!leftHandle.gameObject.activeInHierarchy)
            {
                Relax(AvatarIKGoal.LeftHand,AvatarIKHint.LeftElbow,-1);
                Relax(AvatarIKGoal.RightHand,AvatarIKHint.RightElbow,1);
                return;
            }
            animator.SetIKHintPositionWeight(AvatarIKHint.LeftElbow, 0);
            animator.SetIKHintPositionWeight(AvatarIKHint.RightElbow, 0);
            animator.SetIKPositionWeight(AvatarIKGoal.RightHand, 0);
            animator.SetIKRotationWeight(AvatarIKGoal.RightHand, 0);
            animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, 1);
            animator.SetIKRotationWeight(AvatarIKGoal.LeftHand, 1);
            animator.SetIKPosition(AvatarIKGoal.LeftHand, leftHandle.position);
            animator.SetIKRotation(AvatarIKGoal.LeftHand, leftHandle.rotation);
        }
        private void Relax(AvatarIKGoal goal,AvatarIKHint elbow,int side)
        {
            animator.SetIKPositionWeight(goal,1); animator.SetIKRotationWeight(goal,0);
            animator.SetIKPosition(goal,transform.TransformPoint(new Vector3(side*.26f,.82f,.045f)));
            animator.SetIKHintPositionWeight(elbow,.85f);
            animator.SetIKHintPosition(elbow,transform.TransformPoint(new Vector3(side*.35f,1.09f,-.09f)));
        }
    }
}
