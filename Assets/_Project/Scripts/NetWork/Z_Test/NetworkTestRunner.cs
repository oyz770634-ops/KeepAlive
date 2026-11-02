using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Network.Protocol;
using Network.Server;
using Network.Transport;
using Network.Shared;
/// <summary>
/// 网络模块测试入口。挂到任意 GameObject 上即可。
/// 
/// 快捷键（手动测试）：
///   S = 启动服务器
///   C = 创建 N 个客户端
///   1/2/3 = 对应客户端发 Chat
///   L = 所有客户端发 Login
///   B = 服务端广播
///   K = 服务端踢第一个玩家
///   D = 强制断开客户端0（模拟网络断开，触发重连）
///   P = 暂停/恢复客户端0的 Update（模拟心跳停止，触发服务端超时踢人）
///   Q = 全部关闭
///   T = 运行完整自动测试
/// </summary>
public class NetworkTestRunner : MonoBehaviour
{
    [Header("Server")]
    public ServerProtocol protocol = ServerProtocol.Kcp;
    public int port = 8888;

    [Header("Client")]
    public int clientCount = 3;
    public string serverHost = "127.0.0.1";

    [Header("Auto Test")]
    public bool autoRunFullTest = false;

    private GameServerManager _serverManager;
    private ServerNetworkService _serverNetwork;
    private readonly List<NetworkService> _clients = new List<NetworkService>();
    private readonly List<ConnectionManager> _managers = new List<ConnectionManager>();
    private readonly HashSet<int> _pausedClients = new HashSet<int>();
    private readonly Dictionary<int, ServerSession> _sessionByClientIndex = new Dictionary<int, ServerSession>();

    void Start()
    {
        Debug.Log($"[Test] Ready. Protocol={protocol}. Press S to start server, C to create {clientCount} clients.");
        Debug.Log("[Test] Keys: S=Server C=Clients L=Login 1/2/3=Chat B=Broadcast K=Kick D=Disconnect P=Pause Q=Close T=FullTest");

        if (autoRunFullTest) StartCoroutine(FullTestCoroutine());
    }

    void Update()
    {
        // 驱动客户端（被暂停的跳过，模拟心跳/Update 停止）
        for (int i = 0; i < _clients.Count; i++)
        {
            if (_pausedClients.Contains(i)) continue;
            _clients[i].Update();
        }

        if (Input.GetKeyDown(KeyCode.S)) StartServer();
        if (Input.GetKeyDown(KeyCode.C)) CreateNClients();
        if (Input.GetKeyDown(KeyCode.L)) AllClientsLogin();
        if (Input.GetKeyDown(KeyCode.Alpha1)) ClientSendChat(0);
        if (Input.GetKeyDown(KeyCode.Alpha2)) ClientSendChat(1);
        if (Input.GetKeyDown(KeyCode.Alpha3)) ClientSendChat(2);
        if (Input.GetKeyDown(KeyCode.B)) ServerBroadcast();
        if (Input.GetKeyDown(KeyCode.K)) KickFirstPlayer();
        if (Input.GetKeyDown(KeyCode.D)) DisconnectFirstClient();
        if (Input.GetKeyDown(KeyCode.P)) TogglePauseClient(0);
        if (Input.GetKeyDown(KeyCode.Q)) CloseAll();
        if (Input.GetKeyDown(KeyCode.T)) StartCoroutine(FullTestCoroutine());
    }

    // ===================== Server =====================

    void StartServer()
    {
        if (_serverManager != null) { Debug.LogWarning("[Test] Server already exists"); return; }

        var go = new GameObject("[TestServer]");
        _serverManager = go.AddComponent<GameServerManager>();
        _serverManager.StartServer(protocol, port, new KcpConfig());

        _serverNetwork = new ServerNetworkService(_serverManager);
        _serverNetwork.RegisterHandler((int)MsgId.C2S_Login, HandleServerLogin);
        _serverNetwork.RegisterHandler((int)MsgId.C2S_Chat, HandleServerChat);

        _serverManager.OnSessionConnected += s =>
            Debug.Log($"[Server] + Session {s.PlayerId}, total={_serverManager.SessionCount}");
        _serverManager.OnSessionDisconnected += s =>
            Debug.Log($"[Server] - Session {s.PlayerId}, total={_serverManager.SessionCount}");

        Debug.Log($"[Test] Server started: protocol={protocol}, port={port}");
    }

