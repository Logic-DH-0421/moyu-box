# 第三方组件与本地项目快照

## 框架运行组件

- Microsoft.Web.WebView2 SDK：Core / WinForms / x64 Loader；Microsoft 授权条款见 `licenses/WebView2.txt`。Evergreen Runtime 由系统提供。
- Python 3.14：B站及同步输入器适配 EXE 内包含运行时，授权文本见 `licenses/Python.txt`。
- websocket-client 1.9.0：Apache License 2.0，文本见 `licenses/websocket-client.txt`。
- PyInstaller：构建工具，分发的 bootloader 带有允许打包应用分发的例外；对应授权文件见 `licenses/PyInstaller.txt`。

## 用户现有项目快照

- 时节：`本仓库 vendor 中的对应项目快照`，0.11.0 网页资源。
- 仿AI小说阅读器：`本仓库 vendor 中的对应项目快照`，0.3.14 C# 后端与网页资源。
- B站稍后再看管理器：`本仓库 vendor 中的对应项目快照`，0.7.23 源码与网页资源。

上述文件复制到本仓库 vendor，原项目没有被修改。B站复制版配置适配支持 `MOYU_PROJECT_DATA` 指定独立数据目录；宿主不创建 pywebview 窗口，由受控服务代理原有明确接口。阅读器原入口未调用，改由宿主启动同一阅读后端；复制版增加请求 Host / Origin 检查，编译时移除重复的 assembly 元数据。

时节内含 SheetJS/xlsx 资源，其原有版权和授权头随文件保留。B站网页原有图像资源随来源快照保留。第三方资源保留来源声明和原版权头；不因本项目公开而改变其原有授权。

DeepSeek Harness 仅作为交互与视觉风格参考，没有打包其源码或官方 Logo。

## Word 小说阅读器（0.3.0 新增）

- 项目：[NiaoBlush/novel-disguise](https://github.com/NiaoBlush/novel-disguise)，2.15.0。
- 固定源码提交：`db1501ec07270a22a741bf78f8b8ba4ebca8cc7d`。
- MIT License，Copyright (c) 2024 NiaoBlush；完整许可见 `licenses/novel-disguise-MIT.txt` 和 `plugins/word-reader/LICENSE-novel-disguise.txt`。
- 上游源码和资源保存在 `vendor/novel-disguise`，SHA-256 记录在 UPSTREAM.json；构建前检查指纹。
- 复用原项目 `common()`、`setWordContent()`、Word 图像资源和相关样式。构建脚本只注入本地适配分支，提前返回，不启用网络站点处理、原 E 键重载、油猴菜单或脚本更新；本地书库和 TXT/EPUB 由既有解析后端提供。
- 上游自带 jQuery 2.0.3，Copyright 2013 jQuery Foundation and other contributors，MIT；完整许可见 `licenses/jquery-2.0.3-MIT.txt`，文件原版权头保留。
- Word 外观资源来自上游资源文件；本体不包含 Microsoft Office 可执行组件，不提供 DOCX 编辑功能。

## 0.4.0 本地适配修改

在本仓库的阅读器与 B站快照中增加了小屏 CSS/目录抽屉，B站快照增加受宿主缩放控制的 DPI 处理。修改均位于本仓库 vendor 副本，未写入原项目目录。Word 本地适配增加响应式功能区与抽屉定位，上游固定提交源码指纹不变。

## 0.6.0 适配依赖

- 多平台 AI 同步输入器：用户现有项目 0.14.1 的明确源码快照；原项目及账号数据未复制、修改。
- Playwright 1.61.0：Apache-2.0，见 `licenses/Playwright.txt`。随包仅含运行驱动，不包含预装浏览器或用户资料。
- pyee 13.0.1：MIT，见 `licenses/pyee.txt`。
- greenlet 3.5.4：MIT / PSF 条款，见 `licenses/greenlet.txt`、`licenses/greenlet-PSF.txt`。
- 阅读器改为独立服务进程，各项目运行日志留在项目数据目录，不汇入本体诊断 ZIP。
- TikTok 适配已暂缓，experimental/tiktok 草稿不参与构建或发布。
