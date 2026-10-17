import AppKit

/// Main thread. Resolves the active layout's target (a presenter view, or a screen), keeps a borderless,
/// click-through panel over the layout's rectangle of it (plus a black backdrop panel for blackout layouts),
/// and hands it the latest timer state 10 times a second (30 while fading at zero).
final class OverlayController {
    private static let tickInterval = 0.1
    private static let blinkTickInterval = 0.033    // ~30 fps while fading
    private static let blinkPeriod = 2.0            // one full fade out + fade in
    private static let blinkMinLevel = 0.12
    private static let screensRefreshInterval = 3.0

    private let timer: TimerModel
    private let settings: SettingsStore
    private let panel: NSPanel
    private let backdrop: NSPanel
    private let view = OverlayView()
    private var tick: Timer?
    private var tickInterval = 0.0
    private var expiredSince: TimeInterval?
    private var presenters: [PresenterTarget] = []
    private var screens: [ScreenEntry] = []
    private var screensAt: TimeInterval = -.infinity
    private var screensSummary = ""
    private var lastLayout = ""
    private var lastTarget: String??

    /// What the timer is placed on ("Keynote presenter display", "Screen 2 · DELL U2720"), nil when not found.
    private(set) var targetDescription: String?

    init(timer: TimerModel, settings: SettingsStore) {
        self.timer = timer
        self.settings = settings

        panel = Self.makePanel(level: .screenSaver, clickThrough: settings.current.clickThrough)
        panel.contentView = view
        // Black, just below the timer; still above Keynote's slideshow windows (layer 25).
        backdrop = Self.makePanel(level: NSWindow.Level(rawValue: NSWindow.Level.screenSaver.rawValue - 1), clickThrough: settings.current.clickThrough)
        backdrop.isOpaque = true
        backdrop.backgroundColor = .black

        NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main) { [weak self] _ in
            self?.screensAt = -.infinity
            self?.update()
        }
        schedule(Self.tickInterval)
    }

    private static func makePanel(level: NSWindow.Level, clickThrough: Bool) -> NSPanel {
        let p = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 200, height: 80),
                        styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        p.isOpaque = false
        p.backgroundColor = .clear
        p.hasShadow = false
        p.hidesOnDeactivate = false
        p.isReleasedWhenClosed = false
        p.becomesKeyOnlyIfNeeded = true
        p.ignoresMouseEvents = clickThrough
        // Above Keynote's slideshow windows (layer 25) and anything a presenter app might float.
        p.level = level
        // Follow the presenter view into full-screen spaces and onto every space.
        p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        return p
    }

    /// The presenter views PresenterFinder currently sees.
    func setPresenters(_ list: [PresenterTarget]) {
        presenters = list
        update()
    }

    private func schedule(_ interval: TimeInterval) {
        guard interval != tickInterval else { return }
        tickInterval = interval
        tick?.invalidate()
        let t = Timer(timeInterval: interval, repeats: true) { [weak self] _ in self?.update() }
        RunLoop.main.add(t, forMode: .common)
        tick = t
    }

    private func update() {
        let cfg = settings.current
        let layout = cfg.activeLayout
        refreshScreens()
        if layout.name != lastLayout {
            lastLayout = layout.name
            Log.info("Layout '\(layout.name)': \(Json.serialize(layout.toDictionary(includeName: false)))")
        }

        let (target, missing) = resolve(layout)
        targetDescription = target?.description
        timer.setTarget(target?.description, width: Int(target?.frame.width ?? 0), height: Int(target?.frame.height ?? 0))
        if lastTarget != .some(target?.description) {
            lastTarget = .some(target?.description)
            if let target {
                Log.info("Timer target: \(target.description) at \(Int(target.frame.minX)),\(Int(target.frame.minY)) \(Int(target.frame.width))x\(Int(target.frame.height))")
            } else {
                Log.info("Timer target: none (\(missing))")
            }
        }
        guard let target else {
            hide()
            return
        }

        let snap = timer.snapshot()
        let level = blinkLevel(snap, cfg)
        guard snap.visible else {
            hide()
            return
        }

        if layout.blackout {
            let area = Self.appKitRect(target.frame)
            if backdrop.frame != area { backdrop.setFrame(area, display: false) }
            if !backdrop.isVisible { backdrop.orderFrontRegardless() }
        } else if backdrop.isVisible {
            backdrop.orderOut(nil)
        }

        let frame = Self.appKitRect(Self.timerRect(target.frame, layout))
        if panel.frame != frame { panel.setFrame(frame, display: false) }
        panel.alphaValue = CGFloat(max(0.2, min(1, layout.opacity)))
        view.update(snap, cfg, level)
        if !panel.isVisible { panel.orderFrontRegardless() }
    }

    /// The area the layout puts the timer on, or why there is none.
    private func resolve(_ layout: Layout) -> (target: (description: String, frame: CGRect)?, missing: String) {
        if layout.isScreen {
            guard let screen = Screens.resolve(screens, layout.screen) else {
                return (nil, "no screen matches '\(layout.screen)' (\(screens.count) connected)")
            }
            return (("Screen \(screen.description.id) · \(screen.description.name)", screen.frame), "")
        }
        let match = presenters.first { p in
            switch layout.target {
            case "powerpoint": return p.app == "PowerPoint"
            case "keynote": return p.app == "Keynote"
            default: return true
            }
        }
        guard let match else {
            let app = layout.target == "powerpoint" ? "PowerPoint" : layout.target == "keynote" ? "Keynote" : "Keynote or PowerPoint"
            return (nil, "no \(app) presenter view")
        }
        return ((match.app == "Keynote" ? "Keynote presenter display" : "PowerPoint presenter view", match.frame), "")
    }

    private func refreshScreens() {
        let now = ProcessInfo.processInfo.systemUptime
        guard now - screensAt >= Self.screensRefreshInterval else { return }
        screensAt = now
        screens = Screens.list()
        let summary = Screens.summary(screens)
        guard summary != screensSummary else { return }
        screensSummary = summary
        Log.info("Screens: \(summary)")
        timer.setScreens(screens.map(\.description))
    }

    private func hide() {
        if panel.isVisible { panel.orderOut(nil) }
        if backdrop.isVisible { backdrop.orderOut(nil) }
        schedule(Self.tickInterval)
    }

    /// The layout's rectangle (percentages of the target area), kept inside it, in global display coordinates.
    private static func timerRect(_ host: CGRect, _ layout: Layout) -> CGRect {
        let w = clamp((host.width * layout.widthPercent / 100).rounded(), 8, host.width)
        let h = clamp((host.height * layout.heightPercent / 100).rounded(), 8, host.height)
        let x = clamp((host.width * layout.xPercent / 100).rounded(), 0, host.width - w)
        let y = clamp((host.height * layout.yPercent / 100).rounded(), 0, host.height - h)
        return CGRect(x: host.minX + x, y: host.minY + y, width: w, height: h)
    }

    /// Global display coordinates (y down from the main display's top) to AppKit screen coordinates (y up).
    private static func appKitRect(_ r: CGRect) -> NSRect {
        let mainHeight = CGDisplayBounds(CGMainDisplayID()).height
        return NSRect(x: r.minX, y: mainHeight - r.maxY, width: r.width, height: r.height)
    }

    private static func clamp(_ v: CGFloat, _ lo: CGFloat, _ hi: CGFloat) -> CGFloat { max(lo, min(hi, v)) }

    /// 1 = fully visible. While blinking at zero, a cosine fade that starts at full brightness when
    /// zero is reached; the tick speeds up meanwhile so the fade is smooth.
    private func blinkLevel(_ snap: TimerSnapshot, _ cfg: Settings) -> Double {
        let blinking = snap.phase == "expired" && snap.running && cfg.blinkAtZero
        schedule(blinking ? Self.blinkTickInterval : Self.tickInterval)
        guard blinking else {
            expiredSince = nil
            return 1
        }
        let now = ProcessInfo.processInfo.systemUptime
        if expiredSince == nil { expiredSince = now }
        let wave = 0.5 + 0.5 * cos(2 * Double.pi * (now - expiredSince!) / Self.blinkPeriod)
        return Self.blinkMinLevel + (1 - Self.blinkMinLevel) * wave
    }
}

