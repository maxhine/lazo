// swift-tools-version:5.9
import PackageDescription

// Lazo para macOS (Apple Silicon, macOS 14+). Compilar con scripts/build-mac.sh:
// la salida va a ~/Library/Caches/lazo-build, fuera de OneDrive.
let package = Package(
    name: "Lazo",
    platforms: [.macOS(.v14)],
    targets: [
        .target(name: "LazoCore", path: "Sources/LazoCore"),
        .executableTarget(name: "Lazo", dependencies: ["LazoCore"], path: "Sources/Lazo"),
        .executableTarget(name: "lazo-interop", dependencies: ["LazoCore"], path: "Sources/LazoInterop"),
    ]
)
