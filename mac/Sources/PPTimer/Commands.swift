import Foundation

/// Request arguments with case-insensitive lookup (keys keep their original spelling for error messages).
struct Args {
    private(set) var pairs: [(key: String, value: String)] = []

    subscript(key: String) -> String? {
        get { pairs.last(where: { $0.key.caseInsensitiveCompare(key) == .orderedSame })?.value }
        set {
            pairs.removeAll { $0.key.caseInsensitiveCompare(key) == .orderedSame }
            if let newValue { pairs.append((key, newValue)) }
        }
    }

    var keys: [String] { pairs.map(\.key) }
}

/// The command set shared by the REST API (`/api/{cmd}`) and the WebSocket (`{"cmd": ...}`).
enum Commands {
    static let names = [
        "start", "pause", "toggle", "reset", "restart", "set", "add",
        "show", "hide", "togglevisible", "settings", "togglesetting", "testsound", "speed",
    ]

    private static let reservedArgs: Set<String> = ["cmd", "id", "token"]

    /// Runs a command. Returns an error message, or nil on success.
    static func execute(_ timer: TimerModel, _ settings: SettingsStore, _ cmd: String?, _ args: Args) -> String? {
        switch (cmd ?? "").trimmingCharacters(in: .whitespaces).lowercased() {
        case "start":
            timer.start()
        case "pause", "stop":
            timer.pause()
        case "toggle":
            timer.toggle()
        case "reset":
            timer.reset()
        case "restart":
            timer.restart()
        case "set":
            let ms: Int64
            switch duration(args) {
            case .failure(let error): return error.message
            case .success(let value): ms = value
            }
            if ms < 0 { return "Duration cannot be negative" }
            var start: Bool?
            if let text = args["start"] {
                do { start = try Settings.bool(text) } catch let e as Settings.InvalidValue { return e.message } catch { return "Invalid 'start'" }
            }
            timer.set(ms, start: start)
            // Remember it so a restart comes back with the same duration.
            settings.update([("defaultDurationSeconds", Int(ms / 1000))], allowAll: true)
        case "add":
            switch duration(args) {
            case .failure(let error): return error.message
            case .success(let ms): timer.add(ms)
            }
        case "speed":
            // percent=105 or rate=1.05 sets it; step=5 / step=-5 nudges it.
            if let s = args["step"], let step = parseNumber(s) {
                timer.setSpeed(step, relative: true)
                return nil
            }
            let percent: Double
            if let p = args["percent"], let v = parseNumber(p) { percent = v }
            else if let r = args["rate"], let v = parseNumber(r) { percent = v * 100 }
            else { return "Give 'percent' (e.g. 105), 'rate' (e.g. 1.05) or 'step' (e.g. 5 or -5)" }
            if percent < TimerModel.minSpeedPercent || percent > TimerModel.maxSpeedPercent {
                return "Speed must be between \(Int(TimerModel.minSpeedPercent)) and \(Int(TimerModel.maxSpeedPercent)) %"
            }
            timer.setSpeed(percent, relative: false)
        case "show":
            timer.setVisible(true)
        case "hide":
            timer.setVisible(false)
        case "togglevisible":
            timer.toggleVisible()
        case "testsound":
            timer.requestSoundTest()
        case "settings":
            return settings.update(args.pairs
                .filter { !reservedArgs.contains($0.key.lowercased()) }
                .map { ($0.key, $0.value as Any) })
        case "togglesetting":
            // Flipped against the app's own value, so two quick presses always cancel out.
            guard let key = args["key"] else {
                return "Give the on/off setting to flip as 'key' (\(settings.current.toggleKeys.joined(separator: ", ")))"
            }
            return settings.toggle(key)
        default:
            return "Unknown command '\(cmd ?? "")'. Commands: \(names.joined(separator: ", "))"
        }
        return nil
    }

    struct DurationError: Error {
        let message: String
    }

    /// Reads a duration from `seconds`, `minutes` or `time` ("90", "5:00", "1:05:00", "-1:00").
    private static func duration(_ args: Args) -> Result<Int64, DurationError> {
        if let s = args["seconds"], let seconds = parseNumber(s) { return .success(Int64((seconds * 1000).rounded())) }
        if let m = args["minutes"], let minutes = parseNumber(m) { return .success(Int64((minutes * 60_000).rounded())) }
        if let t = args["time"], let ms = parseTime(t) { return .success(ms) }
        return .failure(DurationError(message: "Give a duration as 'seconds', 'minutes' or 'time' (e.g. time=5:00)"))
    }

    private static func parseNumber(_ text: String) -> Double? {
        guard let v = Double(text.trimmingCharacters(in: .whitespaces)), !v.isNaN, abs(v) < 360000 else { return nil }
        return v
    }

    static func parseTime(_ input: String) -> Int64? {
        var text = input.trimmingCharacters(in: .whitespaces)
        guard !text.isEmpty else { return nil }
        var sign: Double = 1
        if text.first == "-" || text.first == "+" {
            if text.first == "-" { sign = -1 }
            text = String(text.dropFirst()).trimmingCharacters(in: .whitespaces)
        }
        let parts = text.split(separator: ":", omittingEmptySubsequences: false)
        guard parts.count <= 3 else { return nil }
        var total = 0.0
        for part in parts {
            // Digits and an optional decimal point only: no signs or exponents inside a part.
            guard !part.isEmpty, part.allSatisfy({ $0.isASCII && ($0.isNumber || $0 == ".") }), let v = Double(part) else { return nil }
            total = total * 60 + v
        }
        guard total < 360000 else { return nil }
        return Int64((sign * total * 1000).rounded())
    }
}