/// Draws the digits (fitted to the window), with an optional dark outline or solid rounded box.
final class OverlayView: NSView {
    private static let amber = NSColor(srgbRed: 1, green: 176 / 255, blue: 0, alpha: 1)
    private static let red = NSColor(srgbRed: 235 / 255, green: 45 / 255, blue: 45 / 255, alpha: 1)
    private static let solidBack = NSColor(srgbRed: 24 / 255, green: 24 / 255, blue: 24 / 255, alpha: 1)
    private static let probeSize: CGFloat = 100

    private var snap = TimerSnapshot()
    private var cfg = Settings()
    private var level = 1.0
    private var renderKey = ""

    override var isOpaque: Bool { false }

    func update(_ snap: TimerSnapshot, _ cfg: Settings, _ blinkLevel: Double) {
        let level = (blinkLevel * 64).rounded() / 64 // quantised: skips redraws of invisible differences
        let key = "\(snap.display)|\(snap.phase)|\(snap.running)|\(level)|\(bounds.size)|\(cfg.transparentBackground)|" +
            "\(cfg.textOutline)|\(cfg.warnEnabled)|\(cfg.criticalEnabled)"
        guard key != renderKey else { return }
        renderKey = key
        self.snap = snap
        self.cfg = cfg
        self.level = level
        needsDisplay = true
    }

