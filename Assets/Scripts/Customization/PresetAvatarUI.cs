using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>Connect manually authored preset buttons, ordered like the catalog. This does not generate UI.</summary>
public sealed class PresetAvatarUI : MonoBehaviour
{
    public PlayerAvatarCatalog catalog;
    public Button[] presetButtons = new Button[0];
    public TMP_Text status;
    private UnityEngine.Events.UnityAction[] actions;
    private void Awake()
    {
        actions = new UnityEngine.Events.UnityAction[presetButtons.Length];
        for (int i = 0; i < presetButtons.Length; i++)
        {
            int index = i;
            actions[i] = () => AccountClient.EnsureInstance().SaveAvatar((byte)index, (success, message) =>
            { if (this != null && status != null) status.text = success ? "头像已保存。" : message; });
            if (presetButtons[i] != null) presetButtons[i].onClick.AddListener(actions[i]);
        }
    }
    private void OnEnable() { AccountClient.EnsureInstance().StateChanged += Refresh; PartyLobbyClient.EnsureInstance().Changed += Refresh; Refresh(); }
    private void OnDisable()
    {
        if (AccountClient.Instance != null) AccountClient.Instance.StateChanged -= Refresh;
        if (PartyLobbyClient.Instance != null) PartyLobbyClient.Instance.Changed -= Refresh;
    }
    private void OnDestroy()
    {
        if (actions == null) return;
        for (int i = 0; i < presetButtons.Length; i++) if (presetButtons[i] != null) presetButtons[i].onClick.RemoveListener(actions[i]);
    }
    private void Refresh()
    {
        AccountClient account = AccountClient.EnsureInstance();
        bool waiting = PartyLobbyClient.Instance == null || PartyLobbyClient.Instance.Party == null || PartyLobbyClient.Instance.Party.state == "Waiting";
        for (int i = 0; i < presetButtons.Length; i++)
            if (presetButtons[i] != null) presetButtons[i].interactable = waiting && account.IsAuthenticated && !account.IsBusy && i < 32
                && catalog != null && catalog.Get((byte)i) != null;
    }
}
