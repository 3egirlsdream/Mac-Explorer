#!/usr/bin/env python3
"""Local-only, state-driven scene recorder. See README.md and scenes/local.json."""
import argparse
import csv
import hashlib
import json
import os
from pathlib import Path
import plistlib
import re
import secrets
import shutil
import subprocess
import sys
import time
import traceback

from fixtures import FIXTURE_VERSION, prepare, safe_root

REPO = Path(__file__).resolve().parents[2]
TOOLS = REPO / 'outputs/demo-tools'
NATIVE = TOOLS / 'Demo Recorder.app/Contents/MacOS/DemoNative'


def write_json(path, value):
    path = Path(path);path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_name(path.name+'.tmp')
    temp.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')
    temp.replace(path)


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def command(args, **kwargs):
    return subprocess.run([str(a) for a in args], check=True, **kwargs)


def until(predicate, timeout=25, description='condition', process=None):
    deadline = time.monotonic()+timeout
    while time.monotonic() < deadline:
        if process is not None and process.poll() is not None:
            raise RuntimeError(f'Process exited ({process.returncode}) while waiting for {description}')
        value = predicate()
        if value: return value
        time.sleep(.08)
    raise TimeoutError(f'Timed out waiting for {description}')


def validate_spec(spec):
    if spec.get('schemaVersion') != 1 or not spec.get('scenes'):
        raise ValueError('A nonempty schemaVersion=1 scene list is required')
    ids=set()
    allowed={'app','wait','hold','click','move','key','text','command-down','command-up','file','receiver','drag'}
    for scene in spec['scenes']:
        scene_id=scene['id']
        if not re.fullmatch(r'[a-z0-9]+(?:-[a-z0-9]+)*',scene_id) or scene_id in ids:
            raise ValueError(f'Invalid or duplicate scene ID: {scene_id}')
        ids.add(scene_id)
        if not scene.get('title') or not scene.get('description') or not scene.get('record'):
            raise ValueError(f'Missing documented scene content: {scene_id}')
        if scene.get('captureProfile','app') not in {'app','desktop'}:raise ValueError('Unknown capture profile')
        for action in scene.get('prepare',[])+scene['record']:
            if action.get('action') not in allowed:raise ValueError(f'Unknown action in {scene_id}: {action}')