    override func draw(_ dirtyRect: NSRect) {
        guard let g = NSGraphicsContext.current?.cgContext else { return }
        g.clear(bounds)
        let size = bounds.size
        let alpha = (snap.running ? 1.0 : 0.55) * level
        let (fore, back) = colors()

        if !cfg.transparentBackground {
            let r = size.height * 0.12
            g.addPath(CGPath(roundedRect: bounds, cornerWidth: r, cornerHeight: r, transform: nil))
            g.setFillColor(back.withAlphaComponent(alpha).cgColor)
            g.fillPath()
        }

        let outline = cfg.textOutline ? max(1.5, size.height * 0.045) : 0
        let pad = size.height * 0.06 + outline
        let area = bounds.insetBy(dx: pad, dy: pad)
        guard area.width > 1, area.height > 1 else { return }

        // Size from a template with every digit replaced by '0' so the text doesn't pulse as digits change.
        let template = Self.path(String(snap.display.map { $0.isNumber ? "0" : $0 })).boundingBoxOfPath
        guard template.width > 0, template.height > 0 else { return }
        let scale = min(area.width / template.width, area.height / template.height)

        let text = Self.path(snap.display)
        let actual = text.boundingBoxOfPath
        let dx = area.minX + (area.width - actual.width * scale) / 2 - actual.minX * scale
        let dy = area.minY + (area.height - template.height * scale) / 2 - template.minY * scale
        var transform = CGAffineTransform(a: scale, b: 0, c: 0, d: scale, tx: dx, ty: dy)
        guard let placed = text.copy(using: &transform) else { return }

        if outline > 0 {
            // Twice the width: the fill covers the inner half.
            g.addPath(placed)
            g.setLineWidth(outline * 2)
            g.setLineJoin(.round)
            g.setStrokeColor(NSColor(white: 0, alpha: alpha * 220 / 255).cgColor)
            g.strokePath()
        }
        g.addPath(placed)
        g.setFillColor(fore.withAlphaComponent(alpha).cgColor)
        g.fillPath()
    }

    private func colors() -> (fore: NSColor, back: NSColor) {
        // At zero, keep the most severe colour that is enabled.
        var phase = snap.phase
        if phase == "expired" { phase = cfg.criticalEnabled ? "critical" : cfg.warnEnabled ? "warning" : "normal" }
        let transparent = cfg.transparentBackground
        switch phase {
        case "warning": return (transparent ? Self.amber : .black, Self.amber)
        case "critical": return (transparent ? Self.red : .white, Self.red)
        default: return (.white, Self.solidBack)
        }
    }

    /// Glyph outlines of `text` in the bold system font with tabular digits, at the probe size.
    private static func path(_ text: String) -> CGPath {
        let font = NSFont.monospacedDigitSystemFont(ofSize: probeSize, weight: .bold)
        let line = CTLineCreateWithAttributedString(NSAttributedString(string: text, attributes: [.font: font]))
        let path = CGMutablePath()
        for run in CTLineGetGlyphRuns(line) as? [CTRun] ?? [] {
            let attributes = CTRunGetAttributes(run) as NSDictionary
            let runFont = attributes[kCTFontAttributeName] as! CTFont
            let count = CTRunGetGlyphCount(run)
            var glyphs = [CGGlyph](repeating: 0, count: count)
            var positions = [CGPoint](repeating: .zero, count: count)
            CTRunGetGlyphs(run, CFRange(location: 0, length: count), &glyphs)
            CTRunGetPositions(run, CFRange(location: 0, length: count), &positions)
            for i in 0..<count {
                guard let glyph = CTFontCreatePathForGlyph(runFont, glyphs[i], nil) else { continue }
                path.addPath(glyph, transform: CGAffineTransform(translationX: positions[i].x, y: positions[i].y))
            }
        }
        return path
    }
}
