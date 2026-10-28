#!/usr/bin/env python3
"""Reuse DemoRecord's native capture/input implementation, leaving it untouched."""
from pathlib import Path
import plistlib, subprocess
repo=Path(__file__).resolve().parents[3]
out=repo/'outputs/finder-comparison-20261025/tools'
app=out/'Comparison Recorder.app'
exe=app/'Contents/MacOS/CompareNative'
exe.parent.mkdir(parents=True,exist_ok=True)
s=(repo/'Tools/DemoRecord/native/DemoNative.swift').read_text().split('@MainActor final class DropView:')[0]
s=s.replace('if layer==20,NSRunningApplication', 'if layer==21,NSRunningApplication(processIdentifier:pid)?.bundleIdentifier=="com.apple.notificationcenterui",r.size==CGDisplayBounds(CGMainDisplayID()).size {continue}\n        if layer==20,NSRunningApplication')
s=s.replace('func hit(_ p:CGPoint', 'func visualHit(_ p:CGPoint',1)
s=s.replace('@MainActor func input(_ d:', '@MainActor func legacyInput(_ d:',1)
s=s.replace('func target(_ d:', 'func demoTarget(_ d:',1)
s=s.replace('let filter=SCContentFilter(display:display,including:apps,exceptingWindows:[])', '''let filter:SCContentFilter
    if let ids=d["windowIds"] as? [UInt32] {
        let windows=content.windows.filter{ids.contains($0.windowID) || $0.owningApplication?.processID==recorderPid}
        guard ids.allSatisfy({id in windows.contains{$0.windowID==id}}) else {failure("Controlled window unavailable")}
        filter=SCContentFilter(display:display,including:windows)
    } else {filter=SCContentFilter(display:display,including:apps,exceptingWindows:[])}''')
s=s.replace('let pid=target(d),op=d["action"] as! String','let inputStart=CMClockGetTime(CMClockGetHostTimeClock()).seconds;let pid=target(d),op=d["action"] as! String')
s=s.replace('emit(["ok":true])','emit(["ok":true,"inputStartedHostSeconds":inputStart,"inputFinishedHostSeconds":CMClockGetTime(CMClockGetHostTimeClock()).seconds])')
s=s.replace('addingTimeInterval(180)','addingTimeInterval(1000)')
s=s.replace('while Date()<deadline && !FileManager.default.fileExists(atPath:path+".stop") && sink.error==nil {try await Task.sleep(for:.milliseconds(60))}', '''var nextWindowRefresh=Date.distantPast
    var lastOwnedIds=Set<CGWindowID>()
    while Date()<deadline && !FileManager.default.fileExists(atPath:path+".stop") && sink.error==nil {
        if d["dynamicOwnedWindows"] as? Bool == true && Date()>=nextWindowRefresh {
            nextWindowRefresh=Date().addingTimeInterval(0.25)
            let current=try await SCShareableContent.excludingDesktopWindows(false,onScreenWindowsOnly:true)
            let baseIds=Set((d["windowIds"] as? [UInt32]) ?? [])
            let isFinder=NSRunningApplication(processIdentifier:pid)?.bundleIdentifier=="com.apple.finder"
            let fixtureTitles:Set<String>=["Comparison","list-1000","list-10000","list-100000","images-12MP","pdf-20-pages"]
            let owned=current.windows.filter { w in
                let controlledTab=d["dynamicControlledFinderTabs"] as? Bool == true && fixtureTitles.contains(w.title ?? "") && abs(w.frame.minX-120)<1 && abs(w.frame.minY-70)<1 && abs(w.frame.width-1200)<1 && abs(w.frame.height-680)<1
                return w.owningApplication?.processID==recorderPid || (w.owningApplication?.processID==pid && (!isFinder || baseIds.contains(w.windowID) || w.windowLayer>0 || controlledTab))
            }
            let ownedIds=Set(owned.map{$0.windowID})
            if ownedIds != lastOwnedIds {
                let updated=SCContentFilter(display:display,including:owned)
                if #available(macOS 14.2,*) {updated.includeMenuBar=false}
                try await stream.updateContentFilter(updated);lastOwnedIds=ownedIds
            }
        }
        try await Task.sleep(for:.milliseconds(60))
    }''')
s+='\n'+(Path(__file__).with_name('CompareExtensions.swift')).read_text()
source=out/'CompareNative.generated.swift'
source.write_text(s)
subprocess.run(['xcrun','swiftc','-parse-as-library','-O',str(source),'-framework','AppKit','-framework','ScreenCaptureKit','-framework','AVFoundation','-o',str(exe)],check=True)
with (app/'Contents/Info.plist').open('wb') as f:plistlib.dump({'CFBundleIdentifier':'com.macexplorer.comparisonrecorder','CFBundleExecutable':'CompareNative','CFBundleName':'Comparison Recorder','CFBundlePackageType':'APPL','CFBundleVersion':'1','LSUIElement':True},f)
subprocess.run(['codesign','--force','--sign','-',str(app)],check=True)
print(exe)
