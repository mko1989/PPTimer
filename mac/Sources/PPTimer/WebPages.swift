import Foundation

/// The browser pages, shared with the Windows add-in (addin/PPTimer/Core/*.html). build.sh copies them
/// into the app's Resources; `swift run` falls back to the source tree.
enum WebPages {
    /// Control remote, served at "/".
    static let remote = load("remote")

    /// Timer only, black background, served at "/display".
    static let display = load("display")

    private static func load(_ name: String) -> String {
        let sourceTree = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().appendingPathComponent("../../../addin/PPTimer/Core/\(name).html").standardized
        for url in [Bundle.main.url(forResource: name, withExtension: "html"), sourceTree].compactMap({ $0 }) {
            if let text = try? String(contentsOf: url, encoding: .utf8) { return text }
        }
        return "<h1>\(name).html missing from the build</h1>"
    }
}
