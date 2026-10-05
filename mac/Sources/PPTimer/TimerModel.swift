import Foundation

struct TimerSnapshot {
    var running = false
    var visible = true
    var presenterView = false
    var presenterWidth = 0
    var presenterHeight = 0
    var durationMs: Int64 = 0
    var remainingMs: Int64 = 0
    var remainingSeconds = 0
    var display = ""
    /// normal | warning | critical | expired
    var phase = "normal"
    var overtime = false
    var progress = 0.0
    /// How fast the countdown runs, in % of real time (100 = normal).
    var speedPercent = 100.0

    /// Changes whenever anything a client would display changes.
    var changeKey: String {
        "\(display)|\(phase)|\(running)|\(visible)|\(presenterView)|\(presenterWidth)x\(presenterHeight)|\(durationMs)|\(speedPercent)"
    }

    func toDictionary() -> [String: Any] {
        [
            "running": running,
            "visible": visible,
            "presenterView": presenterView,
            "presenterWidth": presenterWidth,
            "presenterHeight": presenterHeight,
            "durationMs": durationMs,
            "duration": TimerModel.format(Int((Double(durationMs) / 1000).rounded(.up)), showMinus: true),
            "remainingMs": remainingMs,
            "remainingSeconds": remainingSeconds,
            "display": display,
            "phase": phase,
            "overtime": overtime,
            // Decimal, so JSON gets 0.7597 rather than 0.75970000000000004.
            "progress": NSDecimalNumber(value: Int((progress * 10000).rounded())).multiplying(byPowerOf10: -4),
            "speedPercent": NSDecimalNumber(value: Int((speedPercent * 10).rounded())).multiplying(byPowerOf10: -1),
        ]
    }
}

/// Thread-safe countdown. Time is derived from a monotonic clock, so a missed UI tick or
/// a dropped network message never makes the timer drift.
final class TimerModel {
    static let minSpeedPercent = 50.0
    static let maxSpeedPercent = 200.0

    private let lock = NSLock()
    private let settings: SettingsStore

    private var durationMs: Int64
    private var remainingAtAnchorMs: Int64
    private var anchorMs: Int64 = 0
    private var speed = 1.0 // countdown ms per real ms
    private var running = false
    private var visible = true
    private var presenterView = false
    private var presenterWidth = 0
    private var presenterHeight = 0
    private var zeroFired = false

    /// Called once each time a running countdown reaches zero (on whichever thread noticed).
    /// Register handlers before any other thread starts using the model.
    var onZeroReached: [() -> Void] = []

    /// Called by the "testsound" command.
    var onSoundTestRequested: [() -> Void] = []

    init(settings: SettingsStore) {
        self.settings = settings
        durationMs = Int64(settings.current.defaultDurationSeconds) * 1000
        remainingAtAnchorMs = durationMs
    }

    private var now: Int64 { Int64(clock_gettime_nsec_np(CLOCK_MONOTONIC) / 1_000_000) }

    private func remainingLocked(countUp: Bool) -> Int64 {
        let remaining = running ? remainingAtAnchorMs - Int64((Double(now - anchorMs) * speed).rounded()) : remainingAtAnchorMs
        return !countUp && remaining < 0 ? 0 : remaining
    }

    private func rebase(_ remainingMs: Int64) {
        remainingAtAnchorMs = remainingMs
        anchorMs = now
    }

    private func locked(_ body: () -> Void) {
        lock.lock()
        body()
        lock.unlock()
    }

    func start() {
        locked {
            guard !running else { return }
            rebase(remainingAtAnchorMs)
            running = true
        }
    }

    func pause() {
        let countUp = settings.current.countUp
        locked {
            guard running else { return }
            rebase(remainingLocked(countUp: countUp))
            running = false
        }
    }

    func toggle() {
        let countUp = settings.current.countUp
        locked {
            rebase(running ? remainingLocked(countUp: countUp) : remainingAtAnchorMs)
            running.toggle()
        }
    }

