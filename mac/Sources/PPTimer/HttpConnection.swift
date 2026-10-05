import CryptoKit
import Foundation
import Network

struct HttpRequest {
    var method = ""
    var path = "/"
    var query = Args()
    /// Lower-case names.
    var headers: [String: String] = [:]
    var body = Data()

    /// Parses `a=1&b=two` (query strings and form bodies). `+` is a space.
    static func parseForm(_ text: Substring) -> [(String, String)] {
        text.split(separator: "&").compactMap { pair in
            guard let eq = pair.firstIndex(of: "="), eq > pair.startIndex else { return nil }
            return (decode(pair[..<eq]), decode(pair[pair.index(after: eq)...]))
        }
    }

    static func parseForm(_ text: String) -> [(String, String)] { parseForm(Substring(text)) }

    private static func decode(_ s: Substring) -> String {
        let spaced = s.replacingOccurrences(of: "+", with: " ")
        return spaced.removingPercentEncoding ?? spaced
    }
}

/// One TCP connection: a single HTTP request (answered with Connection: close), or a WebSocket after an upgrade.
/// All methods run on the server's queue.
final class HttpConnection {
    private static let maxHeaderBytes = 32 * 1024
    private static let maxBodyBytes = 1024 * 1024
    private static let maxMessageBytes = 64 * 1024
    private static let maxPendingSends = 16

    let remote: String
    private let connection: NWConnection
    private unowned let server: ApiServer
    private var buffer: [UInt8] = []
    private var requestSeen = false
    private(set) var isWebSocket = false
    private var closed = false
    private var message: [UInt8] = []
    private var messageIsText = false
    private var pendingSends = 0

    init(connection: NWConnection, server: ApiServer) {
        self.connection = connection
        self.server = server
        if case let .hostPort(host, port) = connection.endpoint {
            remote = "\(host):\(port)".replacingOccurrences(of: "::ffff:", with: "")
        } else {
            remote = "\(connection.endpoint)"
        }
    }

    func start() {
        connection.stateUpdateHandler = { [weak self] state in
            switch state {
            case .failed, .cancelled: self?.close()
            default: break
            }
        }
        connection.start(queue: server.queue)
        receive()
        // A plain HTTP client that never finishes its request.
        server.queue.asyncAfter(deadline: .now() + 15) { [weak self] in
            guard let self, !self.isWebSocket, !self.requestSeen else { return }
            self.close()
        }
    }

