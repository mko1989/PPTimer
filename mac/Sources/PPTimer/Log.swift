import Foundation

/// Append-only file log (rotates at ~1 MB). Never throws.
enum Log {
    private static let queue = DispatchQueue(label: "pptimer.log")
    private static let formatter: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "yyyy-MM-dd HH:mm:ss.SSS"
        return f
    }()

    private(set) static var filePath: String?
    static var echoToConsole = false

    static func initialize(directory: URL) {
        filePath = directory.appendingPathComponent("pptimer.log").path
    }

    static func info(_ message: String) { write("INFO", message) }

    static func warn(_ message: String) { write("WARN", message) }

    static func error(_ message: String, _ error: Error? = nil) {
        write("ERROR", error.map { "\(message): \($0)" } ?? message)
    }

    private static func write(_ level: String, _ message: String) {
        let now = Date()
        queue.async {
            let line = "\(formatter.string(from: now)) [\(level)] \(message)\n"
            if echoToConsole { FileHandle.standardError.write(Data(line.utf8)) }
            guard let path = filePath else { return }
            let fm = FileManager.default
            if let size = (try? fm.attributesOfItem(atPath: path))?[.size] as? Int, size > 1_000_000 {
                try? fm.removeItem(atPath: path + ".1")
                try? fm.moveItem(atPath: path, toPath: path + ".1")
            }
            if let handle = FileHandle(forWritingAtPath: path) {
                handle.seekToEndOfFile()
                handle.write(Data(line.utf8))
                handle.closeFile()
            } else {
                fm.createFile(atPath: path, contents: Data(line.utf8))
            }
        }
    }
}
