import Foundation
import Vision
import CoreLocation
import PDFKit
import AppKit

struct FaceResult: Codable {
    let BoundingBoxX: Float
    let BoundingBoxY: Float
    let BoundingBoxW: Float
    let BoundingBoxH: Float
    let FeaturePrint: String?
}

struct TextResult: Codable {
    let Text: String
    let Confidence: Float
    let Keywords: [String]
}

struct ClassificationResult: Codable {
    let Identifier: String
    let Confidence: Float
}

struct LocationResult: Codable {
    let Latitude: Double
    let Longitude: Double
    let PlaceName: String
}

struct DateResult: Codable {
    let TakenAt: String
    let YearMonth: String
    let Day: String
}

struct AnalysisResult: Codable {
    var Faces: [FaceResult] = []
    var RecognizedTexts: [TextResult] = []
    var Classifications: [ClassificationResult] = []
    var Location: LocationResult?
    var DateInfo: DateResult?
    var CameraInfo: String?
}

func keywords(from text: String) -> [String] {
    let separators = CharacterSet.whitespacesAndNewlines
        .union(.punctuationCharacters)
        .union(.symbols)
    var seen = Set<String>()
    return text.components(separatedBy: separators).compactMap { word in
        guard word.count >= 2, seen.insert(word).inserted, seen.count <= 50 else { return nil }
        return word
    }
}

func metadata(for path: String) -> [String: String] {
    let process = Process()
    process.executableURL = URL(fileURLWithPath: "/usr/bin/mdls")
    process.arguments = [
        "-name", "kMDItemLatitude",
        "-name", "kMDItemLongitude",
        "-name", "kMDItemContentCreationDate",
        "-name", "kMDItemAcquisitionMake",
        "-name", "kMDItemAcquisitionModel",
        path
    ]
    let pipe = Pipe()
    process.standardOutput = pipe
    process.standardError = FileHandle.nullDevice
    do {
        try process.run()
        process.waitUntilExit()
    } catch {
        return [:]
    }

    let data = pipe.fileHandleForReading.readDataToEndOfFile()
    guard let output = String(data: data, encoding: .utf8) else { return [:] }
    var values: [String: String] = [:]
    for line in output.split(separator: "\n") {
        let parts = line.split(separator: "=", maxSplits: 1)
        guard parts.count == 2 else { continue }
        let key = parts[0].trimmingCharacters(in: .whitespaces)
        let value = parts[1].trimmingCharacters(in: .whitespaces)
            .trimmingCharacters(in: CharacterSet(charactersIn: "\""))
        if value != "(null)" { values[key] = value }
    }
    return values
}

func reverseGeocode(latitude: Double, longitude: Double) -> String? {
    let semaphore = DispatchSemaphore(value: 0)
    var result: String?
    CLGeocoder().reverseGeocodeLocation(CLLocation(latitude: latitude, longitude: longitude)) { placemarks, _ in
        if let place = placemarks?.first {
            let parts = [place.country, place.administrativeArea, place.locality, place.subLocality]
                .compactMap { $0 }
            result = parts.suffix(2).joined(separator: ", ")
        }
        semaphore.signal()
    }
    _ = semaphore.wait(timeout: .now() + 8)
    return result
}

enum PdfExtractionError: LocalizedError {
    case failed(String)
    var errorDescription: String? {
        switch self { case .failed(let message): return message }
    }
}

func extractPdf(at url: URL) throws -> AnalysisResult {
    guard let document = PDFDocument(url: url) else {
        throw PdfExtractionError.failed("无法读取 PDF，文件可能已损坏。")
    }
    guard !document.isLocked else {
        throw PdfExtractionError.failed("PDF 已加密，需要先解锁文件。")
    }
    guard document.allowsCopying else {
        throw PdfExtractionError.failed("PDF 不允许提取文字。")
    }
    guard document.pageCount > 0 else {
        throw PdfExtractionError.failed("PDF 没有可读取的页面。")
    }
    var result = AnalysisResult()
    var characterCount = 0
    func append(_ text: String, confidence: Float) throws {
        for line in text.components(separatedBy: .newlines) {
            // PDF font maps may emit compatibility radicals/ligatures; normalize the search text.
            let line = line.precomposedStringWithCompatibilityMapping.trimmingCharacters(in: .whitespacesAndNewlines)
            if line.isEmpty { continue }
            characterCount += line.utf16.count
            guard characterCount <= 5_000_000 else {
                throw PdfExtractionError.failed("PDF 文字超过 500 万字符，已停止分析。")
            }
            result.RecognizedTexts.append(TextResult(Text: line, Confidence: confidence, Keywords: keywords(from: line)))
        }
    }
    func report(_ completed: Int) {
        let line = "PDF_PROGRESS {\"CompletedPages\":\(completed),\"TotalPages\":\(document.pageCount)}\n"
        FileHandle.standardError.write(Data(line.utf8))
    }
    report(0)
    for index in 0..<document.pageCount {
        try autoreleasepool {
            guard let page = document.page(at: index) else {
                throw PdfExtractionError.failed("无法读取 PDF 第 \(index + 1) 页。")
            }
            if let text = page.string, text.unicodeScalars.contains(where: { CharacterSet.alphanumerics.contains($0) }) {
                try append(text, confidence: 1)
            } else {
                let bounds = page.bounds(for: .cropBox)
                guard bounds.width.isFinite, bounds.height.isFinite, bounds.width > 0, bounds.height > 0 else {
                    throw PdfExtractionError.failed("PDF 第 \(index + 1) 页尺寸无效。")
                }
                let scale = min(200.0 / 72.0, 3000.0 / max(bounds.width, bounds.height))
                let rotated = abs(page.rotation % 180) == 90
                let width = Int(max(1, floor((rotated ? bounds.height : bounds.width) * scale)))
                let height = Int(max(1, floor((rotated ? bounds.width : bounds.height) * scale)))
                guard let pageRef = page.pageRef,
                      let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8,
                        bytesPerRow: 0, space: CGColorSpaceCreateDeviceRGB(),
                        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
                    throw PdfExtractionError.failed("无法渲染 PDF 第 \(index + 1) 页。")
                }
                let target = CGRect(x: 0, y: 0, width: width, height: height)
                context.setFillColor(CGColor(gray: 1, alpha: 1))
                context.fill(target)
                context.concatenate(pageRef.getDrawingTransform(.cropBox, rect: target, rotate: 0, preserveAspectRatio: true))
                context.drawPDFPage(pageRef)
                guard let image = context.makeImage() else {
                    throw PdfExtractionError.failed("无法渲染 PDF 第 \(index + 1) 页。")
                }
                let request = VNRecognizeTextRequest()
                request.recognitionLevel = .accurate
                request.recognitionLanguages = ["zh-Hans", "zh-Hant", "en"]
                try VNImageRequestHandler(cgImage: image).perform([request])
                for observation in request.results ?? [] {
                    if let candidate = observation.topCandidates(1).first {
                        try append(candidate.string, confidence: candidate.confidence)
                    }
                }
            }
        }
        report(index + 1)
    }
    return result
}

