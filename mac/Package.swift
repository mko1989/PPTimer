// swift-tools-version:5.9
// Menu bar app that draws the PPTimer countdown over Keynote's presenter display or
// PowerPoint's Presenter View. Build the .app with `./build.sh mac` from the repo root.
import PackageDescription

let package = Package(
    name: "PPTimer",
    platforms: [.macOS(.v12)],
    targets: [
        .executableTarget(name: "PPTimer", path: "Sources/PPTimer"),
    ]
)
