import Foundation
import Network

/// One TCP listener serving (same API as the Windows add-in):
///   GET  /                  browser remote
///   GET  /display           timer only, black background (stage / confidence monitor)
///   GET  /api/state         current state
///   GET|POST /api/{cmd}     commands (args via query string, JSON body or form body)
///   GET|POST /api/settings  read / change runtime settings
///   GET  /api/debug/windows Keynote / PowerPoint windows and displays, as PPTimer sees them
///   GET  /ws                WebSocket: send {"cmd": ...}, receive pushed "state" messages
/// Everything runs on `queue`, including every connection.
final class ApiServer {
    static let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev"

    let queue = DispatchQueue(label: "pptimer.api")
    private let timer: TimerModel
    private let settings: SettingsStore
    private let diagnostics: (@escaping (Any) -> Void) -> Void
    private var listener: NWListener?
    private var connections: [ObjectIdentifier: HttpConnection] = [:]
    private var broadcaster: DispatchSourceTimer?
    private var lastKey: String?
    private var lastSentAt: UInt64 = 0
    private var stopped = false

    /// Called on the main thread when the listener starts or stops listening.
    var onListeningChanged: ((Bool) -> Void)?

    init(timer: TimerModel, settings: SettingsStore, diagnostics: @escaping (@escaping (Any) -> Void) -> Void) {
        self.timer = timer
        self.settings = settings
        self.diagnostics = diagnostics
        settings.onChanged.append { [weak self] in self?.onSettingsChanged() }
        timer.onZeroReached.append { [weak self] in self?.onZeroReached() }
    }

    func start() {
        queue.async { self.listen() }
        let source = DispatchSource.makeTimerSource(queue: queue)
        source.schedule(deadline: .now(), repeating: .milliseconds(50))
        source.setEventHandler { [weak self] in self?.broadcastIfChanged() }
        source.resume()
        broadcaster = source
    }

    private func listen() {
        guard !stopped else { return }
        let port = settings.current.port
        do {
            let params = NWParameters.tcp
            params.allowLocalEndpointReuse = true
            let l = try NWListener(using: params, on: NWEndpoint.Port(rawValue: UInt16(port))!)
            l.newConnectionHandler = { [weak self] connection in self?.accept(connection) }
            l.stateUpdateHandler = { [weak self, weak l] state in
                guard let self else { return }
                switch state {
                case .ready:
                    Log.info("API listening on port \(port)")
                    DispatchQueue.main.async { self.onListeningChanged?(true) }
                case .failed(let error):
                    // Usually the port is in use (a second copy, or the dev server). Keep retrying.
                    Log.error("Could not listen on port \(port); retrying in 5 s. Is another program using it?", error)
                    l?.cancel()
                    DispatchQueue.main.async { self.onListeningChanged?(false) }
                    self.queue.asyncAfter(deadline: .now() + 5) { self.listen() }
                default:
                    break
                }
            }
            l.start(queue: queue)
            listener = l
        } catch {
            Log.error("Could not start the API server on port \(port)", error)
        }
    }

    private func accept(_ connection: NWConnection) {
        let client = HttpConnection(connection: connection, server: self)
        connections[ObjectIdentifier(client)] = client
        client.start()
    }

    func remove(_ client: HttpConnection) {
        connections[ObjectIdentifier(client)] = nil
        if client.isWebSocket { Log.info("WebSocket client \(client.remote) disconnected") }
    }

    /// Pushes state immediately rather than on the next 50 ms poll.
    func notifyChanged() {
        queue.async { self.broadcastIfChanged() }
    }

    private func onZeroReached() {
        Log.info("Countdown reached zero")
        queue.async { self.broadcast(Json.serialize(["type": "event", "event": "zero"])) }
    }

    private func onSettingsChanged() {
        queue.async { self.broadcast(Json.serialize(self.settingsMessage())) }
        notifyChanged()
    }

    /// Pushes state when anything visible changes, plus a heartbeat every 5 s.
    private func broadcastIfChanged() {
        let snap = timer.snapshot()
        let now = DispatchTime.now().uptimeNanoseconds
        if snap.changeKey == lastKey && now - lastSentAt < 5_000_000_000 { return }
        lastKey = snap.changeKey
        lastSentAt = now
        broadcast(Json.serialize(stateMessage(snap)))
    }

    private func broadcast(_ json: String) {
        for client in connections.values where client.isWebSocket { client.sendText(json) }
    }

    // ---- Requests ---------------------------------------------------------------------------

