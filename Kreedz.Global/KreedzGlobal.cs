/*
 * yappershq/Kreedz (KZ) — Global-API plugin (1:1 cs2kz src/kz/global)
 *
 * Standalone ModSharp module (split out of Core). Connects to the cs2kz global backend over a WebSocket,
 * does the hello/hello_ack handshake (plugin checksum + map), and submits finished runs as NewRecord
 * messages so they land on the global leaderboard — alongside the always-on local ranking. Reads run
 * results via the public IKzRunService.RunFinished + IKzModeRegistry; needs no Core-internal services.
 *
 *   kz_global_apikey  ""                       — global API key. EMPTY = disabled (local ranking only).
 *   kz_global_url     "https://api.cs2kz.org"  — API base; https→wss, "/auth/cs2" appended, Bearer auth.
 *
 * Dormant without a key. The protocol shape mirrors cs2kz; the official checksum validation is theirs to
 * gate, so a real issued key + a live handshake are needed to certify official submission. All socket I/O
 * runs on background tasks; the only game-thread touch is capturing run data on the RunFinished event.
 */

using System;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;
using Kreedz.Shared.Interfaces;
using Kreedz.Shared.Models.Timer;

namespace Kreedz.Global;

internal enum GlobalState { Uninitialized, Disabled, Connecting, Connected, HandshakeSent, Ready, Reconnecting, Disconnected }

public sealed class KreedzGlobal : IModSharpModule, IClientListener
{
    private const string HelloType     = "hello";
    private const string HelloAckType  = "hello_ack";
    private const string NewRecordType = "NewRecord";

    private readonly ISharedSystem       _shared;
    private readonly IModSharp           _modSharp;
    private readonly IClientManager      _clientManager;
    private readonly ILogger<KreedzGlobal> _logger;
    private readonly Version             _version;
    private readonly string              _dllPath;

    private readonly IConVar? _apiKeyCvar;
    private readonly IConVar? _urlCvar;

    private IKzRunService?   _run;
    private IKzModeRegistry? _modes;

    private ClientWebSocket?         _socket;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim   _sendLock = new(1, 1);
    private int    _messageId;
    private string _checksum = "";

    private volatile GlobalState _state = GlobalState.Uninitialized;

    public string DisplayName   => "[Kreedz] Global";
    public string DisplayAuthor => "yappershq";

    public KreedzGlobal(ISharedSystem shared, string? dllPath, string? sharpPath, Version? version, IConfiguration? coreConfiguration, bool hotReload)
    {
        _shared        = shared;
        _modSharp      = shared.GetModSharp();
        _clientManager = shared.GetClientManager();
        _logger        = shared.GetLoggerFactory().CreateLogger<KreedzGlobal>();
        _version       = version ?? new Version(0, 0);
        _dllPath       = dllPath ?? "";

        var cvar = shared.GetConVarManager();
        _apiKeyCvar = cvar.CreateConVar("kz_global_apikey", "", "cs2kz global API key. Empty = global disabled (local ranking only).");
        _urlCvar    = cvar.CreateConVar("kz_global_url", "https://api.cs2kz.org", "cs2kz global API base URL.");
    }

    public int ListenerVersion  => IClientListener.ApiVersion;
    public int ListenerPriority => 10;

    public bool Init()
    {
        _clientManager.InstallClientListener(this);
        return true;
    }

    public void OnAllModulesLoaded()
    {
        var mgr = _shared.GetSharpModuleManager();
        _run   = mgr.GetOptionalSharpModuleInterface<IKzRunService>(IKzRunService.Identity)?.Instance;
        _modes = mgr.GetOptionalSharpModuleInterface<IKzModeRegistry>(IKzModeRegistry.Identity)?.Instance;

        if (_run is not null)
            _run.RunFinished += OnRunFinished;

        Start();
    }

    public void Shutdown()
    {
        if (_run is not null)
            _run.RunFinished -= OnRunFinished;
        _clientManager.RemoveClientListener(this);

        _cts?.Cancel();
        try { _socket?.Abort(); } catch { /* already dead */ }
        _socket?.Dispose();
        _cts?.Dispose();
    }

    private void Start()
    {
        if (string.IsNullOrWhiteSpace(_apiKeyCvar?.GetString()))
        {
            _state = GlobalState.Disabled;
            _logger.LogInformation("[KZ.Global] disabled — kz_global_apikey not set; using local ranking only.");
            return;
        }

        _checksum = ComputeChecksum();
        _cts      = new CancellationTokenSource();
        _ = ConnectLoopAsync(_cts.Token);
    }

