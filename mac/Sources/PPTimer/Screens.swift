import AppKit

/// One display, numbered the way a layout's "screen" setting refers to it.
struct ScreenEntry {
    var description: ScreenDescription
    var displayID: CGDirectDisplayID
    /// Global display coordinates (origin top-left of the main display, y down), like PresenterTarget.frame.
    var frame: CGRect
}

/// The displays, numbered like on Windows: 1 = the main display (with the menu bar), then the others left to right.
/// "external" = the first one that isn't a built-in panel (and isn't the main display, if that is external too).
/// Mirrored displays count once, so with mirroring there is no external screen. Main thread (NSScreen).
enum Screens {
    static func list() -> [ScreenEntry] {
        var count: UInt32 = 0
        CGGetActiveDisplayList(0, nil, &count)
        var ids = [CGDirectDisplayID](repeating: 0, count: Int(count))
        CGGetActiveDisplayList(count, &ids, &count)
        let main = CGMainDisplayID()
        // A mirror shows another display's image; only the display it mirrors is listed.
        let shown = ids.prefix(Int(count)).filter { CGDisplayMirrorsDisplay($0) == kCGNullDirectDisplay }
        let sorted = shown.sorted { a, b in
            if (a == main) != (b == main) { return a == main }
            let ba = CGDisplayBounds(a), bb = CGDisplayBounds(b)
            return ba.minX != bb.minX ? ba.minX < bb.minX : ba.minY < bb.minY
        }
        var names: [CGDirectDisplayID: String] = [:]
        for screen in NSScreen.screens {
            if let n = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber {
                names[n.uint32Value] = screen.localizedName
            }
        }
        return sorted.enumerated().map { i, id in
            let bounds = CGDisplayBounds(id)
            let builtIn = CGDisplayIsBuiltin(id) != 0
            return ScreenEntry(
                description: ScreenDescription(
                    id: String(i + 1),
                    name: names[id] ?? (builtIn ? "Built-in display" : "Display \(i + 1)"),
                    width: Int(bounds.width), height: Int(bounds.height),
                    main: id == main, external: !builtIn),
                displayID: id,
                frame: bounds)
        }
    }

    /// The screen a layout's "screen" setting means, or nil (e.g. "external" with only the built-in display).
    static func resolve(_ screens: [ScreenEntry], _ spec: String) -> ScreenEntry? {
        let spec = spec.trimmingCharacters(in: .whitespaces)
        switch spec.lowercased() {
        case "main":
            return screens.first { $0.description.main } ?? screens.first
        case "external":
            return screens.first { $0.description.external && !$0.description.main } ?? screens.first { !$0.description.main }
        default:
            if let n = Int(spec) { return n >= 1 && n <= screens.count ? screens[n - 1] : nil }
            return screens.first { $0.description.name.range(of: spec, options: .caseInsensitive) != nil }
        }
    }

    /// "1 Built-in Retina Display 1440x900 at 0,0 main built-in; 2 ..." for the log.
    static func summary(_ screens: [ScreenEntry]) -> String {
        screens.map { s in
            let d = s.description
            return "\(d.id) \(d.name) \(d.width)x\(d.height) at \(Int(s.frame.minX)),\(Int(s.frame.minY))" +
                (d.main ? " main" : "") + (d.external ? "" : " built-in")
        }.joined(separator: "; ")
    }
}
