using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PPTimer.Core
{
    /// <summary>
    /// One HttpListener serving:
    ///   GET  /                  browser remote
    ///   GET  /display           timer only, black background (stage / confidence monitor)
    ///   GET  /api/state         current state
    ///   GET|POST /api/{cmd}     commands (args via query string, JSON body or form body)
    ///   GET|POST /api/settings  read / change runtime settings
    ///   GET  /api/debug/windows PowerPoint's windows, slide shows, monitors and the detection result (Windows only)
    ///   GET  /api/debug/log     the end of pptimer.log as plain text (send this instead of a photo of the screen)
    ///   GET  /ws                WebSocket: send {"cmd": ...}, receive pushed "state" messages
    /// </summary>
    public sealed class ApiServer : IDisposable
    {
        public static readonly string Version =
            (typeof(ApiServer).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)
            ?.InformationalVersion.Split('+')[0] ?? "0.0.0";

        readonly TimerModel timer;
        readonly SettingsStore settings;
        readonly Func<object> diagnostics;
        readonly CancellationTokenSource cts = new CancellationTokenSource();
        readonly List<WsClient> clients = new List<WsClient>();
        readonly ManualResetEventSlim changed = new ManualResetEventSlim(false);
        HttpListener listener;
        Thread broadcaster;

        public ApiServer(TimerModel timer, SettingsStore settings, Func<object> diagnostics = null)
        {
            this.timer = timer;
            this.settings = settings;
            this.diagnostics = diagnostics;
            settings.Changed += OnSettingsChanged;
            timer.ZeroReached += OnZeroReached;
        }

        public string ListeningOn { get; private set; }

        /// <summary>False when only localhost could be bound (URL reservation missing: install.ps1 adds it).</summary>
        public bool LanAccess { get; private set; }

        public void Start()
        {
            var port = settings.Current.Port;
            listener = TryListen($"http://+:{port}/");
            LanAccess = listener != null;
            if (listener == null)
            {
                listener = TryListen($"http://localhost:{port}/");
                if (listener != null)
                    Log.Warn("Listening on localhost only: the URL reservation is missing. Run install.cmd again and accept the admin prompt to allow network control.");
            }
            if (listener == null)
            {
                Log.Error($"Could not start the API server on port {port}. Is another program using it?");
                return;
            }

            Task.Run(AcceptLoopAsync);
            broadcaster = new Thread(BroadcastLoop) { IsBackground = true, Name = "PPTimer broadcaster" };
            broadcaster.Start();
        }

        HttpListener TryListen(string prefix)
        {
            var l = new HttpListener();
            l.Prefixes.Add(prefix);
            try
            {
                l.Start();
                ListeningOn = prefix;
                Log.Info($"API listening on {prefix}");
                return l;
            }
            catch (Exception ex)
            {
                // Access denied (no URL ACL) or port in use; the caller falls back / reports.
                Log.Warn($"Cannot listen on {prefix}: {ex.Message}");
                try { l.Close(); } catch { }
                return null;
            }
        }

        /// <summary>Wakes the broadcaster so a change is pushed immediately rather than on the next poll.</summary>
        public void NotifyChanged() => changed.Set();

        void OnZeroReached()
        {
            Log.Info("Countdown reached zero");
            Broadcast(Json.Serialize(new Dictionary<string, object> { ["type"] = "event", ["event"] = "zero" }));
        }

        void OnSettingsChanged()
        {
            Broadcast(Json.Serialize(SettingsMessage()));
            NotifyChanged();
        }

        async Task AcceptLoopAsync()
        {
            while (!cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception) when (cts.IsCancellationRequested || !listener.IsListening)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Error("Accept failed", ex);
                    continue;
                }
                _ = Task.Run(() => HandleAsync(ctx));
            }
        }

        async Task HandleAsync(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            try
            {
                res.AddHeader("Access-Control-Allow-Origin", "*");
                res.AddHeader("Access-Control-Allow-Headers", "Content-Type, X-Api-Token");
                res.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                if (req.HttpMethod == "OPTIONS")
                {
                    res.StatusCode = 204;
                    res.Close();
                    return;
                }

                var path = req.Url.AbsolutePath.TrimEnd('/').ToLowerInvariant();
                if (path.Length == 0) path = "/";

                if (path == "/" || path == "/index.html")
                {
                    WriteText(res, 200, WebPages.Remote, "text/html; charset=utf-8");
                    return;
                }

                if (path == "/display")
                {
                    WriteText(res, 200, WebPages.Display, "text/html; charset=utf-8");
                    return;
                }

                if (!Authorized(req))
                {
                    WriteJson(res, 401, Error("Missing or wrong API token (X-Api-Token header or ?token=)"));
                    return;
                }

                if (path == "/ws")
                {
                    if (!req.IsWebSocketRequest)
                    {
                        WriteJson(res, 400, Error("Expected a WebSocket upgrade"));
                        return;
                    }
                    await HandleWebSocketAsync(ctx).ConfigureAwait(false);
                    return;
                }

                if (path == "/api/state")
                {
                    WriteJson(res, 200, StateMessage(timer.Snapshot()));
                    return;
                }

                if (path == "/api/debug/windows")
                {
                    var body = diagnostics?.Invoke() as Dictionary<string, object>
                               ?? new Dictionary<string, object> { ["windows"] = "not available on this platform" };
                    body["version"] = Version;
                    body["presenterWindowClasses"] = settings.Current.PresenterWindowClasses;
                    body["presenterWindowTitles"] = settings.Current.PresenterWindowTitles;
                    WriteJson(res, 200, body);
                    return;
                }

                if (path == "/api/debug/log")
                {
                    var kb = int.TryParse(req.QueryString["kb"], out var k) ? Math.Max(1, Math.Min(k, 2000)) : 200;
                    WriteText(res, 200, Log.Tail(kb * 1024), "text/plain; charset=utf-8");
                    return;
                }

                if (path.StartsWith("/api/", StringComparison.Ordinal))
                {
                    var cmd = path.Substring(5);
                    var args = ReadArgs(req);
                    if (cmd == "settings" && args.Keys.All(k => k.Equals("token", StringComparison.OrdinalIgnoreCase)))
                    {
                        WriteJson(res, 200, SettingsMessage());
                        return;
                    }

                    var error = Commands.Execute(timer, settings, cmd, args);
                    if (error != null)
                    {
                        WriteJson(res, cmd.Length > 0 && Commands.Names.Contains(cmd) ? 400 : 404, Error(error));
                        return;
                    }
                    Log.Info($"HTTP {cmd} {FormatArgs(args)} from {RemoteOf(req)}");
                    NotifyChanged();
                    var body = cmd == "settings" ? SettingsMessage() : StateMessage(timer.Snapshot());
                    body["ok"] = true;
                    WriteJson(res, 200, body);
                    return;
                }

                WriteJson(res, 404, Error("Not found"));
            }
            catch (Exception ex)
            {
                Log.Error($"Request {req.HttpMethod} {req.Url} failed", ex);
                try
                {
                    res.StatusCode = 500;
                    res.Close();
                }
                catch { }
            }
        }

        bool Authorized(HttpListenerRequest req)
        {
            var token = settings.Current.ApiToken;
            if (string.IsNullOrEmpty(token)) return true;
            var given = req.Headers["X-Api-Token"] ?? req.QueryString["token"];
            return string.Equals(given, token, StringComparison.Ordinal);
        }

        static Dictionary<string, string> ReadArgs(HttpListenerRequest req)
        {
            var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in req.QueryString.AllKeys)
                if (key != null) args[key] = req.QueryString[key];

            if (!req.HasEntityBody) return args;
            string body;
            using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                body = reader.ReadToEnd().Trim();
            if (body.Length == 0) return args;

            if (body.StartsWith("{", StringComparison.Ordinal))
            {
                if (Json.Parse(body) is Dictionary<string, object> json)
                    foreach (var kv in json) args[kv.Key] = ArgToString(kv.Value);
            }
            else
            {
                foreach (var pair in body.Split('&'))
                {
                    var eq = pair.IndexOf('=');
                    if (eq <= 0) continue;
                    args[WebUtility.UrlDecode(pair.Substring(0, eq))] = WebUtility.UrlDecode(pair.Substring(eq + 1));
                }
            }
            return args;
        }

        static string ArgToString(object value) => value switch
        {
            null => "",
            bool b => b ? "true" : "false",
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            string s => s,
            _ => Json.Serialize(value),
        };

        static string RemoteOf(HttpListenerRequest req)
        {
            try { return req.RemoteEndPoint?.ToString() ?? "?"; }
            catch { return "?"; } // connection already gone
        }

        static string FormatArgs(Dictionary<string, string> args) =>
            string.Join(" ", args.Where(kv => !kv.Key.Equals("token", StringComparison.OrdinalIgnoreCase)).Select(kv => $"{kv.Key}={kv.Value}"));

        // ---- WebSocket ------------------------------------------------------------------------

        async Task HandleWebSocketAsync(HttpListenerContext ctx)
        {
            HttpListenerWebSocketContext wsCtx;
            try
            {
                wsCtx = await ctx.AcceptWebSocketAsync(null, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("WebSocket upgrade failed", ex);
                ctx.Response.StatusCode = 500;
                ctx.Response.Close();
                return;
            }

            var remote = RemoteOf(ctx.Request);
            var client = new WsClient(wsCtx.WebSocket);
            lock (clients) clients.Add(client);
            Log.Info($"WebSocket client connected from {remote}");
            try
            {
                await client.SendAsync(Json.Serialize(HelloMessage())).ConfigureAwait(false);
                await client.SendAsync(Json.Serialize(StateMessage(timer.Snapshot()))).ConfigureAwait(false);

                var buffer = new byte[8192];
                var message = new MemoryStream();
                while (client.Socket.State == WebSocketState.Open && !cts.IsCancellationRequested)
                {
                    var result = await client.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await client.CloseAsync().ConfigureAwait(false);
                        break;
                    }
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > 64 * 1024) break;
                    if (!result.EndOfMessage) continue;

                    var text = Encoding.UTF8.GetString(message.ToArray());
                    message.SetLength(0);
                    await HandleWsMessageAsync(client, text, remote).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is WebSocketException || ex is OperationCanceledException || ex is ObjectDisposedException || ex is HttpListenerException)
            {
                // Client went away.
            }
            catch (Exception ex)
            {
                Log.Error("WebSocket error", ex);
            }
            finally
            {
                lock (clients) clients.Remove(client);
                client.Dispose();
                try { ctx.Response.Abort(); } catch { } // drop the TCP connection too
                Log.Info($"WebSocket client {remote} disconnected");
            }
        }

        async Task HandleWsMessageAsync(WsClient client, string text, string remote)
        {
            string id = null;
            string cmd = null;
            string error;
            try
            {
                if (!(Json.Parse(text) is Dictionary<string, object> msg)) throw new FormatException("Expected a JSON object");
                var args = msg.ToDictionary(kv => kv.Key, kv => ArgToString(kv.Value), StringComparer.OrdinalIgnoreCase);
                args.TryGetValue("id", out id);
                args.TryGetValue("cmd", out cmd);

                if (cmd == "state")
                {
                    await client.SendAsync(Json.Serialize(StateMessage(timer.Snapshot()))).ConfigureAwait(false);
                    return;
                }
                if (cmd == "settings" && args.Keys.All(k => k == "cmd" || k == "id"))
                {
                    await client.SendAsync(Json.Serialize(SettingsMessage())).ConfigureAwait(false);
                    return;
                }

                error = Commands.Execute(timer, settings, cmd, args);
                if (error == null)
                {
                    Log.Info($"WS {cmd} {FormatArgs(args)} from {remote}");
                    NotifyChanged();
                }
            }
            catch (FormatException ex)
            {
                error = "Bad message: " + ex.Message;
            }

            await client.SendAsync(Json.Serialize(new Dictionary<string, object>
            {
                ["type"] = "result",
                ["id"] = id,
                ["cmd"] = cmd,
                ["ok"] = error == null,
                ["error"] = error,
            })).ConfigureAwait(false);
        }

        /// <summary>Pushes state when anything visible changes (at most every 50 ms), plus a 5 s heartbeat.</summary>
        void BroadcastLoop()
        {
            string lastKey = null;
            var sinceLastSend = System.Diagnostics.Stopwatch.StartNew();
            while (!cts.IsCancellationRequested)
            {
                if (changed.Wait(50)) changed.Reset();
                try
                {
                    var snap = timer.Snapshot();
                    var key = snap.ChangeKey;
                    if (key == lastKey && sinceLastSend.ElapsedMilliseconds < 5000) continue;
                    lastKey = key;
                    sinceLastSend.Restart();
                    Broadcast(Json.Serialize(StateMessage(snap)));
                }
                catch (Exception ex)
                {
                    Log.Error("Broadcast failed", ex);
                }
            }
        }

        void Broadcast(string json)
        {
            WsClient[] targets;
            lock (clients) targets = clients.ToArray();
            foreach (var c in targets) _ = c.SendAsync(json);
        }

        // ---- Messages -------------------------------------------------------------------------

        static Dictionary<string, object> StateMessage(TimerSnapshot snap)
        {
            var d = snap.ToDictionary();
            d["type"] = "state";
            return d;
        }

        Dictionary<string, object> SettingsMessage() => new Dictionary<string, object>
        {
            ["type"] = "settings",
            ["settings"] = settings.Current.ToDictionary(includeSecrets: false),
            ["runtimeKeys"] = Settings.RuntimeKeys,
        };

        Dictionary<string, object> HelloMessage() => new Dictionary<string, object>
        {
            ["type"] = "hello",
            ["app"] = "PPTimer",
            ["version"] = Version,
            ["lanAccess"] = LanAccess,
            ["commands"] = Commands.Names,
            ["settings"] = settings.Current.ToDictionary(includeSecrets: false),
        };

        static Dictionary<string, object> Error(string message) => new Dictionary<string, object>
        {
            ["ok"] = false,
            ["error"] = message,
        };

        static void WriteJson(HttpListenerResponse res, int status, object body) =>
            WriteText(res, status, Json.Serialize(body), "application/json; charset=utf-8");

        static void WriteText(HttpListenerResponse res, int status, string text, string contentType)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            res.StatusCode = status;
            res.ContentType = contentType;
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes, 0, bytes.Length);
            res.Close();
        }

        public void Dispose()
        {
            settings.Changed -= OnSettingsChanged;
            timer.ZeroReached -= OnZeroReached;
            cts.Cancel();
            try { listener?.Stop(); } catch { }
            try { listener?.Close(); } catch { }
            WsClient[] open;
            lock (clients) open = clients.ToArray();
            foreach (var c in open) c.Dispose();
            broadcaster?.Join(500);
        }

        sealed class WsClient : IDisposable
        {
            readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);

            public WsClient(WebSocket socket) { Socket = socket; }

            public WebSocket Socket { get; }

            public async Task SendAsync(string text)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                // A client that can't keep up just misses a frame; the next state supersedes it.
                if (!await sendLock.WaitAsync(2000).ConfigureAwait(false)) return;
                try
                {
                    if (Socket.State == WebSocketState.Open)
                        await Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Closed underneath us; the receive loop will clean up.
                }
                finally
                {
                    sendLock.Release();
                }
            }

            public async Task CloseAsync()
            {
                try { await Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false); }
                catch { }
            }

            public void Dispose()
            {
                try { Socket.Abort(); } catch { }
                try { Socket.Dispose(); } catch { }
            }
        }
    }
}
