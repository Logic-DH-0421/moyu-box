# 摸鱼盒子本体日志与简短排查指南

适用版本：0.5.0。范围仅限摸鱼盒子框架；不接管各项目的日志，不导出小说、聊天、账号资料、B站任务、浏览器配置或用户文件。

## 日志在哪里

默认 `%LOCALAPPDATA%\MoyuBox\logs`；使用 `--data-dir` 后为该数据目录下的 `logs`。主目录不能写时尝试 `%TEMP%\MoyuBox-Diagnostics\<数据目录指纹>`。设置 → 本体诊断日志显示实际目录，提供“打开日志目录”和“导出诊断包”。降级提示还会出现在底部状态栏。

主、备用日志共享默认 64 MiB 清理预算，单文件默认 4 MiB，保留 14 天。启动及轮转时清理本体 `moyu-*.jsonl`；当前文件不会在写入时删除。清理失败会提示，硬盘/权限拒绝清理时不能保证容量上限。旧版日期 `.log` 文件保留、不再追加，也不包含在诊断包中。清理已完成检查点：14 天或最多 128 份；中断检查点在确认恢复记录已写入后删除。

## 格式和级别

UTF-8 JSONL，一行一个事件。每条包含 `timestamp`（带 UTC 偏移）、`level`、`module`、`event`、`session_id`、`process_id`、`sequence`、`message`。软件版本、EXE SHA-256 前 16 位构建标识、CLR、OS 在 `session.started` 中。

关键操作包含 `operation_id`、`correlation_id`、`parent_operation_id`，跨 Task.Run 沿用同一关联链。独立用户请求使用不同根关联标识；对象用指纹表示，不记录名字、完整路径或 URL。启动器进程 ID 与宿主 ID 分开记录。意外退出的观察记录关联到原项目打开操作。

| 级别 | 本项目规则 |
| --- | --- |
| INFO | 生命周期、收到/执行/终态、配置结果、重要状态变化 |
| WARN | 可恢复故障、首次失败后的重试、备份恢复、日志降级、释放未验证、中断会话 |
| ERROR | 打开/保存/导入等失败、超时、未处理异常、服务进程意外退出 |
| DEBUG | 缩放/视口等诊断细节；平时关闭 |

在设置切换 INFO/DEBUG，配置写入 `<数据目录>\diagnostics.json`。也可退出后修改：

```json
{"level":"DEBUG","maxFileBytes":4194304,"maxTotalBytes":67108864,"retentionDays":14}
```

文件上限可配 64 KiB–8 MiB，总容量最多 128 MiB、且至少为单文件的两倍，保留 1–30 天。队列最多 2048 条，另设约 8 MiB 文本预算。配置损坏记录 WARN，使用默认值，不删除原配置。

`phase=received/started/terminal` 分别表示收到请求、开始执行、到达终态。`result=success/failed/cancelled/timeout/interrupted`；`elapsed_ms` 使用 Stopwatch 单调时钟。`verified=true` 只用于已执行校验的步骤，例如设置回读一致、备份回读一致、书籍文件长度校验及解析返回、ZIP 回读；页面加载成功、启动进程、收到就绪文件不等于项目业务功能已验证。

循环和重复事件合并成 `.summary`，记录 `repeats/first_time/last_time`，约每 30 秒或刷新时输出。实际操作失败和终态不做静默过滤。异常首次出现记录类型、HRESULT、Win32/网络代码、调用栈和内部异常；后续传播沿用 `error_id`，避免重复大栈。原始异常 message 可能包含令牌、正文和绝对路径，因此使用固定说明代替；定位依靠类型、代码、方法栈和关联上下文。优化构建通常没有源码行号，需要结合构建指纹定位。

## 写入、降级和突然退出的边界

普通日志与检查点由单一后台线程写入，检查点批量合并。队列满时，非关键明细计数后输出 `queue.saturated`；WARN/ERROR、生命周期和操作始末改走备用文件，不直接丢弃。紧急备用写入会有少量同步 I/O，慢盘情况下仍可能延迟调用。两个磁盘出口都失败时保留最近最多 256 条、约 4 MiB 文本于内存，超出后保留最新记录并计数，界面提示降级，导出清单注明丢失数量。

操作从检查点移除前，终态必须到达某个磁盘出口。正常结束会刷新队列和检查点；已捕获启动失败也会尽力刷新，以错误事件说明原因。纯粹的强杀/断电来不及执行刷新，最后一批异步记录和检查点可能丢失。检查点通常在队列排空时合并；未落盘的操作标识可能无法恢复，只能从上次会话和最后记录辅助判断。系统磁盘缓存未做逐条物理落盘承诺，不能保证断电零丢失。

下次启动检查 PID 和进程启动时间，避免 PID 复用误判。`session.previous_incomplete` 与 `operation.previous_interrupted` 指向前次会话/操作；这些记录证明没有确认的完整结束证据，**不能单独证明是崩溃、断电、被结束还是系统重启**。全局未观察 Task 异常若运行时未提供原操作上下文，可能只有会话和异常标识。

## 用户如何导出

1. 记下出问题的本地时间、操作、界面现象；需要详细排查时先开 DEBUG 再复现一次。
2. 打开设置 → 本体诊断日志 → 导出诊断包，选择 ZIP 位置。
3. 包内只有重新按白名单脱敏的 `host.jsonl` 和 `manifest.json`。不会把原目录整体打包。
4. 清单中的 `complete/skipped/memory_records_lost` 表明本次快照是否有截取、不可读或内存丢失。快照最多读取每个出口最新 32 个文件，总读取预算约 32 MiB；导出动作自身终态发生在快照之后，保留在本地后续日志中。

启动失败无法进入设置时，错误框显示诊断目录，可先保留本体 JSONL。不要直接压缩整个数据目录，里面可能包含项目和个人数据。

## 从报告定位故障

1. 按报告时间的时区偏移查找附近事件，从 `session.started` 确认版本、构建、PID 和会话。
2. 找 `action.<操作>.received/started`，取其 `correlation_id`，按同一标识串起配置、导入、WebView、进程及终态。
3. 找 `result=failed/timeout` 或 ERROR，通过 `error_id` 找首次异常；查看类型、错误码、方法栈、内部异常。WARN 后面有 `settings.recovered` 和成功加载时，应判为恢复成功。
4. 若缺终态，检查下一会话的 `previous_*`、本次日志降级及丢失计数。不能从“最后一行”直接推断崩溃根因。
5. `navigation.response_failed.status=503` 是当时服务响应证据；连接拒绝/超时只说明传输失败，不能仅据此断言远端服务、代理、DNS 或防火墙哪个是根因。项目内部显示异常但本体边界都成功，需要另查该项目自身日志。

可在 PowerShell 对导出的 JSONL 过滤：

```powershell
$records = Get-Content -LiteralPath '.\host.jsonl' | ForEach-Object { $_ | ConvertFrom-Json }
$records | Where-Object correlation_id -eq '<关联标识>' | Sort-Object sequence | Format-List
$records | Where-Object error_id -eq '<异常标识>' | Format-List
```

多会话合并时先按 `session_id` 分组，再按 `sequence` 排序；系统时钟调整时，耗时以 `elapsed_ms` 为准。
