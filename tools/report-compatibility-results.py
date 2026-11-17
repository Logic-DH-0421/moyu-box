"""Summarize real public compatibility runs without shipping third-party binaries."""
from pathlib import Path
from collections import Counter
from urllib.parse import urlsplit, urlunsplit
import csv
import hashlib
import json
import shutil
import sys
import zipfile

root=Path(sys.argv[1]).resolve()
out=root/'report';out.mkdir(exist_ok=True)
def read(path):return json.loads(path.read_text(encoding='utf-8-sig'))
samples={s['id']:s for s in read(root/'samples.json')}
rows={}
for run,formats in [('.',{'EXE'}),('verified',{'ZIP'}),('verified-v3',None),('review',None),('hextris-recheck',None)]:
    path=root/run/'results.json'
    if not path.exists():continue
    for r in read(path):
        if formats and r['format'] not in formats:continue
        r['evidence_run']=run;rows[r['id']]=r
    manifest=root/run/'samples.json'
    if manifest.exists():
        for s in read(manifest):samples[s['id']]=s
ordered=sorted(rows.values(),key=lambda x:(x['format'],x['source'],x['id']))

def scrub(value):
    if isinstance(value,list):return [scrub(x) for x in value]
    if isinstance(value,dict):return {k:scrub(v) for k,v in value.items()}
    if not isinstance(value,str):return value
    if value.startswith(('https://','http://')):
        u=urlsplit(value);return urlunsplit((u.scheme,u.netloc,u.path,'',''))
    return value.replace(str(root),'[TEST_ROOT]').replace(str(root).replace('\\','/'),'[TEST_ROOT]')

clean_rows=scrub(ordered)
(out/'results.json').write_text(json.dumps(clean_rows,ensure_ascii=False,indent=2),encoding='utf-8')
(out/'sources.json').write_text(json.dumps(scrub(list(samples.values())),ensure_ascii=False,indent=2),encoding='utf-8')
with (out/'results.csv').open('w',encoding='utf-8-sig',newline='') as f:
    writer=csv.writer(f);writer.writerow(['ID','来源','格式','名称','结果','耗时毫秒','小窗横向溢出','章节数','错误','证据目录','来源地址'])
    for r in clean_rows:
        writer.writerow([r['id'],r['source'],r['format'],r['name'],r.get('result'),r.get('elapsed_ms'),r.get('compact',{}).get('overflow',''),r.get('reader',{}).get('chapters',''),r.get('error',r.get('host_error','')),r['evidence_run'],samples[r['id']].get('homepage',samples[r['id']].get('repo',samples[r['id']].get('url','')))])

# Only host JSONL files; exclude all per-project business logs, book content and profiles.
log_out=out/'host-logs';log_out.mkdir(exist_ok=True)
representative=[]
for run in ['verified','verified-v3','review','hextris-recheck']:
    for p in (root/run/'data/logs').glob('*.jsonl'):
        lines=[json.loads(line) for line in p.read_text(encoding='utf-8-sig').splitlines() if line.strip()]
        (log_out/(run+'-'+p.name)).write_text('\n'.join(json.dumps(scrub(x),ensure_ascii=False) for x in lines)+'\n',encoding='utf-8')
        for e in lines:
            if (e.get('code') in ('CertificateExpired','ConnectionAborted') or
                e.get('event')=='book.import.finished' and e.get('exception',{}).get('type')=='System.IO.InvalidDataException'):
                operation=e.get('operation_id');chain=[x for x in lines if operation and x.get('operation_id')==operation]
                representative.extend(chain if chain else [e])
unique={(e['session_id'],e['sequence']):e for e in representative}
(out/'representative-host-log.jsonl').write_text('\n'.join(json.dumps(scrub(x),ensure_ascii=False) for x in unique.values())+'\n',encoding='utf-8')

screens=out/'screenshots';screens.mkdir(exist_ok=True)
selected=[('verified','gh-zip-1-compact.png'),('verified','gh-zip-2-compact.png'),('hextris-recheck','gh-zip-2-fit.png'),('verified','web-zip-1-compact.png'),('verified-v3','web-txt-2-compact.png'),('verified-v3','gh-epub-1-compact.png'),('verified-v3','web-web-1-compact.png'),('verified-v3','gh-html-2-compact.png'),('review','web-epub-3-compact.png')]
for run,name in selected:
    p=root/run/'screenshots'/name
    if p.is_file():shutil.copy2(p,screens/(run+'-'+name))