if CommandLine.arguments.count > 1 && CommandLine.arguments[1] == "--pdf" {
    do {
        guard CommandLine.arguments.count == 3 else {
            throw PdfExtractionError.failed("missing PDF path")
        }
        let result = try extractPdf(at: URL(fileURLWithPath: CommandLine.arguments[2]))
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.withoutEscapingSlashes]
        FileHandle.standardOutput.write(try encoder.encode(result))
        exit(0)
    } catch {
        FileHandle.standardError.write(Data("PDF error: \(error.localizedDescription)\n".utf8))
        exit(1)
    }
}

guard CommandLine.arguments.count > 1 else {
    fputs("missing image path\n", stderr)
    exit(2)
}

let imageURL = URL(fileURLWithPath: CommandLine.arguments[1])
var output = AnalysisResult()

do {
    let faceRequest = VNDetectFaceRectanglesRequest()
    let textRequest = VNRecognizeTextRequest()
    textRequest.recognitionLevel = .accurate
    textRequest.recognitionLanguages = ["zh-Hans", "zh-Hant", "en"]
    let classificationRequest = VNClassifyImageRequest()
    let handler = VNImageRequestHandler(url: imageURL)
    try handler.perform([faceRequest, textRequest, classificationRequest])

    for face in faceRequest.results ?? [] {
        var featurePrint: String?
        let printRequest = VNGenerateImageFeaturePrintRequest()
        printRequest.regionOfInterest = face.boundingBox
        let printHandler = VNImageRequestHandler(url: imageURL)
        try? printHandler.perform([printRequest])
        if let observation = printRequest.results?.first {
            featurePrint = observation.data.base64EncodedString()
        }
        output.Faces.append(FaceResult(
            BoundingBoxX: Float(face.boundingBox.origin.x),
            BoundingBoxY: Float(face.boundingBox.origin.y),
            BoundingBoxW: Float(face.boundingBox.size.width),
            BoundingBoxH: Float(face.boundingBox.size.height),
            FeaturePrint: featurePrint))
    }

    for observation in textRequest.results ?? [] {
        guard let candidate = observation.topCandidates(1).first else { continue }
        output.RecognizedTexts.append(TextResult(
            Text: candidate.string,
            Confidence: candidate.confidence,
            Keywords: keywords(from: candidate.string)))
    }

    for observation in classificationRequest.results ?? [] where observation.confidence >= 0.3 {
        output.Classifications.append(ClassificationResult(
            Identifier: observation.identifier,
            Confidence: observation.confidence))
    }
} catch {
    fputs("Vision error: \(error)\n", stderr)
}

let md = metadata(for: imageURL.path)
let latitude = Double(md["kMDItemLatitude"] ?? "") ?? 0
let longitude = Double(md["kMDItemLongitude"] ?? "") ?? 0
if latitude != 0 || longitude != 0 {
    let place = reverseGeocode(latitude: latitude, longitude: longitude)
        ?? String(format: "%.4f, %.4f", latitude, longitude)
    output.Location = LocationResult(Latitude: latitude, Longitude: longitude, PlaceName: place)
}

if let rawDate = md["kMDItemContentCreationDate"] {
    let formatter = ISO8601DateFormatter()
    if let date = formatter.date(from: rawDate.replacingOccurrences(of: " +0000", with: "Z")) {
        let full = ISO8601DateFormatter().string(from: date)
        let monthFormatter = DateFormatter()
        monthFormatter.dateFormat = "yyyy-MM"
        let dayFormatter = DateFormatter()
        dayFormatter.dateFormat = "yyyy-MM-dd"
        output.DateInfo = DateResult(
            TakenAt: full,
            YearMonth: monthFormatter.string(from: date),
            Day: dayFormatter.string(from: date))
    }
}

let make = md["kMDItemAcquisitionMake"] ?? ""
let model = md["kMDItemAcquisitionModel"] ?? ""
if !make.isEmpty || !model.isEmpty {
    output.CameraInfo = model.lowercased().hasPrefix(make.lowercased())
        ? model
        : [make, model].filter { !$0.isEmpty }.joined(separator: " ")
}

let encoder = JSONEncoder()
encoder.outputFormatting = [.withoutEscapingSlashes]
let json = try encoder.encode(output)
FileHandle.standardOutput.write(json)