def build(skip):
    TOOLS.mkdir(parents=True, exist_ok=True)
    sources = [REPO/'Tools/DemoRecord/native/DemoNative.swift']
    if not skip:
        with (TOOLS/'build.log').open('w') as log:
            command(['dotnet','build','MacExplorer.csproj','-c','Release','--artifacts-path',TOOLS/'build',
                     '-p:EnableDemoAutomation=true','-p:SkipMacOSReleaseDMG=true'],cwd=REPO,stdout=log,stderr=subprocess.STDOUT)
        NATIVE.parent.mkdir(parents=True, exist_ok=True)
        native_inputs={'sourceSha256':sha(sources[0]),'compiler':subprocess.check_output(['xcrun','swiftc','--version'],text=True,stderr=subprocess.STDOUT)}
        stamp=TOOLS/'native-inputs.json'
        if not NATIVE.exists() or not stamp.exists() or json.loads(stamp.read_text())!=native_inputs:
            with (TOOLS/'native-build.log').open('w') as log:
                command(['xcrun','swiftc','-parse-as-library','-O',*sources,'-framework','AppKit','-framework','ScreenCaptureKit','-framework','AVFoundation','-o',NATIVE],stdout=log,stderr=subprocess.STDOUT)
            write_json(stamp,native_inputs)
        info = {'CFBundleIdentifier':'com.macexplorer.demorecorder','CFBundleExecutable':'DemoNative','CFBundleName':'Demo Recorder','CFBundlePackageType':'APPL','CFBundleVersion':'1','LSUIElement':True, 'NSScreenCaptureUsageDescription':'录制隔离演示实例的功能素材。'}
        with (NATIVE.parent.parent/'Info.plist').open('wb') as f: plistlib.dump(info,f)
        command(['codesign','--force','--sign','-',str(NATIVE.parent.parent.parent)],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
    apps = list((TOOLS/'build/bin/MacExplorer').glob('release*/Mac Explorer.app/Contents/MacOS/MacExplorer'))
    if len(apps) != 1 or not NATIVE.exists():
        raise RuntimeError('Dedicated demo build not found; rerun without --no-build')
    app = apps[0].resolve()
    with (app.parent.parent/'Info.plist').open('rb') as f:
        if plistlib.load(f)['CFBundleIdentifier'] != 'com.macexplorer.demo': raise RuntimeError('Refusing a production app bundle')
    return app


class Session:
    def __init__(self, root, project, token, app, out):
        self.root,self.project,self.token,self.app,self.out = root,project,token,app,out
        self.number=0;self.receiver=None;self.recorder=None;self.command_held=False;self.log=(out/'app.log').open('w')
        env={**os.environ,'MACEXPLORER_TEST_ROOT':str(root),'MACEXPLORER_DEMO_TOKEN':token}
        self.process=subprocess.Popen([str(app)],env=env,stdout=self.log,stderr=subprocess.STDOUT)
        self.mail=root/'.demo'
        self.identity={'root':str(root),'token':token,'pid':self.process.pid,'executable':str(app)}
        try:
            ready=until(lambda:json.loads((self.mail/'ready.json').read_text()) if (self.mail/'ready.json').exists() else None,40,'app handshake',self.process)
            if ready['pid']!=self.process.pid or ready['root']!=str(root) or ready['token']!=token or Path(ready['database'])!=root/'.macexplorer/index.db':
                raise RuntimeError('Isolation handshake mismatch')
            self.call(op='setup')
            self.native(action='activate')
            self.wait({'loading':False,'mainActive':True})
        except BaseException:
            self.close()
            raise

    def call(self, **data):
        self.number+=1;stem=f'{self.number:06}'
        request=self.mail/(stem+'.request.json');response=self.mail/(stem+'.response.json')
        write_json(request,{'token':self.token,**data})
        result=until(lambda:json.loads(response.read_text()) if response.exists() else None,35,f'app operation {data.get("op")}',self.process)
        response.unlink()
        if not result['ok']: raise RuntimeError(result['error'])
        return result.get('data')

    def native(self, **data):
        path=self.root/'native-input.json';write_json(path,{**self.identity,**data})
        try:result=command([NATIVE,'input',path],capture_output=True,text=True)
        except subprocess.CalledProcessError as ex:
            raise RuntimeError(f'Native input rejected: {ex.stdout.strip()} {ex.stderr.strip()}') from ex
        return json.loads(result.stdout)

    def wait(self, expected, timeout=25, window='main'):
        def check():
            state=self.call(op='state',window=window)
            for key,value in expected.items():
                if key=='window':
                    if not any(w['type']==value for w in state['windows']): return None
                elif key=='entry':
                    if value not in state['entries']: return None
                elif key=='menu':
                    if value not in state['menus']: return None
                elif key=='globalResult':
                    if value not in state['globalResults']: return None
                elif key=='editorContains':
                    if value not in state['editorText']: return None
                elif key=='tasksComplete':
                    if any(t['state'] in ['Failed','Cancelled'] for t in state['tasks']): raise RuntimeError(str(state['tasks']))
                    if not state['tasks'] or any(t['state']!='Completed' for t in state['tasks']): return None
                elif state.get(key)!=value: return None
            return state
        return until(check,timeout,f'UI state {expected}',self.process)

    def expand(self,value):
        if isinstance(value,str):return value.replace('${PROJECT}',str(self.project)).replace('${ROOT}',str(self.root))
        if isinstance(value,list):return [self.expand(v) for v in value]
        if isinstance(value,dict):return {k:self.expand(v) for k,v in value.items()}
        return value

    def action(self, raw):
        a=self.expand(raw);op=a.pop('action')
        if op=='app':return self.call(**a)
        if op=='wait':return self.wait(a['state'],a.get('timeout',25),a.get('window','main'))
        if op=='hold':time.sleep(a['seconds']);return
        if op in ['click','move']:
            target=self.call(op='target',**a['target'])
            return self.native(action=op,x=target['x'],y=target['y'],right=a.get('right',False),count=a.get('count',1))
        if op in ['key','text','command-down','command-up']:
            if op=='command-down':self.command_held=True
            result=self.native(action=op,**a)
            if op=='command-up':self.command_held=False
            return result
        if op=='file':
            path=Path(a['path'])
            if not path.resolve().is_relative_to(self.root.resolve()):raise ValueError('File assertion outside profile')
            def check():
                if a.get('exists') is False:return not path.exists()
                if not path.is_file():return None
                if 'contains' in a and a['contains'] not in path.read_text():return None
                if 'matches' in a and sha(path)!=sha(a['matches']):return None
                if a.get('format') in ['zip','docx']:
                    import zipfile
                    try:
                        with zipfile.ZipFile(path) as archive:
                            if archive.testzip() is not None:return None
                            if a['format']=='docx' and 'word/document.xml' not in archive.namelist():return None
                    except zipfile.BadZipFile:return None
                if a.get('format')=='png' and path.read_bytes()[:8]!=b'\x89PNG\r\n\x1a\n':return None
                return True
            return until(check,a.get('timeout',25),f'file assertion {path.name}',self.process)
        if op=='receiver':
            path=self.root/'receiver.json';ready=self.root/'receiver-ready.json';self.receiver_event=self.root/'receiver-event.json'
            write_json(path,{'root':str(self.root),'token':self.token,'readyPath':str(ready),'eventPath':str(self.receiver_event)})
            self.receiver=subprocess.Popen([str(NATIVE),'receiver',str(path)],stdout=self.log,stderr=subprocess.STDOUT)
            self.receiver_info=until(lambda:json.loads(ready.read_text()) if ready.exists() else None,15,'receiver',self.receiver)
            self.native(action='activate-receiver',receiverPid=self.receiver.pid)
            until(lambda:self.native(action='inspect')['frontmostPid']==self.receiver.pid,5,'receiver foreground',self.receiver)
            return
        if op=='drag':
            self.native(action='activate-receiver',receiverPid=self.receiver.pid)
            until(lambda:self.native(action='inspect')['frontmostPid']==self.receiver.pid,5,'receiver foreground',self.receiver)
            activations=self.call(op='state')['mainActivations']
            target=self.call(op='target',**a['source'])
            self.native(action='drag',x=target['x'],y=target['y'],receiverPid=self.receiver.pid,destination=self.receiver_info,modifiers=a.get('modifiers',''))
            until(lambda:self.receiver_event.exists(),15,'native receiver completion',self.receiver)
            source=self.root/'Downloads/高山湖泊.jpg';received=self.root/'Documents/收件箱/高山湖泊.jpg'
            if sha(source)!=sha(received):raise RuntimeError('Dragged file content differs')
            final=self.call(op='state');foreground=self.native(action='inspect')['frontmostPid']
            evidence={'beforeMainActivations':activations,'afterMainActivations':final['mainActivations'],
                      'foregroundPid':foreground,'expectedReceiverPid':self.receiver.pid,'contentSha256':sha(received),'modifiers':a.get('modifiers','')}
            write_json(self.out/'drag-evidence.json',evidence)
            if final['mainActivations']!=activations or foreground!=self.receiver.pid:
                raise RuntimeError(f'Main window activated by file-delivery drag: {evidence}')
            return
        raise ValueError(f'Unknown scene action: {op}')

    def capture(self, raw_path, profile):
        config=self.root/'capture.json'
        background=self.root/'demo-desktop.jpg'
        shutil.copyfile(REPO/'docs/Assets/alpine-lake.jpg',background)
        write_json(self.out/'framing.json',{'rect':[0,0,1440,810],'window':{'x':120,'y':70,'width':1200,'height':680},
                   'background':'docs/Assets/alpine-lake.jpg','backgroundSha256':sha(background),'profile':profile})
        write_json(config,{**self.identity,'path':str(raw_path),'rect':[0,0,1440,810],'desktopBackground':str(background),
                           'menuBar':profile=='desktop',**({'receiverPid':self.receiver.pid} if self.receiver else {})})
        self.capture_path=raw_path
        self.recorder=subprocess.Popen([str(NATIVE),'record',str(config)],stdout=self.log,stderr=subprocess.STDOUT)
        return until(lambda:json.loads(Path(str(raw_path)+'.frames.json').read_text()) if Path(str(raw_path)+'.frames.json').exists() else None,15,'capture first frame',self.recorder)

    def stop_capture(self):
        if not self.recorder:return None
        Path(str(self.capture_path)+'.stop').touch()
        self.recorder.wait(timeout=20)
        metadata=json.loads(Path(str(self.capture_path)+'.done.json').read_text())
        self.recorder=None
        if not metadata['ok']:raise RuntimeError(f'Capture failed: {metadata}')
        return metadata

    def close(self):
        try:
            if self.command_held:
                self.native(action='command-up');self.command_held=False
            if self.recorder:self.stop_capture()
        finally:
            for child in [self.recorder,self.receiver,self.process]:
                if child and child.poll() is None:
                    if child is self.process:
                        try:self.call(op='shutdown')
                        except (TimeoutError,RuntimeError):pass
                    try:child.wait(timeout=5)
                    except subprocess.TimeoutExpired:child.terminate();child.wait(timeout=5)
            self.log.close()


def quality(raw, clip, qa):
    command(['ffmpeg','-v','error','-i',raw,'-an','-vf','fps=30,scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2',
             '-c:v','libx264','-preset','fast','-crf','18','-pix_fmt','yuv420p','-movflags','+faststart','-y',clip])
    metadata=json.loads(subprocess.check_output(['ffprobe','-v','error','-show_streams','-show_format','-of','json',clip],text=True))
    duration=float(metadata['format']['duration'])
    if duration<2:raise RuntimeError('Clip too short')
    command(['ffmpeg','-v','error','-i',clip,'-f','null','-'],stdout=subprocess.DEVNULL,stderr=subprocess.PIPE)
    for name,t in [('head',.3),('middle',duration/2),('result',max(.3,duration-.4))]:
        command(['ffmpeg','-v','error','-ss',str(t),'-i',clip,'-frames:v','1','-vf','scale=960:-1','-y',qa/f'{name}.jpg'])
    return {'durationSeconds':duration,'width':1920,'height':1080,'fps':30,'fullDecode':'passed','frames':int(metadata['streams'][0]['nb_frames'])}


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out',default='outputs/demo-capture-local')
    parser.add_argument('--scenes',default=str(REPO/'Tools/DemoRecord/scenes/local.json'))
    parser.add_argument('--only',help='Comma-separated scene IDs')
    parser.add_argument('--no-build',action='store_true')
    parser.add_argument('--resume',action='store_true',help='Reuse matching passed clips in --out; reconstruct failed scenes in fresh profiles')
    parser.add_argument('--doctor',action='store_true')
    parser.add_argument('--list',action='store_true')
    parser.add_argument('--attempts',type=int,default=2)
    args=parser.parse_args()
    spec_path=Path(args.scenes).resolve();spec=json.loads(spec_path.read_text());validate_spec(spec)
    scenes=spec['scenes']
    if args.only:
        ids=set(args.only.split(','));scenes=[s for s in scenes if s['id'] in ids]
        if ids!={s['id'] for s in scenes}:raise ValueError('Unknown scene ID')
    if args.list:
        for s in scenes:print(s['id'],s['title'],'—',s['description'])
        return
    parent=safe_root(os.environ.get('MACEXPLORER_TEST_ROOT',''))
    app=build(args.no_build)
    doctor=json.loads(subprocess.check_output([str(NATIVE),'doctor'],text=True))
    print(json.dumps(doctor,ensure_ascii=False),flush=True)
    if args.doctor:return
    if not doctor['screenCapture'] or not doctor['postEvents']:raise RuntimeError('Grant Screen Recording and Accessibility to Demo Recorder, then rerun --doctor')
    if not doctor['displays'] or doctor['displays'][0]['width']<1440 or doctor['displays'][0]['height']<900:
        raise RuntimeError('This scene pack requires a primary display with at least 1440×900 logical points')
    if args.attempts<1 or args.attempts>3:raise ValueError('--attempts must be between 1 and 3')
    if not shutil.which('ffmpeg') or not shutil.which('ffprobe'):raise RuntimeError('ffmpeg and ffprobe are required')
    out=Path(args.out).resolve();out.mkdir(parents=True,exist_ok=True)
    manifest_path=out/'manifest.json'
    previous=json.loads(manifest_path.read_text()) if args.resume and manifest_path.exists() else None
    if manifest_path.exists() and not args.resume:raise ValueError('Output already has a manifest; use --resume or a new --out')
    fingerprint={'appSha256':sha(app.parent.parent/'Resources/Managed/MacExplorer.dll'),'scenesSha256':sha(spec_path),'fixtureVersion':FIXTURE_VERSION,'fixtureCodeSha256':sha(REPO/'Tools/DemoRecord/fixtures.py'),'nativeSha256':sha(NATIVE),'runnerSha256':sha(__file__),'desktopBackgroundSha256':sha(REPO/'docs/Assets/alpine-lake.jpg')}
    if previous and previous['inputs']!=fingerprint:raise ValueError('Resume inputs changed; use a new output directory')
    with (app.parent.parent/'Info.plist').open('rb') as stream:
        version=plistlib.load(stream)['CFBundleShortVersionString']
    manifest=previous or {'schemaVersion':1,'runId':time.strftime('%Y%m%d-%H%M%S'),'inputs':fingerprint,'environment':doctor,'app':str(app),
                         'build':{'version':version,'commit':subprocess.check_output(['git','rev-parse','HEAD'],cwd=REPO,text=True).strip()},
                         'limitations':spec.get('limitations',[]),'clips':{},'attempts':[]}
    manifest['requiredScenes']=sorted(set(manifest.get('requiredScenes',[]))|{s['id'] for s in scenes})
    write_json(manifest_path,manifest)
    events=(out/'events.jsonl').open('a');events.flush()
    for scene in scenes:
        scene_id=scene['id'];old=manifest['clips'].get(scene_id)
        if old and old['status']=='passed' and (out/old['file']).exists() and sha(out/old['file'])==old['sha256']:
            print(f'REUSE {scene_id}',flush=True);continue
        for attempt in range(1,args.attempts+1):
            take=1+sum(t['scene']==scene_id for t in manifest['attempts'])
            take_name=f'{scene_id}__light__zh-CN__t{take:02}'
            work=out/'takes'/take_name;work.mkdir(parents=True)
            root=parent/take_name/'山野工作室';token=secrets.token_hex(32);session=None
            entry={'scene':scene_id,'take':take,'status':'preparing','profileRoot':str(root)}
            manifest['attempts'].append(entry);write_json(manifest_path,manifest)
            print(f'RECORD {take_name}: {scene["title"]}',flush=True)
            try:
                project=prepare(root,REPO,token,NATIVE)
                shutil.copyfile(root/'fixture-manifest.json',work/'fixture-manifest.json')
                session=Session(root,project,token,app,work)
                for action in scene.get('prepare',[]):session.action(dict(action))
                session.wait({'loading':False})
                raw=work/'source.mov';first=session.capture(raw,scene.get('captureProfile','app'))
                entry['status']='recording';write_json(manifest_path,manifest)
                time.sleep(.8)
                markers=[]
                for index,action in enumerate(scene['record']):
                    event={'scene':scene_id,'take':take,'step':index,'hostNs':time.monotonic_ns(),'action':action}
                    events.write(json.dumps(event,ensure_ascii=False)+'\n');events.flush()
                    markers.append({'step':index,'hostSeconds':event['hostNs']/1e9,'action':action['action']})
                    session.action(dict(action))
                result=session.call(op='state');write_json(work/'final-state.json',result)
                session.call(op='commit')
                result_host=time.monotonic()
                progress_path=Path(str(raw)+'.frames.json')
                until(lambda:json.loads(progress_path.read_text()).get('samplePts',0)>=result_host,
                      5,'capture sample after result state',session.recorder)
                markers.append({'step':'result','hostSeconds':result_host,'action':'result-visible'})
                time.sleep(1.2)
                capture=session.stop_capture();write_json(work/'capture.json',capture)
                session.close();session=None
                clip=out/'clips'/f'{take_name}.mp4';clip.parent.mkdir(exist_ok=True)
                qa=out/'qa'/take_name;qa.mkdir(parents=True)
                media=quality(raw,clip,qa)
                for marker in markers:
                    marker['seconds']=max(0,marker['hostSeconds']-capture['firstPts'])
                    marker['frame']=round(marker['seconds']*30)
                    if marker['frame']>=media['frames']:raise RuntimeError('Action marker is outside the captured video')
                entry['status']='passed'
                manifest['clips'][scene_id]={'status':'passed','title':scene['title'],'description':scene['description'],'take':take,'file':str(clip.relative_to(out)),
                    'sha256':sha(clip),'media':media,'capture':capture,'markers':markers,'interactionMode':scene.get('interactionMode','mixed'),
                    'fixtureManifest':str(work.relative_to(out)/'fixture-manifest.json'),'fixtureManifestSha256':sha(work/'fixture-manifest.json'),
                    'framing':str(work.relative_to(out)/'framing.json'),
                    'qa':str(qa.relative_to(out)),'finalState':str(work.relative_to(out)/'final-state.json')}
                print(f'PASS {scene_id}: {media["durationSeconds"]:.2f}s',flush=True)
                break
            except BaseException as ex:
                entry.update(status='failed',error=str(ex))
                (work/'error.txt').write_text(traceback.format_exc())
                manifest['clips'][scene_id]={'status':'failed','error':str(ex),'take':take}
                print(f'FAIL {scene_id}: {ex}',flush=True)
                if session:
                    try:write_json(work/'failure-state.json',session.call(op='state'))
                    except Exception:pass
                    try:session.close()
                    except Exception as cleanup:(work/'cleanup-error.txt').write_text(str(cleanup))
                if isinstance(ex, (KeyboardInterrupt,SystemExit)):raise
                if 'permission' in str(ex).lower():break
            finally:write_json(manifest_path,manifest)
    events.close()
    with (out/'markers.csv').open('w') as f:
        writer=csv.writer(f);writer.writerow(['scene','file','step','seconds','frame','timecode','action'])
        for scene_id,clip in manifest['clips'].items():
            if clip['status']!='passed':continue
            for m in clip['markers']:
                frame=m['frame'];tc=f'{frame//108000:02}:{frame//1800%60:02}:{frame//30%60:02}:{frame%30:02}'
                writer.writerow([scene_id,clip['file'],m['step'],round(m['seconds'],3),frame,tc,m['action']])
    failed=[scene_id for scene_id in manifest['requiredScenes'] if manifest['clips'].get(scene_id,{}).get('status')!='passed']
    manifest['status']='failed' if failed else 'passed';manifest['failedScenes']=failed;write_json(manifest_path,manifest)
    (out/'README.md').write_text('# 本地功能演示素材\n\n直接导入 clips/*.mp4（1920×1080，30 fps，无声）。原速母片在 takes/*/source.mov。\n\n逐镜功能/结果见 manifest.json，操作时间码见 markers.csv，首/中/尾帧见 qa/。\n\n失败镜头：'+(', '.join(failed) or '无')+'\n\n仅使用自动生成的隔离示例资料。支持场景与扩展方法见 Tools/DemoRecord/README.md。\n')
    print(f'MATERIALS {out}',flush=True)
    if failed:raise SystemExit(1)


if __name__=='__main__':
    main()
