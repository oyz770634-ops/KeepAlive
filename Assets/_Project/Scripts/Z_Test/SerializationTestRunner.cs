using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Network.Protocol;
using Network.Server;
using Network.Transport;
using Network.Shared;
using Network.Shared.Messages;
using KeepAlive.Core;

/// <summary>
/// 序列化层端到端验证。
/// 测试链路：
///   客户端 Send<LoginReq> → 服务端 RegisterHandler<LoginReq>
///   → 服务端 SendToSession<LoginRsp> → 客户端 RegisterHandler<LoginRsp>
///   → 客户端 Send<ChatMsg> → 服务端 Broadcast<ChatMsg> → 所有客户端收到
/// 全流程用泛型 API，验证 protobuf-net 序列化层是否工作。
/// </summary>
public class SerializationTestRunner : MonoBehaviour
{
    [Header("协议选择")]
    public ServerProtocol protocol = ServerProtocol.Kcp;

    [Header("连接参数")]
    public int port = 8888;
    public string serverHost = "127.0.0.1";
    public int clientCount = 3;

    [Header("自动测试")]
    public bool autoRunOnStart = true;

    private GameServerManager _serverManager;
    private ServerNetworkService _serverNetwork;
    private readonly List<NetworkService> _clients = new List<NetworkService>();

    // 用来记录"哪个客户端已经收到 LoginRsp / ChatMsg"，验证所有客户端都能收到
    private readonly HashSet<int> _loginRspReceived = new HashSet<int>();
    private readonly HashSet<int> _chatReceived = new HashSet<int>();

    void Start()
    {
        if (MainThreadDispatcher.Instance != null) 
        if (autoRunOnStart) StartCoroutine(RunFullTest());
    }

    void Update()
    {
        // 驱动客户端（TCP 空转，KCP 必需）
        foreach (var c in _clients) c?.Update();
    }

    IEnumerator RunFullTest()
    {
        Debug.Log("========== [序列化层验证] START ==========");
        Debug.Log($"[Test] Protocol = {protocol}");

        // ---------- Phase 0: AOT 预热 ----------
        // 即使是 Editor 平台也跑一遍，确保预热方法本身没问题
        Debug.Log("[Phase 0] AOT 预热");
        ProtobufNetSerializer.Prepare<LoginReq>();
        ProtobufNetSerializer.Prepare<LoginRsp>();
        ProtobufNetSerializer.Prepare<ChatMsg>();
        Debug.Log("[Phase 0] ✅ 预热完成");

        // ---------- Phase 0.5: 本地序列化往返 ----------
        // 最基础的验证：不涉及网络，纯序列化器是否工作
        Debug.Log("[Phase 0.5] 本地序列化往返测试");
        var serializer = new ProtobufNetSerializer();
        var testReq = new LoginReq { Name = "RoundTrip", Password = "abc123" };
        byte[] bytes = serializer.Serialize(testReq);
        var back = serializer.Deserialize<LoginReq>(bytes);
        if (back == null || back.Name != "RoundTrip" || back.Password != "abc123")
        {
            Debug.LogError($"[Phase 0.5] ❌ 序列化往返失败：Name={back?.Name}, Password={back?.Password}");
            yield break;
        }
        Debug.Log($"[Phase 0.5] ✅ 往返成功（bytes={bytes.Length}, Name={back.Name}）");

        // ---------- Phase 1: 启动服务端 ----------
        Debug.Log("[Phase 1] 启动服务端");
        var serverGo = new GameObject("[TestServer]");
        _serverManager = serverGo.AddComponent<GameServerManager>();
        _serverManager.StartServer(protocol, port, new KcpConfig());

        _serverNetwork = new ServerNetworkService(_serverManager);

        // 注册服务端业务 handler（泛型版）
        _serverNetwork.RegisterHandler<LoginReq>((int)MsgId.C2S_Login, OnServerLogin);
        _serverNetwork.RegisterHandler<ChatMsg>((int)MsgId.C2S_Chat, OnServerChat);

        _serverManager.OnSessionConnected += s =>
            Debug.Log($"[Server] + Session {s.PlayerId}");
        _serverManager.OnSessionDisconnected += s =>
            Debug.Log($"[Server] - Session {s.PlayerId}");

        Debug.Log("[Phase 1] ✅ 服务端启动");
        yield return new WaitForSeconds(0.5f);

        // ---------- Phase 2: 创建客户端 ----------
        Debug.Log($"[Phase 2] 创建 {clientCount} 个客户端");
        for (int i = 0; i < clientCount; i++) CreateClient(i);
        yield return new WaitForSeconds(3f);

        // 检查所有客户端是否连上
        int connectedCount = 0;
        foreach (var c in _clients) if (c.State == ChannelState.Connected) connectedCount++;
        Debug.Log($"[Phase 2] 已连接 {connectedCount}/{clientCount}");
        if (connectedCount < clientCount)
        {
            Debug.LogError("[Phase 2] ❌ 不是所有客户端都连上了");
            yield break;
        }
        Debug.Log("[Phase 2] ✅ 所有客户端已连接");

        // ---------- Phase 3: 所有客户端发 LoginReq ----------
        Debug.Log("[Phase 3] 所有客户端 Send<LoginReq>");
        for (int i = 0; i < _clients.Count; i++)
        {
            var req = new LoginReq { Name = $"Client{i}", Password = "pwd" };
            _clients[i].Send((int)MsgId.C2S_Login, req);
            Debug.Log($"[Client{i}] → Send<LoginReq> Name={req.Name}");
        }
        yield return new WaitForSeconds(1.5f);

        // 检查所有客户端是否收到 LoginRsp
        Debug.Log($"[Phase 3] LoginRsp 收到 {_loginRspReceived.Count}/{clientCount}");
        if (_loginRspReceived.Count < clientCount)
        {
            Debug.LogError("[Phase 3] ❌ 不是所有客户端都收到 LoginRsp");
            yield break;
        }
        Debug.Log("[Phase 3] ✅ 所有客户端收到 LoginRsp");

        // ---------- Phase 4: 每个客户端发 ChatMsg，服务端广播 ----------
        Debug.Log("[Phase 4] 客户端逐个 Send<ChatMsg>，服务端 Broadcast<ChatMsg>");
        _chatReceived.Clear();
        for (int i = 0; i < _clients.Count; i++)
        {
            var chat = new ChatMsg { Text = $"Hello from Client{i}" };
            _clients[i].Send((int)MsgId.C2S_Chat, chat);
            Debug.Log($"[Client{i}] → Send<ChatMsg> Text={chat.Text}");
            yield return new WaitForSeconds(0.3f);
        }
        yield return new WaitForSeconds(1.5f);

        Debug.Log($"[Phase 4] ChatMsg 收到 {_chatReceived.Count}/{clientCount}");
        if (_chatReceived.Count < clientCount)
        {
            Debug.LogError("[Phase 4] ❌ 不是所有客户端都收到 ChatMsg");
            yield break;
        }
        Debug.Log("[Phase 4] ✅ 所有客户端收到 ChatMsg");

        // ---------- Phase 5: 清理 ----------
        Debug.Log("[Phase 5] 清理");
        foreach (var c in _clients) c.Close();
        _clients.Clear();
        _serverNetwork.Dispose();
        _serverManager.StopServer();
        UnityEngine.Object.Destroy(_serverManager.gameObject);
        _serverManager = null;

        Debug.Log("========== [序列化层验证] ✅ ALL PASSED ==========");
    }

