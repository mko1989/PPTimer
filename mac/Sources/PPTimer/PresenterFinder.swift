import AppKit
import CoreGraphics

/// Where the presenter view is, in global display coordinates (origin top-left of the main display,
/// y down, in points), the same space as CGWindowList and AppleScript bounds.
struct PresenterTarget: Equatable {
    var app: String
    var frame: CGRect
    var displayID: CGDirectDisplayID
}

/// Polls (on a background queue) for a presenter view:
///
/// - PowerPoint: asked over Apple Events for `bounds of every presenter view window`. Needs the
///   one-time Automation consent ("PPTimer wants to control Microsoft PowerPoint"). Only asked while
///   PowerPoint has a window covering a whole display, so normal editing never gets an Apple Event.
/// - Keynote: needs no permission. While a slideshow plays on two displays, the audience slideshow
///   is a full-display window at a higher level (25) than the presenter display (9). So the
///   presenter display is a full-display Keynote window on another display, below the slideshow level.
///   Playing on one display (or mirrored) gives only the slideshow, and nothing matches.
final class PresenterFinder {
    static let powerPointBundleID = "com.microsoft.Powerpoint"
    static let keynoteBundleID = "com.apple.iWork.Keynote"

    private let queue = DispatchQueue(label: "pptimer.finder", qos: .userInitiated)
    private let consentQueue = DispatchQueue(label: "pptimer.consent")
    private var source: DispatchSourceTimer?
    private var last: PresenterTarget?
    private var bundleIDs: [pid_t: String] = [:]
    private var consentAskedFor: pid_t = 0
    private var powerPointConsent: OSStatus?
    private var consentRecheckCountdown = 0
    private var lastLoggedConsent: OSStatus? // only touched on consentQueue
    private var lastAppleEventError: Int?

    /// Called on the main thread when the target appears, moves or disappears.
    var onChange: ((PresenterTarget?) -> Void)?

    /// Main-thread copy of the PowerPoint Automation consent (nil = not checked yet).
    private(set) var powerPointConsentStatus: OSStatus?

    func start() {
        let s = DispatchSource.makeTimerSource(queue: queue)
        s.schedule(deadline: .now(), repeating: .milliseconds(250))
        s.setEventHandler { [weak self] in self?.poll() }
        s.resume()
        source = s
    }

    private func poll() {
        let target = scan()
        guard target != last else { return }
        if let target {
            Log.info("\(target.app) presenter view found at \(Self.describe(target.frame)) on display \(target.displayID)")
        } else if let last {
            Log.info("\(last.app) presenter view gone")
        }
        last = target
        DispatchQueue.main.async { self.onChange?(target) }
    }

    // ---- Scanning -----------------------------------------------------------------------------

    private struct WindowInfo {
        var pid: pid_t
        var owner: String
        var layer: Int
        var bounds: CGRect
        var onScreen: Bool
        var number: Int
    }

    private func scan() -> PresenterTarget? {
        let displays = Self.displays()
        let windows = Self.windows(onScreenOnly: true)

        // Full-display windows per app.
        var fullByPid: [pid_t: [(window: WindowInfo, display: CGDirectDisplayID)]] = [:]
        for w in windows {
            guard let display = displays.first(where: { Self.covers(w.bounds, $0.bounds) }) else { continue }
            fullByPid[w.pid, default: []].append((w, display.id))
        }

        var powerPointRunning: pid_t = 0
        var found: PresenterTarget?
        for (pid, full) in fullByPid where found == nil {
            switch bundleID(pid) {
            case Self.keynoteBundleID:
                found = keynotePresenter(full, displays)
            case Self.powerPointBundleID:
                powerPointRunning = pid
                found = powerPointPresenter(pid: pid, displays)
            default:
                break
            }
        }
        if powerPointRunning == 0, let pid = NSRunningApplication.runningApplications(withBundleIdentifier: Self.powerPointBundleID).first?.processIdentifier {
            powerPointRunning = pid
        }
        askForPowerPointConsentIfNeeded(pid: powerPointRunning)
        return found
    }

    private func keynotePresenter(_ full: [(window: WindowInfo, display: CGDirectDisplayID)], _ displays: [(id: CGDirectDisplayID, bounds: CGRect)]) -> PresenterTarget? {
        guard let showLevel = full.map(\.window.layer).max(), showLevel > 0 else { return nil }
        let showDisplays = Set(full.filter { $0.window.layer == showLevel }.map(\.display))
        guard let presenter = full.first(where: { $0.window.layer > 0 && $0.window.layer < showLevel && !showDisplays.contains($0.display) }),
              let display = displays.first(where: { $0.id == presenter.display })
        else { return nil }
        return PresenterTarget(app: "Keynote", frame: display.bounds, displayID: display.id)
    }

