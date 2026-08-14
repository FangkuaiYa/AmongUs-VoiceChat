using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using UnityEngine;
using Interstellar.Voice;

namespace Interstellar;

public class PublicLobbyWindow : MonoBehaviour
{
    public PublicLobbyWindow(System.IntPtr ptr) : base(ptr) { }

    public static PublicLobbyWindow? Instance { get; private set; }

    public bool ShowWindow { get; private set; }
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;

    public void Toggle()
    {
        ShowWindow = !ShowWindow;
        if (ShowWindow) StartLobbyConnection();
        else StopLobbyConnection();
    }

    private Vector2 _scrollPosition;
    private GUIStyle? _titleStyle, _lobbyBtnStyle;
    private bool _stylesBuilt;
    private string _status = "";
    private const KeyCode ToggleKey = KeyCode.F2;

    // Draggable window
    private Rect _winRect;
    private bool _winInitialized;
    private bool _isDragging;
    private Vector2 _dragOffset;

    void Awake() { Instance = this; }
    void OnDestroy() { StopLobbyConnection(); if (Instance == this) Instance = null; }

    void Update()
    {
        if (Input.GetKeyDown(ToggleKey)) Toggle();
        if (ShowWindow && Input.GetKeyDown(KeyCode.Escape)) { ShowWindow = false; StopLobbyConnection(); }
    }

    // ── Lobby WebSocket (separate from voice connection) ──────────

