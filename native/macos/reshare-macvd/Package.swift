// swift-tools-version:5.7
import PackageDescription

let package = Package(
    name: "reshare-macvd",
    platforms: [.macOS(.v11)],
    targets: [
        .target(name: "CGVirtualDisplayPrivate", path: "Sources/CGVirtualDisplayPrivate"),
        .executableTarget(
            name: "reshare-macvd",
            dependencies: ["CGVirtualDisplayPrivate"],
            path: "Sources/reshare-macvd",
            linkerSettings: [.linkedFramework("CoreGraphics"), .linkedFramework("Foundation")]
        ),
    ]
)
