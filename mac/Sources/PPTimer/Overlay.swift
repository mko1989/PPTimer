import AppKit

/// Main thread. Keeps a borderless, click-through panel over the configured rectangle of the presenter
/// view, and hands it the latest timer state 10 times a second (30 while fading at zero).
final class OverlayController {
    private static let tickInterval = 0.1
    private static let blinkTickInterval = 0.033    // ~30 fps while fading
    private static let blinkPeriod = 2.0            // one full fade out + fade in
    private static let blinkMinLevel = 0.12

    private let timer: TimerModel
    private let settings: SettingsStore
    private let panel: NSPanel
    private let view = OverlayView()
    private var tick: Timer?
    private var tickInterval = 0.0
    private var expiredSince: TimeInterval?

    private(set) var target: PresenterTarget?

    init(timer: TimerModel, settings: SettingsStore) {
        self.timer = timer
        self.settings = settings

        panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 200, height: 80),
                        styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.becomesKeyOnlyIfNeeded = true
        panel.ignoresMouseEvents = settings.current.clickThrough
        // Above Keynote's slideshow windows (layer 25) and anything a presenter app might float.
        panel.level = .screenSaver
        // Follow the presenter view into full-screen spaces and onto every space.
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        panel.contentView = view

        schedule(Self.tickInterval)
    }

    func setTarget(_ target: PresenterTarget?) {
        self.target = target
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
        guard let target else {
            timer.setPresenterView(false, width: 0, height: 0)
            hide()
            return
        }
        timer.setPresenterView(true, width: Int(target.frame.width), height: Int(target.frame.height))

        let snap = timer.snapshot()
        let level = blinkLevel(snap, cfg)
        guard snap.visible else {
            hide()
            return
        }

        let frame = Self.computeFrame(target.frame, cfg)
        if panel.frame != frame { panel.setFrame(frame, display: false) }
        panel.alphaValue = CGFloat(max(0.2, min(1, cfg.opacity)))
        view.update(snap, cfg, level)
        if !panel.isVisible { panel.orderFrontRegardless() }
    }

    private func hide() {
        if panel.isVisible { panel.orderOut(nil) }
        schedule(Self.tickInterval)
    }

    /// The configured rectangle (percentages of the presenter view), kept inside it, in AppKit
    /// screen coordinates (origin bottom-left of the main display).
    private static func computeFrame(_ host: CGRect, _ cfg: Settings) -> NSRect {
        let w = clamp((host.width * cfg.widthPercent / 100).rounded(), 8, host.width)
        let h = clamp((host.height * cfg.heightPercent / 100).rounded(), 8, host.height)
        let x = clamp((host.width * cfg.xPercent / 100).rounded(), 0, host.width - w)
        let y = clamp((host.height * cfg.yPercent / 100).rounded(), 0, host.height - h)
        let mainHeight = CGDisplayBounds(CGMainDisplayID()).height
        return NSRect(x: host.minX + x, y: mainHeight - (host.minY + y + h), width: w, height: h)
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
