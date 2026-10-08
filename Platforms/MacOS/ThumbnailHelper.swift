import Foundation
import ImageIO
import QuickLookThumbnailing
import UniformTypeIdentifiers

let arguments = CommandLine.arguments
guard arguments.count >= 4,
      let size = Double(arguments[3]), size.isFinite, size > 0 else {
    fputs("usage: MacExplorer.Thumbnail <input> <output.png> <size> [image|face x y width height]\n", stderr)
    exit(2)
}

let inputURL = URL(fileURLWithPath: CommandLine.arguments[1])
let outputURL = URL(fileURLWithPath: CommandLine.arguments[2])

func writePNG(_ image: CGImage) -> Bool {
    guard let destination = CGImageDestinationCreateWithURL(
        outputURL as CFURL, UTType.png.identifier as CFString, 1, nil) else { return false }
    CGImageDestinationAddImage(destination, image, nil)
    return CGImageDestinationFinalize(destination)
}

// ImageIO handles HEIC and EXIF orientation inside the signed, inherited sandbox.
// Downsample before cropping so a face does not require a full-resolution decode.
if arguments.count > 4 {
    let mode = arguments[4]
    guard (mode == "image" && arguments.count == 5) || (mode == "face" && arguments.count == 9),
          let source = CGImageSourceCreateWithURL(inputURL as CFURL,
              [kCGImageSourceShouldCache: false] as CFDictionary) else { exit(2) }
    var face: [Double] = []
    var decodeSize = size
    if mode == "face" {
        face = arguments[5...8].compactMap(Double.init)
        guard face.count == 4, face.allSatisfy({ $0.isFinite }), face[2] > 0, face[3] > 0,
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let pixelWidth = properties[kCGImagePropertyPixelWidth] as? NSNumber,
              let pixelHeight = properties[kCGImagePropertyPixelHeight] as? NSNumber else { exit(2) }
        let orientation = (properties[kCGImagePropertyOrientation] as? NSNumber)?.intValue ?? 1
        let width = (5...8).contains(orientation) ? pixelHeight.doubleValue : pixelWidth.doubleValue
        let height = (5...8).contains(orientation) ? pixelWidth.doubleValue : pixelHeight.doubleValue
        let cropSize = max(min(1, face[2] * 1.6) * width, min(1, face[3] * 1.6) * height)
        guard width > 0, height > 0, cropSize > 0 else { exit(2) }
        decodeSize = min(max(width, height), ceil(size * max(width, height) / cropSize))
    }
    guard var image = CGImageSourceCreateThumbnailAtIndex(source, 0, [
        kCGImageSourceCreateThumbnailFromImageAlways: true,
        kCGImageSourceCreateThumbnailWithTransform: true,
        kCGImageSourceThumbnailMaxPixelSize: max(1, decodeSize)
    ] as CFDictionary) else { exit(1) }
    if mode == "face" {
        let width = Double(image.width), height = Double(image.height)
        let cropWidth = min(width, max(1, (face[2] * width * 1.6).rounded()))
        let cropHeight = min(height, max(1, (face[3] * height * 1.6).rounded()))
        let x = min(width - cropWidth, max(0, ((face[0] + face[2] / 2) * width - cropWidth / 2).rounded()))
        let y = min(height - cropHeight, max(0, ((1 - face[1] - face[3] / 2) * height - cropHeight / 2).rounded()))
        guard let cropped = image.cropping(to: CGRect(x: x, y: y, width: cropWidth, height: cropHeight)) else { exit(1) }
        image = cropped
        let scale = min(1, size / Double(max(image.width, image.height)))
        let targetWidth = max(1, Int((Double(image.width) * scale).rounded()))
        let targetHeight = max(1, Int((Double(image.height) * scale).rounded()))
        guard let context = CGContext(data: nil, width: targetWidth, height: targetHeight,
            bitsPerComponent: 8, bytesPerRow: 0, space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { exit(1) }
        context.interpolationQuality = .high
        context.draw(image, in: CGRect(x: 0, y: 0, width: targetWidth, height: targetHeight))
        guard let resized = context.makeImage() else { exit(1) }
        image = resized
    }
    exit(writePNG(image) ? 0 : 1)
}

let request = QLThumbnailGenerator.Request(
    fileAt: inputURL,
    size: CGSize(width: max(32, size), height: max(32, size)),
    // The caller already passes physical pixels (including display scaling).
    scale: 1,
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

    guard writePNG(image) else {
        fputs("Could not write PNG thumbnail\n", stderr)
        return
    }
    exitCode = 0
}

if semaphore.wait(timeout: .now() + 3) == .timedOut {
    fputs("Quick Look thumbnail generation timed out\n", stderr)
    exit(3)
}
exit(exitCode)
