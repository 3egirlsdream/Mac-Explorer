// External observations only. This helper is never linked into the tested product.
func target(_ d:[String:Any]) -> pid_t {
    let pid=pid_t((d["pid"] as! NSNumber).intValue)
    guard let app=NSRunningApplication(processIdentifier:pid),
          app.executableURL?.path == d["executable"] as? String,
          ["com.thankful.top.macexplorer","com.apple.finder"].contains(app.bundleIdentifier ?? "") else {failure("Unexpected comparison target")}
    return pid
}
func ax(_ e:AXUIElement,_ key:String)->CFTypeRef? {
    var value:CFTypeRef?;AXUIElementCopyAttributeValue(e,key as CFString,&value);return value
}
func snapshot(_ e:AXUIElement,_ depth:Int=0,_ budget:inout Int)->[String:Any] {
    if depth>40 || budget<=0{return [:]};budget-=1
    var row:[String:Any]=[:]
    for key in ["AXRole","AXSubrole","AXTitle","AXDescription","AXValue","AXIdentifier","AXEnabled","AXSelected","AXFocused"] {
        if let v=ax(e,key),CFGetTypeID(v) != AXUIElementGetTypeID() {row[key]=String(describing:v)}
    }
    if let value=ax(e,"AXPosition"),CFGetTypeID(value)==AXValueGetTypeID() {
        var p=CGPoint.zero;AXValueGetValue(value as! AXValue,.cgPoint,&p);row["x"]=p.x;row["y"]=p.y
    }
    if let value=ax(e,"AXSize"),CFGetTypeID(value)==AXValueGetTypeID() {
        var s=CGSize.zero;AXValueGetValue(value as! AXValue,.cgSize,&s);row["width"]=s.width;row["height"]=s.height
    }
    if let children=ax(e,"AXChildren") as? [AXUIElement] {row["children"]=children.prefix(40).map{snapshot($0,depth+1,&budget)}}
    return row
}

func stateTexts(_ e:AXUIElement,_ depth:Int=0)->[String] {
    if depth>40{return []}
    let role=ax(e,"AXRole") as? String ?? ""
    var result=[String]()
    for key in ["AXTitle","AXValue","AXDescription"] {if let value=ax(e,key) as? String,!value.isEmpty {result.append(value)}}
    // The custom list exposes no file children; Finder exposes all rows.
    // Status and title are read without walking its potentially 100,000 rows.
    if role=="AXOutline" || role=="AXList" {return result}
    if let children=ax(e,"AXChildren") as? [AXUIElement] {for child in children.prefix(80){result += stateTexts(child,depth+1)}}
    return result
}
nonisolated(unsafe) var controlledWindowNumber:CGWindowID?
func frontWindow(_ pid:pid_t)->AXUIElement {
    let application=AXUIElementCreateApplication(pid)
    guard let windows=ax(application,"AXWindows") as? [AXUIElement] else {failure("No controlled window")}
    if NSRunningApplication(processIdentifier:pid)?.bundleIdentifier=="com.apple.finder",let number=controlledWindowNumber {
        let all=CGWindowListCopyWindowInfo(.optionAll,kCGNullWindowID) as! [[String:Any]]
        guard let target=all.first(where:{($0[kCGWindowNumber as String] as? NSNumber)?.uint32Value==number}),let bounds=target[kCGWindowBounds as String] as? [String:Any],let rect=CGRect(dictionaryRepresentation:bounds as CFDictionary) else {failure("Controlled Finder window missing")}
        let matches=windows.filter { w in
            guard let position=ax(w,"AXPosition"),CFGetTypeID(position)==AXValueGetTypeID() else {return false}
            var point=CGPoint.zero;AXValueGetValue(position as! AXValue,.cgPoint,&point)
            return abs(point.x-rect.minX)<1 && abs(point.y-rect.minY)<1 && (ax(w,"AXSubrole") as? String)=="AXStandardWindow"
        }
        guard matches.count==1 else {failure("Controlled Finder window is ambiguous")};return matches[0]
    }
    guard let e=windows.first(where:{(ax($0,"AXSubrole") as? String)=="AXStandardWindow"}) ?? windows.first else {failure("No controlled window")};return e
}


func statusElement(_ e:AXUIElement,_ depth:Int=0)->AXUIElement? {
    if depth>40 {return nil}
    let role=ax(e,"AXRole") as? String ?? ""
    if role=="AXStaticText",let value=ax(e,"AXValue") as? String,value.contains("项") {return e}
    if role=="AXOutline" || role=="AXList" {return nil}
    if let children=ax(e,"AXChildren") as? [AXUIElement] {for child in children.prefix(80){if let found=statusElement(child,depth+1){return found}}}
    return nil
}