    private func powerPointPresenter(pid: pid_t, _ displays: [(id: CGDirectDisplayID, bounds: CGRect)]) -> PresenterTarget? {
        guard powerPointConsent == noErr, let frame = powerPointPresenterBounds(pid: pid), frame.width > 10, frame.height > 10 else { return nil }
        let center = CGPoint(x: frame.midX, y: frame.midY)
        let display = displays.first(where: { $0.bounds.contains(center) })?.id ?? CGMainDisplayID()
        return PresenterTarget(app: "PowerPoint", frame: frame, displayID: display)
    }

    private func bundleID(_ pid: pid_t) -> String? {
        if let cached = bundleIDs[pid] { return cached }
        let id = NSRunningApplication(processIdentifier: pid)?.bundleIdentifier ?? ""
        if bundleIDs.count > 200 { bundleIDs.removeAll() }
        bundleIDs[pid] = id
        return id
    }

    // ---- PowerPoint over Apple Events -----------------------------------------------------------

    /// Asks once per PowerPoint launch, as soon as PowerPoint is seen running, so the consent dialog
    /// comes up while setting up rather than mid-presentation. Runs on its own queue: the call blocks
    /// until the user answers.
    private func askForPowerPointConsentIfNeeded(pid: pid_t) {
        guard pid != 0 else { return }
        let denied = pid == consentAskedFor && powerPointConsent == OSStatus(errAEEventNotPermitted)
        if denied {
            // The user may allow it later in System Settings; check quietly every 5 s.
            consentRecheckCountdown -= 1
            guard consentRecheckCountdown <= 0 else { return }
        } else if pid == consentAskedFor {
            return // allowed, or still waiting for an answer
        }
        consentAskedFor = pid
        consentRecheckCountdown = 20
        consentQueue.async {
            let target = NSAppleEventDescriptor(bundleIdentifier: Self.powerPointBundleID)
            let status = AEDeterminePermissionToAutomateTarget(target.aeDesc, typeWildCard, typeWildCard, !denied)
            if status != self.lastLoggedConsent {
                self.lastLoggedConsent = status
                switch Int(status) {
                case Int(noErr): Log.info("Allowed to ask PowerPoint for its Presenter View")
                case Int(errAEEventNotPermitted):
                    Log.warn("Not allowed to control PowerPoint. Allow it in System Settings → Privacy & Security → Automation → PPTimer")
                default: Log.warn("PowerPoint Automation consent check returned \(status)")
                }
            }
            self.queue.async {
                self.powerPointConsent = status
                // Anything but allowed / denied (e.g. -600, PowerPoint still starting): ask again on the next scan.
                if status != noErr && status != OSStatus(errAEEventNotPermitted) { self.consentAskedFor = 0 }
            }
            DispatchQueue.main.async { self.powerPointConsentStatus = status }
        }
    }

    private func powerPointPresenterBounds(pid: pid_t) -> CGRect? {
        let every = Self.descriptor(type: "abso", code: "all ")
        let windows = Self.objectSpecifier(want: "pPVw", from: .null(), form: "indx", data: every)
        let bounds = Self.objectSpecifier(want: "prop", from: windows, form: "prop", data: NSAppleEventDescriptor(typeCode: Self.fourCC("pbnd")))
        let event = NSAppleEventDescriptor(
            eventClass: Self.fourCC("core"), eventID: Self.fourCC("getd"),
            targetDescriptor: NSAppleEventDescriptor(processIdentifier: pid),
            returnID: AEReturnID(kAutoGenerateReturnID), transactionID: AETransactionID(kAnyTransactionID))
        event.setParam(bounds, forKeyword: Self.fourCC("----"))

        let reply: NSAppleEventDescriptor
        do {
            reply = try event.sendEvent(options: [.waitForReply, .neverInteract], timeout: 2)
        } catch {
            let code = (error as NSError).code
            if code != lastAppleEventError {
                lastAppleEventError = code
                Log.warn("Asking PowerPoint for its Presenter View failed (\(code))")
            }
            return nil
        }
        lastAppleEventError = nil
        if let err = reply.paramDescriptor(forKeyword: Self.fourCC("errn")), err.int32Value != 0 { return nil }
        guard let result = reply.paramDescriptor(forKeyword: Self.fourCC("----")) else { return nil }
        return Self.rect(result)
    }

    /// PowerPoint answers with a QuickDraw rectangle ('qdrt': Int16 top, left, bottom, right, native
    /// byte order). Also accepts a list {left, top, right, bottom}, or a list of those (one per window).
    private static func rect(_ d: NSAppleEventDescriptor) -> CGRect? {
        if d.descriptorType == fourCC("qdrt"), d.data.count == 8 {
            let v = d.data.withUnsafeBytes { raw in (0..<4).map { CGFloat(raw.loadUnaligned(fromByteOffset: $0 * 2, as: Int16.self)) } }
            return CGRect(x: v[1], y: v[0], width: v[3] - v[1], height: v[2] - v[0])
        }
        guard d.numberOfItems > 0, let first = d.atIndex(1) else { return nil }
        if first.numberOfItems > 0 || first.descriptorType == fourCC("qdrt") { return rect(first) }
        guard d.numberOfItems == 4 else { return nil }
        let v = (1...4).map { CGFloat(d.atIndex($0)?.int32Value ?? 0) }
        return CGRect(x: v[0], y: v[1], width: v[2] - v[0], height: v[3] - v[1])
    }

