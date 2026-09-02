import Foundation
import ImageIO
import QuickLookThumbnailing
import UniformTypeIdentifiers

guard CommandLine.arguments.count == 4,
      let size = Double(CommandLine.arguments[3]) else {
    fputs("usage: MacExplorer.Thumbnail <input> <output.png> <size>\n", stderr)
    exit(2)
}

let inputURL = URL(fileURLWithPath: CommandLine.arguments[1])
let outputURL = URL(fileURLWithPath: CommandLine.arguments[2])
let request = QLThumbnailGenerator.Request(
    fileAt: inputURL,
    size: CGSize(width: max(32, size), height: max(32, size)),
    scale: 2,
    representationTypes: .thumbnail
)

let semaphore = DispatchSemaphore(value: 0)
var exitCode: Int32 = 1

QLThumbnailGenerator.shared.generateBestRepresentation(for: request) { representation, error in
    defer { semaphore.signal() }
    guard error == nil,
          let image = representation?.cgImage else {
        if let error { fputs("\(error.localizedDescription)\n", stderr) }
        return
    }

    guard let destination = CGImageDestinationCreateWithURL(
        outputURL as CFURL,
        UTType.png.identifier as CFString,
        1,
        nil
    ) else {
        fputs("Could not create PNG destination\n", stderr)
        return
    }
    CGImageDestinationAddImage(destination, image, nil)
    guard CGImageDestinationFinalize(destination) else {
        fputs("Could not write PNG thumbnail\n", stderr)
        return
    }
    exitCode = 0
}

if semaphore.wait(timeout: .now() + 30) == .timedOut {
    fputs("Quick Look thumbnail generation timed out\n", stderr)
    exit(3)
}
exit(exitCode)
