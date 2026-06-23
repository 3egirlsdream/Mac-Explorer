import Foundation
import Vision
import CoreLocation

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

