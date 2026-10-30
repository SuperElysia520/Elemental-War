using System;
using System.Collections;
using UnityEngine;

/// <summary>Fetch the logged-in account's data in GameLobby. The class name preserves existing serialized references.</summary>
[DisallowMultipleComponent]
public sealed class GameStartProfileLoader : MonoBehaviour
{
    public AccountProfileEvent onUserDataLoaded = new AccountProfileEvent();
    public AccountStringEvent onMessageChanged = new AccountStringEvent();
    public AccountBoolEvent onLoadingChanged = new AccountBoolEvent();
    public bool IsReady { get; private set; }
    public bool IsLoading { get; private set; }
    public event Action Ready;
    [NonSerialized] public bool ReuseAuthenticatedProfile = true;

    private void Start()
    {
        if (DedicatedServerBootstrap.IsDedicatedServerProcess) { IsReady = true; Ready?.Invoke(); return; }
        BeginLoad(ReuseAuthenticatedProfile);
    }
    public void LoadUserData()
    {
        BeginLoad(false);
    }
    private void BeginLoad(bool reuseAuthenticatedProfile)
    {
        if (IsLoading || DedicatedServerBootstrap.IsDedicatedServerProcess) return;
        IsLoading = true;
        IsReady = false;
        onLoadingChanged.Invoke(true);
        onMessageChanged.Invoke("正在读取用户数据…");
        StartCoroutine(Load(reuseAuthenticatedProfile));
    }
    private IEnumerator Load(bool reuseAuthenticatedProfile)
    {
        AccountClient account = AccountClient.EnsureInstance();
        while (account != null && account.IsBusy) yield return null;
        if (account == null) { Failed("账号会话已关闭，请重新登录。"); yield break; }
        if (!account.HasSession) { Failed("请先登录账号。"); yield break; }
        // Returning from a match retains the already authenticated profile and network membership.
        if (reuseAuthenticatedProfile && account.IsAuthenticated)
        {
            IsLoading = false; IsReady = true;
            onLoadingChanged.Invoke(false); onMessageChanged.Invoke("");
            onUserDataLoaded.Invoke(account.Profile); Ready?.Invoke(); yield break;
        }
        account.RefreshProfile((ok, error) =>
        {
            if (this == null) return;
            if (!ok) { Failed(error); return; }
            IsLoading = false;
            IsReady = true;
            onLoadingChanged.Invoke(false);
            onMessageChanged.Invoke("");
            onUserDataLoaded.Invoke(account.Profile);
            Ready?.Invoke();
        });
    }
    private void Failed(string error)
    {
        IsLoading = false;
        onLoadingChanged.Invoke(false);
        onMessageChanged.Invoke(error);
        // Network failures keep the in-memory session for retry; HTTP 401 already clears it.
        LoginSceneBootstrap.ReturnToLogin(error);
    }
    public void ReturnToLogin() => LoginSceneBootstrap.ReturnToLogin();
}
