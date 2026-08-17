#pragma warning disable CS8602, CS8603, CS8618
using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Interstellar.Voice;
using Object = UnityEngine.Object;

namespace Interstellar;

/// <summary>
/// Public Lobby browser - native uGUI implementation (same look as the settings window).
/// WebSocket lobby-list logic is unchanged; only the rendering was converted from IMGUI to uGUI.
/// The lobby list is rebuilt whenever the underlying data changes (polled in Update).
/// </summary>
public class PublicLobbyWindow : MonoBehaviour
{
    public PublicLobbyWindow(System.IntPtr ptr) : base(ptr) { }

    public static PublicLobbyWindow? Instance { get; private set; }
    public bool ShowWindow { get; private set; }

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private string _status = "";
    private const KeyCode ToggleKey = KeyCode.F2;

    // Layout constants
    private const float WinW = 860f;
    private const float WinH = 780f;
    private const float TitleBarH = 60f;
    private const float BottomH = 40f;
    private const float RowH = 62f;
    private const float ContentW = WinW - 96f;
    private const float ContentRight = ContentW / 2f - 40f;

    // UI refs
    private GameObject _uiRoot;
    private RectTransform _winRt;
    private Canvas _canvas;
    private ScrollRect _scroll;
    private RectTransform _content;
    private TextMeshProUGUI _statusTmp;

    // State
    private bool _built;
    private bool _dragging;
    private Vector2 _dragOffset;
    private int _lastSig = -1;
    private int _ackSeq;
    private int _pendingCopyAck = -1;
    private int _pendingCopyLobbyId = -1;
    private int _lastCopiedId = -1;

    void Awake() { Instance = this; }

    void OnDestroy()
    {
        StopLobbyConnection();
        if (Instance == this) Instance = null;
        if (_uiRoot != null) Object.Destroy(_uiRoot);
    }

    void Update()
    {
        if (Input.GetKeyDown(ToggleKey)) Toggle();
        if (ShowWindow && Input.GetKeyDown(KeyCode.Escape)) Close();
        RefreshIfChanged();
    }

    public void Toggle()
    {
        if (ShowWindow) Close(); else Open();
    }

    void Open()
    {
        _dragging = false;
        VCUiKit.AnyWindowDragging = false;
        if (!_built) BuildUI();
        if (_uiRoot == null) return;

        // avoid stacking two windows on top of each other
        try { VoiceSettingsWindow.Instance?.Close(); } catch { }

        _uiRoot.SetActive(true);
        ShowWindow = true;
        StartLobbyConnection();
        RebuildLobbyList();
    }

    public void Close()
    {
        ShowWindow = false;
        _dragging = false;
        VCUiKit.AnyWindowDragging = false;
        if (_uiRoot != null) _uiRoot.SetActive(false);
        StopLobbyConnection();
    }

    // ── Lobby WebSocket (unchanged from IMGUI version) ─────────

