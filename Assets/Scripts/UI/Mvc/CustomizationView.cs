using System;
using UnityEngine;
using UnityEngine.Events;

namespace FPS.UI
{
    public sealed class CustomizationView : LobbyPageView
    {
        public UnityEngine.UI.Text accountLabel, message, saveLabel;
        public UnityEngine.UI.Button saveButton, reloadButton;
        public UnityEngine.UI.Text[] values = new UnityEngine.UI.Text[0];
        public UnityEngine.UI.Button[] previous = new UnityEngine.UI.Button[0], next = new UnityEngine.UI.Button[0];
        public CharacterPreviewView preview;
        private UnityAction[] previousActions, nextActions;
        public event Action<int, int> ChoiceRequested;
        protected override void Awake()
        {
            base.Awake();
            previousActions = new UnityAction[previous.Length]; nextActions = new UnityAction[next.Length];
            for (int i = 0; i < previous.Length; i++)
            {
                int choice = i; previousActions[i] = () => ChoiceRequested?.Invoke(choice, -1);
                previous[i].onClick.AddListener(previousActions[i]);
            }
            for (int i = 0; i < next.Length; i++)
            {
                int choice = i; nextActions[i] = () => ChoiceRequested?.Invoke(choice, 1);
                next[i].onClick.AddListener(nextActions[i]);
            }
        }
        protected override void OnDestroy()
        {
            if (previousActions != null)
                for (int i = 0; i < previous.Length; i++) previous[i].onClick.RemoveListener(previousActions[i]);
            if (nextActions != null)
                for (int i = 0; i < next.Length; i++) next[i].onClick.RemoveListener(nextActions[i]);
            base.OnDestroy();
        }
        public void RenderDraft(CustomizationModel draft, PlayerProfileData profile, string text, bool canEdit, bool busy)
        {
            if (accountLabel != null) accountLabel.text = profile != null ? "已登录：" + profile.DisplayName + "\nUID: " + AccountUid.Format(profile.uid, profile.accountId) : "请先登录账号。";
            if (message != null) message.text = text;
            if (saveLabel != null) saveLabel.text = busy ? "正在同步…" : draft.IsDirty ? "保存更改到账号" : "保存到账号";
            saveButton.interactable = reloadButton.interactable = canEdit && !busy;
            for (int i = 0; i < values.Length; i++) values[i].text = draft.Value(i);
            foreach (var button in previous) button.interactable = canEdit && !busy;
            foreach (var button in next) button.interactable = canEdit && !busy;
            if (gameObject.activeInHierarchy && preview != null) preview.Apply(draft.Appearance);
        }
    }
}
