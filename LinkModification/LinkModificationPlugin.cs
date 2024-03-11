using System.Collections;
using System.IO;
using AmongUs.Data;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Utils;
using HarmonyLib;
using Hazel;
using Hazel.Dtls;
using Hazel.Udp;
using Il2CppSystem;
using Il2CppSystem.Security.Cryptography.X509Certificates;
using InnerNet;
using IPAddress = Il2CppSystem.Net.IPAddress;
using IPEndPoint = Il2CppSystem.Net.IPEndPoint;

namespace LinkModification;

[BepInAutoPlugin]
[BepInProcess("Among Us.exe")]
public partial class LinkModificationPlugin : BasePlugin
{
    public static string CertificateText = string.Empty;
    public Harmony Harmony { get; } = new(Id);

    private ConfigEntry<bool> DtlToUDPEntry { get; set; }

    private ConfigEntry<bool> AddDtlCertificateEntry { get; set; }
    
    private ConfigEntry<bool> authConnectToUdpEntry { get; set; }

    public static bool DtlToUDP { get; set; }

    public static bool AddDtlCertificate { get; set; }
    
    public static bool authConnectToUdp { get; set; }

    public override void Load()
    {
        DtlToUDPEntry = Config.Bind("LinkConfig", nameof(DtlToUDP), false);
        AddDtlCertificateEntry = Config.Bind("LinkConfig", nameof(AddDtlCertificate), false);
        authConnectToUdpEntry = Config.Bind("LinkConfig", nameof(authConnectToUdp), false);

        DtlToUDP = DtlToUDPEntry.Value;
        AddDtlCertificate = AddDtlCertificateEntry.Value;
        authConnectToUdp = authConnectToUdpEntry.Value;

        if (AddDtlCertificate)
        {
            var path = Path.Combine(Paths.GameRootPath, "Certificate.txt");
            var stream = File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite);
            using var reader = new StreamReader(stream);
            CertificateText = reader.ReadToEnd();

            if (CertificateText == string.Empty)
            {
                Log.LogError("未读取到证书");
                AddDtlCertificate = false;
            }
        }

        Harmony.PatchAll();
    }
}

[Harmony]
public static class PatchClass
{
    private static UnityUdpClientConnection _clientConnection;