    func handle(_ req: HttpRequest, on client: HttpConnection) {
        if req.method == "OPTIONS" {
            client.respond(status: 204)
            return
        }

        var path = req.path.lowercased()
        while path.count > 1 && path.hasSuffix("/") { path.removeLast() }

        switch path {
        case "/", "/index.html":
            client.respond(status: 200, contentType: "text/html; charset=utf-8", body: Data(WebPages.remote.utf8))
            return
        case "/display":
            client.respond(status: 200, contentType: "text/html; charset=utf-8", body: Data(WebPages.display.utf8))
            return
        default:
            break
        }

        guard authorized(req) else {
            respondJson(client, 401, errorBody("Missing or wrong API token (X-Api-Token header or ?token=)"))
            return
        }

        if path == "/ws" {
            guard req.headers["upgrade"]?.lowercased().contains("websocket") == true,
                  let key = req.headers["sec-websocket-key"]
            else {
                respondJson(client, 400, errorBody("Expected a WebSocket upgrade"))
                return
            }
            client.upgradeToWebSocket(key: key)
            Log.info("WebSocket client connected from \(client.remote)")
            client.sendText(Json.serialize(helloMessage()))
            client.sendText(Json.serialize(stateMessage(timer.snapshot())))
            return
        }

        if path == "/api/state" {
            respondJson(client, 200, stateMessage(timer.snapshot()))
            return
        }

        if path == "/api/debug/windows" {
            diagnostics { [weak self] info in
                self?.queue.async { self?.respondJson(client, 200, ["windows": info]) }
            }
            return
        }

        guard path.hasPrefix("/api/") else {
            respondJson(client, 404, errorBody("Not found"))
            return
        }

        let cmd = String(path.dropFirst(5))
        let args: Args
        do {
            args = try readArgs(req)
        } catch {
            respondJson(client, 400, errorBody("Bad request body: \(error)"))
            return
        }
        if cmd == "settings" && args.keys.allSatisfy({ $0.lowercased() == "token" }) {
            respondJson(client, 200, settingsMessage())
            return
        }

        if let error = Commands.execute(timer, settings, cmd, args) {
            respondJson(client, Commands.names.contains(cmd) ? 400 : 404, errorBody(error))
            return
        }
        Log.info("HTTP \(cmd) \(format(args)) from \(client.remote)")
        notifyChanged()
        var body = cmd == "settings" ? settingsMessage() : stateMessage(timer.snapshot())
        body["ok"] = true
        respondJson(client, 200, body)
    }

    func handleWebSocketMessage(_ text: String, from client: HttpConnection) {
        var id: String?
        var cmd: String?
        var error: String?
        do {
            guard let msg = try Json.parse(text) as? [String: Any] else { throw Json.ParseError(description: "Expected a JSON object") }
            var args = Args()
            for (k, v) in msg { args[k] = Json.argString(v) }
            id = args["id"]
            cmd = args["cmd"]

            if cmd == "state" {
                client.sendText(Json.serialize(stateMessage(timer.snapshot())))
                return
            }
            if cmd == "settings" && args.keys.allSatisfy({ ["cmd", "id"].contains($0.lowercased()) }) {
                client.sendText(Json.serialize(settingsMessage()))
                return
            }

            error = Commands.execute(timer, settings, cmd, args)
            if error == nil {
                Log.info("WS \(cmd ?? "") \(format(args)) from \(client.remote)")
                notifyChanged()
            }
        } catch let e {
            error = "Bad message: \(e)"
        }

        client.sendText(Json.serialize([
            "type": "result",
            "id": id ?? NSNull(),
            "cmd": cmd ?? NSNull(),
            "ok": error == nil,
            "error": error ?? NSNull(),
        ] as [String: Any]))
    }

    private func authorized(_ req: HttpRequest) -> Bool {
        let token = settings.current.apiToken
        if token.isEmpty { return true }
        return (req.headers["x-api-token"] ?? req.query["token"]) == token
    }

    private func readArgs(_ req: HttpRequest) throws -> Args {
        var args = req.query
        let body = String(decoding: req.body, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
        if body.isEmpty { return args }
        if body.hasPrefix("{") {
            guard let json = try Json.parse(body) as? [String: Any] else { return args }
            for (k, v) in json { args[k] = Json.argString(v) }
        } else {
            for (k, v) in HttpRequest.parseForm(body) { args[k] = v }
        }
        return args
    }

    private func format(_ args: Args) -> String {
        args.pairs.filter { $0.key.lowercased() != "token" }.map { "\($0.key)=\($0.value)" }.joined(separator: " ")
    }

    // ---- Messages -----------------------------------------------------------------------------

    private func stateMessage(_ snap: TimerSnapshot) -> [String: Any] {
        var d = snap.toDictionary()
        d["type"] = "state"
        return d
    }

    private func settingsMessage() -> [String: Any] {
        [
            "type": "settings",
            "settings": settings.current.toDictionary(includeSecrets: false),
            "runtimeKeys": Settings.runtimeKeys,
        ]
    }

    private func helloMessage() -> [String: Any] {
        [
            "type": "hello",
            "app": "PPTimer",
            "version": Self.version,
            "platform": "macOS",
            "lanAccess": true,
            "commands": Commands.names,
            "settings": settings.current.toDictionary(includeSecrets: false),
        ]
    }

    private func errorBody(_ message: String) -> [String: Any] {
        ["ok": false, "error": message]
    }

    private func respondJson(_ client: HttpConnection, _ status: Int, _ body: [String: Any]) {
        client.respond(status: status, contentType: "application/json; charset=utf-8", body: Data(Json.serialize(body).utf8))
    }

    func stop() {
        queue.sync {
            stopped = true
            broadcaster?.cancel()
            listener?.cancel()
            for client in connections.values { client.close() }
        }
    }
}
