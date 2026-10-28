import AppKit
import ApplicationServices
import ScreenCaptureKit
import AVFoundation
import CoreMedia
import QuartzCore

func json(_ value: Any) -> Data { try! JSONSerialization.data(withJSONObject: value, options: [.sortedKeys]) }
func emit(_ value: Any) { FileHandle.standardOutput.write(json(value)); print(""); fflush(stdout) }
func save(_ path: String, _ value: Any) { try! json(value).write(to: URL(fileURLWithPath: path), options: .atomic) }
func failure(_ message: String) -> Never { emit(["error":message]); exit(1) }
func config(_ path: String) throws -> [String:Any] {
    let d = try JSONSerialization.jsonObject(with: Data(contentsOf: URL(fileURLWithPath:path))) as! [String:Any]
    let root = d["root"] as! String, token = d["token"] as! String
    guard root.hasPrefix("/private/tmp/fkfinder-test."), token.count >= 32,
          try String(contentsOfFile:root+"/.demo-owner", encoding:.utf8) == token else { failure("Invalid isolated profile") }
    return d
}
func point(_ d:[String:Any]) -> CGPoint { CGPoint(x:(d["x"] as! NSNumber).doubleValue, y:(d["y"] as! NSNumber).doubleValue) }
func target(_ d:[String:Any]) -> pid_t {
    let pid = pid_t((d["pid"] as! NSNumber).intValue)
    guard let app=NSRunningApplication(processIdentifier:pid), app.bundleIdentifier=="com.macexplorer.demo",
          app.executableURL?.path == d["executable"] as? String else { failure("Only this run's isolated demo executable is allowed") }
    return pid
}
func hit(_ p:CGPoint, _ allowed:Set<pid_t>) {
    let windows=CGWindowListCopyWindowInfo([.optionOnScreenOnly,.excludeDesktopElements],kCGNullWindowID) as! [[String:Any]]
    for w in windows {
        guard let layer=w[kCGWindowLayer as String] as? Int,layer>=0,layer<=102,
              (w[kCGWindowAlpha as String] as? Double ?? 1)>0.05,
              let b=w[kCGWindowBounds as String] as? [String:Any],
              let r=CGRect(dictionaryRepresentation:b as CFDictionary),r.contains(p) else {continue}
        let pid=pid_t((w[kCGWindowOwnerPID as String] as! NSNumber).intValue)
        // Dock owns a full-display level-20 passive surface on current macOS.
        // It is not a mouse target; retain checks for its actual smaller windows.
        if layer==20,NSRunningApplication(processIdentifier:pid)?.bundleIdentifier=="com.apple.dock",
           r.size==CGDisplayBounds(CGMainDisplayID()).size {continue}
        guard allowed.contains(pid) else {failure("Input target is covered by an unrelated window (PID \(pid))")}
        return
    }
    failure("No permitted window at input point")
}
func mouse(_ type:CGEventType,_ p:CGPoint,_ right:Bool=false,_ count:Int64=1,_ modifiers:CGEventFlags=[]) {
    let event=CGEvent(mouseEventSource:nil,mouseType:type,mouseCursorPosition:p,mouseButton:right ? .right:.left)!
    event.flags=modifiers
    event.setIntegerValueField(.mouseEventClickState,value:count);event.post(tap:.cghidEventTap)
}
func flags(_ text:String) -> CGEventFlags {
    var f:CGEventFlags=[]
    if text.contains("cmd") {f.insert(.maskCommand)}
    if text.contains("shift") {f.insert(.maskShift)}
    if text.contains("alt") {f.insert(.maskAlternate)}
    if text.contains("ctrl") {f.insert(.maskControl)}
    return f
}
@MainActor func input(_ d:[String:Any]) {
    let pid=target(d),op=d["action"] as! String
    guard CGPreflightPostEventAccess() else {failure("Post-event permission is missing for DemoNative")}
    if op=="activate" {NSRunningApplication(processIdentifier:pid)!.activate(options:[]);emit(["ok":true]);return}
    if op=="inspect" {emit(["frontmostPid":NSWorkspace.shared.frontmostApplication?.processIdentifier ?? 0]);return}
    if op=="activate-receiver" {
        let receiver=pid_t((d["receiverPid"] as! NSNumber).intValue)
        guard let app=NSRunningApplication(processIdentifier:receiver),app.bundleIdentifier=="com.macexplorer.demorecorder" else {failure("Unknown receiver")}
        app.activate(options:[]);emit(["ok":true]);return
    }
    if op=="click" || op=="move" || op=="drag" {
        let p=point(d),right=d["right"] as? Bool ?? false
        hit(p,[pid])
        if op=="move" {mouse(.mouseMoved,p)}
        else if op=="click" {
            for n in 1...(d["count"] as? Int ?? 1) {
                mouse(right ? .rightMouseDown:.leftMouseDown,p,right,Int64(n));Thread.sleep(forTimeInterval:0.05)
                mouse(right ? .rightMouseUp:.leftMouseUp,p,right,Int64(n));Thread.sleep(forTimeInterval:0.08)
            }
        } else {
            let end=point(d["destination"] as! [String:Any])
            let receiver=pid_t((d["receiverPid"] as! NSNumber).intValue)
            guard NSRunningApplication(processIdentifier:receiver)?.bundleIdentifier=="com.macexplorer.demorecorder" else {failure("Unknown receiver")}
            hit(end,[receiver])
            let modifiers=flags(d["modifiers"] as? String ?? "")
            mouse(.leftMouseDown,p,false,1,modifiers)
            defer {mouse(.leftMouseUp,end,false,1,modifiers)}
            for i in 1...90 {
                let f=Double(i)/90;mouse(.leftMouseDragged,CGPoint(x:p.x+(end.x-p.x)*f,y:p.y+(end.y-p.y)*f),false,1,modifiers)
                Thread.sleep(forTimeInterval:0.016)
            }
        }
    } else {
        guard op=="command-up" || NSWorkspace.shared.frontmostApplication?.processIdentifier==pid else {failure("Demo app is not frontmost")}
        let f=flags(d["modifiers"] as? String ?? "")
        if op=="text" {
            for character in d["text"] as! String {
                if character == "\n" {
                    for down in [true,false] {let e=CGEvent(keyboardEventSource:nil,virtualKey:36,keyDown:down)!;e.flags=[];e.postToPid(pid)}
                } else {
                    let text=Array(String(character).utf16)
                    for down in [true,false] {
                        let e=CGEvent(keyboardEventSource:nil,virtualKey:0,keyDown:down)!
                        e.flags=[]
                        text.withUnsafeBufferPointer{e.keyboardSetUnicodeString(stringLength:text.count,unicodeString:$0.baseAddress!)}
                        e.postToPid(pid)
                    }
                }
                Thread.sleep(forTimeInterval:0.025)
            }
        } else if op=="key" || op=="command-down" || op=="command-up" {
            let code=CGKeyCode(d["code"] as? Int ?? 55)
            for down in (op=="key" ? [true,false] : [op=="command-down"]) {
                let e=CGEvent(keyboardEventSource:CGEventSource(stateID:.hidSystemState),virtualKey:code,keyDown:down)!
                e.flags=op=="command-down" ? .maskCommand:op=="command-up" ? []:f
                if op != "key" {e.type = .flagsChanged}
                if op=="key" {e.postToPid(pid)} else {e.post(tap:.cghidEventTap)}
                Thread.sleep(forTimeInterval:0.07)
            }
            if op=="key" && !f.isEmpty {
                let release=CGEvent(keyboardEventSource:nil,virtualKey:55,keyDown:false)!
                release.type = .flagsChanged;release.flags=[];release.postToPid(pid)
            }
        } else {failure("Unknown input action")}
    }
    emit(["ok":true])
}
final class CaptureSink:NSObject,SCStreamOutput,SCStreamDelegate,@unchecked Sendable {
    let writer:AVAssetWriter,input:AVAssetWriterInput,path:String
    private let errorLock=NSLock()
    private var captureError:String?
    var error:String? {
        get {errorLock.lock();defer{errorLock.unlock()};return captureError}
        set {errorLock.lock();defer{errorLock.unlock()};captureError=newValue}
    }
    var count=0,dropped=0,idle=0,started=false,first:CMTime = .zero,last:CMTime = .zero,samplePts=0.0
    init(path:String,width:Int,height:Int) throws {
        self.path=path
        writer=try AVAssetWriter(outputURL:URL(fileURLWithPath:path),fileType:.mov)
        input=AVAssetWriterInput(mediaType:.video,outputSettings:[AVVideoCodecKey:AVVideoCodecType.h264,AVVideoWidthKey:width,AVVideoHeightKey:height,AVVideoCompressionPropertiesKey:[AVVideoAverageBitRateKey:20000000,AVVideoMaxKeyFrameIntervalKey:60]])
        input.expectsMediaDataInRealTime=true;writer.movieFragmentInterval=CMTime(seconds:2,preferredTimescale:600)
        writer.add(input)
    }
    func stream(_ stream:SCStream,didStopWithError error:Error) {self.error=error.localizedDescription;save(path+".error.json",["error":error.localizedDescription])}
    func stream(_ stream:SCStream,didOutputSampleBuffer s:CMSampleBuffer,of type:SCStreamOutputType) {
        guard type == .screen,s.isValid,let attachments=CMSampleBufferGetSampleAttachmentsArray(s,createIfNecessary:false) as? [[SCStreamFrameInfo:Any]],
              let raw=attachments.first?[.status] as? Int,let status=SCFrameStatus(rawValue:raw) else {return}
        samplePts=s.presentationTimeStamp.seconds
        if status == .idle {idle += 1;if started {saveProgress()};return}
        guard status == .complete,CMSampleBufferGetImageBuffer(s) != nil else {return}
        if !started {
            guard writer.startWriting() else {error=writer.error?.localizedDescription ?? "startWriting failed";return}
            first=s.presentationTimeStamp;writer.startSession(atSourceTime:first);started=true
        }
        guard input.isReadyForMoreMediaData else {dropped += 1;return}
        guard input.append(s) else {error=writer.error?.localizedDescription ?? "append failed";return}
        count += 1;last=s.presentationTimeStamp
        saveProgress()
    }
    private func saveProgress() {
        save(path+".frames.json",["frames":count,"dropped":dropped,"idle":idle,"firstPts":first.seconds,"lastPts":last.seconds,"samplePts":samplePts,"hostSeconds":CMClockGetTime(CMClockGetHostTimeClock()).seconds])
    }
}
@MainActor final class DesktopBackdrop:NSView {
    let image:NSImage
    init(image:NSImage,frame:NSRect) {self.image=image;super.init(frame:frame)}
    required init?(coder:NSCoder){fatalError()}
    override func draw(_ dirtyRect:NSRect) {
        let scale=max(bounds.width/image.size.width,bounds.height/image.size.height)
        let size=NSSize(width:image.size.width*scale,height:image.size.height*scale)
        image.draw(in:NSRect(x:(bounds.width-size.width)/2,y:(bounds.height-size.height)/2,width:size.width,height:size.height))
        NSColor.black.withAlphaComponent(0.16).setFill();bounds.fill()
    }
}
@MainActor func desktopBackdrop(_ d:[String:Any]) throws -> NSWindow {
    let root=URL(fileURLWithPath:d["root"] as! String).resolvingSymlinksInPath()
    let path=URL(fileURLWithPath:d["desktopBackground"] as! String).resolvingSymlinksInPath()
    guard path.path.hasPrefix(root.path+"/"),let image=NSImage(contentsOf:path),image.size.width>0,image.size.height>0,
          let screen=NSScreen.screens.first(where:{($0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value == CGMainDisplayID()}) else {
        throw NSError(domain:"DemoRecorder",code:1,userInfo:[NSLocalizedDescriptionKey:"Invalid isolated desktop background"])
    }
    let window=NSWindow(contentRect:screen.frame,styleMask:.borderless,backing:.buffered,defer:false)
    window.isReleasedWhenClosed=false;window.isOpaque=true;window.hasShadow=false;window.ignoresMouseEvents=true
    window.level=NSWindow.Level(rawValue:NSWindow.Level.normal.rawValue-1)
    window.collectionBehavior=[.canJoinAllSpaces,.stationary,.ignoresCycle]
    window.contentView=DesktopBackdrop(image:image,frame:NSRect(origin:.zero,size:screen.frame.size))
    window.orderFrontRegardless();window.displayIfNeeded()
    return window
}
@MainActor func record(_ d:[String:Any]) async throws {
    let pid=target(d)
    guard CGPreflightScreenCaptureAccess() else {failure("Screen recording permission missing for DemoNative")}
    let backdrop=try desktopBackdrop(d)
    defer {backdrop.close()}
    NSApplication.shared.finishLaunching();NSApplication.shared.updateWindows();CATransaction.flush()
    try await Task.sleep(for:.milliseconds(200))
    let content=try await SCShareableContent.excludingDesktopWindows(false,onScreenWindowsOnly:true)
    let displayId=(d["displayId"] as? NSNumber)?.uint32Value ?? CGMainDisplayID()
    guard let display=content.displays.first(where:{$0.displayID == displayId}) else {failure("Primary display unavailable")}
    let recorderPid=ProcessInfo.processInfo.processIdentifier
    guard content.windows.contains(where:{$0.windowID == CGWindowID(backdrop.windowNumber)}) else {failure("Desktop background is not capturable")}
    var pids:Set<pid_t>=[pid,recorderPid]
    if let receiver=d["receiverPid"] as? NSNumber {pids.insert(pid_t(receiver.intValue))}
    let apps=content.applications.filter{pids.contains($0.processID)}
    guard apps.contains(where:{$0.processID == recorderPid}) else {failure("Desktop background application is missing")}
    let filter=SCContentFilter(display:display,including:apps,exceptingWindows:[])
    if #available(macOS 14.2,*) {filter.includeMenuBar=d["menuBar"] as? Bool ?? false}
    let cfg=SCStreamConfiguration()
    let rect=d["rect"] as! [Double]
    cfg.sourceRect=CGRect(x:rect[0],y:rect[1],width:rect[2],height:rect[3])
    cfg.width=Int(rect[2]*2);cfg.height=Int(rect[3]*2)
    cfg.minimumFrameInterval=CMTime(value:1,timescale:30);cfg.showsCursor=true;cfg.capturesAudio=false;cfg.queueDepth=5
    let backgroundColor=CGColor(red:0.96,green:0.97,blue:0.98,alpha:1)
    cfg.backgroundColor=backgroundColor
    if #available(macOS 14.2,*) {cfg.includeChildWindows=true}
    let path=d["path"] as! String,sink=try CaptureSink(path:path,width:cfg.width,height:cfg.height)
    let queue=DispatchQueue(label:"demo.capture.samples"),stream=SCStream(filter:filter,configuration:cfg,delegate:sink)
    try stream.addStreamOutput(sink,type:.screen,sampleHandlerQueue:queue)
    try await stream.startCapture()
    let deadline=Date().addingTimeInterval(180)
    while Date()<deadline && !FileManager.default.fileExists(atPath:path+".stop") && sink.error==nil {try await Task.sleep(for:.milliseconds(60))}
    try await stream.stopCapture()
    await withCheckedContinuation{c in queue.async{c.resume()}}
    guard sink.started else {failure("No valid screen frame")}
    sink.input.markAsFinished();await sink.writer.finishWriting()
    let result:[String:Any]=["ok":sink.writer.status == .completed && sink.error==nil,"status":sink.writer.status.rawValue,"frames":sink.count,"dropped":sink.dropped,"idle":sink.idle,"firstPts":sink.first.seconds,"lastPts":sink.last.seconds,"error":sink.error ?? sink.writer.error?.localizedDescription ?? "","width":cfg.width,"height":cfg.height,"displayId":display.displayID]
    save(path+".done.json",result);emit(result)
    withExtendedLifetime(backgroundColor) {}
    if sink.writer.status != .completed || sink.error != nil {exit(1)}
}
@MainActor final class DropView:NSView {
    let d:[String:Any],label=NSTextField(labelWithString:"拖入文件，保存一份副本")
    init(_ d:[String:Any]) {
        self.d=d;super.init(frame:NSRect(x:0,y:0,width:450,height:300));registerForDraggedTypes([.fileURL]);wantsLayer=true;layer?.backgroundColor=NSColor.windowBackgroundColor.cgColor
        let title=NSTextField(labelWithString:"演示收件箱");title.font = .systemFont(ofSize:24,weight:.semibold);title.frame=NSRect(x:24,y:220,width:380,height:35);addSubview(title)
        label.frame=NSRect(x:24,y:120,width:390,height:70);label.maximumNumberOfLines=3;label.font = .systemFont(ofSize:16);addSubview(label)
    }
    required init?(coder:NSCoder){fatalError()}
    override func draggingEntered(_ sender:NSDraggingInfo)->NSDragOperation{emit(["drag":"entered","types":sender.draggingPasteboard.types?.map{$0.rawValue} ?? []]);return .copy}
    override func prepareForDragOperation(_ sender:NSDraggingInfo)->Bool{true}
    override func performDragOperation(_ sender:NSDraggingInfo)->Bool {
        guard let urls=sender.draggingPasteboard.readObjects(forClasses:[NSURL.self],options:[.urlReadingFileURLsOnly:true]) as? [URL],!urls.isEmpty else{emit(["drag":"no-file-urls"]);return false}
        let root=URL(fileURLWithPath:d["root"] as! String),destination=root.appendingPathComponent("Documents/收件箱")
        do {
            try FileManager.default.createDirectory(at:destination,withIntermediateDirectories:true)
            for url in urls {
                guard url.resolvingSymlinksInPath().path.hasPrefix(root.resolvingSymlinksInPath().path+"/") else {emit(["drag":"outside-profile"]);return false}
                let to=destination.appendingPathComponent(url.lastPathComponent)
                if FileManager.default.fileExists(atPath:to.path){try FileManager.default.removeItem(at:to)}
                try FileManager.default.copyItem(at:url,to:to)
            }
            label.stringValue="已收到 \(urls.count) 个文件\n"+urls.map{$0.lastPathComponent}.joined(separator:"、")
            save(d["eventPath"] as! String,["saved":urls.map{$0.lastPathComponent}]);return true
        }catch{label.stringValue=error.localizedDescription;emit(["drag":"error","message":error.localizedDescription]);return false}
    }
}
@MainActor func receiver(_ d:[String:Any]) {
    let app=NSApplication.shared;app.setActivationPolicy(.regular)
    let w=NSWindow(contentRect:NSRect(x:850,y:200,width:450,height:300),styleMask:[.titled,.closable],backing:.buffered,defer:false)
    w.title="演示收件箱";w.contentView=DropView(d);w.makeKeyAndOrderFront(nil);app.activate(ignoringOtherApps:true)
    let display=CGMainDisplayID(),height=CGDisplayBounds(display).height
    save(d["readyPath"] as! String,["pid":ProcessInfo.processInfo.processIdentifier,"x":w.frame.midX,"y":height-w.frame.midY])
    app.run()
}
@main struct DemoNative {
    @MainActor static func main() async {
        do {
            let args=CommandLine.arguments
            if args[1] != "receiver" {NSApplication.shared.setActivationPolicy(args[1] == "record" ? .accessory:.prohibited)}
            switch args[1] {
            case "doctor":
                emit(["screenCapture":CGPreflightScreenCaptureAccess(),"postEvents":CGPreflightPostEventAccess(),"accessibility":AXIsProcessTrusted(),"displays":NSScreen.screens.map{["width":$0.frame.width,"height":$0.frame.height,"scale":$0.backingScaleFactor]}])
            case "input": input(try config(args[2]))
            case "fixture-image":
                let d=try config(args[2]),root=d["root"] as! String
                let image=NSImage(size:NSSize(width:1200,height:800));image.lockFocus()
                NSColor(calibratedRed:0.95,green:0.97,blue:1,alpha:1).setFill();NSRect(x:0,y:0,width:1200,height:800).fill()
                for (text,y,size) in [("MOUNTAIN STUDIO",600.0,64.0),("LAKE PHOTOGRAPHY",420.0,54.0),("山野计划 · 交付清单",240.0,50.0)] {
                    (text as NSString).draw(at:NSPoint(x:80,y:y),withAttributes:[.font:NSFont.systemFont(ofSize:size),.foregroundColor:NSColor.labelColor])
                }
                image.unlockFocus()
                let rep=NSBitmapImageRep(data:image.tiffRepresentation!)!
                try rep.representation(using:.png,properties:[:])!.write(to:URL(fileURLWithPath:root+"/Documents/山野计划/原始资料/会议白板.png"))
                emit(["ok":true])
            case "record": try await record(config(args[2]))
            case "receiver": receiver(try config(args[2]))
            default:failure("Unknown native command")
            }
        }catch{failure(String(describing:error))}
    }
}
