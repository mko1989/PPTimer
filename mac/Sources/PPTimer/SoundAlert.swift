import AppKit

/// Plays the zero alert: a sound file from settings (anything NSSound reads: .wav, .aiff, .mp3, .m4a),
/// or a generated three-beep. Main thread only.
enum SoundAlert {
    private static var current: NSSound?
    private static let beep = makeBeep()

    static func play(_ soundFile: String) {
        current?.stop()
        var sound: NSSound?
        let path = (soundFile as NSString).expandingTildeInPath
        if !soundFile.isEmpty {
            sound = NSSound(contentsOfFile: path, byReference: true)
            if sound == nil { Log.warn("Sound file not found or unreadable: \(soundFile); playing the built-in beep") }
        }
        current = sound ?? NSSound(data: beep)
        if current?.play() != true { Log.error("Could not play the zero sound") }
    }

    /// Three short 880 Hz beeps, 16-bit mono PCM WAV.
    private static func makeBeep() -> Data {
        let rate = 44100
        let beep = 0.18, gap = 0.12
        let total = Int(Double(rate) * (3 * beep + 2 * gap))
        var samples = [Int16](repeating: 0, count: total)
        let fade = Int(Double(rate) * 0.006)
        for n in 0..<3 {
            let start = Int(Double(rate) * Double(n) * (beep + gap))
            let length = Int(Double(rate) * beep)
            for i in 0..<length where start + i < total {
                let envelope = min(1.0, Double(min(i, length - i)) / Double(fade))
                samples[start + i] = Int16(sin(2 * Double.pi * 880 * Double(i) / Double(rate)) * envelope * 0.6 * Double(Int16.max))
            }
        }

        var data = Data()
        func append<T: FixedWidthInteger>(_ v: T) { withUnsafeBytes(of: v.littleEndian) { data.append(contentsOf: $0) } }
        let dataBytes = samples.count * 2
        data.append(contentsOf: Array("RIFF".utf8)); append(UInt32(36 + dataBytes))
        data.append(contentsOf: Array("WAVEfmt ".utf8)); append(UInt32(16))
        append(UInt16(1)); append(UInt16(1))                  // PCM, mono
        append(UInt32(rate)); append(UInt32(rate * 2))        // sample rate, byte rate
        append(UInt16(2)); append(UInt16(16))                 // block align, bits per sample
        data.append(contentsOf: Array("data".utf8)); append(UInt32(dataBytes))
        for s in samples { append(s) }
        return data
    }
}
