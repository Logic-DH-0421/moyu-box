# 项目描述规范 v1

本期可导入的项目包是一个包含 `manifest.json` 的本地网页文件夹。

```json
{
  "schemaVersion": 1,
  "id": "hello-project",
  "name": "我的网页项目",
  "description": "项目描述，最多240字。",
  "kind": "local-web",
  "entry": "index.html",
  "icon": "blocks"
}
```

- `id`：2–64 位，小写字母开头，后续为小写字母、数字、连字符；须唯一。
- `name`：1–60 字符。
- `entry`：包内相对路径，不允许绝对路径和 `../` 越界。
- 包内普通网页可使用 localStorage / IndexedDB，浏览存储由框架按项目ID隔离。
- 项目包导入后复制到独立 package 目录，不依赖原文件夹继续存在。
- 项目不自动获得文件系统、系统命令、其他项目数据等原生权限。
- 首期不执行 npm install、构建命令或任意启动脚本。前端项目应先在自己的开发环境构建为静态资源。
- `icon` 可选 `clock`、`video`、`book`、`compass`、`blocks`、`globe`、`app`。未知名称使用通用图标。

网站项目通过“添加网站”创建；独立桌面程序通过系统文件选择器关联。复杂本地服务由受控适配器实现，不伪装成可直接导入的静态网页包。

项目页面关闭再打开时重新加载；返回鱼塘或切换左侧功能时保留当前实例。需要跨软件重启保留的状态，由项目写入自己的 localStorage / IndexedDB 或后端数据目录。

## 0.2.0 项目类型

- `website`：`url` 为 HTTPS 或 loopback HTTP。
- `github`：`url` 为 https://github.com/owner/repo；可选 `launchUrl` 为已部署应用地址，缺省打开仓库。本版通过添加对话框注册，不运行任意源码。
- `local-web`：`entry` 为复制包内的网页入口；支持 manifest 导入和 HTML 文件夹导入。
- `book`：`entry` 为项目目录内 .txt / .epub；由电子书文件选择器复制、解析后注册，进入独立书库。
- `external`：`entry` 为本机绝对 EXE 路径，由系统文件选择器关联；程序独立运行。

只有 local-web 使用 manifest 文件夹导入。其他类型通过各自添加入口创建，避免伪造 manifest 获取任意文件或原生启动权限。内置 adapter 仍由宿主明确注册。

## 0.6.0 受控适配服务

内置 multiai 与 bilibili 由框架启动明确打包的服务；reader、word-reader、book 使用独立阅读服务。均通过随机回环端口、每会话授权和就绪文件管理生命周期。普通项目 manifest 不能声明这些受信任类型。关闭标签页会停止相应服务，切换标签页保留实例。TikTok 暂未启用。

## 0.7.0 ZIP 导入

“ZIP / 网页项目包”支持 ZIP 与 manifest.json。ZIP 展开上限 50MB/5000 条目，校验 CRC、路径与符号链接；优先 local-web manifest，否则寻找浅层 index.html，歧义时选择入口。不会执行 ZIP 中的 EXE、构建脚本或安装依赖。原有 manifest 与文件夹导入保持兼容。
