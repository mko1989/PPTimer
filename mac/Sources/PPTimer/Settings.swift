import Foundation

/// A named layout preset: where the timer goes (a presenter view, or a screen), its rectangle in % of that
/// area, and how it looks. Exactly one is active; the flat settings (xPercent, opacity, ...) read and write it.
/// Same keys as the Windows app's Layout.
struct Layout: Equatable {
    /// presenter = PowerPoint or Keynote, whichever shows one; screen = a whole display.
    static let targets = ["presenter", "powerpoint", "keynote", "screen"]

    /// Settings that belong to a layout (also readable / writable as flat settings).
    static let keys = [
        "target", "screen", "xPercent", "yPercent", "widthPercent", "heightPercent",
        "opacity", "transparentBackground", "textOutline", "blackout",
    ]

    var name = "Layout"
    var target = "powerpoint"
    /// For target "screen": "main", "external", a number (1 = main, then left to right) or part of the screen's name.
    var screen = "main"
    var xPercent = 22.0
    var yPercent = 74.0
    var widthPercent = 16.0
    var heightPercent = 9.0
    var opacity = 1.0
    var transparentBackground = true
    var textOutline = true
    /// Paints the whole target area black behind the timer (fullscreen display).
    var blackout = false

    var isScreen: Bool { target == "screen" }

    static func isKey(_ key: String) -> Bool { keys.contains { $0.caseInsensitiveCompare(key) == .orderedSame } }

    /// The presets a new install starts with.
    static let defaults = [
        Layout(name: "PowerPoint", target: "powerpoint"),
        Layout(name: "Keynote", target: "keynote"),
        Layout(name: "Screen", target: "screen", screen: "main", xPercent: 80, yPercent: 2, widthPercent: 18, heightPercent: 10, transparentBackground: false),
        Layout(name: "Fullscreen", target: "screen", screen: "external", xPercent: 5, yPercent: 15, widthPercent: 90, heightPercent: 70, textOutline: false, blackout: true),
    ]

    func toDictionary(includeName: Bool = true) -> [String: Any] {
        var d: [String: Any] = [
            "target": target,
            "screen": screen,
            "xPercent": xPercent,
            "yPercent": yPercent,
            "widthPercent": widthPercent,
            "heightPercent": heightPercent,
            "opacity": opacity,
            "transparentBackground": transparentBackground,
            "textOutline": textOutline,
            "blackout": blackout,
        ]
        if includeName { d["name"] = name }
        return d
    }

    /// Applies one layout key; throws Settings.InvalidValue.
    mutating func apply(_ key: String, _ value: Any) throws {
        switch key.lowercased() {
        case "target":
            let t = Json.argString(value).trimmingCharacters(in: .whitespaces).lowercased()
            guard Self.targets.contains(t) else { throw Settings.InvalidValue(message: "use \(Self.targets.joined(separator: ", "))") }
            target = t
        case "screen":
            let v = Json.argString(value).trimmingCharacters(in: .whitespaces)
            guard !v.isEmpty else { throw Settings.InvalidValue(message: "use main, external, a screen number or part of its name") }
            screen = ["main", "external"].contains(v.lowercased()) ? v.lowercased() : v
        case "xpercent": xPercent = try Settings.range(value, 0, 100)
        case "ypercent": yPercent = try Settings.range(value, 0, 100)
        case "widthpercent": widthPercent = try Settings.range(value, 2, 100)
        case "heightpercent": heightPercent = try Settings.range(value, 2, 100)
        case "opacity": opacity = try Settings.range(value, 0.2, 1)
        case "transparentbackground": transparentBackground = try Settings.bool(value)
        case "textoutline": textOutline = try Settings.bool(value)
        case "blackout": blackout = try Settings.bool(value)
        default: throw Settings.InvalidValue(message: "unknown layout setting")
        }
    }