status=Counter(r.get('result') for r in ordered)
counts=Counter(r['format'] for r in ordered)
host=root/'runtime/摸鱼盒子.exe';host_hash=hashlib.sha256(host.read_bytes()).hexdigest()
labels={'window_running':'独立窗口已运行','runtime_loaded':'已导入并渲染','runtime_error':'运行错误','failed':'导入/测试失败','unsupported':'明确不支持'}
table=[]
for r in ordered:
    s=samples[r['id']];source=s.get('homepage',s.get('repo',s.get('url','')))
    detail=''
    if r['id']=='gh-zip-2':detail='小窗溢出；适合宽度仍残留 8px'
    elif r['id']=='gh-web-1':detail='导航超时 / ConnectionAborted'
    elif r['id']=='gh-web-2':detail='CertificateExpired'
    elif r['id']=='web-epub-2':detail='下载文件缺失中央目录；独立 ZIP 校验失败'
    elif r['format']=='PDF':detail='TXT/EPUB 导入边界明确拒绝'
    elif r['format']=='TXT':detail='可读；仅识别为 1 章'
    elif r['format']=='MANIFEST':detail='本地编写 manifest 包装真实 HTML'
    elif r.get('reader'):detail=str(r['reader']['chapters'])+' 章；已测试下一章'
    elif r['format']=='EXE':detail='实际窗口已用 Computer Use 核查'
    elif r['format']=='ZIP':detail='直接 ZIP 不支持；解压后加载'
    table.append('| '+ ' | '.join([r['id'],r['source'],r['format'],f"[{r['name']}]({source})",labels.get(r.get('result'),str(r.get('result'))),detail])+' |')

