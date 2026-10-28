import AVFoundation
import Vision
import CoreImage
import Foundation

@main struct PreviewPixels {
 static func main() async throws {
  let a=CommandLine.arguments
  let events=try JSONSerialization.jsonObject(with:Data(contentsOf:URL(fileURLWithPath:a[2]))) as! [[String:Any]]
  let first=Double(a[3])!
  let app=events.first?["app"] as? String ?? "macexplorer"
  let fullDecode=a.contains("--full-decode")
  let asset=AVURLAsset(url:URL(fileURLWithPath:a[1]))
  let tracks=try await asset.loadTracks(withMediaType:.video),reader=try AVAssetReader(asset:asset)
  let output=AVAssetReaderTrackOutput(track:tracks[0],outputSettings:[kCVPixelBufferPixelFormatTypeKey as String:kCVPixelFormatType_32BGRA])
  reader.add(output);reader.startReading()
  let ctx=CIContext(options:[.cacheIntermediates:false])
  let request=VNRecognizeTextRequest();request.recognitionLevel = .fast;request.recognitionLanguages=["en-US"];request.usesLanguageCorrection=false
  var results=[[String:Any]](),finished=Set<Int>()
  while let sample=output.copyNextSampleBuffer(),let b=CMSampleBufferGetImageBuffer(sample) {
   let t=CMSampleBufferGetPresentationTimeStamp(sample).seconds
   guard let i=events.indices.first(where:{ !finished.contains($0) && t>=((events[$0]["inputHostSeconds"] as! Double)-first) && t<=((events[$0]["endHoldHostSeconds"] as! Double)-first) }) else {continue}
   let event=events[i],kind=event["kind"] as! String,name=event["file"] as! String
   let image=CIImage(cvPixelBuffer:b).transformed(by:CGAffineTransform(scaleX:0.5,y:0.5))
   // Finder's underlying right preview pane must never count as the newly
   // opened Quick Look content. Restrict recognition to the central panel.
   let region=app=="finder" ? CGRect(x:260,y:200,width:790,height:530) : CGRect(x:170,y:200,width:1120,height:530)
   let crop=ctx.createCGImage(image,from:region)!
   try VNImageRequestHandler(cgImage:crop).perform([request])
   let texts=(request.results ?? []).compactMap{$0.topCandidates(1).first?.string}
   let joined=texts.joined(separator:" ").uppercased()
   let noSpace=joined.filter{!$0.isWhitespace}
   let numeric=noSpace.replacingOccurrences(of:"O",with:"0").replacingOccurrences(of:"I",with:"1").replacingOccurrences(of:"L",with:"1")
   let digits=name.filter{$0.isNumber}
   let imageMatch=texts.contains { line in
    let s=line.uppercased();return s.contains("SYNTHETIC") && s.contains("12MP") && s.contains(digits)
   }
   let valid=kind=="image" ? imageMatch : (joined.contains("SYNTHETIC") && numeric.contains("D0CUMENT"+digits) && numeric.contains("PAGE010F20"))
   if valid {
    finished.insert(i)
    results.append(["eventIndex":i,"file":name,"contentPts":t,"seconds":t-((event["inputHostSeconds"] as! Double)-first),"ocr":texts])
   }
   if finished.count==events.count && !fullDecode {break}
  }
  if fullDecode && reader.status != .completed {throw NSError(domain:"SourceDecode",code:1,userInfo:[NSLocalizedDescriptionKey:reader.error?.localizedDescription ?? "Incomplete source decode"])}
  FileHandle.standardOutput.write(try JSONSerialization.data(withJSONObject:results,options:[.sortedKeys]))
 }
}
