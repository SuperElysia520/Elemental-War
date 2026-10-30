using System;
using TMPro;
using UnityEngine;

namespace FPS.UI
{
    public sealed class LobbyDialogView : MonoBehaviour
    {
        public UnityEngine.UI.Button confirmButton, cancelButton;
        public UnityEngine.UI.Text message;
        public TMP_Text tmpMessage;
        public event Action Confirmed, Cancelled;
        private void Awake()
        {
            if (confirmButton != null) confirmButton.onClick.AddListener(Confirm);
            if (cancelButton != null) cancelButton.onClick.AddListener(Cancel);
        }
        private void OnDestroy()
        {
            if (confirmButton != null) confirmButton.onClick.RemoveListener(Confirm);
            if (cancelButton != null) cancelButton.onClick.RemoveListener(Cancel);
        }
        private void Confirm() => Confirmed?.Invoke();
        private void Cancel() => Cancelled?.Invoke();
        public void Show(string text)
        {
            gameObject.SetActive(true);
            if (message != null) message.text = text;
            if (tmpMessage != null) tmpMessage.text = text;
        }
        public void Hide() => gameObject.SetActive(false);
    }
}