    async void StartLobbyConnection()
    {
        StopLobbyConnection();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _status = "Connecting...";
        PublicLobbyManager.IsLoading = true;
        PublicLobbyManager.LobbyMap.Clear();

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
                        else if (payload.StartsWith("3")) // ack (join_lobby response)
                        {
                            InterstellarPlugin.Logger?.LogInfo($"[VC] Ack raw: {payload}");
                            var rest = payload.Substring(1);
                            int bracket = rest.IndexOf('[');
                            if (bracket > 0 && int.TryParse(rest.Substring(0, bracket), out int ackId))
                                HandleLobbyAck(ackId, rest.Substring(bracket));
                            else
                                InterstellarPlugin.Logger?.LogWarning($"[VC] Ack parse failed: {payload}");
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status = "Error: " + ex.Message; PublicLobbyManager.IsLoading = false; }

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

    // ── Window frame ───────────────────────────────────────────

    private void BuildUI()
    {
        if (_uiRoot != null) Object.Destroy(_uiRoot);
        _canvas = VCUiKit.EnsureCanvas();

        _uiRoot = new GameObject("VCPublicLobbyUI");
        _uiRoot.transform.SetParent(_canvas.transform, false);
        var rootRt = _uiRoot.AddComponent<RectTransform>();
        rootRt.anchorMin = Vector2.zero;
        rootRt.anchorMax = Vector2.one;
        rootRt.offsetMin = Vector2.zero;
        rootRt.offsetMax = Vector2.zero;
        _uiRoot.SetActive(false);

        // dim (click outside to close)
        var dim = VCUiKit.CreateImage(_uiRoot.transform, "Dim", Vector2.zero, Vector2.zero, VCUiKit.PixelSprite, new Color(0f, 0f, 0f, 0.42f));
        var dimRt = (RectTransform)dim.transform;
        dimRt.anchorMin = Vector2.zero;
        dimRt.anchorMax = Vector2.one;
        dimRt.offsetMin = Vector2.zero;
        dimRt.offsetMax = Vector2.zero;
        var dimBtn = dim.gameObject.AddComponent<Button>();
        dimBtn.transition = Selectable.Transition.None;
        dimBtn.onClick.AddListener((Action)(() => Close()));

        // window panel (sharp corners + white outline, dragged together)
        _winRt = VCUiKit.CreatePanel(_uiRoot.transform, "Window", new Vector2(WinW, WinH),
            new Color(0.88f, 0.94f, 1f, 1f), new Color(0.07f, 0.10f, 0.16f, 0.97f), 6f);
        _winRt.anchorMin = _winRt.anchorMax = new Vector2(0.5f, 0.5f);
        _winRt.anchoredPosition = Vector2.zero;

        // title
        var title = VCUiKit.CreateText(_winRt, "Title", Get("vc.lobby.title", "Public Lobbies"),
            Vector2.zero, new Vector2(400f, 40f), 26f, new Color(0.92f, 0.95f, 1f, 1f),
            FontStyles.Bold, TextAlignmentOptions.Left);
        var titleRt = (RectTransform)title.transform;
        titleRt.anchorMin = new Vector2(0f, 0.5f);
        titleRt.anchorMax = new Vector2(0f, 0.5f);
        titleRt.pivot = new Vector2(0f, 0.5f);
        titleRt.anchoredPosition = new Vector2(30f, WinH / 2f - TitleBarH / 2f);

        // refresh button
        var refresh = VCUiKit.CreateButton(_winRt, Get("vc.lobby.refresh", "Refresh"), Vector2.zero, new Vector2(120f, 42f),
            new Color(0.30f, 0.36f, 0.48f, 1f), () => { StopLobbyConnection(); StartLobbyConnection(); }, 20f);
        var refreshRt = (RectTransform)refresh.transform;
        refreshRt.anchorMin = refreshRt.anchorMax = new Vector2(1f, 0.5f);
        refreshRt.anchoredPosition = new Vector2(-100f, WinH / 2f - TitleBarH / 2f);

        // close button
        var close = VCUiKit.CreateButton(_winRt, Get("vc.lobby.close", "X"), Vector2.zero, new Vector2(42f, 42f),
            new Color(0.58f, 0.22f, 0.24f, 1f), () => Close(), 22f);
        var closeRt = (RectTransform)close.transform;
        closeRt.anchorMin = closeRt.anchorMax = new Vector2(1f, 0.5f);
        closeRt.anchoredPosition = new Vector2(-34f, WinH / 2f - TitleBarH / 2f);

        // status text (below title bar)
        _statusTmp = VCUiKit.CreateText(_winRt, "Status", "", Vector2.zero, new Vector2(ContentW, 30f),
            19f, new Color(0.75f, 0.80f, 0.90f, 1f), FontStyles.Normal, TextAlignmentOptions.Left);
        var statusRt = (RectTransform)_statusTmp.transform;
        statusRt.anchorMin = statusRt.anchorMax = new Vector2(0f, 1f);
        statusRt.pivot = new Vector2(0f, 1f);
        statusRt.anchoredPosition = new Vector2(30f, -(TitleBarH + 8f));
        statusRt.sizeDelta = new Vector2(ContentW, 30f);

        BuildScrollArea(_winRt);
        _built = true;
    }

    private void BuildScrollArea(Transform win)
    {
        float topY = WinH / 2f - TitleBarH - 48f;
        float bottomY = -WinH / 2f + BottomH + 10f;
        float viewH = topY - bottomY;

        var viewport = VCUiKit.NewRect(win, "Viewport");
        viewport.anchorMin = viewport.anchorMax = new Vector2(0.5f, 0.5f);
        viewport.anchoredPosition = new Vector2(0f, (topY + bottomY) / 2f);
        viewport.sizeDelta = new Vector2(ContentW + 20f, viewH);
        viewport.gameObject.AddComponent<RectMask2D>();

        _content = VCUiKit.NewRect(viewport, "Content");
        _content.anchorMin = new Vector2(0f, 1f);
        _content.anchorMax = new Vector2(0f, 1f);
        _content.pivot = new Vector2(0f, 1f);
        _content.anchoredPosition = Vector2.zero;
        _content.sizeDelta = new Vector2(ContentW, 10f);

        var bg = VCUiKit.CreateImage(_content, "ScrollBG", Vector2.zero, _content.sizeDelta, VCUiKit.PixelSprite, Color.clear);
        var bgRt = bg.rectTransform;
        bgRt.anchorMin = Vector2.zero;
        bgRt.anchorMax = Vector2.one;
        bgRt.offsetMin = Vector2.zero;
        bgRt.offsetMax = Vector2.zero;
        bgRt.SetAsFirstSibling();

        var scroll = viewport.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = viewport;
        scroll.content = _content;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 24f;
        scroll.inertia = true;
        scroll.verticalNormalizedPosition = 1f;
        _scroll = scroll;
    }

    // ── Lobby list rendering ───────────────────────────────────

    private float _y;

    private void RefreshIfChanged()
    {
        if (!ShowWindow || _content == null) return;

        int sig = 0;
        foreach (var kv in PublicLobbyManager.LobbyMap)
        {
            var l = kv.Value;
            sig = sig * 31 + l.id + l.current_players * 7 + l.max_players * 3 + l.gameState * 13 + l.title.Length;
        }
        sig += (_status ?? "").Length * 17 + (PublicLobbyManager.IsLoading ? 1000 : 0);

        if (sig != _lastSig)
        {
            _lastSig = sig;
            RebuildLobbyList();
        }
    }

    private void RebuildLobbyList()
    {
        if (_content == null) return;
        float keepScroll = _scroll != null ? _scroll.verticalNormalizedPosition : 1f;

        for (int i = _content.childCount - 1; i >= 0; i--)
        {
            var child = _content.GetChild(i);
            if (child.name == "ScrollBG") continue;
            Object.Destroy(child.gameObject);
        }

        if (_statusTmp != null)
        {
            if (PublicLobbyManager.IsLoading) _statusTmp.text = Get("vc.lobby.loading", "Loading...");
            else if (!string.IsNullOrEmpty(_status)) _statusTmp.text = _status;
            else _statusTmp.text = "";
        }

        _y = 0f;
        var lobbies = PublicLobbyManager.CachedLobbies;
        if (lobbies.Count == 0)
        {
            VCUiKit.CreateText(_content, "Empty", Get("vc.lobby.empty", "No public lobbies available."),
                new Vector2(ContentW / 2f, -60f), new Vector2(ContentW - 80f, 40f),
                22f, new Color(0.55f, 0.60f, 0.70f, 1f), FontStyles.Normal, TextAlignmentOptions.Center);
        }
        else
        {
            foreach (var lobby in lobbies)
                RenderLobbyRow(lobby);
        }

        _content.sizeDelta = new Vector2(ContentW, _y + 24f);
        if (_scroll != null) _scroll.verticalNormalizedPosition = keepScroll;
    }

    private void RenderLobbyRow(PublicLobbyManager.LobbyInfo lobby)
    {
        var row = VCUiKit.NewRect(_content, "Row");
        row.anchorMin = row.anchorMax = new Vector2(0f, 1f);
        row.pivot = new Vector2(0f, 1f);
        row.anchoredPosition = new Vector2(0f, -_y);
        row.sizeDelta = new Vector2(ContentW, RowH);
        _y += RowH;

        string gameState = PublicLobbyManager.GetGameStateName(lobby.gameState);
        string players = lobby.current_players + "/" + lobby.max_players;
        string title = string.IsNullOrEmpty(lobby.title) ? "(no title)" : lobby.title;
        string host = lobby.host.Length > 15 ? lobby.host[..15] : lobby.host;
        string modsStr = string.IsNullOrEmpty(lobby.mods) ? Get("vc.lobby.vanilla", "Vanilla") : lobby.mods;
        Color stateColor = lobby.gameState switch { 1 => Color.green, 2 => Color.cyan, 3 => Color.yellow, _ => new Color(0.6f, 0.6f, 0.6f, 1f) };

        // state dot (left edge)
        var dot = VCUiKit.CreateImage(row, "Dot", Vector2.zero, new Vector2(14f, 14f), VCUiKit.CircleSprite, stateColor);
        var dotRt = (RectTransform)dot.transform;
        dotRt.anchorMin = dotRt.anchorMax = new Vector2(0f, 0.5f);
        dotRt.pivot = new Vector2(0.5f, 0.5f);
        dotRt.anchoredPosition = new Vector2(24f, 0f);

        // title (left aligned)
        var titleTmp = VCUiKit.CreateText(row, "Title", title, Vector2.zero, new Vector2(250f, RowH - 10f),
            20f, Color.white, FontStyles.Bold, TextAlignmentOptions.Left, true);
        var titleRt = (RectTransform)titleTmp.transform;
        titleRt.anchorMin = titleRt.anchorMax = new Vector2(0f, 0.5f);
        titleRt.pivot = new Vector2(0f, 0.5f);
        titleRt.anchoredPosition = new Vector2(46f, 0f);

        // host
        var hostTmp = VCUiKit.CreateText(row, "Host", string.Format(Get("vc.lobby.by", "by {0}"), host), Vector2.zero, new Vector2(120f, RowH - 10f),
            17f, new Color(0.62f, 0.67f, 0.78f, 1f), FontStyles.Normal, TextAlignmentOptions.Left, true);
        var hostRt = (RectTransform)hostTmp.transform;
        hostRt.anchorMin = hostRt.anchorMax = new Vector2(0f, 0.5f);
        hostRt.pivot = new Vector2(0f, 0.5f);
        hostRt.anchoredPosition = new Vector2(310f, 0f);

        // mods
        var modsTmp = VCUiKit.CreateText(row, "Mods", modsStr, Vector2.zero, new Vector2(70f, RowH - 10f),
            15f, new Color(0.40f, 0.80f, 1f, 1f), FontStyles.Normal, TextAlignmentOptions.Left, true);
        var modsRt = (RectTransform)modsTmp.transform;
        modsRt.anchorMin = modsRt.anchorMax = new Vector2(0f, 0.5f);
        modsRt.pivot = new Vector2(0f, 0.5f);
        modsRt.anchoredPosition = new Vector2(440f, 0f);

        // copy button (rightmost) - copies the lobby room code to the clipboard
        bool showCopy = lobby.gameState == 1;
        float rightPad = showCopy ? 40f + 70f + 12f : 40f;

        // players | state (right aligned, before the copy button)
        var infoTmp = VCUiKit.CreateText(row, "Info", players + " | " + gameState, Vector2.zero, new Vector2(110f, RowH - 10f),
            17f, new Color(0.85f, 0.88f, 0.95f, 1f), FontStyles.Bold, TextAlignmentOptions.Right);
        var infoRt = (RectTransform)infoTmp.transform;
        infoRt.anchorMin = infoRt.anchorMax = new Vector2(1f, 0.5f);
        infoRt.pivot = new Vector2(1f, 0.5f);
        infoRt.anchoredPosition = new Vector2(-rightPad, 0f);

        if (showCopy)
        {
            var lobbyRef = lobby;
            bool wasCopied = lobby.id == _lastCopiedId;
            var copyBtn = VCUiKit.CreateButton(row,
                wasCopied ? Get("vc.lobby.copiedShort", "Copied") : Get("vc.lobby.copy", "Copy"),
                Vector2.zero, new Vector2(70f, 36f),
                wasCopied ? new Color(0.16f, 0.42f, 0.26f, 1f) : new Color(0.20f, 0.55f, 0.32f, 1f),
                () => RequestLobbyCode(lobbyRef), 17f);
            var copyRt = (RectTransform)copyBtn.transform;
            copyRt.anchorMin = copyRt.anchorMax = new Vector2(1f, 0.5f);
            copyRt.pivot = new Vector2(0.5f, 0.5f);
            copyRt.anchoredPosition = new Vector2(-40f, 0f);
        }

        // divider
        var div = VCUiKit.CreateDivider(row, Vector2.zero, new Vector2(ContentW - 40f, 2f));
        var divRt = div.rectTransform;
        divRt.anchorMin = divRt.anchorMax = new Vector2(0f, 0f);
        divRt.pivot = new Vector2(0f, 0f);
        divRt.anchoredPosition = new Vector2(20f, 3f);
        divRt.sizeDelta = new Vector2(ContentW - 40f, 2f);
    }

    /// <summary>Request the lobby's real room code from the server (join_lobby ack), then copy it.</summary>
    private void RequestLobbyCode(PublicLobbyManager.LobbyInfo lobby)
    {
        try
        {
            if (_ws == null || _ws.State != WebSocketState.Open)
            {
                _status = Get("vc.lobby.copyFailed", "Copy failed");
                return;
            }
            _ackSeq = Math.Max(1, _ackSeq + 1);
            _pendingCopyAck = _ackSeq;
            _pendingCopyLobbyId = lobby.id;
            _status = Get("vc.lobby.copying", "Getting code...");
            InterstellarPlugin.Logger?.LogInfo($"[VC] Request lobby code: ack={_ackSeq} id={lobby.id}");
            SendRaw($"42{_ackSeq}[\"join_lobby\",{lobby.id}]");
        }
        catch (Exception e)
        {
            _status = Get("vc.lobby.copyFailed", "Copy failed");
            InterstellarPlugin.Logger?.LogError($"[VC] Request lobby code failed: {e}");
        }
    }

    private void HandleLobbyAck(int ackId, string argsJson)
    {
        InterstellarPlugin.Logger?.LogInfo($"[VC] Lobby ack: ackId={ackId} args={argsJson} pending={_pendingCopyAck}");
        if (ackId != _pendingCopyAck) return;
        _pendingCopyAck = -1;
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var arr = doc.RootElement;
            if (arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() >= 2)
            {
                int state = arr[0].GetInt32();
                string value = arr[1].GetString() ?? "";
                if (state == 0 && !string.IsNullOrEmpty(value))
                {
                    GUIUtility.systemCopyBuffer = value;
                    _lastCopiedId = _pendingCopyLobbyId;
                    _status = string.Format(Get("vc.lobby.copied", "Copied: {0}"), value);
                    return;
                }
            }
            _status = Get("vc.lobby.copyFailed", "Copy failed");
        }
        catch (Exception e)
        {
            _status = Get("vc.lobby.copyFailed", "Copy failed");
            InterstellarPlugin.Logger?.LogError($"[VC] Handle lobby ack failed: {e}");
        }
    }