    /// One entry of config.json's "layouts" list. Bad values are logged and left at their defaults.
    static func from(_ value: Any) throws -> Layout {
        guard let d = value as? [String: Any] else {
            throw Settings.InvalidValue(message: "each layout must be an object like {\"name\": \"Keynote\", \"target\": \"keynote\", ...}")
        }
        let name = d.first { $0.key.lowercased() == "name" }.map { Json.argString($0.value).trimmingCharacters(in: .whitespaces) } ?? ""
        guard !name.isEmpty else { throw Settings.InvalidValue(message: "a layout has no name") }
        var layout = Layout(name: name)
        for (key, v) in d where key.lowercased() != "name" {
            do { try layout.apply(key, v) } catch let e as Settings.InvalidValue {
                Log.warn("config.json layout '\(name)': invalid value for '\(key)': \(e.message) (using default)")
            }
        }
        return layout
    }
}

/// Settings snapshot (a value type, so every read is a consistent copy). Same keys and ranges as the
/// Windows app's config.json, minus the Windows-only presenter window classes.
struct Settings {
    /// 3: position and look moved into "layouts".
    static let currentConfigVersion = 3

    /// Keys that can be changed at runtime over the API. Everything else is file-only.
    /// "layout" selects a layout by name; the layout keys change the active layout.
    static let runtimeKeys = [
        "layout", "target", "screen", "xPercent", "yPercent", "widthPercent", "heightPercent", "opacity",
        "transparentBackground", "textOutline", "blackout",
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

    // Layout presets: target, overlay rectangle (in % of the target) and look. Never empty.
    var layouts = Layout.defaults
    var activeLayoutIndex = 0
    var activeLayout: Layout {
        get { layouts[activeLayoutIndex] }
        set { layouts[activeLayoutIndex] = newValue }
    }

    var xPercent: Double { activeLayout.xPercent }
    var yPercent: Double { activeLayout.yPercent }
    var widthPercent: Double { activeLayout.widthPercent }
    var heightPercent: Double { activeLayout.heightPercent }
    var opacity: Double { activeLayout.opacity }
    var transparentBackground: Bool { activeLayout.transparentBackground }
    var textOutline: Bool { activeLayout.textOutline }

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

    /// For the API: the active layout's values also appear flat (xPercent, ...), as before layouts existed.
    func toDictionary(includeSecrets: Bool) -> [String: Any] {
        var d = toFileDictionary(includeSecrets: includeSecrets)
        for (k, v) in activeLayout.toDictionary(includeName: false) { d[k] = v }
        return d
    }

    /// What config.json holds: the layouts as a list, no flat copies.
    func toFileDictionary(includeSecrets: Bool = true) -> [String: Any] {
        var d: [String: Any] = [
            "configVersion": configVersion,
            "port": port,
            "clickThrough": clickThrough,
            "layout": activeLayout.name,
            "layouts": layouts.map { $0.toDictionary() },
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
            case "layouts": try setLayouts(value)
            case "layout": if let error = selectLayout(Json.argString(value)) { return error }
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
            default:
                guard Layout.isKey(key) else { return "Unknown setting '\(key)'" }
                try activeLayout.apply(key, value)
            }
            return nil
        } catch let e as InvalidValue {
            return "Invalid value for '\(key)': \(e.message)"
        } catch {
            return "Invalid value for '\(key)'"
        }
    }

    // ---- Layouts ------------------------------------------------------------------------------

    var layoutNames: [String] { layouts.map(\.name) }

    private func index(of name: String) -> Int? {
        let n = name.trimmingCharacters(in: .whitespaces)
        return layouts.firstIndex { $0.name.caseInsensitiveCompare(n) == .orderedSame }
    }

    private func unknownLayout(_ name: String) -> String {
        "No layout named '\(name)' (layouts: \(layoutNames.joined(separator: ", ")))"
    }

    mutating func selectLayout(_ name: String) -> String? {
        guard let i = index(of: name) else { return unknownLayout(name) }
        activeLayoutIndex = i
        return nil
    }

    /// Next (+1) or previous (-1) layout, wrapping around.
    mutating func stepLayout(_ step: Int) {
        let n = layouts.count
        activeLayoutIndex = ((activeLayoutIndex + step) % n + n) % n
    }

