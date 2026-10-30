using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Fetch account data on entering GameLobby; dedicated servers do not create account UI.</summary>
public static class LoginSceneBootstrap
{
    private static string pendingMessage;
    private static bool refreshNextLobbyEntry;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Reset()
    {
        pendingMessage = "";
        refreshNextLobbyEntry = false;
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Initialize()
    {
        if (DedicatedServerBootstrap.IsDedicatedServerProcess) return;
        SceneManager.sceneLoaded += OnSceneLoaded;
        OnSceneLoaded(SceneManager.GetActiveScene(), LoadSceneMode.Single);
    }
    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.name != GameFlowScenes.MainMenu) return;
        var existing = FindLoader(scene);
        if (existing != null) { existing.ReuseAuthenticatedProfile = !refreshNextLobbyEntry; refreshNextLobbyEntry = false; return; }
        var root = new GameObject("LobbyProfileLoader");
        SceneManager.MoveGameObjectToScene(root, scene);
        root.AddComponent<GameStartProfileLoader>().ReuseAuthenticatedProfile = !refreshNextLobbyEntry;
        refreshNextLobbyEntry = false;
    }
    public static void PrepareLobbyEntry() => refreshNextLobbyEntry = true;
    public static GameStartProfileLoader FindLoader(Scene scene)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            var loader = root.GetComponentInChildren<GameStartProfileLoader>(true);
            if (loader != null) return loader;
        }
        return null;
    }
    public static void ReturnToLogin(string message = "")
    {
        if (!GameFlowScenes.TryValidateSceneAvailable(GameFlowScenes.Login, out string error))
        { Debug.LogError("[Login] " + error); return; }
        pendingMessage = message;
        Time.timeScale = 1f;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        SceneManager.LoadSceneAsync(GameFlowScenes.Login, LoadSceneMode.Single);
    }
    internal static string TakeMessage()
    {
        string message = pendingMessage ?? "";
        pendingMessage = "";
        return message;
    }
}
