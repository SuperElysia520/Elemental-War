using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;

public sealed partial class AccountClient
{
    public IEnumerator SocialRequest(Dictionary<string, object> args, Action<string, string> completed)
    {
        if (!IsAuthenticated)
        {
            completed(null, "请先登录账号。");
            yield break;
        }
        if (!UsesUgs)
        {
            string token = AccessToken;
            yield return ClientRequest("POST", "/api/social", JsonConvert.SerializeObject(args), token, (body, status, error) =>
            {
                if (token != AccessToken) return;
                if (status == 401) ClearSession();
                completed(body, error);
            });
            yield break;
        }
        Task<UgsPvpReply> task = CallUgsPvp("social", args);
        while (!task.IsCompleted) yield return null;
        UgsPvpReply reply = null;
        string failure;
        try
        {
            reply = task.GetAwaiter().GetResult();
            failure = PvpBusinessError("social", reply);
        }
        catch (Exception error) { failure = HandleUgsPvpFailure("social", error); }
        completed(reply?.body, failure);
    }
}
