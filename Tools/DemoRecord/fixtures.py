"""Deterministic, synthetic demo data. Writes only inside a fresh run-isolated profile."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import sqlite3
import subprocess
import zipfile

FIXTURE_VERSION = 'mountain-studio-v1'
FIXED_TIME = 1792807200


def safe_root(root):
    root = Path(root)
    if not str(root).startswith('/private/tmp/fkfinder-test.') or root != root.resolve() or root.is_symlink():
        raise ValueError('A fresh run-isolated.sh temporary profile is required')
    return root


def prepare(root, repo, token, native=None):
    root = safe_root(root)
    root.mkdir(parents=True, exist_ok=False, mode=0o700)
    (root / '.demo-owner').write_text(token)
    for name in ['Desktop', 'Documents', 'Downloads', 'Pictures', 'Music', 'Movies']:
        (root / name).mkdir()
    project = root / 'Documents/山野计划'
    for name in ['摄影作品/精选', '设计稿', '交付文件', '原始资料', '自动化', '批量重命名', '收件箱']:
        (project / name).mkdir(parents=True)
    photo = repo / 'docs/Assets/alpine-lake.jpg'
    for name in ['摄影作品/高山湖泊.jpg', '摄影作品/精选/湖畔晨光.jpg', '设计稿/品牌封面.jpg']:
        shutil.copyfile(photo, project / name)
    for index in range(1, 7):
        shutil.copyfile(photo, project / f'摄影作品/IMG_{index:04}.jpg')
        shutil.copyfile(photo, project / f'批量重命名/IMG_{index:04}.jpg')
    (project / '产品方案.md').write_text('# 山野计划\n\n把灵感带到更远的地方。\n\n## 本周交付\n\n- 整理湖泊照片\n- 准备设计简报\n- 输出项目归档\n', encoding='utf-8')
    (project / '会议记录.txt').write_text('山野计划：确认封面，准备设计简报，整理湖泊摄影。\n', encoding='utf-8')
    (project / '交付文件/交付说明.txt').write_text('山野计划\n项目方案、摄影作品与设计稿。\n', encoding='utf-8')
    (project / '设计稿/品牌标志.svg').write_text('<svg xmlns="http://www.w3.org/2000/svg" width="1200" height="800"><rect width="1200" height="800" fill="#eef4fa"/><path d="M100 650L430 180L660 500L820 280L1100 650Z" fill="#3276c5"/><text x="100" y="110" font-family="Helvetica" font-size="58" fill="#18354f">MOUNTAIN STUDIO</text></svg>')
    script = project / '自动化/整理交付.sh'
    script.write_text('#!/bin/sh\nset -eu\nmkdir -p "$(dirname "$0")/../交付文件/资料归档"\nprintf "山野计划：资料已整理完成\\n"\n', encoding='utf-8')
    # Minimal valid PDF with searchable text; no external service or user document.
    stream = b'BT /F1 30 Tf 72 720 Td (Mountain Studio) Tj 0 -60 Td /F1 17 Tf (Lake photography project proposal) Tj 0 -40 Td (Design, photographs and delivery archive.) Tj ET'
    objects = [b'<< /Type /Catalog /Pages 2 0 R >>', b'<< /Type /Pages /Kids [3 0 R] /Count 1 >>', b'<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>', b'<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>', b'<< /Length '+str(len(stream)).encode()+b' >>\nstream\n'+stream+b'\nendstream']
    pdf = bytearray(b'%PDF-1.4\n'); offsets = [0]
    for index, obj in enumerate(objects, 1):
        offsets.append(len(pdf)); pdf.extend(f'{index} 0 obj\n'.encode()+obj+b'\nendobj\n')
    xref = len(pdf);pdf.extend(b'xref\n0 6\n0000000000 65535 f \n')
    for offset in offsets[1:]: pdf.extend(f'{offset:010} 00000 n \n'.encode())
    pdf.extend(f'trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n'.encode())
    (project / '原始资料/项目提案.pdf').write_bytes(pdf)
    with zipfile.ZipFile(project / '原始资料/设计简报.docx', 'w') as z:
        def member(name, text):
            z.writestr(zipfile.ZipInfo(name, (2026, 10, 24, 10, 0, 0)), text)
        member('[Content_Types].xml', '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>')
        member('_rels/.rels', '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>')
        member('word/document.xml', '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>山野计划 · 设计简报</w:t></w:r></w:p><w:p><w:r><w:t>自然、简洁、清晰。交付封面照片与项目方案。</w:t></w:r></w:p></w:body></w:document>')
    with zipfile.ZipFile(project / '项目归档.zip', 'w', zipfile.ZIP_DEFLATED) as z:
        for path in [project / '产品方案.md', project / '会议记录.txt', photo]:
            info = zipfile.ZipInfo(path.name, (2026, 10, 24, 10, 0, 0));info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, path.read_bytes())
    shutil.copyfile(photo, root / 'Downloads/高山湖泊.jpg')
    if native is not None:
        config = root / 'fixture-image.json'
        config.write_text(json.dumps({'root':str(root),'token':token}))
        subprocess.run([str(native),'fixture-image',str(config)],check=True,stdout=subprocess.DEVNULL)
    settings = {'app_language':'zh-CN','theme_mode':'light','sidebar_show_username':'false',
                'localsend_enabled':'false','file_delivery_enabled':'true','shortcut_hints_enabled':'true',
                'copilot.enabled':'false','ai_analysis_enabled':'false','window.primary.normal_width':'1200','window.primary.normal_height':'680',
                'window.primary.position_x':'120','window.primary.position_y':'70'}
    settings['home_script_commands_v1'] = json.dumps({str(script):[{'Id':'prepare-delivery','Name':'整理交付','Command':'/bin/sh "$SCRIPT"','Shell':'/bin/zsh','WorkingDirectory':str(script.parent),'MenuIcon':1}]}, ensure_ascii=False)
    db = root / '.macexplorer/index.db';db.parent.mkdir()
    with sqlite3.connect(db) as connection:
        connection.execute('CREATE TABLE app_settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)')
        connection.executemany('INSERT INTO app_settings VALUES (?, ?)', settings.items())
    records = []
    for path in sorted(project.rglob('*')):
        os.utime(path, (FIXED_TIME, FIXED_TIME))
        if path.is_file():
            records.append({'path':str(path.relative_to(root)), 'sha256':hashlib.sha256(path.read_bytes()).hexdigest()})
    (root / 'fixture-manifest.json').write_text(json.dumps({'version':FIXTURE_VERSION,'files':records}, ensure_ascii=False, indent=2))
    return project