    /// Copies the active layout under `name` (replacing a layout of that name) and selects it.
    mutating func saveLayoutAs(_ name: String?) -> String? {
        let name = (name ?? "").trimmingCharacters(in: .whitespaces)
        if name.isEmpty { return "Give the new layout a 'name'" }
        if name.count > 40 { return "Layout names are limited to 40 characters" }
        var copy = activeLayout
        copy.name = name
        if let i = index(of: name) {
            layouts[i] = copy
            activeLayoutIndex = i
        } else {
            layouts.append(copy)
            activeLayoutIndex = layouts.count - 1
        }
        return nil
    }

    mutating func deleteLayout(_ name: String) -> String? {
        guard let i = index(of: name) else { return unknownLayout(name) }
        if layouts.count == 1 { return "The last layout cannot be deleted" }
        let active = activeLayout.name
        layouts.remove(at: i)
        activeLayoutIndex = index(of: active) ?? 0
        return nil
    }

    mutating func renameLayout(_ name: String, to: String?) -> String? {
        guard let i = index(of: name) else { return unknownLayout(name) }
        let to = (to ?? "").trimmingCharacters(in: .whitespaces)
        if to.isEmpty { return "Give the new name as 'to'" }
        if to.count > 40 { return "Layout names are limited to 40 characters" }
        if let other = index(of: to), other != i { return "There is already a layout named '\(layouts[other].name)'" }
        layouts[i].name = to
        return nil
    }

    private mutating func setLayouts(_ value: Any) throws {
        guard let list = value as? [Any], !list.isEmpty else { throw InvalidValue(message: "expected a non-empty list of layouts") }
        var result: [Layout] = []
        for item in list {
            let layout = try Layout.from(item)
            if result.contains(where: { $0.name.caseInsensitiveCompare(layout.name) == .orderedSame }) {
                throw InvalidValue(message: "two layouts are named '\(layout.name)'")
            }
            result.append(layout)
        }
        layouts = result
        activeLayoutIndex = 0
    }

    /// Applies a pre-layouts flat value (e.g. xPercent) to every presenter view layout.
    mutating func applyToPresenterLayouts(_ key: String, _ value: Any) {
        for i in layouts.indices where !layouts[i].isScreen { try? layouts[i].apply(key, value) }
    }

    /// "layouts" first, then "layout" (which one is active), then the values that go into it.
    static func applyOrder(_ key: String) -> Int {
        switch key.lowercased() {
        case "layouts": return 0
        case "layout": return 1
        default: return 2
        }
    }

    static func range(_ value: Any, _ min: Double, _ max: Double) throws -> Double {
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
            let fileVersion = (values["configVersion"] as? NSNumber)?.intValue ?? 1
            let hasLayouts = values.keys.contains { $0.lowercased() == "layouts" }
            var next = Settings()
            let ordered = values.sorted { Settings.applyOrder($0.key) < Settings.applyOrder($1.key) }
            for (key, value) in ordered where key.lowercased() != "configversion" {
                if !hasLayouts && Layout.isKey(key) {
                    // Written before layouts existed: the position was for the presenter view.
                    next.applyToPresenterLayouts(key, value)
                    continue
                }
                if let error = next.apply(key, value) { Log.warn("config.json: \(error) (using default)") }
            }
            lock.lock()
            settings = next
            lock.unlock()
            Log.info("Loaded config from \(url.path)")
            if fileVersion < Settings.currentConfigVersion {
                save(next)
                Log.info("Upgraded config.json from version \(fileVersion) to \(Settings.currentConfigVersion)")
            }
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
        for (key, value) in changes.sorted(by: { Settings.applyOrder($0.0) < Settings.applyOrder($1.0) }) {
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

    /// Runs `change` on a copy of the settings; if it returns nil (no error), the copy becomes current and is
    /// saved. Returns the error message, or nil on success.
    @discardableResult
    func mutate(_ change: (inout Settings) -> String?) -> String? {
        lock.lock()
        var next = settings
        if let error = change(&next) {
            lock.unlock()
            return error
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
            try (Json.serialize(s.toFileDictionary(), pretty: true) + "\n").write(to: url, atomically: true, encoding: .utf8)
        } catch {
            Log.error("Could not save \(url.path)", error)
        }
    }
}
