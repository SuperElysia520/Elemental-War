using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Fps.AccountService;

/// <summary>Probes the existing discovery ports on the same host before allocating an actual gameplay process.</summary>
public sealed class PartyMatchmaker(PartyStore parties, AccountStore accounts, ILogger<PartyMatchmaker> logger,
    UgsBridgeOptions options, IServiceProvider services) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var probes = Enumerable.Range(parties.StartPort, parties.ServerCount).Select(p => Probe(p, stoppingToken));
                int[] idle = (await Task.WhenAll(probes)).Where(p => p != 0).ToArray();
                Func<string, Profile> readProfile = options.Enabled ? services.GetRequiredService<UgsProfiles>().GetCached : accounts.GetStoredProfile;
                parties.RefreshProfiles(readProfile, skipUnavailable: options.Enabled);
                parties.MatchQueued(idle);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning("Matchmaking probe failed: {Type}", error.GetType().Name); }
        }
    }

    private static async Task<int> Probe(int port, CancellationToken shutdown)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Connect(IPAddress.Loopback, port + 41000);
            await udp.SendAsync(Encoding.UTF8.GetBytes("FPS-PVP-DISC-QUERY"), shutdown);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            timeout.CancelAfter(600);
            UdpReceiveResult response = await udp.ReceiveAsync(timeout.Token);
            string text = Encoding.UTF8.GetString(response.Buffer);
            const string prefix = "FPS-PVP-DISC-RESPONSE|";
            if (!text.StartsWith(prefix, StringComparison.Ordinal)) return 0;
            using JsonDocument document = JsonDocument.Parse(text[prefix.Length..]);
            JsonElement root = document.RootElement;
            return root.GetProperty("gamePort").GetInt32() == port && root.GetProperty("canCreate").GetBoolean()
                && !root.GetProperty("hasRoom").GetBoolean() && root.GetProperty("accountProtocolVersion").GetInt32() == 5 ? port : 0;
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException or JsonException or KeyNotFoundException)
        { return 0; }
    }
}