    // Chat status command (!global) — matched off the say-command listener since this plugin has no
    // Core command manager; mirrors how Core's CommandManager dispatches chat triggers.
    public ECommandAction OnClientSayCommand(IGameClient client, bool teamOnly, bool isCommand, string commandName, string message)
    {
        if (string.IsNullOrEmpty(message) || !"!/.".Contains(message[0])) return ECommandAction.Skipped;
        if (!message[1..].Trim().Equals("global", StringComparison.OrdinalIgnoreCase)) return ECommandAction.Skipped;

        client.Print(HudPrintChannel.Chat, $"[KZ] Global ranking: {_state}.");
        return ECommandAction.Handled;
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        var backoff = 5.0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectOnceAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { _logger.LogWarning(e, "[KZ.Global] connection error"); }

            if (ct.IsCancellationRequested) break;

            _state = GlobalState.Reconnecting;
            try { await Task.Delay(TimeSpan.FromSeconds(backoff), ct); }
            catch (OperationCanceledException) { break; }
            backoff = Math.Min(60.0, backoff * 2.0);
        }

        _state = GlobalState.Disconnected;
    }

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        var key = _apiKeyCvar?.GetString() ?? "";
        var url = ToWebSocketUrl(_urlCvar?.GetString() ?? "https://api.cs2kz.org");

        _socket = new ClientWebSocket();
        _socket.Options.SetRequestHeader("Authorization", $"Bearer {key}");

        _state = GlobalState.Connecting;
        await _socket.ConnectAsync(new Uri(url), ct);
        _state = GlobalState.Connected;

        await SendHelloAsync(ct);
        _state = GlobalState.HandshakeSent;

        await ReceiveLoopAsync(_socket, ct);
    }

    private Task SendHelloAsync(CancellationToken ct) => SendJsonAsync(new
    {
        type           = HelloType,
        id             = NextId(),
        plugin_version = _version.ToString(),
        checksum       = _checksum,
        map            = _modSharp.GetGlobals().MapName,
    }, ct);

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var sb     = new StringBuilder();

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return;
                }

                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            HandleMessage(sb.ToString());
            sb.Clear();
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.Split('\n', 2)[0]); // JSON line; any binary follows a newline
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (string.Equals(type, HelloAckType, StringComparison.OrdinalIgnoreCase))
            {
                _state = GlobalState.Ready;
                _logger.LogInformation("[KZ.Global] handshake complete — global ranking active.");
            }
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "[KZ.Global] unparsable message");
        }
    }

    // Public run-finished event (game thread). Styled runs are never globally ranked.
    private void OnRunFinished(PlayerSlot slot, ITimerInfo info, int teleports, bool styled)
    {
        if (_state != GlobalState.Ready || styled) return;
        if (_clientManager.GetGameClient(slot) is not { IsFakeClient: false } client) return;

        // Capture as values on the game thread — nothing game-owned crosses to the send task.
        var steamId = client.SteamId.AsPrimitive();
        var name    = client.Name;
        var map     = _modSharp.GetGlobals().MapName;
        var time    = info.Time;
        var mode    = _modes?.GetPlayerMode(slot) ?? "vnl";

        _ = SubmitRunAsync(steamId, name, map, time, teleports, mode);
    }

    private async Task SubmitRunAsync(ulong steamId, string name, string map, float time, int teleports, string mode)
    {
        try
        {
            await SendJsonAsync(new
            {
                type      = NewRecordType,
                id        = NextId(),
                steamid64 = steamId.ToString(),
                player    = name,
                map,
                mode,
                teleports,
                pro       = teleports == 0,
                time,
            }, _cts?.Token ?? CancellationToken.None);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[KZ.Global] run submission failed for {Sid}", steamId);
        }
    }

    private async Task SendJsonAsync(object message, CancellationToken ct)
    {
        if (_socket is not { State: WebSocketState.Open } socket) return;

        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);

        await _sendLock.WaitAsync(ct);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private int NextId() => Interlocked.Increment(ref _messageId);

    private static string ToWebSocketUrl(string baseUrl)
    {
        var url = baseUrl.TrimEnd('/');
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "wss://" + url["https://".Length..];
        else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            url = "ws://" + url["http://".Length..];
        return url + "/auth/cs2";
    }

    private string ComputeChecksum()
    {
        try
        {
            using var md5    = MD5.Create();
            using var stream = File.OpenRead(_dllPath);
            return Convert.ToHexString(md5.ComputeHash(stream));
        }
        catch
        {
            return _version.ToString();
        }
    }
}
