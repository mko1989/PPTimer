import Foundation

/// Settings snapshot (a value type, so every read is a consistent copy). Same keys and ranges as the
/// Windows add-in's config.json, minus the Windows-only presenter window classes.
struct Settings {
    static let currentConfigVersion = 2

    /// Keys that can be changed at runtime over the API. Everything else is file-only.
    static let runtimeKeys = [
        "xPercent", "yPercent", "widthPercent", "heightPercent", "opacity",
        "transparentBackground", "textOutline",
        "warnEnabled", "warnSeconds", "criticalEnabled", "criticalSeconds",
        "blinkAtZero", "countUp", "showMinus", "soundEnabled", "soundFile",
    ]

    struct InvalidValue: Error {
        let message: String
    }

    var configVersion = Settings.currentConfigVersion

    // Network (file-only, restart PPTimer after changing)
    var port = 9595
    var apiToken = ""

    // Overlay behaviour (file-only)
    var clickThrough = true

    // Overlay rectangle, in % of the presenter view (top-left corner + size)
    var xPercent = 22.0
    var yPercent = 74.0
    var widthPercent = 16.0
    var heightPercent = 9.0

    // Look
    var opacity = 1.0
    var transparentBackground = true
    var textOutline = true

    // Colour thresholds
    var warnEnabled = true
    var warnSeconds = 180
    var criticalEnabled = true
    var criticalSeconds = 60

    // At zero
    var blinkAtZero = true
    var countUp = true
    var showMinus = false
    var soundEnabled = false
    var soundFile = ""

    var defaultDurationSeconds = 300

    func toDictionary(includeSecrets: Bool) -> [String: Any] {
        var d: [String: Any] = [
            "configVersion": configVersion,
            "port": port,
            "clickThrough": clickThrough,
            "xPercent": xPercent,
            "yPercent": yPercent,
            "widthPercent": widthPercent,
            "heightPercent": heightPercent,
            "opacity": opacity,
            "transparentBackground": transparentBackground,
            "textOutline": textOutline,
            "warnEnabled": warnEnabled,
            "warnSeconds": warnSeconds,
            "criticalEnabled": criticalEnabled,
            "criticalSeconds": criticalSeconds,
            "blinkAtZero": blinkAtZero,
            "countUp": countUp,
            "showMinus": showMinus,
            "soundEnabled": soundEnabled,
            "soundFile": soundFile,
            "defaultDurationSeconds": defaultDurationSeconds,
        ]
        if includeSecrets { d["apiToken"] = apiToken }
        return d
    }

    /// The on/off runtime settings, which `togglesetting` can flip.
    var toggleKeys: [String] {
        let values = toDictionary(includeSecrets: false)
        return Self.runtimeKeys.filter { values[$0].map(Json.isBool) ?? false }
    }

    /// Applies one value. Returns an error message, or nil on success.
    mutating func apply(_ key: String, _ value: Any) -> String? {
        do {
            switch key.lowercased() {
            case "configversion": configVersion = Int(try Self.range(value, 0, 1000))
            case "port": port = Int(try Self.range(value, 1, 65535))
            case "apitoken": apiToken = Self.text(value)
            case "clickthrough": clickThrough = try Self.bool(value)
            case "xpercent": xPercent = try Self.range(value, 0, 100)
            case "ypercent": yPercent = try Self.range(value, 0, 100)
            case "widthpercent": widthPercent = try Self.range(value, 2, 100)
            case "heightpercent": heightPercent = try Self.range(value, 2, 100)
            case "opacity": opacity = try Self.range(value, 0.2, 1)
            case "transparentbackground": transparentBackground = try Self.bool(value)
            case "textoutline": textOutline = try Self.bool(value)
            case "warnenabled": warnEnabled = try Self.bool(value)
            case "warnseconds": warnSeconds = Int(try Self.range(value, 0, 86400))
            case "criticalenabled": criticalEnabled = try Self.bool(value)
            case "criticalseconds": criticalSeconds = Int(try Self.range(value, 0, 86400))
            case "blinkatzero": blinkAtZero = try Self.bool(value)
            case "countup": countUp = try Self.bool(value)
            case "showminus": showMinus = try Self.bool(value)
            case "soundenabled": soundEnabled = try Self.bool(value)
            case "soundfile": soundFile = Self.text(value).trimmingCharacters(in: .whitespaces).trimmingCharacters(in: CharacterSet(charactersIn: "\""))
            case "defaultdurationseconds": defaultDurationSeconds = Int(try Self.range(value, 0, 360000))
            default: return "Unknown setting '\(key)'"
            }
            return nil
        } catch let e as InvalidValue {
            return "Invalid value for '\(key)': \(e.message)"
        } catch {
            return "Invalid value for '\(key)'"
        }
    }