    [HarmonyPatch(typeof(AuthManager), nameof(AuthManager.CreateDtlsConnection))]
    [HarmonyPrefix]
    public static void OnCreateDtlPatch(ref DtlsUnityConnection __result)
    {
        if (!LinkModificationPlugin.AddDtlCertificate) return;
        var certificate = new X509Certificate2(CryptoHelpers.DecodePEM(LinkModificationPlugin.CertificateText));
        var collection = new X509Certificate2Collection();
        collection.Add(__result.serverCertificates[0]);
        collection.Add(certificate);
        __result.SetValidServerCertificates(collection);
    }

    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.GetConnectionData))]
    [HarmonyPrefix]
    public static bool OnGetConnectionDataPatch(
        [HarmonyArgument(1)] string matchmakerToken,
        ref byte[] __result
    )
    {
        if (!LinkModificationPlugin.DtlToUDP) return true;

        var messageWriter = new MessageWriter(1000);
        messageWriter.Write(Constants.GetBroadcastVersion());
        messageWriter.Write(DataManager.Player.Customization.Name);
        messageWriter.Write(matchmakerToken ?? string.Empty);
        messageWriter.Write((uint)DataManager.Settings.Language.CurrentLanguage);
        messageWriter.Write((byte)DataManager.Settings.Multiplayer.ChatMode);
        Constants.GetPlatformData().Serialize(messageWriter);
        messageWriter.Write(DestroyableSingleton<EOSManager>.Instance.FriendCode ?? string.Empty);

        __result = messageWriter.ToByteArray(true);
        return false;
    }

    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.Connect))]
    [HarmonyPrefix]
    public static bool ConnectPatch(
        InnerNetClient __instance,
        [HarmonyArgument(0)] MatchMakerModes mode,
        [HarmonyArgument(1)] string matchmakerToken
    )
    {
        __instance.StartCoroutine(CoConnect(__instance, mode, matchmakerToken));
        return false;
    }

    private static IEnumerator CoConnect(InnerNetClient instance, MatchMakerModes mode, string matchmakerToken)
    {
        if (instance.mode != MatchMakerModes.None) instance.DisconnectInternal(DisconnectReasons.NewConnection);

        instance.mode = mode;
        instance.isConnecting = true;

        yield return CoConnect(instance, matchmakerToken);

        if (instance.connection is not { State: ConnectionState.Connected })
        {
            instance.HandleDisconnect(DisconnectReasons.Error);
            instance.isConnecting = false;
            yield break;
        }

        var matchMakerModes = instance.mode;

        if (matchMakerModes == MatchMakerModes.Client)
        {
            instance.JoinGame();
            yield return instance.WaitWithTimeout(
                (Func<bool>)(() => instance.ClientId >= 0),
                DestroyableSingleton<TranslationController>.Instance.GetString(StringNames.PSNErrorSessionJoinFailed,
                    Array.Empty<Object>()));
            instance.isConnecting = false;
            var amConnected = instance.AmConnected;
            yield break;
        }

        if (matchMakerModes != MatchMakerModes.HostAndClient) yield break;

        instance.GameId = 0;

        GameOptionsManager.Instance.CurrentGameOptions = GameOptionsManager.Instance.GameHostOptions;
        instance.HostGame(GameOptionsManager.Instance.CurrentGameOptions,
            DataManager.Settings.Multiplayer.HostGameFilterOptions);
        yield return instance.WaitWithTimeout((Func<bool>)(() => instance.GameId != 0),
            DestroyableSingleton<TranslationController>.Instance.GetString(StringNames.ErrorFailedToCreateGame,
                Array.Empty<Object>()));

        if (!instance.AmConnected)
        {
            instance.isConnecting = false;
            yield break;
        }

        instance.JoinGame();

        yield return instance.WaitWithTimeout((Func<bool>)(() => instance.ClientId >= 0),
            DestroyableSingleton<TranslationController>.Instance.GetString(StringNames.ErrorFailedToJoinCreatedGame,
                Array.Empty<Object>()));
        instance.isConnecting = false;
    }

    private static IEnumerator CoConnect(InnerNetClient instance, string matchmakerToken)
    {
        if (instance.AmConnected) yield break;

        for (;;)
        {
            var ipAddr = instance.networkAddress;
            DestroyableSingleton<DisconnectPopup>.Instance.Close();
            instance.LastDisconnectReason = DisconnectReasons.Unknown;
            instance.NetIdCnt = 1U;
            instance.DestroyedObjects.Clear();
            if (DestroyableSingleton<EOSManager>.Instance.ProductUserId == null) break;
            if (instance.useDtls && instance.NetworkMode == NetworkModes.OnlineGame && !LinkModificationPlugin.DtlToUDP)
            {
                instance.connection = AuthManager.CreateDtlsConnection(ipAddr, (ushort)(instance.networkPort + 3));
            }
            else
            {
                if (instance.NetworkMode == NetworkModes.OnlineGame)
                {
                    if (LinkModificationPlugin.authConnectToUdp)
                        yield return CoConnect(ipAddr, (ushort)(instance.networkPort + 2), matchmakerToken);
                    else
                        yield return DestroyableSingleton<AuthManager>.Instance.CoConnect(ipAddr,
                            (ushort)(instance.networkPort + 2), matchmakerToken);
                    yield return DestroyableSingleton<AuthManager>.Instance.CoWaitForNonce();
                }

                var endPoint = new IPEndPoint(IPAddress.Parse(ipAddr), instance.networkPort);
                var log = new UnityLogger();
                instance.connection = new UnityUdpClientConnection(log.Cast<ILogger>(), endPoint);
            }

            instance.connection.KeepAliveInterval = 1000;
            instance.connection.DisconnectTimeoutMs = 7500;
            instance.connection.ResendPingMultiplier = 1.2f;
            instance.connection.DataReceived += (Action<DataReceivedEventArgs>)instance.OnMessageReceived;
            instance.connection.Disconnected += (EventHandler<DisconnectedEventArgs>)instance.OnDisconnect;

            var dispatcher = instance.Dispatcher;
            lock (dispatcher)
            {
                instance.Dispatcher.Clear();
            }

            instance.connection.ConnectAsync(instance.GetConnectionData(instance.useDtls, matchmakerToken));

            while (instance.connection is { State: ConnectionState.Connecting }) yield return null;

            if (instance.connection is { State: ConnectionState.Connected } ||
                instance.LastDisconnectReason == DisconnectReasons.IncorrectVersion ||
                instance.LastDisconnectReason == DisconnectReasons.InvalidName) goto IL_281;

            if (!DestroyableSingleton<ServerManager>.Instance.TrackServerFailure(ipAddr)) goto IL_281;

            instance.DisconnectInternal(DisconnectReasons.NewConnection);
        }

        instance.EnqueueDisconnect(DisconnectReasons.NotAuthorized);
        yield break;
        IL_281: ;
    }

    private static IEnumerator CoConnect(string targetIp, ushort targetPort, string matchmakerToken)
    {
        var instance = DestroyableSingleton<AuthManager>.Instance;
        if (_clientConnection != null)
        {
            _clientConnection.DataReceived -= (Action<DataReceivedEventArgs>)instance.Connection_DataReceived;
            _clientConnection.Disconnected -= (EventHandler<DisconnectedEventArgs>)instance.Connection_Disconnected;
            _clientConnection.Dispose();
            _clientConnection = null;
        }

        _clientConnection = new UnityUdpClientConnection(new UnityLogger().Cast<ILogger>(),
            new IPEndPoint(IPAddress.Parse(targetIp), targetPort));
        _clientConnection = AuthManager.CreateDtlsConnection(targetIp, targetPort);
        _clientConnection.DataReceived += (Action<DataReceivedEventArgs>)instance.Connection_DataReceived;
        _clientConnection.Disconnected += (EventHandler<DisconnectedEventArgs>)instance.Connection_Disconnected;
        _clientConnection.ConnectAsync(instance.BuildData(matchmakerToken));
        while (instance.connection is { State: ConnectionState.Connecting }) yield return null;
    }
}