    // ==================== 服务端 handler ====================

    private void OnServerLogin(ServerSession session, LoginReq req)
    {
        Debug.Log($"[Server] ← C2S_Login from {session.PlayerId}: Name={req.Name}, Password={req.Password}");

        // 服务端回 LoginRsp（泛型版）
        var rsp = new LoginRsp
        {
            PlayerId = session.PlayerId,
            Welcome = $"Welcome {req.Name}!"
        };
        _serverNetwork.SendToSession(session, (int)MsgId.S2C_Login, rsp);
        Debug.Log($"[Server] → S2C_Login to {session.PlayerId}: PlayerId={rsp.PlayerId}");
    }

    private void OnServerChat(ServerSession session, ChatMsg chat)
    {
        Debug.Log($"[Server] ← C2S_Chat from {session.PlayerId}: Text={chat.Text}");

        // 服务端广播（泛型版）
        var broadcast = new ChatMsg { Text = $"Player{session.PlayerId}: {chat.Text}" };
        _serverNetwork.Broadcast((int)MsgId.S2C_Chat, broadcast);
        Debug.Log($"[Server] → Broadcast S2C_Chat: {broadcast.Text}");
    }

    // ==================== 客户端创建 ====================

    private void CreateClient(int index)
    {
        var config = new ChannelConfig { Host = serverHost, Port = port };
        var kcpConfig = new KcpConfig();

        Func<IClientChannel> factory = protocol switch
        {
            ServerProtocol.Kcp => () => new KcpClient(kcpConfig),
            _ => (Func<IClientChannel>)(() => new TcpNetClient(config)),
        };

        var mgr = new ConnectionManager(config, factory);
        var service = new NetworkService(mgr);

        // 注册客户端 handler（泛型版）
        service.RegisterHandler<LoginRsp>((int)MsgId.S2C_Login, rsp =>
        {
            Debug.Log($"[Client{index}] ← S2C_Login: PlayerId={rsp.PlayerId}, Welcome={rsp.Welcome}");
            _loginRspReceived.Add(index);
        });

        service.RegisterHandler<ChatMsg>((int)MsgId.S2C_Chat, chat =>
        {
            Debug.Log($"[Client{index}] ← S2C_Chat: {chat.Text}");
            _chatReceived.Add(index);
        });

        service.OnConnected += () => Debug.Log($"[Client{index}] OnConnected");
        service.OnConnectFailed += (e, r) => Debug.LogWarning($"[Client{index}] OnConnectFailed: {e}, {r}");
        service.OnDisconnected += e => Debug.LogWarning($"[Client{index}] OnDisconnected: {e}");

        service.Connect(serverHost, port);
        _clients.Add(service);
    }
}