    async void StartLobbyConnection()
    {
        StopLobbyConnection();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _status = "Connecting...";
        PublicLobbyManager.IsLoading = true;
        PublicLobbyManager.LobbyMap.Clear();

        // Safety timeout: stop loading after 10s if no lobbies arrive
        _ = Task.Delay(10000, token).ContinueWith(_ =>
        {
            if (!token.IsCancellationRequested) { PublicLobbyManager.IsLoading = false; if (string.IsNullOrEmpty(_status)) _status = "No lobbies received."; }
        }, TaskScheduler.Default);

        try
        {
            var url = VoiceConfig.GetActiveServerURL();
            if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http"))
                throw new Exception("Invalid server URL: " + url);
            var u = new Uri(url);
            // Always use EIO=3, matching the official BetterCrewLink client which
            // works on both the Cloudflare official server and EdgeOne servers.
            var wsUrl = (u.Scheme == "https" ? "wss" : "ws") + "://" + u.Host + (u.IsDefaultPort ? "" : ":" + u.Port) + "/socket.io/?EIO=3&transport=websocket";

            _ws = new ClientWebSocket();
            await _ws.ConnectAsync(new Uri(wsUrl), token);
            _status = "Connected, waiting...";

            var buf = new byte[8192];
            var sb = new StringBuilder();
            bool gotOpen = false;

            while (_ws.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(buf, token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, result.Count));
                if (!result.EndOfMessage) continue;

                var data = sb.ToString(); sb.Clear();
                if (string.IsNullOrEmpty(data)) continue;

                switch (data[0])
                {
                    case '0': // engine.io open
                        if (!gotOpen)
                        {
                            gotOpen = true;
                            // NOTE: Do NOT actively send "40". The server sends it itself;
                            // actively sending it makes Cloudflare-hosted v4 servers close
                            // the connection. The server's "40" is handled below.
                        }
                        break;
                    case '2': // ping
                        SendRaw("3");
                        break;
                    case '4': // socket.io message
                        var payload = data.Substring(1);
                        if (payload.StartsWith("0")) // socket.io connected
                        {
                            SendRaw("42[\"lobbybrowser\",true]");
                            _status = "Loading lobbies...";
                        }
                        else if (payload.StartsWith("2")) // event
                        {
                            try
                            {
                                using var d = JsonDocument.Parse(payload.Substring(1));
                                var arr = d.RootElement;
                                if (arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
                                {
                                    var ev = arr[0].GetString();
                                    if (ev == "new_lobbies") { PublicLobbyManager.OnNewLobbies(arr[1].GetRawText()); _status = ""; PublicLobbyManager.IsLoading = false; }
                                    else if (ev == "update_lobby") PublicLobbyManager.OnUpdateLobby(arr[1].GetRawText());
                                    else if (ev == "remove_lobby" && arr.GetArrayLength() > 1) PublicLobbyManager.OnRemoveLobby(arr[1].GetInt32());
                                }
                            }
                            catch { }
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status = "Error: " + ex.Message; PublicLobbyManager.IsLoading = false; }

        // Final safety: ensure loading is off
        if (PublicLobbyManager.IsLoading && _status != "Loading lobbies...")
            PublicLobbyManager.IsLoading = false;
    }

    async void SendRaw(string data)
    {
        if (_ws?.State == WebSocketState.Open)
            try { await _ws.SendAsync(Encoding.UTF8.GetBytes(data), WebSocketMessageType.Text, true, CancellationToken.None); } catch { }
    }

    void StopLobbyConnection()
    {
        _cts?.Cancel();
        try { _ws?.Dispose(); } catch { }
        _ws = null;
        PublicLobbyManager.IsLoading = false;
        PublicLobbyManager.StopWatching();
    }

    // ── GUI ────────────────────────────────────────────────────

    void OnGUI()
    {
        if (!ShowWindow) return;
        BuildStyles();

        float winW = 480f;
        float winH = 580f;

        if (!_winInitialized)
        {
            _winRect = new Rect((Screen.width - winW) / 2f, (Screen.height - winH) / 2f, winW, winH);
            _winInitialized = true;
        }
        _winRect.width = winW;
        _winRect.height = winH;

        // ── Drag handling ──
        const float titleH = 28f;
        var titleRect = new Rect(_winRect.x, _winRect.y, _winRect.width, titleH);
        var ev = Event.current;
        if (ev.type == EventType.MouseDown && titleRect.Contains(ev.mousePosition))
        {
            _isDragging = true;
            _dragOffset = ev.mousePosition - new Vector2(_winRect.x, _winRect.y);
            ev.Use();
        }
        if (ev.type == EventType.MouseUp) _isDragging = false;
        if (_isDragging && ev.type == EventType.MouseDrag)
        {
            _winRect.x = ev.mousePosition.x - _dragOffset.x;
            _winRect.y = ev.mousePosition.y - _dragOffset.y;
            ev.Use();
        }
        _winRect.x = Mathf.Clamp(_winRect.x, -_winRect.width + 60f, Screen.width - 60f);
        _winRect.y = Mathf.Clamp(_winRect.y, -titleH + 8f, Screen.height - 30f);

        // ── Background ──
        GUI.color = new Color(0.04f, 0.05f, 0.08f, 0.95f);
        GUI.Box(new Rect(_winRect.x - 4, _winRect.y - 4, _winRect.width + 8, _winRect.height + 8), "");
        GUI.color = new Color(0.06f, 0.08f, 0.12f, 0.98f);
        GUI.Box(_winRect, "");
        GUI.color = Color.white;

        // ── Title bar (drag handle) ──
        GUI.Label(titleRect, "  ≡  <b>Public Lobbies</b>", _titleStyle);

        GUILayout.BeginArea(new Rect(_winRect.x + 10, _winRect.y + titleH + 2, _winRect.width - 20, _winRect.height - titleH - 12));
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("↻ Refresh", GUILayout.Width(90f))) { StopLobbyConnection(); StartLobbyConnection(); }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("X", GUILayout.Width(28f), GUILayout.Height(20f))) { ShowWindow = false; StopLobbyConnection(); }
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            if (!string.IsNullOrEmpty(_status)) { GUILayout.Label(_status); }

            if (PublicLobbyManager.IsLoading) { GUILayout.Label("Loading..."); GUILayout.EndArea(); return; }

            var lobbies = PublicLobbyManager.CachedLobbies;
            if (lobbies.Count == 0) { GUILayout.Label("No public lobbies available.", new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, normal = new GUIStyleState { textColor = Color.gray } }); GUILayout.EndArea(); return; }

            _scrollPosition = GUILayout.BeginScrollView(_scrollPosition, GUILayout.Height(_winRect.height - titleH - 48f));
            foreach (var lobby in lobbies) { RenderLobbyEntry(lobby); GUILayout.Space(4f); }
            GUILayout.EndScrollView();
        }
        GUILayout.EndArea();
    }

    void RenderLobbyEntry(PublicLobbyManager.LobbyInfo lobby)
    {
        string gameState = PublicLobbyManager.GetGameStateName(lobby.gameState);
        string players = lobby.current_players + "/" + lobby.max_players;
        string title = string.IsNullOrEmpty(lobby.title) ? "(no title)" : lobby.title;
        string host = lobby.host.Length > 15 ? lobby.host[..15] : lobby.host;

        GUILayout.BeginVertical(_lobbyBtnStyle);
        GUILayout.BeginHorizontal();
        var stateColor = lobby.gameState switch { 1 => Color.green, 2 => Color.cyan, 3 => Color.yellow, _ => Color.gray };
        var oc = GUI.color; GUI.color = stateColor;
        GUILayout.Label("●", GUILayout.Width(16f)); GUI.color = oc;
        GUILayout.Label("<b>" + title + "</b>", GUILayout.Width(180f));
        GUILayout.Label("by " + host, new GUIStyle(GUI.skin.label) { normal = new GUIStyleState { textColor = new Color(0.6f, 0.6f, 0.6f) } }, GUILayout.Width(100f));
        GUILayout.FlexibleSpace();
        string modsStr = string.IsNullOrEmpty(lobby.mods) ? "Vanilla" : lobby.mods;
        GUILayout.Label(modsStr, new GUIStyle(GUI.skin.label) { fontSize = 10, normal = new GUIStyleState { textColor = new Color(0.4f, 0.8f, 1f) } }, GUILayout.Width(70f));
        GUILayout.Label(players + " | " + gameState, GUILayout.Width(110f));
        if (lobby.gameState == 1 && GUILayout.Button("Join", GUILayout.Width(50f), GUILayout.Height(22f)))
            _status = "Join not implemented yet";
        GUILayout.EndHorizontal();
        GUILayout.EndVertical();
    }

    void BuildStyles()
    {
        if (_stylesBuilt) return; _stylesBuilt = true;
        _titleStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 16, normal = new GUIStyleState { textColor = new Color(0.55f, 0.70f, 0.90f) } };
        _lobbyBtnStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset { left = 8, right = 8, top = 6, bottom = 6 }, normal = new GUIStyleState { background = MakeTex(1, 1, new Color(0.15f, 0.18f, 0.22f)) } };
    }

    static Texture2D MakeTex(int w, int h, Color c) { var t = new Texture2D(w, h); for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) t.SetPixel(x, y, c); t.Apply(); return t; }
}