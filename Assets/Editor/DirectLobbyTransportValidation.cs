using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

// Local editor integration check with a disposable account-service instance.
public static class DirectLobbyTransportValidation
{
    private const string Root = "tmp/LobbyDirect-20261120/";
    public static async void Run()
    {
        try
        {
            var fixture = JObject.Parse(File.ReadAllText(Root + "tls-probe.json"));
            var endpoint = new Uri((string)fixture["url"]);
            if (!endpoint.IsLoopback || endpoint.Scheme != "https") throw new InvalidOperationException("Requires loopback TLS.");
            var gateway = typeof(UgsAccountGateway);
            var grantType = gateway.GetNestedType("Grant", BindingFlags.NonPublic);
            var send = gateway.GetMethod("SendDirectRequest", BindingFlags.Static | BindingFlags.NonPublic);
            var grant = fixture.ToObject(grantType);
            var wrongPin = fixture.DeepClone();
            wrongPin["certificateSha256"] = new string('0', 64);
            using (var request = UnityWebRequest.Get(endpoint + "/ping"))
            {
                request.timeout = 4;
                await (Task)send.Invoke(null, new[] { request, wrongPin.ToObject(grantType) });
                if (request.result == UnityWebRequest.Result.Success) throw new Exception("Mismatched pin was accepted.");
            }
            using (var request = new UnityWebRequest(endpoint + "/command", "POST"))
            {
                request.timeout = 5;
                var command = new JObject { ["operation"] = "current", ["requestId"] = Guid.NewGuid().ToString("N"), ["args"] = new JObject() };
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(command.ToString()));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", "Bearer " + (string)fixture["token"]);
                await (Task)send.Invoke(null, new[] { request, grant });
                if (request.responseCode != 200 || JObject.Parse(request.downloadHandler.text)["party"] == null)
                    throw new Exception("Pinned command failed " + request.responseCode + " " + request.error);
            }
            File.WriteAllText(Root + "tls-result.txt", "PASS Unity rejected the mismatched pin and ran an authenticated HTTPS command with the verified pin.");
        }
        catch (Exception error) { File.WriteAllText(Root + "tls-result.txt", "FAIL " + error.GetBaseException().Message); }
    }
}
