#!/usr/bin/env python3
"""Generate crawlable, dependency-free Pages from the shared template and locales."""
import json
from html import escape
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DOCS = ROOT / 'docs'
BASE = 'https://3egirlsdream.github.io/Mac-Explorer/'
REPO = 'https://github.com/3egirlsdream/Mac-Explorer'
ROUTES = {'zh-CN': '', 'zh-Hant': 'zh-Hant/', 'en': 'en/', 'fr': 'fr/'}
LOCALES = {lang: json.loads((DOCS / 'locales' / (lang + '.json')).read_text()) for lang in ROUTES}
assert all(set(t) == set(LOCALES['zh-CN']) for t in LOCALES.values()), 'Locale keys must match'

def icon(name):
    return f'<svg class="icon" aria-hidden="true"><use href="{{asset}}Assets/icons/fluent.svg#{name}"/></svg>'

def browser():
    sidebar = '<aside class="browser-sidebar"><small>常用位置</small>' + ''.join(
        f'<span>{icon(i)}{name}</span>' for i,name in [('home','个人文件'),('desktop','桌面'),('document','文稿'),('arrow-download','下载'),('image','图片'),('music','音乐')]
    ) + '<small>位置</small>' + ''.join(f'<span>{icon(i)}{name}</span>' for i,name in [('hard-drive','Macintosh HD'),('globe','网络'),('apps','应用程序'),('delete','废纸篓')]) + '<small class="ai-nav">AI 智能 '+icon('chevron-right')+'</small><span class="sidebar-settings">'+icon('settings')+'设置</span></aside>'
    files = [('产品方案.md','147 B','MD'),('会议记录.md','134 B','MD'),('发布清单.txt','50 B','TXT'),('素材索引.csv','81 B','CSV'),('设计说明.md','134 B','MD'),('项目归档.zip','136 B','ZIP'),('项目进度.md','134 B','MD'),('高山湖泊.jpg','500.75 KB','JPG'),('交付文件','—','文件夹'),('品牌素材','—','文件夹'),('摄影作品','—','文件夹')]
    rows = ''.join(f'<div class="browser-row {"file-selected" if i == 0 else ""}"><span><img src="{{asset}}Assets/showcase/latest-{"folder" if i>7 else "lake" if i==7 else "archive" if i==5 else "document"}.webp" alt="">{name}</span><span>2026/10/21 10:00</span><span>{size}</span><span>{kind}</span></div>' for i,(name,size,kind) in enumerate(files))
    def menu_row(label, glyph='', suffix='', highlight=False):
        return f'<div class="menu-row {"highlight" if highlight else ""}">{icon(glyph) if glyph else "<span class=icon></span>"}<span>{label}</span><em>{suffix}</em></div>'
    quick = '<div class="menu-quick">'+''.join(f'<span>{icon(i)}<small>{n}</small></span>' for i,n in [('text-edit','编辑'),('copy','拷贝'),('cut','剪切'),('delete','删除')])+'</div>'
    menu = '<div class="browser-context-menu">'+quick+'<hr>'+menu_row('打开','document')+menu_row('打开方式','apps','›')+'<hr>'+menu_row('重命名','text-edit','↩')+menu_row('移动到','folder','›')+menu_row('发送','share','›')+'<hr>'+menu_row('文件转换','arrow-clockwise','›',True)+menu_row('压缩','archive')+'<hr>'+menu_row('复制路径','copy','⌥⌘C')+'<hr>'+menu_row('在 Finder 中显示','folder')+menu_row('在终端中打开','code')+'<hr>'+menu_row('标签','tag','›')+'<hr>'+menu_row('查看文件信息','info','⌘I')+'</div>'
    submenu = '<div class="browser-submenu">'+menu_row('转为 Word（.docx）','document')+menu_row('转为 PDF','document')+'</div>'
    return '<div class="replica browser-replica" aria-hidden="true"><div class="browser-titlebar"><span class="traffic"><i></i><i></i><i></i></span><span class="browser-active-tab">'+icon('folder')+'设计项目'+icon('dismiss')+'</span><span class="browser-add">'+icon('add')+'</span><span class="browser-window-tools">'+icon('panel-left')+icon('arrow-clockwise')+'</span></div>'+sidebar+'<div class="browser-main"><div class="browser-navigation"><span class="browser-nav-icons">'+''.join(icon(i) for i in ['arrow-left','arrow-right','arrow-up','arrow-clockwise'])+'</span><div class="browser-address">Macintosh HD '+icon('chevron-right')+'个人文件 '+icon('chevron-right')+'<b>设计项目</b></div><div class="browser-search">'+icon('search')+'搜索此位置的文件</div></div><div class="browser-toolbar"><span>＋ 新建</span><i></i>'+''.join(icon(i) for i in ['cut','copy','copy','delete','home'])+'<i></i><span class="browser-toolbar-end"><span class="browser-view-switch">'+icon('grid')+'<span>'+icon('list')+'</span>'+icon('tree')+'</span><i></i><span>↕ 排序</span>'+icon('info')+icon('more')+'</span></div><div class="browser-columns"><span>名称 ↑'+icon('chevron-down')+'</span><span>修改日期 ↕'+icon('chevron-down')+'</span><span>大小 ↕'+icon('chevron-down')+'</span><span>类型 ↕'+icon('chevron-down')+'</span></div><div class="browser-files">'+rows+'</div><div class="browser-status"><span>已选 1 项 · 147 B</span><span>可用 153.66 GB</span></div></div>'+menu+submenu+'</div>'

