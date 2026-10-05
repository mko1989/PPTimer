import Foundation

/// Thin wrapper over JSONSerialization. Objects are [String: Any]; nil values must be passed as NSNull.
enum Json {
    struct ParseError: Error, CustomStringConvertible {
        let description: String
    }

    static func parse(_ text: String) throws -> Any {
        do {
            return try JSONSerialization.jsonObject(with: Data(text.utf8), options: [.fragmentsAllowed])
        } catch {
            throw ParseError(description: "Invalid JSON")
        }
    }

    static func serialize(_ value: Any, pretty: Bool = false) -> String {
        var options: JSONSerialization.WritingOptions = [.withoutEscapingSlashes, .fragmentsAllowed]
        if pretty { options.formUnion([.prettyPrinted, .sortedKeys]) }
        guard JSONSerialization.isValidJSONObject([value]),
              let data = try? JSONSerialization.data(withJSONObject: value, options: options)
        else { return "null" }
        return String(decoding: data, as: UTF8.self)
    }

    /// JSON booleans arrive as NSNumber; this tells them apart from numbers.
    static func isBool(_ value: Any) -> Bool {
        guard let n = value as? NSNumber else { return false }
        return CFGetTypeID(n) == CFBooleanGetTypeID()
    }

    /// Request arguments are strings, whether they came from a query string, a form or JSON.
    static func argString(_ value: Any) -> String {
        switch value {
        case is NSNull: return ""
        case let s as String: return s
        case let n as NSNumber: return isBool(n) ? (n.boolValue ? "true" : "false") : n.stringValue
        default: return serialize(value)
        }
    }
}
