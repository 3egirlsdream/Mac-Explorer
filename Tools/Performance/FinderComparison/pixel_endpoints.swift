import AVFoundation
import Vision
import AppKit
import CoreImage

// Offline-only inspection of original capture pixels. No accessibility access.
@main struct PixelEndpoints {
 static func main() async throws {
  let a=CommandLine.arguments,app=a[2],expected=a.count>3 ? a[3] : "1000"
  let asset=AVURLAsset(url:URL(fileURLWithPath:a[1]))
  let tracks=try await asset.loadTracks(withMediaType:.video)
  let reader=try AVAssetReader(asset:asset)
  let output=AVAssetReaderTrackOutput(track:tracks[0],outputSettings:[kCVPixelBufferPixelFormatTypeKey as String:kCVPixelFormatType_32BGRA])
  reader.add(output);reader.startReading()
  let ctx=CIContext(options:[.cacheIntermediates:false])
  let req=VNRecognizeTextRequest();req.recognitionLevel = .accurate
  req.recognitionLanguages=["zh-Hans","en-US"];req.usesLanguageCorrection=false
  var rows=[[String:Any]](),foundCount=false,foundFile=false
  while let sample=output.copyNextSampleBuffer(),let b=CMSampleBufferGetImageBuffer(sample) {
   let image=CIImage(cvPixelBuffer:b)
   var texts=[String:[String]]()
   let rects:[String:CGRect]=app=="macexplorer" ? ["footer":CGRect(x:750,y:125,width:640,height:62),"firstRow":CGRect(x:790,y:1050,width:620,height:72)] : ["footer":CGRect(x:1150,y:118,width:850,height:68),"firstRow":CGRect(x:800,y:1245,width:620,height:72)]
   for (name,r) in rects {
    if (name=="footer" && foundCount) || (name=="firstRow" && foundFile) {continue}
    let crop=ctx.createCGImage(image,from:r)!
    try VNImageRequestHandler(cgImage:crop).perform([req])
    texts[name]=(req.results ?? []).compactMap{$0.topCandidates(1).first?.string}
    let normalized=texts[name]!.joined().filter{!$0.isWhitespace}.replacingOccurrences(of:",",with:"").replacingOccurrences(of:"，",with:"")
    if name=="footer" && (normalized.contains(expected+"项") || normalized.contains(expected+"个项目")) {foundCount=true}
    if name=="firstRow" && normalized.lowercased().contains("000000") {foundFile=true}
   }
   rows.append(["pts":CMSampleBufferGetPresentationTimeStamp(sample).seconds,"texts":texts])
   if foundCount && foundFile {break}
  }
  FileHandle.standardOutput.write(try JSONSerialization.data(withJSONObject:rows,options:[.sortedKeys]))
 }
}