    private static func fourCC(_ s: String) -> FourCharCode {
        s.utf8.reduce(0) { $0 << 8 | FourCharCode($1) }
    }

    private static func descriptor(type: String, code: String) -> NSAppleEventDescriptor {
        var value = fourCC(code)
        return NSAppleEventDescriptor(descriptorType: fourCC(type), bytes: &value, length: MemoryLayout<FourCharCode>.size)!
    }

    private static func objectSpecifier(want: String, from: NSAppleEventDescriptor, form: String, data: NSAppleEventDescriptor) -> NSAppleEventDescriptor {
        let spec = NSAppleEventDescriptor.record()
        spec.setDescriptor(NSAppleEventDescriptor(typeCode: fourCC(want)), forKeyword: fourCC("want"))
        spec.setDescriptor(from, forKeyword: fourCC("from"))
        spec.setDescriptor(NSAppleEventDescriptor(enumCode: fourCC(form)), forKeyword: fourCC("form"))
        spec.setDescriptor(data, forKeyword: fourCC("seld"))
        return spec.coerce(toDescriptorType: fourCC("obj "))!
    }

    // ---- Window server ------------------------------------------------------------------------------

    private static func displays() -> [(id: CGDirectDisplayID, bounds: CGRect)] {
        var count: UInt32 = 0
        CGGetActiveDisplayList(0, nil, &count)
        var ids = [CGDirectDisplayID](repeating: 0, count: Int(count))
        CGGetActiveDisplayList(count, &ids, &count)
        return ids.prefix(Int(count)).map { ($0, CGDisplayBounds($0)) }
    }

    private static func windows(onScreenOnly: Bool) -> [WindowInfo] {
        let options: CGWindowListOption = onScreenOnly ? [.optionOnScreenOnly, .excludeDesktopElements] : [.optionAll, .excludeDesktopElements]
        let list = CGWindowListCopyWindowInfo(options, kCGNullWindowID) as? [[String: Any]] ?? []
        return list.compactMap { w in
            guard let pid = w[kCGWindowOwnerPID as String] as? pid_t,
                  let b = w[kCGWindowBounds as String] as? NSDictionary,
                  let rect = CGRect(dictionaryRepresentation: b)
            else { return nil }
            return WindowInfo(
                pid: pid,
                owner: w[kCGWindowOwnerName as String] as? String ?? "",
                layer: w[kCGWindowLayer as String] as? Int ?? 0,
                bounds: rect,
                onScreen: w[kCGWindowIsOnscreen as String] as? Bool ?? false,
                number: w[kCGWindowNumber as String] as? Int ?? 0)
        }
    }

    private static func covers(_ window: CGRect, _ display: CGRect) -> Bool {
        abs(window.minX - display.minX) <= 2 && abs(window.minY - display.minY) <= 2 &&
            abs(window.width - display.width) <= 2 && abs(window.height - display.height) <= 2
    }

    private static func describe(_ r: CGRect) -> String {
        "\(Int(r.minX)),\(Int(r.minY)) \(Int(r.width))x\(Int(r.height))"
    }

    // ---- Diagnostics ------------------------------------------------------------------------------

    /// For GET /api/debug/windows: displays, Keynote / PowerPoint windows, and the current result.
    func diagnostics(_ completion: @escaping (Any) -> Void) {
        queue.async {
            let interesting = [Self.keynoteBundleID, Self.powerPointBundleID]
            let windows = Self.windows(onScreenOnly: false).filter { interesting.contains(self.bundleID($0.pid) ?? "") }
            let result: [String: Any] = [
                "displays": Self.displays().map { ["id": Int($0.id), "bounds": Self.describe($0.bounds), "main": $0.id == CGMainDisplayID()] },
                "windows": windows.map { [
                    "app": $0.owner, "number": $0.number, "layer": $0.layer,
                    "bounds": Self.describe($0.bounds), "onScreen": $0.onScreen,
                ] as [String: Any] },
                "powerPointAutomation": self.powerPointConsent.map { $0 == noErr ? "allowed" : "status \($0)" } ?? "not checked",
                "presenter": self.last.map { ["app": $0.app, "frame": Self.describe($0.frame), "display": Int($0.displayID)] as [String: Any] } ?? NSNull(),
            ]
            completion(result)
        }
    }
}
