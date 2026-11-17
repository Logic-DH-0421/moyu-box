# Word 小说阅读器：来源、接入和默认打开方式

本版：摸鱼盒子 0.3.0；日期：2026-10-22。

## 查找结果

找到并采用的项目是 [NiaoBlush/novel-disguise](https://github.com/NiaoBlush/novel-disguise)。它的定位是将网页小说展示成 Word 或 Excel 界面，源码明确声明 MIT 许可。集成版本 2.15.0，固定提交 `db1501ec07270a22a741bf78f8b8ba4ebca8cc7d`，许可文本见 [上游 LICENSE](https://github.com/NiaoBlush/novel-disguise/blob/db1501ec07270a22a741bf78f8b8ba4ebca8cc7d/LICENSE)。使用此开源实现，不需要反编译闭源产品。

另外检索到 [ReadCover 作者在 GitHub 的介绍](https://github.com/ruanyf/weekly/issues/7769)，但介绍帖子不等于公开源码仓库；本次没有找到并核实它的开源源码，未将 ReadCover 的页面代码当作开源代码打包。用户未给出唯一仓库地址，因此这里明确列出实际采用的项目，便于核对。

## 如何使用

- 鱼塘 → Word 小说阅读器 → 打开 TXT / EPUB，可在此维护独立书库。
- 鱼塘 → 添加项目 → TXT / EPUB：复制并解析本地文件，生成独立书籍入口；默认用 Word 界面打开，包括原有 kind=book 的项目。
- 书库与目录：选择本地书籍，点击章节，搜索当前书籍并跳转。
- 上一章 / 下一章，或 `[` / `]` 切换；滚动阅读自动保存段落位置，关闭后重开恢复。
- 字号保存在书库后端，服务端口变化后仍可恢复。
- 安全文档：Ctrl + 反引号或工具栏按钮，在当前阅读页切换到工作文档，再次切回。仅为页面内功能，不是系统全局快捷键，也不会隐藏盒子外层标签和 Windows 窗口。
- 设置 → TXT / EPUB 默认阅读方式：Word 小说阅读器 / 仿 AI 阅读器。修改后新打开的书籍生效，已打开标签保持当前界面，关闭重开应用新选择。
- 默认浏览器方式继续可用，打开同一个本机阅读服务的 Word 界面。退出盒子或停止该项目后本机服务结束。

上方 Office 功能区来自上游界面图像，属于外观；实际阅读操作在下方工具栏。本版不提供真正的 Word 文档编辑，也不将未实现按钮标成阅读功能。

## 软件边界与旧数据

原“仿AI小说阅读器”入口保留原界面和原书库路径。Word 独立入口使用 `projects/word-reader/library`，不会自动合并或搬动 AI 书库。已有单本书籍项目仍沿用自己的 `projects/<book-id>/library`，只改变默认展示界面，正文和进度由同一个后端管理。0.1/0.2 设置缺少新字段时默认选择 Word，追加一个内置入口，保留已有资料、收藏、项目和文件路径。

没有修改 Windows 的 TXT/EPUB 文件关联；本次默认设置作用于摸鱼盒子内部。也没有改写用户原来的独立阅读器项目目录。

EPUB 沿用既有文字解析器，本版主要用于小说正文，不承诺还原所有 EPUB 排版、交互、图片或 DRM 内容。

## 实现结构

- `vendor/novel-disguise`：上游源码、资源、许可和固定提交/指纹记录。
- `tools/prepare-word-reader.py`：检查原始文件指纹，生成仅增加本地入口的 engine.js，复制完整本地运行资源。
- `src/WordReader`：本地协议适配、目录搜索、进度队列、文件导入和安全切换。
- `SessionManager`：word-reader 使用 Word 页；book 按框架 defaultBookReader 选择；reader 仍是原 AI 页。
- 原阅读器本地服务：按明确资源名单提供 /word/*.js、CSS，原有 Host/Origin/令牌检查保留；普通项目不获得原生桥接权限。

页面只访问当前本机服务，未调用小说站点、上传书籍或连接 CDN。正文通过 textContent 写入；异步章节和搜索各有请求序号，过时结果不覆盖当前视图。安全切换会使正在等待的章节结果失效，避免迟到正文重新出现。进度提交使用队列，按章节/书籍绑定保存；书籍切换与显示目标章节前等待保存回执。

## 构建与许可

`build.ps1 -SkipAdapter` 会检查上游源码指纹，生成并打包 Word 适配模块。开发时需要 Python/Node/.NET 编译器；发布包运行不需要 Python 或 Node。MIT 与 jQuery 许可、上游版权头均随包保留。具体见 THIRD-PARTY-NOTICES.md。