@MainActor func input(_ d:[String:Any]) {
    if d["action"] as? String=="text-batch" {
        let pid=target(d)
        guard CGPreflightPostEventAccess(),NSWorkspace.shared.frontmostApplication?.processIdentifier==pid else {failure("Text target is not frontmost")}
        let text=Array((d["text"] as! String).utf16)
        guard text.count<=255,!text.contains(10) else {failure("Continuous text input exceeds the bounded field contract")}
        let start=CMClockGetTime(CMClockGetHostTimeClock()).seconds
        for down in [true,false] {
            let e=CGEvent(keyboardEventSource:nil,virtualKey:0,keyDown:down)!;e.flags=[]
            text.withUnsafeBufferPointer{e.keyboardSetUnicodeString(stringLength:text.count,unicodeString:$0.baseAddress!)}
            e.postToPid(pid);Thread.sleep(forTimeInterval:0.04)
        }
        emit(["ok":true,"inputStartedHostSeconds":start,"continuousText":true]);return
    }
    if d["action"] as? String=="scroll" {
        let pid=target(d),p=point(d)
        guard CGPreflightPostEventAccess() else {failure("Scroll permission missing")};hit(p,[pid]);mouse(.mouseMoved,p)
        let e=CGEvent(scrollWheelEvent2Source:CGEventSource(stateID:.hidSystemState),units:.pixel,wheelCount:1,wheel1:Int32(d["delta"] as! Int),wheel2:0,wheel3:0)!
        e.post(tap:.cghidEventTap);emit(["ok":true]);return
    }
    guard d["action"] as? String=="key" else {legacyInput(d);return}
    let pid=target(d)
    guard CGPreflightPostEventAccess(),NSWorkspace.shared.frontmostApplication?.processIdentifier==pid else {failure("Native key target is not frontmost or permission is missing")}
    let requested=flags(d["modifiers"] as? String ?? "")
    let modifiers:[(CGEventFlags,CGKeyCode)]=[(.maskCommand,55),(.maskShift,56),(.maskAlternate,58),(.maskControl,59)]
    var held:CGEventFlags=[]
    for (flag,code) in modifiers where requested.contains(flag) {
        held.insert(flag)
        let e=CGEvent(keyboardEventSource:CGEventSource(stateID:.hidSystemState),virtualKey:code,keyDown:true)!;e.type = .flagsChanged;e.flags=held;e.post(tap:.cghidEventTap)
        Thread.sleep(forTimeInterval:0.02)
    }
    let start=CMClockGetTime(CMClockGetHostTimeClock()).seconds
    let code=CGKeyCode(d["code"] as! Int)
    for down in [true,false] {
        let e=CGEvent(keyboardEventSource:CGEventSource(stateID:.hidSystemState),virtualKey:code,keyDown:down)!;e.flags=held;e.post(tap:.cghidEventTap)
        Thread.sleep(forTimeInterval:0.04)
    }
    for (flag,code) in modifiers.reversed() where requested.contains(flag) {
        held.remove(flag)
        let e=CGEvent(keyboardEventSource:CGEventSource(stateID:.hidSystemState),virtualKey:code,keyDown:false)!;e.type = .flagsChanged;e.flags=held;e.post(tap:.cghidEventTap)
        Thread.sleep(forTimeInterval:0.02)
    }
    emit(["ok":true,"inputStartedHostSeconds":start,"inputFinishedHostSeconds":CMClockGetTime(CMClockGetHostTimeClock()).seconds,"routing":"physical HID with paired modifiers"])
}