report=f'''# 摸鱼盒子 0.6.0：GitHub / 公网样本实机兼容性报告

测试日期：2026-10-22（Asia/Riyadh，UTC+03:00）。测试系统为本机 Windows x64，实际 WinForms + Edge WebView2。未修改、重打包或发布产品程序，宿主 SHA-256：`{host_hash}`。

## 结论

完成 **{len(ordered)} 个独立测试条目**：28 个初始公开样本/网址，加 1 个补充 EPUB、4 个本地 manifest 包装。GitHub 和其他公开网站分别覆盖 EXE、ZIP、网页、TXT、EPUB、HTML、PDF 各 2 个初始样本；manifest 是为真实下载 HTML 编写的测试包装，不伪称网上已有的摸鱼盒子插件。

结果：**{status['window_running']} 个 EXE 出现实际独立窗口，{status['runtime_loaded']} 个条目导入后渲染，{status['runtime_error']} 个在线入口运行错误，{status['failed']} 个 EPUB 导入失败，{status['unsupported']} 个 PDF 被明确拒绝**。这些是阶段结果，不能等同于所有第三方业务功能、长期稳定性或所有按钮通过。

33 个条目不是全部通过。发现并保留了小屏适配、英文 TXT 分章、格式支持和在线访问等问题；没有把输入文件损坏或测试工具问题冒充产品缺陷。本轮仅测试与记录，以下产品问题尚未修复。

## 方法与证据强度

1. 从项目 GitHub Release、公开作者网站和标准示例库下载，记录地址、大小、SHA-256；EXE 来自便携 ZIP，解压后关联具体 EXE，并非运行安装器。GitHub 两个 EXE 是同一仓库发布的 Notepad4 和 FindInFiles 两个独立程序。
2. `tests/PublicCompatibilityRunner.cs` 在隔离数据目录加载未修改的 0.6.0 程序程序集，创建真实 MainWindow，调用原有 `AddBook`、`ImportPackage`、注册表和 SessionManager；页面打开通过实际壳的鱼塘项目卡片。没有模拟导入返回值、替换正文、伪造服务或使用纯逻辑结果冒充运行。
3. 对实际 WebView2 检查正文、章节、DOM、窗口大小、标签切换后的实例复用、关闭及本地浏览器服务 HTTP 响应。2048 键盘事件后方块数 2→6；Dimension intro 链接打开；EPUB 下一章实际变化。Hextris 进入教学状态（gameState 0→-1），没有宣称完整一局游戏验证。
4. EXE 用真实子进程与窗口状态验证，再用 computer-use 技能独立查看 Notepad4、FindInFiles、HashMyFiles、AlternateStreamView。未扫描用户文件或执行外部工具的删除/写入/联网业务操作。
5. 批量文件选择步骤由测试工具传入路径，绕过选择器；选择后的真实导入方法照常执行。额外实际打开“网页项目包”选择器，确认过滤为 manifest.json。后续窗口自动化两次报 `failed to activate captured window`，已停止 UI 输入；没有声称每份文件均经人工式选择器全流程验证。

## 逐项结果

| ID | 来源 | 格式 | 样本与来源 | 实际结果 | 补充 |
|---|---|---|---|---|---|
{chr(10).join(table)}

## 问题清单与复现

### C-01：ZIP 没有直接导入入口（格式支持缺口）

4 份 ZIP 均需要先解压，再选择 HTML 或补充 manifest。实际“网页项目包”选择器过滤为 manifest.json，源码也没有 ZIP 解包入口。2048、Hextris、HTML5UP Dimension、Massively 解压后能加载，证明不能把“ZIP 直接兼容”与“解压后的网页兼容”混为一谈。

复现：鱼塘→添加项目→网页项目包；观察文件类型和必须选择 manifest 的要求。普通 Github 源码 ZIP 也不保证存在可运行 HTML；本次选取的是已有静态资源的项目。建议优先补充带格式检查、入口识别、路径与容量校验的 ZIP 导入，但本轮没有实现。

### C-02：Hextris 小窗溢出，适合宽度仍未彻底解决（适配问题）

在 760×560 宿主窗口下实际项目视口为 681 CSS px。多次测得 scrollWidth 753–768 px；溢出元素包括 canvas（约 710 px）和 bottomContainer（右边界 689 px）。切换适合宽度后本次 ZoomFactor 仍为 100%，scrollWidth 689 / innerWidth 681，残留 8 px。

复现：导入 Hextris ZIP 解压入口→打开→还原为 760×560→检查横向边界→选择适合宽度。证据：`screenshots/verified-gh-zip-2-compact.png`、`screenshots/hextris-recheck-gh-zip-2-fit.png` 与结果中的 overflow_elements。说明动态画布/固定边距网页需要更强的适配策略；不能只依赖启动后最大化。没有将第三方布局本身完全归咎于宿主。

### C-03：英文 TXT 章节未识别（阅读器能力缺口）

4 个 TXT 都能显示正文并无整体横向溢出。两份 Alice 文本和 Tiny Shakespeare 都被读作 1 章；Alice 文件包含 CHAPTER 等英文标题。代码 `vendor/reader/Program.cs` 的 ChapterRegex 仅匹配中文“第…章”。《三字经》单章并不构成错误；英文 Alice 的章节结构未恢复才是本次发现。

复现：导入 GitHub corpus Alice 或 Gutenberg Alice TXT→查看目录和下一章状态。TXT 单章正文可能较长；Tiny Shakespeare 约 1.1 MB 仍被整体放入一章，本轮没有模拟更大书籍或断言长期性能。

### C-04：线上入口超时与证书失败（环境/外部访问问题）

`https://play2048.co/` 导航 30 秒超时，后续出现 ConnectionAborted；`https://hextris.io/` 返回 CertificateExpired。MDN 与 W3C 两个公网网页可读。相同 2048、Hextris 源码离线导入可以加载，不能将线上失败推断为离线引擎不支持。

证据位于 `verified-v3` 会话 `63b4a40318004e25a5e3466d66993c8d` 的本体日志。未绕过 HTTPS 证书检查、没有更改系统安全设置。仅凭客户端状态不能区分站点、网络中间设备、证书链或其他访问条件；不能断言远端根因。

### C-05：损坏 EPUB 被正确拒绝，但下载检查和用户提示仍需区分（输入异常）

Pride and Prejudice 的初始下载文件为 169,226 字节，虽以 PK 开头，但独立 Python ZipFile 与本体均发现缺少中央目录。盒子没有注册成功项目，抛出 InvalidDataException，日志保留了解析调用栈和失败终态。不是“阅读器不支持这本书”的充分证据。

补充重取相同资源时，响应宣称 Content-Length 24,836,548，实际只取得 643,926 字节，文件仍无效。只能确认取得的文件/传输不完整，不能确定是服务端还是网络链路。补充《三字经》EPUB（65,004 字节）CRC 校验通过，实际导入、切章和小窗阅读均成功，确保另外获得两份可用的公网 EPUB（Alice、三字经）。

本轮样本收集脚本最初仅检查 EPUB 的 PK 文件头，未识别截断。已为**测试收集脚本**补上 Content-Length 和 EPUB ZIP/CRC 检查；历史损坏样本及原始结果保留，不覆盖成“成功”。产品没有修改。用户侧若只看到“找不到中央目录结尾记录”仍不够直观，可改为明确说明文件可能损坏并建议重新下载。

### C-06：HTML 文件不是完整项目包（资源依赖边界）

MDN 单页能显示；HTML5 Boilerplate 的 index.html 引用了 css/style.css、js/app.js、图标等，单独下载该入口不会得到引用资源。盒子可显示其 Hello world 正文，但不能据此认定模板的全部资源正确。CERN、W3C 文本页面可读。宿主复制的是入口所在文件夹，不会自动补抓互联网依赖，也不会构建源码。

4 个 manifest 包装均实际反序列化、复制并加载了原 HTML，但不会凭空补齐模板缺失的资源；这些包装仅验证框架配置机制。

### C-07：PDF 不属于当前电子书入口支持格式（预期边界）

GitHub PDF.js 两份 PDF 和公开网站 W3C / Orimi 两份 PDF 均在 Rules.Validate→AddBook 边界被拒绝，提示“仅支持 TXT / EPUB 文件”。未出现假成功项目。不代表底层 WebView2 永远无法显示 PDF，只表示当前本地文件注册入口没有支持。MD、MOBI、AZW3、7z、tar、目录链接等未列为本轮通过格式。

### EXE 边界（不是新增缺陷）

4 个 EXE 以独立 Windows 窗口运行，标签页管理入口；不能嵌入网页标签页或交给浏览器执行。此次只验证便携程序启动和窗口可用，未安装软件、未触发管理员安装、未测试需要专有运行库或单实例转交的复杂程序。

## 日志如何定位问题

`representative-host-log.jsonl` 为脱敏代表性记录，`host-logs/` 仅含框架本体 JSONL，**不含任何项目内部业务日志、书籍正文、账号、浏览器资料或 EXE 文件**。

例：损坏 EPUB 会话 `63b4a40318004e25a5e3466d66993c8d`，时间 22:50:39 +03:00，operation_id `6e6c14dea22442339bee57ed7d48e372`：`book.import.started`→`book.import.finished`（failed，42.6674 ms）。异常类型 System.IO.InvalidDataException，调用栈从 ZipArchive.ReadEndOfCentralDirectory→EpubBookParser.Parse→DataStore.ImportBook→MainWindow.AddBook，可定位到导入解析阶段；不应继续寻找不存在的阅读界面“打开成功”。配合独立 ZIP 校验才能判断输入损坏。

网站失败先按用户报告时间找到 session_id，然后查 project.open / navigation 的 correlation_id、错误码和终态；ConnectionAborted 不能单独证明远端服务器故障。框架日志的 source_hash 是项目 ID 指纹，结果 JSON 的 project_id 可用于本地对应。

## 过程中的测试工具问题（不计为产品缺陷）

- 初次批量程序直接打开 SessionManager，没有改变壳路由，导致 100px 测试视口；初次非 EXE 布局测量全部弃用。EXE 仍有独立窗口证据。
- 改用真实项目卡片后的运行，在网页超时后测试脚本探针未返回；增加探针超时和错误状态跳过，剩余案例用新隔离目录继续。保留 `verified/runner-interrupted.txt`，没有把测试程序停顿当作产品崩溃。
- 初次 Hextris `.click()` 不触发其 mousedown 监听器，不能称为游戏启动失败；复测使用实际页面监听的事件后进入教学状态。页面事件探针仍不等同于完整手工键鼠游玩。
- 首轮 WinMerge 大下载耗时过长，改用同一可信 GitHub 发布中的小型 FindInFiles EXE；不把未取得的 WinMerge 计入测试通过。

## 未覆盖与交付范围

没有对用户日常目录导入测试项目，所有数据位于独立 artifacts/compatibility-20261022。项目包和原发布程序未修改；只新增测试工具、样本与记录。没有实际逐项启动系统默认浏览器，本地外开地址只验证真实 HTTP 可读；文件选择器全流程、游戏完整一局、长时间运行、EXE 深层业务、登录/付费/管理员程序和全部 EPUB 富排版保真均未证明。PNG 是项目 WebView2 的真实渲染截图，EXE 的独立窗口另外经 Computer Use 核查。

原始各次 results.json、完整来源/哈希在测试根目录保留；报告包仅收录整理后的结果、来源索引、本体日志和代表性截图，**不重新分发第三方 EXE 或完整书籍**。没有创建新版安装包。
'''
(out/'README.md').write_text(report,encoding='utf-8')
summary={'unique_cases':len(ordered),'status':dict(status),'formats':dict(counts),'host_sha256':host_hash,'evidence_runs':sorted({r['evidence_run'] for r in ordered})}
(out/'summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(summary,ensure_ascii=False))