REPLICAS = {'browser':browser()}

def stage(scene, label):
    return f'<div class="replica-stage" data-base-width="1280" role="img" aria-label="{escape(label)}">{REPLICAS[scene]}</div>'

def feature_sections(t):
    return ''.join(f'<section class="capability-section" aria-labelledby="capability-title-{j}"><div class="capability-heading"><p class="section-label">{escape(group["label"])}</p><h2 id="capability-title-{j}">{escape(group["title"])}</h2><p>{escape(group["intro"])}</p></div><div class="capability-grid">'+''.join(f'<article class="capability"><span class="capability-icon">{icon(item[0])}</span><h3>{escape(item[1])}</h3><p>{escape(item[2])}</p></article>' for item in group['items'])+'</div></section>' for j,group in enumerate(t['capabilities']))

def render(lang, t):
    route = ROUTES[lang]
    asset = '../' if route else ''
    url = BASE + route
    og_locale = {'zh-CN':'zh_CN','zh-Hant':'zh_TW','en':'en_US','fr':'fr_FR'}[lang]
    text = lambda key: escape(t[key])
    links = ''.join(f'<link rel="alternate" hreflang="{l}" href="{BASE+r}">' for l,r in ROUTES.items()) + f'<link rel="alternate" hreflang="x-default" href="{BASE}">'
    language_links = ''.join(f'<a href="{asset+r or "./"}" lang="{l}" hreflang="{l}" {"aria-current=page" if l == lang else ""}>{LOCALES[l]["name"]}</a>' for l,r in ROUTES.items())
    schema = {'@context':'https://schema.org','@graph':[{'@type':'WebSite','@id':BASE+'#website','url':BASE,'name':'Mac Explorer','alternateName':['Mac 文件资源管理器','Mac文件资源管理器','Mac file manager'],'inLanguage':list(ROUTES)},{'@type':'SoftwareApplication','@id':BASE+'#app','name':'Mac Explorer','alternateName':'Mac 文件资源管理器','url':url,'description':t['description'],'applicationCategory':'UtilitiesApplication','operatingSystem':'macOS 14.0 or later','downloadUrl':REPO+'/releases/latest','license':REPO+'/blob/main/LICENSE','image':BASE+'Assets/social-preview.png','offers':{'@type':'Offer','price':'0','priceCurrency':'USD','url':REPO+'/releases/latest'},'featureList':[item[1] for group in t['capabilities'] for item in group['items']],'inLanguage':list(ROUTES)},{'@type':'WebPage','@id':url+'#webpage','url':url,'name':t['title'],'description':t['description'],'inLanguage':lang,'isPartOf':{'@id':BASE+'#website'},'about':{'@id':BASE+'#app'}}]}
    html = f'''<!doctype html>
<!-- Generated by Tools/Website/build.py. Edit Tools/Website/build.py or docs/locales/*.json. -->
<html lang="{lang}" data-theme="light"><head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>{text('title')}</title><meta name="description" content="{text('description')}">
<meta name="robots" content="index,follow,max-image-preview:large"><meta name="theme-color" content="#f9fafc"><meta name="color-scheme" content="light">
<link rel="canonical" href="{url}">{links}
<meta property="og:type" content="website"><meta property="og:site_name" content="Mac Explorer"><meta property="og:title" content="{text('title')}"><meta property="og:description" content="{text('description')}"><meta property="og:url" content="{url}"><meta property="og:locale" content="{og_locale}">
<meta property="og:image" content="{BASE}Assets/social-preview.png"><meta property="og:image:width" content="1200"><meta property="og:image:height" content="630"><meta property="og:image:alt" content="{text('browserAlt')}"><meta name="twitter:card" content="summary_large_image"><meta name="twitter:title" content="{text('title')}"><meta name="twitter:description" content="{text('description')}"><meta name="twitter:image" content="{BASE}Assets/social-preview.png">
<link rel="icon" href="{asset}Assets/appicon.svg" type="image/svg+xml"><link rel="preload" href="{asset}Assets/fonts/montserrat-latin.woff2" as="font" type="font/woff2" crossorigin><link rel="stylesheet" href="{asset}Assets/fonts/montserrat.css"><link rel="stylesheet" href="{asset}landing.css"><script src="{asset}script.js" defer></script>
<script type="application/ld+json">{json.dumps(schema,ensure_ascii=False).replace('</','<\\/')}</script>
</head><body><a class="skip-link" href="#main">{text('skip')}</a>
<header class="site-header wrap"><a class="brand" href="{asset or './'}" aria-label="Mac Explorer"><img src="{asset}Assets/appicon.svg" alt="" width="32" height="32">Mac Explorer<span class="brand-label">FOR MAC</span></a>
<nav aria-label="{text('menu')}"><a href="#features">{text('features')}</a><a href="{asset}developers/">{text('docs')}</a><a href="{REPO}">GitHub {icon('arrow-up-right')}</a></nav><div class="header-actions"><details class="language-picker"><summary aria-label="{text('language')}: {text('name')}">{icon('globe')}<span>{text('name')}</span>{icon('chevron-down')}</summary><div>{language_links}</div></details></div></header>
<main id="main"><section class="hero wrap" aria-labelledby="hero-title"><div class="hero-copy"><p class="eyebrow">{text('eyebrow')}</p><h1 id="hero-title">Mac Explorer</h1><p class="hero-tagline">{text('tagline')}</p><p class="hero-intro">{text('intro')}</p><div class="hero-actions"><div class="download-control"><a href="{REPO}/releases/latest">{icon('arrow-download')}{text('get')}</a><details class="download-picker"><summary aria-label="{text('downloadOptions')}">{icon('chevron-down')}</summary><div><a href="{REPO}/releases/latest">{text('silicon')}{icon('arrow-up-right')}</a><a href="{REPO}/releases/latest">{text('intel')}{icon('arrow-up-right')}</a></div></details></div></div></div>
<div class="showcase"><div class="showcase-window"><div data-panel="browser">{stage('browser',t['browserAlt'])}</div></div></div></section>
<div class="features wrap" id="features">{feature_sections(t)}</div>
<section class="faq wrap" aria-labelledby="faq-title"><h2 id="faq-title">{text('faqTitle')}</h2><div>{''.join(f'<details><summary>{escape(q)}{icon("add")}</summary><p>{escape(a)}</p></details>' for q,a in t['faqs'])}</div></section>
<section class="community-section" id="community" aria-labelledby="community-title"><div class="wrap community-content"><h2 id="community-title">{text('endTitle')}</h2><p>{text('endText')}</p><div class="community-links"><a class="button primary" href="{REPO}">{icon('code')}{text('communitySource')}</a><a class="button secondary" href="{asset}developers/sdk.html">{icon('document')}{text('pluginSdk')}</a><a class="button secondary" href="{asset}developers/">{icon('apps')}{text('publishPlugin')}</a><a class="button secondary" href="{REPO}/issues">{icon('info')}{text('feedback')}</a></div></div></section></main>
<footer class="site-footer wrap"><div><a class="brand" href="{asset or './'}"><img src="{asset}Assets/appicon.svg" alt="" width="25" height="25">Mac Explorer</a><p>{text('footerLine')}</p></div><div class="footer-links"><a href="{asset}privacy/">{text('privacy')}</a><a href="{REPO}/issues">{text('feedback')}</a><a href="{REPO}">{text('source')}</a><span>{text('license')}</span></div></footer></body></html>'''
    return html.replace('{asset}',asset)

def main():
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="Check that committed pages match their sources")
    args = parser.parse_args()
    generated = {}
    for lang, route in ROUTES.items():
        target = DOCS / route / 'index.html'
        generated[target] = render(lang,LOCALES[lang])
    urls = [BASE+r for r in ROUTES.values()] + [BASE+'privacy/',BASE+'developers/',BASE+'developers/sdk.html']
    generated[DOCS/'sitemap.xml'] = ('<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n'+''.join(f'  <url><loc>{u}</loc></url>\n' for u in urls)+'</urlset>\n')
    generated[DOCS/'.nojekyll'] = ''
    if args.check:
        stale = [str(path.relative_to(ROOT)) for path, content in generated.items() if not path.exists() or path.read_text() != content]
        if stale:
            parser.exit(1, 'Regenerate with python3 Tools/Website/build.py: ' + ', '.join(stale) + '\n')
        print('Generated pages are up to date.')
        return
    for path, content in generated.items():
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content)
    print('Generated four language pages and sitemap.')

if __name__ == '__main__':
    main()
