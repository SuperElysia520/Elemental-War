#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.IO;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Opt-in test only: two actual player processes drive the authored UI and enter a matched NGO game.</summary>
public sealed class LobbyMatchmakingSmokeProbe : MonoBehaviour
{
    [Serializable] private class Report
    {
        public string status, error, role, matchId, stage, partyStatus;
        public byte team, avatarId;
        public int memberCount;
        public bool returned;
    }
    private string role, output;
    private float deadline;
    private Report report;
    private bool finished;
    private float nextProgress;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Initialize()
    {
        if (Arg("-partySmoke", "") == "") return;
        var root = new GameObject("LobbyMatchmakingSmokeProbe");
        DontDestroyOnLoad(root);
        root.AddComponent<LobbyMatchmakingSmokeProbe>();
    }
    private void Start()
    {
        role = Arg("-partySmoke", ""); output = Arg("-partySmokeOutput", "");
        deadline = Time.unscaledTime + 180;
        report = new Report { role = role, status = "RUNNING" };
        Application.logMessageReceived += Log;
        StartCoroutine(Run());
    }
    private void Update()
    {
        if (finished) return;
        if (Time.unscaledTime >= nextProgress)
        {
            nextProgress = Time.unscaledTime + 5f;
            report.partyStatus = PartyLobbyClient.Instance != null ? PartyLobbyClient.Instance.Status : "No party client";
            Progress();
        }
        if (Time.unscaledTime > deadline) Complete("Timed out at " + report.stage + ": " + report.partyStatus);
    }
    private void OnDestroy() { Application.logMessageReceived -= Log; }
    private void Log(string message, string stack, LogType type)
    { if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) Complete(message); }
    private IEnumerator Run()
    {
        AccountClient account = AccountClient.EnsureInstance();
        report.stage = "login"; Progress();
        bool done = false, success = false;
        account.Login(Environment.GetEnvironmentVariable("FPS_PROBE_USERNAME"), Environment.GetEnvironmentVariable("FPS_PROBE_PASSWORD"),
            (ok, error) => { done = true; success = ok; if (!ok) Complete(error); });
        while (!done) yield return null;
        if (!success || finished) yield break;
        report.stage = "avatar"; Progress();
        done = false;
        account.SaveAvatar(role == "A" ? (byte)1 : (byte)2, (ok, error) => { done = true; success = ok; if (!ok) Complete(error); });
        while (!done) yield return null;
        if (!success || finished) yield break;
        LobbyManager lobby = LobbyManager.Instance;
        if (lobby == null) { Complete("No authored GameStart network root"); yield break; }
        lobby.dedicatedServerAddress = "127.0.0.1";
        lobby.dedicatedServerPort = int.Parse(Arg("-partySmokePort", "17777"));
        yield return SceneManager.LoadSceneAsync(GameFlowScenes.MultiplayerLobby);
        yield return null;
        report.stage = "create-party"; Progress();
        MultiplayerLobbyUI home = FindObjectOfType<MultiplayerLobbyUI>(true);
        if (home == null || home.partyUI == null) { Complete("Authored party UI is not bound"); yield break; }
        home.btnStartGame.onClick.Invoke();
        PartyLobbyClient party = PartyLobbyClient.EnsureInstance();
        while (party.Party == null && !finished) yield return null;
        while (party.IsBusy && !finished) yield return null;
        if (finished) yield break;
        report.stage = "queue"; Progress();
        home.partyUI.primaryButton.onClick.Invoke();
        while (party.IsBusy && !finished) yield return null;
        if (party.Party == null || (party.Party.state != "Matching" && party.Party.state != "Matched"))
        { Complete("Queue failed: " + party.Status); yield break; }
        report.stage = "wait-match"; Progress();
        while (SceneManager.GetActiveScene().name != GameFlowScenes.PvpMatch && !finished) yield return null;
        while (!finished && (NetworkManager.Singleton.LocalClient == null || NetworkManager.Singleton.LocalClient.PlayerObject == null)) yield return null;
        if (finished) yield break;
        report.stage = "in-game"; Progress();
        LobbyState state = lobby.CurrentLobby;
        if (state == null || !state.IsMatchmaking || state.PlayerCount != 2)
        { Complete("Matched NGO roster is not the expected 1v1 game"); yield break; }
        report.team = (byte)state.GetPlayerTeam(NetworkManager.Singleton.LocalClientId);
        report.matchId = state.MatchedSessionId.Value.ToString();
        report.avatarId = account.Profile.avatarId; report.memberCount = party.Party.members.Length;
        if (report.team == 0 || report.memberCount != 1) { Complete("Team assignment or original party was lost"); yield break; }
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, role + "-ready.json"), JsonUtility.ToJson(report, true));
        string other = role == "A" ? "B" : "A";
        while (!File.Exists(Path.Combine(output, other + "-ready.json")) && !finished) yield return null;
        if (finished) yield break;
        Report opponent = JsonUtility.FromJson<Report>(File.ReadAllText(Path.Combine(output, other + "-ready.json")));
        if (opponent.matchId != report.matchId || opponent.team == report.team) { Complete("Clients were not assigned opposite sides in the same match"); yield break; }
        done = false;
        party.Leave(ok => { success = ok; done = true; });
        while (!done && !finished) yield return null;
        if (!success || finished) { Complete(party.Status); yield break; }
        lobby.LeaveRoom();
        while (SceneManager.GetActiveScene().name != GameFlowScenes.MultiplayerLobby && !finished) yield return null;
        report.returned = party.Party == null && !lobby.IsInLobby;
        Complete(report.returned ? null : "Leave did not return to the lobby");
    }
    private void Complete(string error)
    {
        if (finished) return;
        finished = true; report.status = error == null ? "PASS" : "FAIL"; report.error = error ?? "";
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, role + "-report.json"), JsonUtility.ToJson(report, true));
        Application.Quit(error == null ? 0 : 1);
    }
    private void Progress()
    {
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, role + "-progress.json"), JsonUtility.ToJson(report, true));
    }
    private static string Arg(string key, string fallback)
    { string[] args = Environment.GetCommandLineArgs(); for (int i = 0; i + 1 < args.Length; i++) if (args[i] == key) return args[i + 1]; return fallback; }
}
#endif