    private static func range(_ value: Any, _ min: Double, _ max: Double) throws -> Double {
        let v = try number(value)
        if v.isNaN || v < min || v > max {
            throw InvalidValue(message: "must be between \(format(min)) and \(format(max))")
        }
        return v
    }

    private static func format(_ d: Double) -> String {
        d == d.rounded() ? String(Int(d)) : String(d)
    }

    private static func text(_ value: Any) -> String { Json.argString(value) }

    static func number(_ value: Any) throws -> Double {
        if let n = value as? NSNumber, !Json.isBool(n) { return n.doubleValue }
        if Json.isBool(value) { return (value as! NSNumber).boolValue ? 1 : 0 }
        let s = text(value).trimmingCharacters(in: .whitespaces)
        guard let d = Double(s) else { throw InvalidValue(message: "'\(s)' is not a number") }
        return d
    }

    static func bool(_ value: Any) throws -> Bool {
        if Json.isBool(value) { return (value as! NSNumber).boolValue }
        let s = text(value).trimmingCharacters(in: .whitespaces).lowercased()
        if ["true", "1", "yes", "on"].contains(s) { return true }
        if ["false", "0", "no", "off", ""].contains(s) { return false }
        throw InvalidValue(message: "'\(s)' is not a boolean")
    }
}

/// Owns the current Settings and persists them to config.json.
final class SettingsStore {
    private let url: URL
    private let lock = NSLock()
    private var settings = Settings()

    /// Called (on the thread that made the change) after every successful update.
    /// Register handlers before any other thread starts using the store.
    var onChanged: [() -> Void] = []

    init(url: URL) {
        self.url = url
    }

    var current: Settings {
        lock.lock()
        defer { lock.unlock() }
        return settings
    }

    func load() {
        guard FileManager.default.fileExists(atPath: url.path) else {
            save(settings)
            Log.info("Created default config at \(url.path)")
            return
        }
        do {
            let text = try String(contentsOf: url, encoding: .utf8)
            guard let values = try Json.parse(text) as? [String: Any] else {
                throw Json.ParseError(description: "config root must be an object")
            }
            var next = Settings()
            for (key, value) in values where key.lowercased() != "configversion" {
                if let error = next.apply(key, value) { Log.warn("config.json: \(error) (using default)") }
            }
            lock.lock()
            settings = next
            lock.unlock()
            Log.info("Loaded config from \(url.path)")
        } catch {
            Log.error("Could not read \(url.path), using defaults", error)
        }
    }

    /// Applies a set of changes atomically. Returns an error message (nothing is applied), or nil on success.
    /// Only `Settings.runtimeKeys` are accepted unless `allowAll` is set.
    @discardableResult
    func update(_ changes: [(String, Any)], allowAll: Bool = false) -> String? {
        lock.lock()
        var next = settings
        var any = false
        for (key, value) in changes {
            if !allowAll && !Settings.runtimeKeys.contains(where: { $0.caseInsensitiveCompare(key) == .orderedSame }) {
                lock.unlock()
                return "'\(key)' cannot be changed at runtime (runtime settings: \(Settings.runtimeKeys.joined(separator: ", ")))"
            }
            if let error = next.apply(key, value) {
                lock.unlock()
                return error
            }
            any = true
        }
        guard any else {
            lock.unlock()
            return nil
        }
        settings = next
        save(next)
        lock.unlock()
        onChanged.forEach { $0() }
        return nil
    }

    /// Flips an on/off runtime setting in one step. Returns an error message, or nil on success.
    func toggle(_ key: String) -> String? {
        lock.lock()
        let trimmed = key.trimmingCharacters(in: .whitespaces)
        guard let name = settings.toggleKeys.first(where: { $0.caseInsensitiveCompare(trimmed) == .orderedSame }),
              let value = settings.toDictionary(includeSecrets: false)[name] as? Bool else {
            let keys = settings.toggleKeys.joined(separator: ", ")
            lock.unlock()
            return "'\(key)' is not an on/off setting (on/off settings: \(keys))"
        }
        var next = settings
        _ = next.apply(name, !value)
        settings = next
        save(next)
        lock.unlock()
        onChanged.forEach { $0() }
        return nil
    }

    private func save(_ s: Settings) {
        do {
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try (Json.serialize(s.toDictionary(includeSecrets: true), pretty: true) + "\n").write(to: url, atomically: true, encoding: .utf8)
        } catch {
            Log.error("Could not save \(url.path)", error)
        }
    }
}
