using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace FPS.UI
{
    [Serializable] public sealed class LobbyButtonBinding
    {
        public UnityEngine.UI.Button button;
        public LobbyAction action;
    }

    /// <summary>Prefab-local display and intent binding. Never calls account, network or other panels.</summary>
    [RequireComponent(typeof(CanvasGroup))]
    public class LobbyPageView : MonoBehaviour
    {
        public LobbyPage page;
        public LobbyButtonBinding[] buttons = new LobbyButtonBinding[0];
        public TMP_Text playerName, status, uid;
        public PlayerAvatarView avatar;
        private CanvasGroup group;
        private UnityAction[] listeners;
        public event Action<LobbyAction> ActionRequested;

        protected virtual void Awake()
        {
            group = GetComponent<CanvasGroup>();
            listeners = new UnityAction[buttons.Length];
            for (int i = 0; i < buttons.Length; i++)
            {
                var action = buttons[i].action;
                listeners[i] = () => ActionRequested?.Invoke(action);
                if (buttons[i].button != null) buttons[i].button.onClick.AddListener(listeners[i]);
            }
        }
        protected virtual void OnDestroy()
        {
            if (listeners == null) return;
            for (int i = 0; i < listeners.Length; i++)
                if (buttons[i].button != null) buttons[i].button.onClick.RemoveListener(listeners[i]);
        }
        public virtual void Show(bool visible)
        {
            if (gameObject.activeSelf == visible) return;
            gameObject.SetActive(visible);
            if (!visible) return;
            var animator = GetComponent<Animator>();
            if (animator != null && animator.runtimeAnimatorController != null && animator.HasState(0, Animator.StringToHash("FadeIn")))
            {
                animator.updateMode = AnimatorUpdateMode.UnscaledTime;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.Play("FadeIn", 0, 0f);
            }
        }
        public void SetInputEnabled(bool enabled)
        {
            if (group == null) group = GetComponent<CanvasGroup>();
            group.interactable = enabled; group.blocksRaycasts = true;
        }
        public virtual void Render(PlayerProfileData profile, PlayerAvatarCatalog catalog, string message, bool busy)
        {
            if (playerName != null) playerName.text = profile != null ? profile.DisplayName : "";
            if (uid != null) uid.text = profile != null ? "UID: " + AccountUid.Format(profile.uid, profile.accountId) : "";
            if (status != null) { status.text = message; status.gameObject.SetActive(!string.IsNullOrEmpty(message)); }
            if (avatar != null) avatar.Show(profile != null ? profile.avatarId : (byte)255, catalog);
            SetInputEnabled(!busy);
        }
    }
}
