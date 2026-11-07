// reshare-macvd: creates a virtual monitor and keeps it alive until stdin is closed.
// Prints "DISPLAY <CGDirectDisplayID>" on success.
//
//   reshare-macvd --width 1920 --height 1080 --hz 60 [--hidpi]

import CGVirtualDisplayPrivate
import CoreGraphics
import Foundation

var width = 1920, height = 1080, hz = 60.0, hiDPI = false
var args = CommandLine.arguments.dropFirst().makeIterator()
while let a = args.next() {
    switch a {
    case "--width": width = Int(args.next() ?? "") ?? width
    case "--height": height = Int(args.next() ?? "") ?? height
    case "--hz": hz = Double(args.next() ?? "") ?? hz
    case "--hidpi": hiDPI = true
    default:
        FileHandle.standardError.write("unknown argument \(a)\n".data(using: .utf8)!)
        exit(2)
    }
}

let descriptor = CGVirtualDisplayDescriptor()
descriptor.queue = DispatchQueue.main
descriptor.name = "Reshare"
let scale = hiDPI ? 2 : 1
descriptor.maxPixelsWide = UInt32(width * scale)
descriptor.maxPixelsHigh = UInt32(height * scale)
// ~110 dpi physical size, only used for the "actual size" scaling hints
descriptor.sizeInMillimeters = CGSize(width: Double(width) / 110.0 * 25.4, height: Double(height) / 110.0 * 25.4)
descriptor.productID = 0x5253
descriptor.vendorID = 0x5253
descriptor.serialNum = 1
descriptor.terminationHandler = { _, _ in
    FileHandle.standardError.write("virtual display terminated by the system\n".data(using: .utf8)!)
    exit(1)
}

guard let display = CGVirtualDisplay(descriptor: descriptor) else {
    print("ERROR could not create virtual display")
    exit(1)
}

let settings = CGVirtualDisplaySettings()
settings.hiDPI = hiDPI ? 1 : 0
settings.modes = [CGVirtualDisplayMode(width: UInt(width), height: UInt(height), refreshRate: hz)]
guard display.applySettings(settings) else {
    print("ERROR could not apply display settings")
    exit(1)
}

print("DISPLAY \(display.displayID)")
fflush(stdout)

// Exit (removing the display) when the parent closes our stdin or dies.
DispatchQueue.global().async {
    while FileHandle.standardInput.availableData.count > 0 {}
    exit(0)
}
withExtendedLifetime(display) { dispatchMain() }
