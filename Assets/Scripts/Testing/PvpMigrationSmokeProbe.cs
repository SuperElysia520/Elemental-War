#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Opt-in two-process acceptance test. Uses real account authentication and normal client gameplay requests.
/// Not compiled into a release player and never activates without -pvpMigrationProbe host|join.
/// </summary>
public sealed class PvpMigrationSmokeProbe : MonoBehaviour
{
    [Serializable] private sealed class Step { public string name; public string result; public string detail; public float seconds; }
    [Serializable] private sealed class Report
    {
        public string runId, role, status = "RUNNING", stage = "starting";
        public int phase;
        public float elapsed;
        public string scene, error;
        public ulong clientId;
        public CharacterAppearanceData appearance;
        public PvpLoadoutData loadout;
        public List<Step> steps = new List<Step>();
        public List<string> runtimeErrors = new List<string>();
    }

    private Report report;
    private string outputDirectory, role, runId, passwordForRedaction;
    private float started, totalTimeout = 240;
    private bool finished;
    private PvpCharacterProfile localProfile, remoteProfile;
    private PvpFullBodyController localBody;
    private AccountClient account;
    private LobbyManager lobby;
    private CharacterAppearanceData expectedAppearance, remoteAppearance;
    private PvpLoadoutData expectedLoadout, remoteLoadout;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
#if UNITY_SERVER
        return;
#else
        if (string.IsNullOrEmpty(Argument("-pvpMigrationProbe"))) return;
        var go = new GameObject("PvpMigrationSmokeProbe");
        DontDestroyOnLoad(go);
        go.AddComponent<PvpMigrationSmokeProbe>();
#endif
    }

    private void Start()
    {
        started = Time.realtimeSinceStartup;
        role = Argument("-pvpMigrationProbe");
        runId = Argument("-pvpProbeRunId");
        outputDirectory = Argument("-pvpProbeOutput");
        report = new Report { role = role, runId = runId };
        if (float.TryParse(Argument("-pvpProbeTimeout"), out float configured)) totalTimeout = Mathf.Clamp(configured, 60, 600);
        if ((role != "host" && role != "join") || string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(outputDirectory))
        { Fail("configuration", "Require host|join, -pvpProbeRunId and an absolute -pvpProbeOutput directory."); return; }
        if (!Path.IsPathRooted(outputDirectory)) { Fail("configuration", "Probe output directory must be absolute."); return; }
        Directory.CreateDirectory(outputDirectory);
        Application.runInBackground = true;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 60;
        Application.logMessageReceived += RecordRuntimeError;
        WriteReport();
        StartCoroutine(Guarded(Run()));
    }

    private void Update()
    {
        if (!finished && report != null && Time.realtimeSinceStartup - started > totalTimeout)
            Fail("timeout", "Overall test timed out at " + report.stage);
    }

    private IEnumerator Guarded(IEnumerator routine)
    {
        var stack = new Stack<IEnumerator>();
        stack.Push(routine);
        while (stack.Count > 0 && !finished)
        {
            bool moved = false;
            object current = null;
            Exception failure = null;
            try { moved = stack.Peek().MoveNext(); if (moved) current = stack.Peek().Current; }
            catch (Exception exception) { failure = exception; }
            if (failure != null) { Fail("exception", failure.GetType().Name + ": " + failure.Message); yield break; }
            if (!moved) { (stack.Pop() as IDisposable)?.Dispose(); continue; }
            if (current is IEnumerator nested) stack.Push(nested);
            else yield return current;
        }
    }

    private IEnumerator Run()
    {
        account = AccountClient.EnsureInstance();
        string username = Environment.GetEnvironmentVariable("FPS_PROBE_USERNAME");
        string password = Environment.GetEnvironmentVariable("FPS_PROBE_PASSWORD");
        passwordForRedaction = password;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            throw new InvalidOperationException("Set FPS_PROBE_USERNAME and FPS_PROBE_PASSWORD before launching this process.");
        bool done = false, success = false;
        string operationError = "";
        account.Login(username, password, (ok, error) => { done = true; success = ok; operationError = error; });
        password = null;
        yield return WaitFor(() => done, 35, "account-login");
        Require(success && account.IsAuthenticated, "account-login", operationError);

        if (SceneManager.GetActiveScene().name == GameFlowScenes.Login)
        {
            yield return SceneManager.LoadSceneAsync(GameFlowScenes.MainMenu);
            yield return WaitFor(() => LoginSceneBootstrap.FindLoader(SceneManager.GetActiveScene()) != null &&
                LoginSceneBootstrap.FindLoader(SceneManager.GetActiveScene()).IsReady, 35, "main-menu-profile");
        }
        yield return WaitFor(() => LobbyManager.Instance != null && NetworkManager.Singleton != null && !account.IsBusy, 20, "bootstrap");
        lobby = LobbyManager.Instance;

        expectedAppearance = AppearanceFor(role);
        expectedLoadout = LoadoutFor(role);
        remoteAppearance = AppearanceFor(role == "host" ? "join" : "host");
        remoteLoadout = LoadoutFor(role == "host" ? "join" : "host");
        done = false;
        account.SaveProfile(expectedAppearance, expectedLoadout, (ok, error) => { done = true; success = ok; operationError = error; });
        yield return WaitFor(() => done, 35, "cloud-save");
        Require(success, "cloud-save", operationError);
        done = false;
        account.RefreshProfile((ok, error) => { done = true; success = ok; operationError = error; });
        yield return WaitFor(() => done, 35, "cloud-reload");
        Require(success && account.Profile.appearance.Equals(expectedAppearance) && account.Profile.loadout.Equals(expectedLoadout),
            "cloud-reload", "Saved appearance and loadout returned by authenticated GET /api/profile.");
        report.appearance = expectedAppearance;
        report.loadout = expectedLoadout;
        CharacterCustomizationUI.Show();
        yield return Delay(1f);
        CaptureFrame(Path.Combine(outputDirectory, role + "-wardrobe.png"));
        yield return Delay(0.5f);
        var wardrobe = FindObjectOfType<CharacterCustomizationUI>();
        if (wardrobe != null) Destroy(wardrobe.gameObject);
        Phase(10, "cloud-ready");

        lobby.dedicatedServerAddress = "127.0.0.1";
        if (int.TryParse(Argument("-pvpProbeServerPort"), out int serverPort)) lobby.dedicatedServerPort = serverPort;
        lobby.dedicatedServerRoomCount = 1;
        lobby.SetBrowsingGameMode(LobbyGameMode.PVP);
        Require(lobby.StartDedicatedPvpRoomDiscovery(out string discoveryError), "discovery-start", discoveryError);
        float discoveryDeadline = Time.realtimeSinceStartup + 25;
        LanDiscovery.RoomEntry target = null;
        while (target == null && Time.realtimeSinceStartup < discoveryDeadline)
        {
            lobby.RefreshDedicatedPvpRoomDiscovery();
            if (lobby.discovery != null)
            {
                lobby.discovery.RefreshRoomList();
                foreach (var entry in lobby.discovery.Rooms)
                    if (entry.isDedicatedPvp && entry.gamePort == lobby.dedicatedServerPort &&
                        (role == "host" ? entry.canCreate : entry.hasRoom && entry.roomName == "Probe_" + runId))
                    { target = entry; break; }
            }
            if (target == null) yield return Delay(0.3f);
        }
        Require(target != null, "discover-server", "Find local dedicated PVP slot via the real discovery protocol.");
        string connectionMessage;
        bool connecting = role == "host" ? lobby.TryCreateDedicatedPvpRoom("Probe_" + runId, target, out connectionMessage)
            : lobby.TryJoinDedicatedPvpRoom(target, out connectionMessage);
        Require(connecting, "ticket-connect-start", connectionMessage);
        yield return WaitFor(() => lobby.CurrentLobby != null && NetworkManager.Singleton.IsConnectedClient, 50, "authenticated-join");
        report.clientId = NetworkManager.Singleton.LocalClientId;
        yield return WaitFor(() => lobby.CurrentLobby.IsSpawned && lobby.CurrentLobby.GameMode == LobbyGameMode.PVP &&
            lobby.CurrentLobby.GetPlayers().Exists(player => player.ClientId == report.clientId), 10, "own-lobby-snapshot");
        Phase(20, "room-connected");
        LobbyTeam ownTeam = role == "host" ? LobbyTeam.Red : LobbyTeam.Blue;
        lobby.SelectTeam(ownTeam);
        yield return WaitFor(() => lobby.CurrentLobby.GetPlayerTeam(report.clientId) == ownTeam, 10, "select-team");
        yield return WaitFor(() => lobby.CurrentLobby.PlayerCount == 2 && lobby.CurrentLobby.HasMinimumPvpTeams(), 35, "both-players-ready");
        bool localLobbyMatch = false, remoteLobbyMatch = false;
        foreach (var player in lobby.CurrentLobby.GetPlayers())
        {
            if (player.ClientId == report.clientId) localLobbyMatch = player.Appearance.Equals(expectedAppearance) && player.Loadout.Equals(expectedLoadout);
            else remoteLobbyMatch = player.Appearance.Equals(remoteAppearance) && player.Loadout.Equals(remoteLoadout);
        }
        Require(localLobbyMatch && remoteLobbyMatch, "lobby-profile-sync", "Server-approved lobby snapshots match both cloud profiles.");
        Phase(30, "teams-ready");
        yield return WaitPeer(30, 20);
        if (role == "host") Require(lobby.TryStartGame(out string startMessage), "start-match", startMessage);
        yield return WaitFor(() => SceneManager.GetActiveScene().name == "GamePVP" && FindProfiles(), 60, "spawn-two-riflegirls");
        yield return Delay(0.6f);
        localBody = localProfile.GetComponent<PvpFullBodyController>();
        Require(localBody != null, "full-body-controller", "Local network character owns its full-body controller.");
        localBody.SetControlEnabled(false);
        VerifyProfiles("spawn-profile-and-visuals");
        Require(FindObjectsOfType<FirstPersonViewmodel>().Length == 0, "single-body-first-person", "No separate arms viewmodel is active.");
        CaptureFrame(Path.Combine(outputDirectory, role + "-spawn.png"), null, false);
        Phase(40, "characters-verified");
        yield return WaitPeer(40, 30);

        // Both owners move only their own object into an empty staging area; no server damage or respawn APIs are invoked.
        var ownPosition = new Vector3(0, 5000, role == "host" ? 0 : 3);
        Quaternion rotation = role == "host" ? Quaternion.identity : Quaternion.Euler(0, 180, 0);
        var characterController = localProfile.GetComponent<CharacterController>();
        if (characterController != null) characterController.enabled = false;
        localBody.ResetView(rotation);
        localProfile.transform.SetPositionAndRotation(ownPosition, rotation);
        var networkTransform = localProfile.GetComponent<OwnerNetworkTransform>();
        Require(networkTransform != null && networkTransform.IsOwner, "owner-transform", "Test movement uses existing owner-authoritative transform.");
        networkTransform.Teleport(ownPosition, rotation, localProfile.transform.localScale);
        if (characterController != null) characterController.enabled = true;
        Phase(50, "combat-staged");
        yield return WaitPeer(50, 20);
        yield return WaitFor(() => Vector3.Distance(localProfile.transform.position, remoteProfile.transform.position) < 4f, 10, "movement-replicated");
        yield return Delay(0.6f);

        var combat = localProfile.GetComponent<PvpLoadoutCombat>();
        var rig = localProfile.GetComponent<PvpWeaponRig>();
        Require(combat != null && rig != null && rig.ActiveModel != null, "weapon-rig", "Both Demo weapon models are present on the full-body rig.");
        Require(rig.ActiveWeaponId == expectedLoadout.primaryWeaponId && rig.ActiveSightId == expectedLoadout.primarySightId,
            "equipped-primary", "Primary weapon and sight match cloud loadout.");
        CaptureFrame(Path.Combine(outputDirectory, role + "-primary.png"), null, false);
        CaptureRemoteBody(Path.Combine(outputDirectory, role + "-remote-primary.png"));
        yield return WaitFor(() => !combat.IsSwitching, 10, "primary-ready");
        localBody.SetValidationAiming(true);
        yield return Delay(0.8f);
        Require(localBody.AimFraction > 0.99f, "primary-ads", "ADS reaches the selected optic through the normal presentation path.");
        CaptureFrame(Path.Combine(outputDirectory, role + "-primary-ads.png"), null, false);
        yield return CaptureOptics(rig, "primary");
        yield return CaptureIronShot(combat, rig, "primary");
        yield return Delay(0.5f);
        int initialAmmo = combat.CurrentAmmo;
        combat.SetTrigger(true, true, true, Vector3.up);
        float shotStarted = Time.realtimeSinceStartup;
        float nearestOptic = float.PositiveInfinity, largestCorrection = 0f;
        bool capturedShot = false;
        while (Time.realtimeSinceStartup - shotStarted < 0.25f)
        {
            // Presentation resets in Update, then the shared hands and clearance solve in
            // AnimatorIK, and the camera settles in LateUpdate. Capture only the finished frame.
            yield return new WaitForEndOfFrame();
            nearestOptic = Mathf.Min(nearestOptic, ObserveOpticDepth(rig, Camera.main));
            largestCorrection = Mathf.Max(largestCorrection, rig.LastAimClearanceCorrection);
            if (!capturedShot && Time.realtimeSinceStartup - shotStarted >= 0.08f)
            {
                CaptureFrame(Path.Combine(outputDirectory, role + "-primary-fired-ads.png"), null, false);
                if (rig.ActiveWeaponId == 2 && Argument("-pvpProbeOcclusion") == "1") CaptureShotOcclusion(rig, "scope");
                capturedShot = true;
            }
        }
        Require(!float.IsInfinity(nearestOptic) && nearestOptic >= 0.040f, "ads-recoil-clearance",
            $"First 250 ms camera-observed minimum optic depth {nearestOptic:F4} m; largest forward correction {largestCorrection:F4} m; near plane 0.035 m.");
        localBody.SetValidationAiming(false);
        yield return Delay(Mathf.Max(0.7f, combat.ActiveDefinition.ShotInterval + 0.2f) - 0.25f);
        Require(combat.CurrentAmmo == initialAmmo - 1, "server-ammo-consumed", "A real FireServerRpc consumed exactly one round after acknowledgement.");
        int ammoBeforeReload = combat.CurrentAmmo, reserveBeforeReload = combat.ReserveAmmo;
        combat.RequestReload();
        yield return WaitFor(() => combat.IsReloading, 5, "reload-start");
        yield return WaitFor(() => !combat.IsReloading, combat.ActiveDefinition.reloadDuration + 10, "reload-complete");
        Require(combat.CurrentAmmo > ammoBeforeReload && reserveBeforeReload - combat.ReserveAmmo == combat.CurrentAmmo - ammoBeforeReload,
            "server-reload-accounting", "Reload transfers ammunition from reserve on the server.");
        combat.RequestSlot(1);
        yield return WaitFor(() => combat.ActiveSlot == 1 && !combat.IsSwitching, 10, "switch-sidearm");
        Require(rig.ActiveWeaponId == 1 && rig.ActiveSightId == expectedLoadout.sidearmSightId, "equipped-sidearm", "Pistol sight matches cloud loadout.");
        CaptureFrame(Path.Combine(outputDirectory, role + "-sidearm.png"), null, false);
        localBody.SetValidationAiming(true);
        yield return Delay(0.8f);
        CaptureFrame(Path.Combine(outputDirectory, role + "-sidearm-ads.png"), null, false);
        yield return CaptureOptics(rig, "sidearm");
        yield return CaptureIronShot(combat, rig, "sidearm");
        localBody.SetValidationAiming(false);
        Phase(60, "sidearms-ready");
        yield return WaitPeer(60, 20);
        var remoteRig = remoteProfile.GetComponent<PvpWeaponRig>();
        yield return WaitFor(() => remoteRig.ActiveSlot == 1, 8, "remote-slot-replicated");
        Require(remoteRig.ActiveWeaponId == 1 && remoteRig.ActiveSightId == remoteLoadout.sidearmSightId, "remote-sidearm", "Other player sees the selected pistol and sight.");
        CaptureRemoteBody(Path.Combine(outputDirectory, role + "-remote-sidearm.png"));
        Phase(65, "sidearms-observed");
        yield return WaitPeer(65, 20);
        combat.RequestSlot(0);
        yield return WaitFor(() => combat.ActiveSlot == 0 && !combat.IsSwitching, 10, "switch-primary");
        Phase(70, "primary-restored");
        yield return WaitPeer(70, 20);

        var ownHealth = localProfile.GetComponent<PvpPlayerCombat>();
        var remoteHealth = remoteProfile.GetComponent<PvpPlayerCombat>();
        int deathsBefore = role == "join" ? ownHealth.Deaths.Value : remoteHealth.Deaths.Value;
        if (role == "host")
        {
            int killsBefore = ownHealth.Kills.Value;
            float killDeadline = Time.realtimeSinceStartup + 20;
            while (!remoteHealth.IsDead.Value && Time.realtimeSinceStartup < killDeadline)
            {
                Vector3 direction = (remoteProfile.transform.position + Vector3.up * 1.0f - localBody.EyePosition).normalized;
                combat.SetTrigger(true, true, true, direction);
                yield return Delay(Mathf.Max(0.15f, combat.ActiveDefinition.ShotInterval + 0.04f));
            }
            Require(remoteHealth.IsDead.Value, "projectile-kill", "Normal server flight-time projectiles killed the opposing player.");
            yield return WaitFor(() => ownHealth.Kills.Value == killsBefore + 1 && lobby.CurrentLobby.RedKills.Value >= 1, 5, "kill-score-replicated");
        }
        else yield return WaitFor(() => ownHealth.IsDead.Value, 30, "death-replicated");
        Phase(80, "death-observed");
        var victimHealth = role == "join" ? ownHealth : remoteHealth;
        yield return WaitFor(() => !victimHealth.IsDead.Value && victimHealth.Deaths.Value == deathsBefore + 1, 15, "respawn");
        if (role == "join") localBody.SetControlEnabled(false);
        Require(Mathf.Approximately(victimHealth.CurrentHealth.Value, victimHealth.maxHealth), "respawn-health", "Server restored full health after the normal respawn delay.");
        VerifyProfiles("respawn-profile-and-visuals");
        Phase(90, "respawn-verified");
        yield return WaitPeer(90, 20);
        lobby.LeaveRoom();
        yield return WaitFor(() => SceneManager.GetActiveScene().name == GameFlowScenes.MultiplayerLobby &&
            NetworkManager.Singleton != null && !NetworkManager.Singleton.IsListening, 20, "leave-match-to-lobby");
        Require(account.IsAuthenticated && account.Profile.appearance.Equals(expectedAppearance) && account.Profile.loadout.Equals(expectedLoadout),
            "account-survives-match-exit", "Returning to the lobby preserves the authenticated account and saved profile.");
        if (report.runtimeErrors.Count > 0) throw new InvalidOperationException("Runtime errors occurred; inspect runtimeErrors in the report.");
        report.status = "PASS";
        Phase(100, "complete");
        yield return Delay(1.5f);
        finished = true;
        Quit(0);
    }

    private static float ObserveOpticDepth(PvpWeaponRig rig, Camera camera)
    {
        float nearest = float.PositiveInfinity;
        foreach (var sight in rig.ActiveModel.sights)
        {
            if (sight.sightId != rig.ActiveSightId || sight.visual == null) continue;
            if (sight.nearEyePoints != null)
                foreach (Vector3 point in sight.nearEyePoints)
                    nearest = Mathf.Min(nearest, camera.transform.InverseTransformPoint(rig.ActiveModel.transform.TransformPoint(point)).z);
            foreach (var renderer in sight.visual.GetComponentsInChildren<MeshRenderer>())
            {
                Bounds bounds = renderer.localBounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = bounds.center + Vector3.Scale(bounds.extents,
                        new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    nearest = Mathf.Min(nearest, camera.transform.InverseTransformPoint(renderer.transform.TransformPoint(corner)).z);
                }
            }
        }
        if (float.IsPositiveInfinity(nearest) && rig.Sight != null)
            nearest = camera.transform.InverseTransformPoint(rig.Sight.position).z;
        return nearest;
    }

    private void CaptureShotOcclusion(PvpWeaponRig rig, string pose)
    {
        Camera camera = Camera.main;
        string prefix = pose + "-without-";
        Require(true, pose + "-shot-pose", "Model=" + rig.ActiveModel.transform.localPosition.ToString("F5") +
            " euler=" + rig.ActiveModel.transform.localEulerAngles.ToString("F5") +
            " sightCamera=" + camera.transform.InverseTransformPoint(rig.Sight.position).ToString("F5") +
            " viewport=" + camera.WorldToViewportPoint(rig.Sight.position).ToString("F5"));
        var mechanical = new List<Renderer>();
        foreach (var renderer in rig.ActiveModel.GetComponentsInChildren<MeshRenderer>())
            if (renderer.name.StartsWith("Mechanical_", StringComparison.Ordinal)) mechanical.Add(renderer);
        CaptureWithoutRenderers(prefix + "mechanical", mechanical.ToArray());
        foreach (var renderer in mechanical)
            CaptureWithoutRenderers(prefix + renderer.name.Replace('/', '_'), new[] { renderer });
        CaptureWithoutRenderers(prefix + "particles", FindObjectsOfType<ParticleSystemRenderer>());
        var casingRenderers = new List<Renderer>();
        foreach (var renderer in FindObjectsOfType<MeshRenderer>())
            if (renderer.transform.root.name == "PVP Casing") casingRenderers.Add(renderer);
        CaptureWithoutRenderers(prefix + "casings", casingRenderers.ToArray());
        foreach (var sight in rig.ActiveModel.sights)
            if (sight.sightId == rig.ActiveSightId && sight.visual != null)
            {
                var renderers = sight.visual.GetComponentsInChildren<Renderer>();
                CaptureWithoutRenderers(prefix + "optic", renderers);
                foreach (var renderer in renderers)
                {
                    Require(true, pose + "-shot-part-" + renderer.name,
                        "cameraCenter=" + camera.transform.InverseTransformPoint(renderer.bounds.center).ToString("F5") +
                        " localBounds=" + renderer.localBounds.ToString());
                    CaptureWithoutRenderers(prefix + renderer.name.Replace('/', '_'), new[] { renderer });
                }
            }
    }

    private void CaptureWithoutRenderers(string label, Renderer[] renderers)
    {
        var enabled = new bool[renderers.Length];
        for (int i = 0; i < renderers.Length; i++) { enabled[i] = renderers[i].enabled; renderers[i].enabled = false; }
        try { CaptureFrame(Path.Combine(outputDirectory, role + "-shot-" + label + ".png"), null, false); }
        finally { for (int i = 0; i < renderers.Length; i++) if (renderers[i] != null) renderers[i].enabled = enabled[i]; }
    }

    private IEnumerator CaptureIronShot(PvpLoadoutCombat combat, PvpWeaponRig rig, string slotName)
    {
        byte originalSight = rig.ActiveSightId;
        Require(rig.SetValidationSight(0), slotName + "-iron-shot-select", "Check the bare/iron sight during actual recoil.");
        yield return Delay(0.6f);
        int initialAmmo = combat.CurrentAmmo;
        combat.SetTrigger(true, true, true, Vector3.up);
        yield return Delay(0.08f);
        yield return new WaitForEndOfFrame();
        float ironDepth = ObserveOpticDepth(rig, Camera.main);
        Require(ironDepth >= 0.040f, slotName + "-iron-shot-clearance",
            $"Finished-frame closest iron sight/receiver point depth {ironDepth:F4} m; camera near plane 0.035 m.");
        CaptureFrame(Path.Combine(outputDirectory, role + "-" + slotName + "-iron-fired-ads.png"), null, false);
        if (rig.ActiveWeaponId == 2 && Argument("-pvpProbeOcclusion") == "1") CaptureShotOcclusion(rig, "iron");
        yield return Delay(Mathf.Max(0.7f, combat.ActiveDefinition.ShotInterval + 0.2f) - 0.08f);
        Require(combat.CurrentAmmo == initialAmmo - 1, slotName + "-iron-shot-ammo", "The iron-sight screenshot uses a real acknowledged shot.");
        Require(rig.SetValidationSight(originalSight), slotName + "-iron-shot-restore", "Restore the cloud-selected optic after recoil inspection.");
        yield return Delay(0.6f);
    }

    private IEnumerator CaptureOptics(PvpWeaponRig rig, string slotName)
    {
        byte savedSight = rig.ActiveSightId;
        foreach (var sight in rig.ActiveDefinition.sights)
        {
            Require(rig.SetValidationSight(sight.sightId), slotName + "-optic-" + sight.sightId, "A compatible authored optic is selected for visual validation.");
            yield return Delay(0.65f);
            yield return new WaitForEndOfFrame();
            Vector3 screen = Camera.main.WorldToViewportPoint(rig.Sight.position);
            Require(screen.z > 0 && Mathf.Abs(screen.x - 0.5f) < 0.2f && Mathf.Abs(screen.y - 0.5f) < 0.2f,
                slotName + "-optic-alignment-" + sight.sightId, "Animated ADS viewport point: " + screen.ToString("F3"));
            CaptureFrame(Path.Combine(outputDirectory, role + "-" + slotName + "-optic-" + sight.sightId + ".png"), null, false);
        }
        Require(rig.SetValidationSight(savedSight), slotName + "-optic-restored", "Restore the cloud-selected optic after visual validation.");
        yield return Delay(0.3f);
    }

    private void CaptureRemoteBody(string path)
    {
        var capture = new GameObject("ProbeRemoteCamera", typeof(Camera));
        Camera camera = capture.GetComponent<Camera>();
        camera.fieldOfView = 45f;
        camera.nearClipPlane = 0.03f;
        Vector3 focus = remoteProfile.transform.position + Vector3.up * 0.9f;
        camera.transform.position = focus + remoteProfile.transform.forward * 2.6f + remoteProfile.transform.right * 1.4f;
        camera.transform.LookAt(focus);
        try { CaptureFrame(path, camera, false); }
        finally { Destroy(capture); }
    }

    private static void CaptureFrame(string path, Camera overrideCamera = null, bool includeUi = true)
    {
        // Hidden automated clients may suspend backbuffer rendering. Render an explicit target instead.
        foreach (var previewCamera in Camera.allCameras)
            if (previewCamera.name == "PreviewCamera" && previewCamera.targetTexture != null) previewCamera.Render();
        Camera camera = overrideCamera != null ? overrideCamera : Camera.main;
        GameObject temporaryCamera = null;
        if (camera == null)
        {
            temporaryCamera = new GameObject("ProbeCaptureCamera", typeof(Camera));
            camera = temporaryCamera.GetComponent<Camera>();
            camera.cullingMask = 0; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
        }
        var overlays = new List<Canvas>();
        var uiLayers = new Dictionary<GameObject, int>();
        GameObject uiCameraObject = null;
        var previousTarget = camera.targetTexture;
        var previousActive = RenderTexture.active;
        var target = RenderTexture.GetTemporary(1280, 720, 24, RenderTextureFormat.ARGB32);
        Texture2D pixels = null;
        try
        {
            camera.targetTexture = target;
            Canvas.ForceUpdateCanvases();
            camera.Render();
            if (includeUi)
            {
                // Overlay UI normally draws after the entire camera stack. A separate pass preserves
                // that order without letting a scene's portrait/transparent materials cover the UI.
                uiCameraObject = new GameObject("ProbeUiCamera", typeof(Camera));
                var uiCamera = uiCameraObject.GetComponent<Camera>();
                uiCamera.enabled = false;
                uiCamera.cullingMask = 1 << 30;
                uiCamera.clearFlags = CameraClearFlags.Depth;
                uiCamera.nearClipPlane = 0.01f;
                uiCamera.farClipPlane = 10f;
                uiCamera.transform.position = new Vector3(30000, 30000, 30000);
                uiCamera.targetTexture = target;
                foreach (var canvas in FindObjectsOfType<Canvas>())
                {
                    if (canvas.renderMode != RenderMode.ScreenSpaceOverlay) continue;
                    overlays.Add(canvas);
                    foreach (Transform child in canvas.GetComponentsInChildren<Transform>(true))
                        if (!uiLayers.ContainsKey(child.gameObject)) { uiLayers.Add(child.gameObject, child.gameObject.layer); child.gameObject.layer = 30; }
                    canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    canvas.worldCamera = uiCamera;
                    canvas.planeDistance = 1f;
                }
                Canvas.ForceUpdateCanvases();
                uiCamera.Render();
            }
            RenderTexture.active = target;
            pixels = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            pixels.Apply();
            File.WriteAllBytes(path, pixels.EncodeToPNG());
        }
        finally
        {
            camera.targetTexture = previousTarget;
            RenderTexture.active = previousActive;
            foreach (var canvas in overlays) { canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.worldCamera = null; }
            foreach (var layer in uiLayers) if (layer.Key != null) layer.Key.layer = layer.Value;
            if (uiCameraObject != null) Destroy(uiCameraObject);
            if (pixels != null) Destroy(pixels);
            RenderTexture.ReleaseTemporary(target);
            if (temporaryCamera != null) Destroy(temporaryCamera);
        }
    }

    private bool FindProfiles()
    {
        localProfile = null; remoteProfile = null;
        foreach (var profile in FindObjectsOfType<PvpCharacterProfile>())
        {
            if (!profile.IsSpawned) continue;
            if (profile.IsOwner) localProfile = profile;
            else remoteProfile = profile;
        }
        return localProfile != null && remoteProfile != null;
    }

    private void VerifyProfiles(string step)
    {
        var ownView = localProfile.GetComponentInChildren<RifleGirlAppearance>(true);
        var otherView = remoteProfile.GetComponentInChildren<RifleGirlAppearance>(true);
        Require(localProfile.Appearance.Value.Equals(expectedAppearance) && localProfile.Loadout.Value.Equals(expectedLoadout)
            && remoteProfile.Appearance.Value.Equals(remoteAppearance) && remoteProfile.Loadout.Value.Equals(remoteLoadout), step + "-network", "Both network profile snapshots are unchanged.");
        Require(ownView != null && otherView != null && ownView.Current.Equals(expectedAppearance) && otherView.Current.Equals(remoteAppearance)
            && ownView.IsLocalFirstPerson && !otherView.IsLocalFirstPerson
            && ownView.CharacterAnimator != null && ownView.CharacterAnimator.isHuman
            && localProfile.GetComponentsInChildren<RifleGirlAppearance>(true).Length == 1,
            step + "-renderers", "A single humanoid body applies own/remote appearances and first-person visibility.");
    }

    private IEnumerator WaitFor(Func<bool> condition, float timeout, string step)
    {
        report.stage = step;
        WriteReport();
        float deadline = Time.realtimeSinceStartup + timeout;
        while (!condition())
        {
            if (Time.realtimeSinceStartup >= deadline)
                throw new TimeoutException(step + "; connection: " + (lobby != null ? lobby.ConnectionStatus : "not ready"));
            yield return null;
        }
        Pass(step, "Condition observed on this client.");
    }

    private IEnumerator WaitPeer(int phase, float timeout)
    {
        float deadline = Time.realtimeSinceStartup + timeout;
        string peerPath = Path.Combine(outputDirectory, (role == "host" ? "join" : "host") + "-report.json");
        while (true)
        {
            Report peer = null;
            try
            {
                if (File.Exists(peerPath))
                    using (var stream = new FileStream(peerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream)) peer = JsonUtility.FromJson<Report>(reader.ReadToEnd());
            }
            catch (IOException) { }
            if (peer != null && peer.runId == runId)
            {
                if (peer.status == "FAIL") throw new InvalidOperationException("Peer failed at " + peer.stage + ": " + peer.error);
                if (peer.phase >= phase) yield break;
            }
            if (Time.realtimeSinceStartup >= deadline) throw new TimeoutException("Waiting for peer phase " + phase);
            yield return Delay(0.1f);
        }
    }

    private static IEnumerator Delay(float seconds)
    {
        float until = Time.realtimeSinceStartup + seconds;
        while (Time.realtimeSinceStartup < until) yield return null;
    }

    private void Require(bool condition, string step, string detail)
    {
        if (!condition) throw new InvalidOperationException(step + ": " + detail);
        Pass(step, detail);
    }
    private void Pass(string step, string detail)
    {
        report.steps.Add(new Step { name = step, result = "PASS", detail = Redact(detail), seconds = Time.realtimeSinceStartup - started });
        WriteReport();
    }
    private void Phase(int phase, string stage) { report.phase = phase; report.stage = stage; WriteReport(); }
    private void WriteReport()
    {
        if (report == null || string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory)) return;
        report.elapsed = Time.realtimeSinceStartup - started;
        report.scene = SceneManager.GetActiveScene().name;
        string path = Path.Combine(outputDirectory, role + "-report.json");
        string temporary = path + ".pending";
        File.WriteAllText(temporary, JsonUtility.ToJson(report, true));
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                break;
            }
            catch (IOException) when (attempt < 7) { System.Threading.Thread.Sleep(5); }
        }
    }
    private void RecordRuntimeError(string condition, string stackTrace, LogType type)
    {
        if (report == null || finished || (type != LogType.Error && type != LogType.Exception) || report.runtimeErrors.Count >= 20) return;
        report.runtimeErrors.Add(Redact(condition));
    }
    private string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (!string.IsNullOrEmpty(passwordForRedaction)) text = text.Replace(passwordForRedaction, "[redacted]");
        if (account != null && !string.IsNullOrEmpty(account.AccessToken)) text = text.Replace(account.AccessToken, "[redacted]");
        return text;
    }
    private void Fail(string step, string error)
    {
        if (finished) return;
        finished = true;
        if (report != null)
        {
            report.status = "FAIL"; report.stage = step; report.error = Redact(error);
            try { WriteReport(); } catch (IOException) { }
        }
        Debug.LogError("[PVP probe] " + step + ": " + Redact(error));
        Quit(1);
    }
    private void OnDestroy() => Application.logMessageReceived -= RecordRuntimeError;
    private static void Quit(int exitCode)
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit(exitCode);
#endif
    }
    private static string Argument(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == name) return args[i + 1];
        return "";
    }
    private static CharacterAppearanceData AppearanceFor(string selectedRole) => selectedRole == "host"
        ? new CharacterAppearanceData { outfitId = 0, faceId = 1, clothColorId = 2, hairColorId = 1, hat = true, accessories = true }
        : new CharacterAppearanceData { outfitId = 1, faceId = 4, clothColorId = 4, hairColorId = 3, hat = false, accessories = true };
    private static PvpLoadoutData LoadoutFor(string selectedRole) => selectedRole == "host"
        ? new PvpLoadoutData { primaryWeaponId = 0, primarySightId = 1, sidearmSightId = 3 }
        : new PvpLoadoutData { primaryWeaponId = 2, primarySightId = 5, sidearmSightId = 0 };
}
#endif