    // ── Drag (polled, IL2CPP-safe) ─────────────────────────────

    private void UpdateDrag()
    {
        if (!ShowWindow || _winRt == null || _canvas == null) return;

        float scale = Mathf.Max(0.001f, _canvas.scaleFactor);
        Vector2 mouseCanvas = (Vector2)Input.mousePosition - new Vector2(Screen.width, Screen.height) * 0.5f;
        mouseCanvas /= scale;

        Vector2 winPos = _winRt.anchoredPosition;
        Rect title = new Rect(winPos.x - (WinW - 120f) * 0.5f, winPos.y + WinH / 2f - TitleBarH, WinW - 120f, TitleBarH);

        if (Input.GetMouseButtonDown(0) && title.Contains(mouseCanvas) && !VCUiKit.AnyWindowDragging)
        {
            _dragging = true;
            VCUiKit.AnyWindowDragging = true;
            _dragOffset = mouseCanvas - winPos;
        }
        if (Input.GetMouseButtonUp(0) && _dragging)
        {
            _dragging = false;
            VCUiKit.AnyWindowDragging = false;
        }
        // Safety: stop dragging if the mouse is no longer held (missed up event case).
        if (_dragging && !Input.GetMouseButton(0))
        {
            _dragging = false;
            VCUiKit.AnyWindowDragging = false;
        }

        if (_dragging)
        {
            _winRt.anchoredPosition = mouseCanvas - _dragOffset;
            ClampWindow();
        }
    }

    private void ClampWindow()
    {
        var p = _winRt.anchoredPosition;
        p.x = Mathf.Clamp(p.x, -_canvas.pixelRect.width / (2f * _canvas.scaleFactor) + WinW / 2f,
            _canvas.pixelRect.width / (2f * _canvas.scaleFactor) - WinW / 2f);
        p.y = Mathf.Clamp(p.y, -_canvas.pixelRect.height / (2f * _canvas.scaleFactor) + WinH / 2f,
            _canvas.pixelRect.height / (2f * _canvas.scaleFactor) - WinH / 2f);
        _winRt.anchoredPosition = p;
    }

    private static string Get(string key, string fallback) => TranslationHelper.Get(key, fallback);
}