    /// Back to the full duration (and normal speed), paused.
    func reset() {
        locked {
            rebase(durationMs)
            speed = 1
            running = false
        }
    }

    /// Back to the full duration (and normal speed) and running.
    func restart() {
        locked {
            rebase(durationMs)
            speed = 1
            running = true
        }
    }

    /// Sets a new duration and remaining time, at normal speed: a new segment never inherits a
    /// speed-up meant for the previous one. Keeps the running state unless `start` is given.
    func set(_ ms: Int64, start: Bool?) {
        locked {
            durationMs = ms
            rebase(ms)
            speed = 1
            if let start { running = start }
        }
    }

    /// Sets how fast the countdown runs, in % of real time (105 = a 10:00 countdown takes 9:31).
    /// With `relative`, adds to the current speed. Clamped to 50–200 %, rounded to 0.1 %.
    func setSpeed(_ percent: Double, relative: Bool) {
        let countUp = settings.current.countUp
        locked {
            var target = relative ? (speed * 1000).rounded() / 10 + percent : percent
            target = (max(Self.minSpeedPercent, min(Self.maxSpeedPercent, target)) * 10).rounded() / 10
            rebase(remainingLocked(countUp: countUp)) // the time already run keeps the old speed
            speed = target / 100
        }
    }

    /// Adds (or with a negative value, removes) time from the remaining time.
    func add(_ ms: Int64) {
        let countUp = settings.current.countUp
        locked { rebase(remainingLocked(countUp: countUp) + ms) }
    }

    func setVisible(_ value: Bool) {
        locked { visible = value }
    }

    func toggleVisible() {
        locked { visible.toggle() }
    }

    func setPresenterView(_ detected: Bool, width: Int, height: Int) {
        locked {
            presenterView = detected
            if detected {
                presenterWidth = width
                presenterHeight = height
            }
        }
    }

    func requestSoundTest() {
        onSoundTestRequested.forEach { $0() }
    }

    func snapshot() -> TimerSnapshot {
        let cfg = settings.current
        var snap = TimerSnapshot()
        var fireZero = false
        locked {
            let remaining = remainingLocked(countUp: cfg.countUp)
            if remaining > 0 {
                zeroFired = false
            } else if running && !zeroFired {
                zeroFired = true
                fireZero = true
            }

            let seconds = Int((Double(remaining) / 1000).rounded(.up))
            let phase: String
            if remaining <= 0 { phase = "expired" }
            else if cfg.criticalEnabled && seconds <= cfg.criticalSeconds { phase = "critical" }
            else if cfg.warnEnabled && seconds <= cfg.warnSeconds { phase = "warning" }
            else { phase = "normal" }

            snap = TimerSnapshot(
                running: running,
                visible: visible,
                presenterView: presenterView,
                presenterWidth: presenterWidth,
                presenterHeight: presenterHeight,
                durationMs: durationMs,
                remainingMs: remaining,
                remainingSeconds: seconds,
                display: Self.format(seconds, showMinus: cfg.showMinus),
                phase: phase,
                overtime: remaining < 0,
                progress: durationMs > 0 ? max(0, min(1, Double(remaining) / Double(durationMs))) : 0,
                speedPercent: (speed * 1000).rounded() / 10)
        }
        if fireZero { onZeroReached.forEach { $0() } }
        return snap
    }

    /// MM:SS, or H:MM:SS from one hour. Negative values (overtime) get a '-' only if `showMinus`.
    static func format(_ seconds: Int, showMinus: Bool) -> String {
        let sign = seconds < 0 && showMinus ? "-" : ""
        let a = abs(seconds)
        let h = a / 3600, m = a % 3600 / 60, s = a % 60
        return h > 0 ? String(format: "%@%d:%02d:%02d", sign, h, m, s) : String(format: "%@%02d:%02d", sign, m, s)
    }
}