func pointOwner(_ p:CGPoint)->pid_t? {
    var element:AXUIElement?
    guard AXUIElementCopyElementAtPosition(AXUIElementCreateSystemWide(),Float(p.x),Float(p.y),&element) == .success,let element else {return nil}
    var pid:pid_t=0
    guard AXUIElementGetPid(element,&pid) == .success else {return nil}
    return pid
}
// CG stacking includes NotificationCenter's click-through desktop-widget surface.
// Only that exact full-display layer21 surface and DemoRecord's Dock layer20
// exception are skipped. All other windows retain the original owner guard.
func hit(_ p:CGPoint,_ allowed:Set<pid_t>) {
    guard let active=NSWorkspace.shared.frontmostApplication?.processIdentifier,allowed.contains(active) else {failure("Input target is not frontmost")}
    visualHit(p,allowed)
}
@main struct CompareNative {
    @MainActor static func main() async {
        do {
            let args=CommandLine.arguments,op=args[1]
            NSApplication.shared.setActivationPolicy(op=="record" ? .accessory:.prohibited)
            if op=="doctor" {emit(["screenCapture":CGPreflightScreenCaptureAccess(),"postEvents":CGPreflightPostEventAccess(),"accessibility":AXIsProcessTrusted()]);return}
            let d=try config(args[2]);controlledWindowNumber=(d["finderWindowId"] as? NSNumber)?.uint32Value;let pid=target(d)
            switch op {
            case "input":input(d)
            case "record":try await record(d)
            case "snapshot":
                var budget=2000
                let e=frontWindow(pid)
                emit(["hostSeconds":CMClockGetTime(CMClockGetHostTimeClock()).seconds,"tree":snapshot(e,0,&budget)])
            case "raise":
                let result=AXUIElementPerformAction(frontWindow(pid),kAXRaiseAction as CFString)
                emit(["ok":result == .success])
            case "hit-test":
                hit(point(d),[pid]);emit(["ok":true])
            case "state":
                emit(["hostSeconds":CMClockGetTime(CMClockGetHostTimeClock()).seconds,"texts":stateTexts(frontWindow(pid))])
            case "watch-interactive":
                guard NSWorkspace.shared.frontmostApplication?.processIdentifier==pid else {failure("Target is not frontmost")}
                let window=frontWindow(pid)
                guard let footer=statusElement(window) else {failure("Status footer unavailable")}
                let before=CMClockGetTime(CMClockGetHostTimeClock()).seconds
                for down in [true,false] {let event=CGEvent(keyboardEventSource:nil,virtualKey:36,keyDown:down)!;event.flags=[];event.post(tap:.cghidEventTap)}
                var samples=[[String:Any]](),clicks=[Double](),matched=false
                let deadline=Date().addingTimeInterval(30)
                let expected=d["expected"] as! String
                while Date()<deadline {
                    let w=window,texts=[ax(footer,"AXValue") as? String ?? ""],now=CMClockGetTime(CMClockGetHostTimeClock()).seconds
                    samples.append(["hostSeconds":now,"texts":texts])
                    if (ax(w,"AXTitle") as? String)==d["title"] as? String && texts.contains(where:{$0.contains(expected)}) {matched=true;break}
                    let p=point(d)
                    if let owner=pointOwner(p) {
                        guard owner==pid else {failure("Selection probe is covered by another application")}
                        clicks.append(CMClockGetTime(CMClockGetHostTimeClock()).seconds)
                        mouse(.leftMouseDown,p);mouse(.leftMouseUp,p)
                    }
                    try await Task.sleep(for:.milliseconds(25))
                }
                emit(["inputHostSeconds":before,"matched":matched,"samples":samples,"clickHostSeconds":clicks])
            case "watch":
                let window=frontWindow(pid)
                guard let footer=statusElement(window) else {failure("Status footer unavailable")}
                let before=CMClockGetTime(CMClockGetHostTimeClock()).seconds
                let key=CGEvent(keyboardEventSource:nil,virtualKey:36,keyDown:true)!;key.flags=[];key.post(tap:.cghidEventTap)
                let up=CGEvent(keyboardEventSource:nil,virtualKey:36,keyDown:false)!;up.flags=[];up.post(tap:.cghidEventTap)
                var samples=[[String:Any]]()
                let expected=d["expected"] as! String
                let deadline=Date().addingTimeInterval(d["timeout"] as? Double ?? 30)
                var matched=false
                while Date()<deadline {
                    let t=CMClockGetTime(CMClockGetHostTimeClock()).seconds
                    let texts=[ax(footer,"AXValue") as? String ?? ""]
                    samples.append(["startHostSeconds":t,"endHostSeconds":CMClockGetTime(CMClockGetHostTimeClock()).seconds,"texts":texts])
                    if texts.contains(where:{$0.contains(expected)}) && (d["title"] == nil || (ax(window,"AXTitle") as? String)==d["title"] as? String) {matched=true;break}
                    try await Task.sleep(for:.milliseconds(20))
                }
                emit(["inputHostSeconds":before,"matched":matched,"samples":samples])
            case "windows":
                let windows=CGWindowListCopyWindowInfo([.optionOnScreenOnly,.excludeDesktopElements],kCGNullWindowID) as! [[String:Any]]
                emit(windows.filter{($0[kCGWindowOwnerPID as String] as? NSNumber)?.int32Value==pid})
            case "frame":
                let e=AXUIElementCreateApplication(pid)
                guard let windows=ax(e,"AXWindows") as? [AXUIElement],let w=windows.first else {failure("No window")}
                var p=point(d),s=CGSize(width:d["width"] as! Double,height:d["height"] as! Double)
                AXUIElementSetAttributeValue(w,"AXPosition" as CFString,AXValueCreate(.cgPoint,&p)!)
                AXUIElementSetAttributeValue(w,"AXSize" as CFString,AXValueCreate(.cgSize,&s)!)
                emit(["ok":true])
            case "footprint":
                var info=rusage_info_v4()
                let status=withUnsafeMutablePointer(to:&info){ptr in ptr.withMemoryRebound(to:rusage_info_t?.self,capacity:1){proc_pid_rusage(pid,RUSAGE_INFO_V4,$0)}}
                guard status==0 else {failure("proc_pid_rusage failed")}
                emit(["hostSeconds":CMClockGetTime(CMClockGetHostTimeClock()).seconds,"pid":pid,"physicalFootprintBytes":info.ri_phys_footprint])
            default:failure("Unknown comparison command")
            }
        }catch{failure(String(describing:error))}
    }
}
