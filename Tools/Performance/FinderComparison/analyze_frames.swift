import AVFoundation
import Vision
import AppKit

// Read-only source inspection: exact captured PTS, not synthetic UI or FPS claims.
@main struct Analyze {
 static func main() async throws {
  let a=CommandLine.arguments
  let url=URL(fileURLWithPath:a[1]),limit=Double(a[2]) ?? 8
  let asset=AVURLAsset(url:url)
  let tracks=try await asset.loadTracks(withMediaType:.video)
  let reader=try AVAssetReader(asset:asset)
  let output=AVAssetReaderTrackOutput(track:tracks[0],outputSettings:[kCVPixelBufferPixelFormatTypeKey as String:kCVPixelFormatType_32BGRA])
  reader.add(output);reader.startReading()
  let req=VNRecognizeTextRequest();req.recognitionLevel = .accurate
  req.recognitionLanguages=["en-US","zh-Hans"];req.usesLanguageCorrection=false
  var rows=[[String:Any]](),first:Double?=nil
  while let sample=output.copyNextSampleBuffer(),let buffer=CMSampleBufferGetImageBuffer(sample) {
   let pts=CMSampleBufferGetPresentationTimeStamp(sample).seconds
   if first==nil {first=pts}
   let t=pts-first!
   if t>limit {break}
   req.regionOfInterest=CGRect(x:0.26,y:0.065,width:0.65,height:0.70)
   try VNImageRequestHandler(cvPixelBuffer:buffer).perform([req])
   let texts=(req.results ?? []).compactMap{r -> [String:Any]? in
    guard let c=r.topCandidates(1).first else{return nil}
    return ["text":c.string,"confidence":c.confidence,"x":r.boundingBox.minX,"y":r.boundingBox.minY]
   }
   rows.append(["seconds":t,"pts":pts,"texts":texts])
  }
  let data=try JSONSerialization.data(withJSONObject:rows,options:[.sortedKeys])
  FileHandle.standardOutput.write(data)
 }
}
