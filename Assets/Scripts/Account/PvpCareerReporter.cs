using System;
using System.Collections;
using System.IO;
using UnityEngine;

[Serializable]
public sealed class CareerPlayerResultData
{
    public string accountId;
    public byte team;
    public int kills, deaths, headshotKills;
}

[Serializable]
public sealed class CareerMatchResultData
{
    public string matchId;
    public int serverPort;
    public byte winnerTeam;
    public CareerPlayerResultData[] players;
}

/// <summary>Dedicated-server-only durable outbox. Clients never submit career counters.</summary>
public sealed class PvpCareerReporter : MonoBehaviour
{
    private string directory;
    public void Initialize(int port)
    {
        if (directory != null || !DedicatedServerBootstrap.IsDedicatedServerProcess || AccountServiceSettings.SelectedBackend != AccountBackend.UnityGamingServices) return;
        directory = Path.Combine(Application.persistentDataPath, "CareerOutbox_" + port);
        Directory.CreateDirectory(directory);
        StartCoroutine(SendPending());
    }
    public void Queue(CareerMatchResultData result)
    {
        if (directory == null) Initialize(result.serverPort);
        if (directory == null) return;
        string path = Path.Combine(directory, result.matchId + ".json");
        if (File.Exists(path)) return;
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(result));
        using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        File.Move(path + ".tmp", path);
    }
    private IEnumerator SendPending()
    {
        while (true)
        {
            foreach (string path in Directory.GetFiles(directory, "*.json"))
            {
                CareerMatchResultData result = null;
                try { result = JsonUtility.FromJson<CareerMatchResultData>(File.ReadAllText(path)); }
                catch (Exception error) { Debug.LogWarning("[PVP Career] 无法读取待同步结算：" + error.GetType().Name); }
                if (result == null) continue;
                string failure = "";
                yield return AccountClient.SendCareerResult(result, error => failure = error);
                if (string.IsNullOrEmpty(failure)) File.Delete(path);
            }
            yield return new WaitForSecondsRealtime(3f);
        }
    }
}