    void HandleServerLogin(ServerSession session, byte[] body)
    {
        string name = Encoding.UTF8.GetString(body);
        Debug.Log($"[Server] Login: PlayerId={session.PlayerId}, name={name}");

        if (name.StartsWith("Client") && int.TryParse(name.Substring(6), out int idx))
            _sessionByClientIndex[idx] = session;

        var resp = Encoding.UTF8.GetBytes($"Welcome {name}, your PlayerId={session.PlayerId}");
        _serverNetwork.SendToSession(session, (int)MsgId.S2C_Login, resp);
    }

    void HandleServerChat(ServerSession session, byte[] body)
    {
        string text = Encoding.UTF8.GetString(body);
        Debug.Log($"[Server] Chat from PlayerId={session.PlayerId}: {text}");
        var broadcast = Encoding.UTF8.GetBytes($"Player{session.PlayerId}: {text}");
        _serverNetwork.Broadcast((int)MsgId.S2C_Chat, broadcast);
    }

    void ServerBroadcast()
    {
        if (_serverNetwork == null) return;
        var body = Encoding.UTF8.GetBytes($"Server broadcast @ {Time.time:F1}s");
        _serverNetwork.Broadcast((int)MsgId.S2C_Chat, body);
        Debug.Log("[Server] Broadcast sent");
    }

    void KickFirstPlayer()
    {
        if (_serverManager == null) return;
        var sessions = _serverManager.GetAllSessions();
        if (sessions.Count == 0) { Debug.LogWarning("[Test] No sessions"); return; }
        Debug.Log($"[Server] Kick PlayerId={sessions[0].PlayerId}");
        _serverManager.KickPlayer(sessions[0].PlayerId);
    }

    // ===================== Clients =====================

    void CreateNClients()
    {
        for (int i = 0; i < clientCount; i++) CreateClient(i);
    }

    void CreateClient(int index)
    {
        if (index < _clients.Count) return;

        var config = new ChannelConfig { Host = serverHost, Port = port };
        var kcpConfig = new KcpConfig();

        Func<IClientChannel> factory = protocol switch
        {
            ServerProtocol.Kcp => () => new KcpClient(kcpConfig),
            _ => (Func<IClientChannel>)(() => new TcpNetClient(config)),
        };

        var mgr = new ConnectionManager(config, factory);
        var service = new NetworkService(mgr);

        int idx = index;
        service.RegisterHandler((int)MsgId.S2C_Login, body =>
            Debug.Log($"[Client{idx}] S2C_Login: {Encoding.UTF8.GetString(body)}"));
        service.RegisterHandler((int)MsgId.S2C_Chat, body =>
            Debug.Log($"[Client{idx}] S2C_Chat: {Encoding.UTF8.GetString(body)}"));

        service.OnConnected += () => Debug.Log($"[Client{idx}] OnConnected");
        service.OnConnectFailed += (e, r) => Debug.LogWarning($"[Client{idx}] OnConnectFailed: {e}, {r}");
        service.OnDisconnected += e => Debug.LogWarning($"[Client{idx}] OnDisconnected: {e}");

        mgr.OnReconnecting += (n, d) => Debug.LogWarning($"[Client{idx}] Reconnecting#{n}, {d:F1}s");
        mgr.OnReconnected += () => Debug.Log($"[Client{idx}] Reconnected");
        mgr.OnReconnectFailed += () => Debug.LogError($"[Client{idx}] ReconnectFailed");

        service.Connect(serverHost, port);

        _clients.Add(service);
        _managers.Add(mgr);
        Debug.Log($"[Test] Client{index} created → {serverHost}:{port}");
    }

