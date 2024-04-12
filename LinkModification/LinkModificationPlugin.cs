using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AmongUs.Data;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Utils;
using HarmonyLib;
using Hazel;
using Hazel.Dtls;
using Hazel.Udp;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSystem;
using Il2CppSystem.Security.Cryptography.X509Certificates;
using InnerNet;
using MonoMod.Utils;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using ILogger = Hazel.ILogger;
using IPAddress = Il2CppSystem.Net.IPAddress;
using IPEndPoint = Il2CppSystem.Net.IPEndPoint;
using Object = Il2CppSystem.Object;
using UObject = UnityEngine.Object;

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
        
        Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<DataReceivedEventArgs>();

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
        ref Il2CppStructArray<byte> __result
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
        messageWriter.Recycle();
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

        if (!CustomCodePatch.IsCustom)
            instance.GameId = 0;

        GameOptionsManager.Instance.CurrentGameOptions = GameOptionsManager.Instance.GameHostOptions;
        instance.HostGame(GameOptionsManager.Instance.CurrentGameOptions,
            DataManager.Settings.Multiplayer.HostGameFilterOptions);
        
        if (!CustomCodePatch.IsCustom) 
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
        if (instance.ClientId <= 0)
        {
            CustomCodePatch.IsCustom = false;
        }
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
            var res = new DataReceive(instance.connection)
            {
                Action = n => instance.OnMessageReceived(new DataReceivedEventArgs(n.Sender, n.Message, n.SendOption))
            };
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
        IL_281: 
        System.Console.WriteLine("连接成功");
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
        var res = new DataReceive(_clientConnection)
        {
            Action = Connection_DataReceived
        };
        _clientConnection.Disconnected += (EventHandler<DisconnectedEventArgs>)instance.Connection_Disconnected;
        _clientConnection.ConnectAsync(instance.BuildData(matchmakerToken));
        while (instance.connection is { State: ConnectionState.Connecting }) yield return null;
    }
    
    private static void Connection_DataReceived(DataReceive dataReceive)
    {
        var message = dataReceive.Message;
        try
        {
            var messageReader = message.ReadMessage();
            if (messageReader.Tag != 1) return;
            AuthManager.Instance.LastNonceReceived = (Nullable<uint>)messageReader.ReadUInt32();
            _clientConnection.Disconnect("Job done", null);
        }
        finally
        {
            message.Recycle();
        }
    }

    [HarmonyPatch(typeof(Connection), nameof(Connection.Dispose), [typeof(bool)]), HarmonyPostfix]
    public static void OnDispose(Connection __instance)
    {
        DataReceive.AllReceives.RemoveAll(n => n.Sender == __instance);
    }

    [HarmonyPatch(typeof(Connection), nameof(Connection.InvokeDataReceived)), HarmonyPrefix]
    public static bool OnDataReceived(
        Connection __instance, 
        [HarmonyArgument(0)]MessageReader msg,
        [HarmonyArgument(1)]SendOption sendOption
        )
    {
        if (!DataReceive.AllReceives.Exists(n => n.Sender == __instance))
        {
            return true;
        }

        var receives = DataReceive.AllReceives.FindAll(n => n.Sender == __instance);
        receives.Do(data =>
        {
            data.Message = msg;
            data.SendOption = sendOption;
            data.Invoke();
        });

        return false;
    }
}

public class DataReceive
{
    public static readonly List<DataReceive> AllReceives = [];
    
    public readonly Connection Sender;
    public MessageReader Message { get; set; }
    public SendOption SendOption { get; set; } 

    public System.Action<DataReceive> Action { get; set; }

    public DataReceive(Connection sender)
    {
        Sender = sender;
        AllReceives.Add(this);
    }

    public void Invoke() => Action.Invoke(this);
}

[Harmony]
public static class CustomCodePatch
{
    private static string GameCodeString;
    public static bool IsCustom = false;
    
    [HarmonyPatch(typeof(SinglePopHelp), nameof(SinglePopHelp.OnEnable)), HarmonyPostfix]
    public static void SinglePopHelp_OnEnable(SinglePopHelp __instance)
    {
        if (__instance.name != "JoinGameMenu")
            return;

        var TempText = __instance.transform.Find("Text_TMP").gameObject;
        if (!TempText) return;

        var GameCodeTitleText = UObject.Instantiate(TempText, TempText.transform.parent);
        UObject.Destroy(GameCodeTitleText.GetComponent<TextTranslatorTMP>());
        GameCodeTitleText.transform.localPosition = new Vector3(-0.4f, -0.96f, 0);
        GameCodeTitleText.GetComponent<TMPro.TMP_Text>().text = "自定义房间号";
        
        var TempBox = __instance.transform.Find("GameIdText").gameObject;
        if (!TempBox) return;
        var GameCodeBox = UObject.Instantiate(TempBox, TempBox.transform.parent);
        GameCodeBox.transform.localPosition = new Vector3(-0.2f, -1.6425f, -32);
        var textBox = GameCodeBox.GetComponent<TextBoxTMP>();
        textBox.OnChange.AddListener((UnityAction)(() => GameCodeString = textBox.text));
        var arrow = GameCodeBox.transform.Find("arrowEnter").gameObject.GetComponent<PassiveButton>();
        arrow.OnClick.RemoveAllListeners();
        arrow.OnClick.AddListener((UnityAction)JoinCustomCode);
    }

    private static void JoinCustomCode()
    {
        System.Console.WriteLine($"Join{GameCodeString}");
        var Id = GameCode.GameNameToInt(GameCodeString);
        var client = DestroyableSingleton<AmongUsClient>.Instance;
        client.GameId = Id;
        client.NetworkMode = NetworkModes.OnlineGame;

        HttpMatchmakerManager.HostServer server = null;
        string matchmakertoken = null;
        DestroyableSingleton<HttpMatchmakerManager>.Instance.CoFindHostServer((Action<HttpMatchmakerManager.HostServer, string>)((HttpMatchmakerManager.HostServer host, string mmToken) => 
        {
            server = host;
            matchmakertoken = mmToken;
        }));
        if (server == null)
        {
            DestroyableSingleton<MatchMaker>.Instance.NotConnecting();
            SceneManager.LoadScene("MMOnline");
            return;
        }

        IsCustom = true;
        client.StartCoroutine(CoConnectToGameServer(client, MatchMakerModes.HostAndClient, server.Ip, server.Port,
            matchmakertoken));
    }
    
    private static IEnumerator CoConnectToGameServer(AmongUsClient client,MatchMakerModes mode, string ipAddress, ushort port, string matchmakerToken)
    {
        client.SetEndpoint(ipAddress, port, DestroyableSingleton<ServerManager>.Instance.UdpUseDtls);
        client.MainMenuScene = "MMOnline";
        client.OnlineScene = "OnlineGame";
        client.Connect(mode, matchmakerToken);
        yield return client.WaitForConnectionOrFail();
        DestroyableSingleton<MatchMaker>.Instance.NotConnecting();
        if (client.ClientId < 0)
        {
            SceneManager.LoadScene("MMOnline");
        }
    }

    public static List<Vector3> FindVentPoss()
    {
        var poss = new List<Vector3>();
        foreach (var vent in DestroyableSingleton<ShipStatus>.Instance.AllVents)
        {
            var transform = vent.transform;
            var position = transform.position;
            poss.Add(new Vector3(position.x, position.y, position.z - 50));
        }

        return poss;
    }
}