    private func receive() {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self] data, _, isComplete, error in
            guard let self, !self.closed else { return }
            if let data, !data.isEmpty {
                self.buffer.append(contentsOf: data)
                self.process()
            }
            if isComplete || error != nil {
                self.close()
            } else if !self.closed {
                self.receive()
            }
        }
    }

    private func process() {
        if isWebSocket {
            processFrames()
            return
        }
        guard !requestSeen else { return }

        guard let headerEnd = findHeaderEnd() else {
            if buffer.count > Self.maxHeaderBytes { respond(status: 431) }
            return
        }
        let head = String(decoding: buffer[..<headerEnd], as: UTF8.self)
        var lines = head.components(separatedBy: "\r\n")
        let requestLine = lines.removeFirst().split(separator: " ")
        guard requestLine.count >= 2 else {
            respond(status: 400)
            return
        }

        var req = HttpRequest()
        req.method = requestLine[0].uppercased()
        let target = requestLine[1]
        if let q = target.firstIndex(of: "?") {
            req.path = String(target[..<q]).removingPercentEncoding ?? String(target[..<q])
            for (k, v) in HttpRequest.parseForm(target[target.index(after: q)...]) { req.query[k] = v }
        } else {
            req.path = String(target).removingPercentEncoding ?? String(target)
        }
        for line in lines {
            guard let colon = line.firstIndex(of: ":") else { continue }
            req.headers[line[..<colon].trimmingCharacters(in: .whitespaces).lowercased()] =
                line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
        }

        if req.headers["transfer-encoding"] != nil {
            respond(status: 411)
            return
        }
        let length = Int(req.headers["content-length"] ?? "0") ?? -1
        guard length >= 0, length <= Self.maxBodyBytes else {
            respond(status: 413)
            return
        }
        let bodyStart = headerEnd + 4
        guard buffer.count >= bodyStart + length else { return }
        req.body = Data(buffer[bodyStart..<(bodyStart + length)])
        buffer.removeFirst(bodyStart + length)
        requestSeen = true
        server.handle(req, on: self)
    }

    private func findHeaderEnd() -> Int? {
        guard buffer.count >= 4 else { return nil }
        for i in 0...(buffer.count - 4) where buffer[i] == 13 && buffer[i + 1] == 10 && buffer[i + 2] == 13 && buffer[i + 3] == 10 {
            return i
        }
        return nil
    }

    // ---- HTTP responses -----------------------------------------------------------------------

    func respond(status: Int, contentType: String = "text/plain; charset=utf-8", body: Data = Data()) {
        guard !closed, !isWebSocket else { return }
        requestSeen = true
        var head = "HTTP/1.1 \(status) \(Self.reason(status))\r\n"
        if status != 204 {
            head += "Content-Type: \(contentType)\r\nContent-Length: \(body.count)\r\n"
        }
        head += "Access-Control-Allow-Origin: *\r\n"
        head += "Access-Control-Allow-Headers: Content-Type, X-Api-Token\r\n"
        head += "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n"
        head += "Cache-Control: no-store\r\nConnection: close\r\n\r\n"
        connection.send(content: Data(head.utf8) + body, contentContext: .finalMessage, isComplete: true,
                        completion: .contentProcessed { [weak self] _ in self?.close() })
    }

    private static func reason(_ status: Int) -> String {
        switch status {
        case 101: return "Switching Protocols"
        case 200: return "OK"
        case 204: return "No Content"
        case 400: return "Bad Request"
        case 401: return "Unauthorized"
        case 404: return "Not Found"
        case 411: return "Length Required"
        case 413: return "Payload Too Large"
        case 431: return "Request Header Fields Too Large"
        default: return "Error"
        }
    }

    // ---- WebSocket ------------------------------------------------------------------------------

    func upgradeToWebSocket(key: String) {
        let digest = Insecure.SHA1.hash(data: Data((key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").utf8))
        let head = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            "Sec-WebSocket-Accept: \(Data(digest).base64EncodedString())\r\n\r\n"
        connection.send(content: Data(head.utf8), completion: .contentProcessed { _ in })
        isWebSocket = true
        if !buffer.isEmpty { processFrames() }
    }

    func sendText(_ text: String) {
        // A client that can't keep up just misses a frame; the next state supersedes it.
        guard pendingSends < Self.maxPendingSends else { return }
        sendFrame(opcode: 0x1, payload: Array(text.utf8))
    }

    private func sendFrame(opcode: UInt8, payload: [UInt8], thenClose: Bool = false) {
        guard !closed else { return }
        var frame: [UInt8] = [0x80 | opcode]
        let n = payload.count
        if n < 126 {
            frame.append(UInt8(n))
        } else if n <= 0xFFFF {
            frame += [126, UInt8(n >> 8), UInt8(n & 0xFF)]
        } else {
            frame.append(127)
            for shift in stride(from: 56, through: 0, by: -8) { frame.append(UInt8((UInt64(n) >> UInt64(shift)) & 0xFF)) }
        }
        frame += payload
        pendingSends += 1
        connection.send(content: Data(frame), completion: .contentProcessed { [weak self] error in
            guard let self else { return }
            self.pendingSends -= 1
            if error != nil || thenClose { self.close() }
        })
    }

    private func closeWebSocket(code: UInt16) {
        sendFrame(opcode: 0x8, payload: [UInt8(code >> 8), UInt8(code & 0xFF)], thenClose: true)
    }

    private func processFrames() {
        while !closed && buffer.count >= 2 {
            let fin = buffer[0] & 0x80 != 0
            let opcode = buffer[0] & 0x0F
            let masked = buffer[1] & 0x80 != 0
            var length = Int(buffer[1] & 0x7F)
            var offset = 2
            if length == 126 {
                guard buffer.count >= 4 else { return }
                length = Int(buffer[2]) << 8 | Int(buffer[3])
                offset = 4
            } else if length == 127 {
                guard buffer.count >= 10 else { return }
                var l: UInt64 = 0
                for i in 2..<10 { l = l << 8 | UInt64(buffer[i]) }
                guard l <= UInt64(Self.maxMessageBytes) else {
                    closeWebSocket(code: 1009)
                    return
                }
                length = Int(l)
                offset = 10
            }
            guard length <= Self.maxMessageBytes else {
                closeWebSocket(code: 1009)
                return
            }
            let maskOffset = offset
            if masked { offset += 4 }
            guard buffer.count >= offset + length else { return }

            var payload = Array(buffer[offset..<(offset + length)])
            if masked {
                for i in payload.indices { payload[i] ^= buffer[maskOffset + i % 4] }
            }
            buffer.removeFirst(offset + length)

            switch opcode {
            case 0x1, 0x2:
                message = payload
                messageIsText = opcode == 0x1
                if fin { deliver() }
            case 0x0:
                message += payload
                if message.count > Self.maxMessageBytes {
                    closeWebSocket(code: 1009)
                    return
                }
                if fin { deliver() }
            case 0x8:
                sendFrame(opcode: 0x8, payload: Array(payload.prefix(2)), thenClose: true)
                return
            case 0x9:
                sendFrame(opcode: 0xA, payload: payload)
            case 0xA:
                break
            default:
                closeWebSocket(code: 1002)
                return
            }
        }
    }

    private func deliver() {
        let text = String(decoding: message, as: UTF8.self)
        message = []
        if messageIsText { server.handleWebSocketMessage(text, from: self) }
    }

    func close() {
        guard !closed else { return }
        closed = true
        connection.cancel()
        server.remove(self)
    }
}
