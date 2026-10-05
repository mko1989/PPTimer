import AppKit
import ServiceManagement

/// Wires the timer, the API server, presenter detection and the overlay together, and owns the menu bar item.
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    static var dataDirectory: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("PPTimer")
    }

    private var settings: SettingsStore!
    private var timer: TimerModel!
    private var api: ApiServer!
    private var finder: PresenterFinder!
    private var overlay: OverlayController!
    private var statusItem: NSStatusItem!
    private var statusTimer: Timer?
    private var lastTitle = ""
    private var listening = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        let others = NSRunningApplication.runningApplications(withBundleIdentifier: Bundle.main.bundleIdentifier ?? "")
            .filter { $0 != NSRunningApplication.current }
        if !others.isEmpty {
            NSApp.terminate(nil) // already running; it owns the port
            return
        }

        let dir = Self.dataDirectory
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        Log.initialize(directory: dir)
        Log.echoToConsole = isatty(STDERR_FILENO) != 0
        Log.info("PPTimer \(ApiServer.version) starting on macOS \(ProcessInfo.processInfo.operatingSystemVersionString)")

        settings = SettingsStore(url: dir.appendingPathComponent("config.json"))
        settings.load()
        timer = TimerModel(settings: settings)
        timer.onZeroReached.append { [weak self] in
            DispatchQueue.main.async {
                guard let self, self.settings.current.soundEnabled else { return }
                SoundAlert.play(self.settings.current.soundFile)
            }
        }
        timer.onSoundTestRequested.append { [weak self] in
            DispatchQueue.main.async { if let self { SoundAlert.play(self.settings.current.soundFile) } }
        }

        overlay = OverlayController(timer: timer, settings: settings)
        finder = PresenterFinder()
        finder.onChange = { [weak self] target in
            self?.overlay.setTarget(target)
            self?.api.notifyChanged()
        }

        api = ApiServer(timer: timer, settings: settings, diagnostics: { [weak self] done in self?.finder.diagnostics(done) })
        api.onListeningChanged = { [weak self] in self?.listening = $0 }
        api.start()
        finder.start()

        setUpStatusItem()
    }

    func applicationWillTerminate(_ notification: Notification) {
        api?.stop()
        Log.info("PPTimer stopped")
    }

    // ---- Menu bar ---------------------------------------------------------------------------------

    private func setUpStatusItem() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        let menu = NSMenu()
        menu.delegate = self
        statusItem.menu = menu
        updateTitle()
        let t = Timer(timeInterval: 0.25, repeats: true) { [weak self] _ in self?.updateTitle() }
        RunLoop.main.add(t, forMode: .common)
        statusTimer = t
    }

    private func updateTitle() {
        let snap = timer.snapshot()
        let title = "\(snap.display)|\(snap.phase)|\(snap.running)|\(snap.presenterView)"
        guard title != lastTitle, let button = statusItem.button else { return }
        lastTitle = title
        let color: NSColor
        switch snap.phase {
        case "warning": color = .systemOrange
        case "critical", "expired": color = .systemRed
        default: color = snap.running ? .labelColor : .secondaryLabelColor
        }
        let icon = snap.presenterView ? "◉ " : "○ "
        button.attributedTitle = NSAttributedString(string: icon + snap.display, attributes: [
            .font: NSFont.monospacedDigitSystemFont(ofSize: NSFont.systemFontSize, weight: .medium),
            .foregroundColor: color,
        ])
        button.toolTip = snap.presenterView ? "PPTimer: on the presenter view" : "PPTimer: no presenter view"
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        let snap = timer.snapshot()
        let port = settings.current.port

        menu.addItem(info("PPTimer \(ApiServer.version)"))
        menu.addItem(info(presenterStatus()))
        if listening {
            for address in Self.lanAddresses() { menu.addItem(info("Remote: http://\(address):\(port)/")) }
        } else {
            menu.addItem(info("⚠︎ Not listening on port \(port) (in use?)"))
        }
        if finder.powerPointConsentStatus == OSStatus(errAEEventNotPermitted) {
            menu.addItem(item("Allow PowerPoint Access…", #selector(openAutomationSettings)))
        }

        menu.addItem(.separator())
        menu.addItem(item(snap.running ? "Pause" : "Start", #selector(toggleRunning), key: " "))
        menu.addItem(item("Reset to \(TimerModel.format(Int(snap.durationMs / 1000), showMinus: true))", #selector(resetTimer), key: "r"))
        let add = item("Add 1 Minute", #selector(addMinute), key: "=")
        let remove = item("Remove 1 Minute", #selector(removeMinute), key: "-")
        menu.addItem(add)
        menu.addItem(remove)

        // Operator-only: the presenter's overlay never shows the speed.
        menu.addItem(info(snap.speedPercent == 100 ? "Speed: normal" : "Speed: \(Self.percent(snap.speedPercent)) of real time"))
        menu.addItem(item("Run Faster (+5%)", #selector(faster), key: "]"))
        menu.addItem(item("Run Slower (−5%)", #selector(slower), key: "["))
        if snap.speedPercent != 100 { menu.addItem(item("Back to Normal Speed", #selector(normalSpeed))) }
        let visible = item("Show Timer on Presenter View", #selector(toggleVisible))
        visible.state = snap.visible ? .on : .off
        menu.addItem(visible)

        menu.addItem(.separator())
        menu.addItem(item("Open Remote in Browser", #selector(openRemote)))
        menu.addItem(item("Open Display Page", #selector(openDisplay)))
        menu.addItem(item("Open Settings Folder", #selector(openDataFolder)))
        if #available(macOS 13.0, *) {
            let login = item("Open at Login", #selector(toggleLoginItem))
            login.state = SMAppService.mainApp.status == .enabled ? .on : .off
            menu.addItem(login)
        }

        menu.addItem(.separator())
        menu.addItem(item("Quit PPTimer", #selector(quit), key: "q"))
    }

    private func presenterStatus() -> String {
        guard let target = overlay.target else { return "No presenter view (start a slideshow)" }
        let screen = NSScreen.screens.first {
            ($0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value == target.displayID
        }
        return "On \(target.app) presenter view" + (screen.map { " · \($0.localizedName)" } ?? "")
    }

    private func info(_ title: String) -> NSMenuItem {
        let i = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        i.isEnabled = false
        return i
    }

    private func item(_ title: String, _ action: Selector, key: String = "") -> NSMenuItem {
        let i = NSMenuItem(title: title, action: action, keyEquivalent: key)
        i.keyEquivalentModifierMask = key == " " ? [] : .command
        i.target = self
        return i
    }

    private func run(_ cmd: String, _ args: [(String, String)] = []) {
        var a = Args()
        for (k, v) in args { a[k] = v }
        if let error = Commands.execute(timer, settings, cmd, a) { Log.warn("Menu \(cmd): \(error)") }
        api.notifyChanged()
        lastTitle = ""
        updateTitle()
    }

    @objc private func toggleRunning() { run("toggle") }
    @objc private func resetTimer() { run("reset") }
    @objc private func addMinute() { run("add", [("seconds", "60")]) }
    @objc private func removeMinute() { run("add", [("seconds", "-60")]) }
    @objc private func toggleVisible() { run("togglevisible") }
    @objc private func faster() { run("speed", [("step", "5")]) }
    @objc private func slower() { run("speed", [("step", "-5")]) }
    @objc private func normalSpeed() { run("speed", [("percent", "100")]) }

    private static func percent(_ v: Double) -> String {
        (v == v.rounded() ? String(Int(v)) : String(format: "%.1f", v)) + "%"
    }

    @objc private func openRemote() { openPage("") }
    @objc private func openDisplay() { openPage("display") }

    private func openPage(_ path: String) {
        let token = settings.current.apiToken
        var url = "http://localhost:\(settings.current.port)/\(path)"
        if !token.isEmpty { url += "?token=" + (token.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? token) }
        if let u = URL(string: url) { NSWorkspace.shared.open(u) }
    }

    @objc private func openDataFolder() {
        NSWorkspace.shared.activateFileViewerSelecting([Self.dataDirectory.appendingPathComponent("config.json")])
    }

    @objc private func openAutomationSettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Automation") {
            NSWorkspace.shared.open(url)
        }
    }

    @objc private func toggleLoginItem() {
        guard #available(macOS 13.0, *) else { return }
        do {
            if SMAppService.mainApp.status == .enabled {
                try SMAppService.mainApp.unregister()
            } else {
                try SMAppService.mainApp.register()
            }
        } catch {
            Log.error("Could not change the login item", error)
        }
    }

    @objc private func quit() { NSApp.terminate(nil) }

    /// IPv4 addresses of the network interfaces that are up (for the remote URL in the menu).
    private static func lanAddresses() -> [String] {
        var result: [String] = []
        var ifaddr: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&ifaddr) == 0, let first = ifaddr else { return ["localhost"] }
        defer { freeifaddrs(ifaddr) }
        for ptr in sequence(first: first, next: { $0.pointee.ifa_next }) {
            let flags = Int32(ptr.pointee.ifa_flags)
            guard let addr = ptr.pointee.ifa_addr, addr.pointee.sa_family == UInt8(AF_INET),
                  flags & IFF_UP != 0, flags & IFF_LOOPBACK == 0 else { continue }
            var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            if getnameinfo(addr, socklen_t(addr.pointee.sa_len), &host, socklen_t(host.count), nil, 0, NI_NUMERICHOST) == 0 {
                let ip = String(cString: host)
                if !ip.hasPrefix("169.254.") { result.append(ip) }
            }
        }
        return result.isEmpty ? ["localhost"] : result
    }
}