    void AllClientsLogin()
    {
        for (int i = 0; i < _clients.Count; i++)
        {
            if (_pausedClients.Contains(i)) continue;
            if (_clients[i].State != ChannelState.Connected) continue;
            var body = Encoding.UTF8.GetBytes($"Client{i}");
            _clients[i].Send((int)MsgId.C2S_Login, body);
            Debug.Log($"[Client{i}] Sent C2S_Login");
        }
    }

    void ClientSendChat(int index)
    {
        if (index >= _clients.Count) { Debug.LogWarning($"[Test] Client{index} not exists"); return; }
        if (_pausedClients.Contains(index)) { Debug.LogWarning($"[Test] Client{index} paused"); return; }

        var s = _clients[index];
        if (s.State != ChannelState.Connected)
        {
            Debug.LogWarning($"[Test] Client{index} not connected (state={s.State})");
            return;
        }
        var body = Encoding.UTF8.GetBytes($"Hello from Client{index} @ {Time.time:F1}s");
        s.Send((int)MsgId.C2S_Chat, body);
        Debug.Log($"[Client{index}] Sent C2S_Chat");
    }

    void DisconnectFirstClient()
    {
        if (_clients.Count == 0) return;
        Debug.Log("[Test] ForceDisconnect Client0 (trigger reconnect)");
        _managers[0].ForceDisconnect(ChannelError.TimedOut);
    }

    void TogglePauseClient(int index)
    {
        if (index >= _clients.Count) return;
        if (_pausedClients.Contains(index))
        {
            _pausedClients.Remove(index);
            Debug.Log($"[Test] Client{index} RESUMED");
        }
        else
        {
            _pausedClients.Add(index);
            Debug.Log($"[Test] Client{index} PAUSED (heartbeat stops → server kicks after 10s)");
        }
    }

    void CloseAll()
    {
        foreach (var c in _clients) c.Close();
        _clients.Clear();
        _managers.Clear();
        _sessionByClientIndex.Clear();
        _pausedClients.Clear();

        if (_serverNetwork != null) { _serverNetwork.Dispose(); _serverNetwork = null; }
        if (_serverManager != null)
        {
            _serverManager.StopServer();
            Destroy(_serverManager.gameObject);
            _serverManager = null;
        }
        Debug.Log("[Test] All closed");
    }

    // ===================== Full Auto Test =====================

    IEnumerator FullTestCoroutine()
    {
        Debug.Log("========== [FullTest] START ==========");

        StartServer();
        yield return new WaitForSeconds(0.5f);

        Debug.Log("[FullTest] Phase 1: Create clients");
        CreateNClients();
        yield return new WaitForSeconds(3f);

        Debug.Log("[FullTest] Phase 2: Clients login");
        AllClientsLogin();
        yield return new WaitForSeconds(1f);

        Debug.Log("[FullTest] Phase 3: Clients chat");
        for (int i = 0; i < _clients.Count; i++) ClientSendChat(i);
        yield return new WaitForSeconds(1f);

        Debug.Log("[FullTest] Phase 4: Server broadcast");
        ServerBroadcast();
        yield return new WaitForSeconds(1f);

        Debug.Log("[FullTest] Phase 5: Kick first player");
        KickFirstPlayer();
        yield return new WaitForSeconds(2f);

        Debug.Log("[FullTest] Phase 6: Force disconnect Client0 → expect reconnect");
        if (_clients.Count > 0)
        {
            DisconnectFirstClient();
            yield return new WaitForSeconds(8f);
        }

        Debug.Log("[FullTest] Phase 7: Pause Client0 → expect server timeout kick after 10s");
        if (_clients.Count > 0)
        {
            TogglePauseClient(0);
            yield return new WaitForSeconds(12f);
        }

        Debug.Log("========== [FullTest] END ==========");
    }
}