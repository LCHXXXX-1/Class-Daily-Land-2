# Class Daily Land 2 更新器（源码）

这是主程序 `Update\Updater.exe` 的源码。**源码不进应用主仓库的打包内容**——应用只收
`updater-dist\` 里的构建产物；本目录的源码另有云端备份：Gitee 仓库的 `updater-src` 分支。

## 目录结构

```
更新器\
├── CDL2.Updater.csproj      工程文件（AssemblyName 必须是 Updater：主程序只认 Update\Updater.exe）
├── App.xaml(.cs)            入口：全局异常挂接 + OnExit 硬退出兜底（防进程僵尸）
├── UpdaterWindow.xaml(.cs)  界面：无边界卡片、阶段勾选、进度条、恢复操作
├── UpdaterEngine.cs         主干：下载→校验→关程序→解压(staging)→备份→替换→校验→收尾
├── ReplaceJournal.cs        备份账本（journal.txt）：回滚能力的心脏，支持离线重放
├── Downloader.cs            下载（http(s) + file://；逐次读超时 + 停滞检测 + 换源重试）
├── JobLoader.cs             命令行 → UpdateJob（--job 或 --url/--hash/--target）
├── CommandLine.cs           参数解析（--job/--url/--rollback 等，未知参数忽略）
├── Cleanup.cs               退出后借 cmd 延迟删除临时文件（只在 %Temp% 运行时才删自己）
├── Theme.cs                 深浅色配色（跟系统，全内联，不依赖主程序资源）
├── Contract\                更新契约（与主程序 Shared\UpdateContract 同源，改动要两边同步）
└── bin\                     构建输出（不入备份）
```

## 构建

```
dotnet build CDL2.Updater.csproj -c Release -r win-x64
```

## 打包进应用（每次更新器改完都要做）

1. 把 `bin\Release\net10.0-windows10.0.19041.0\win-x64\` 全部内容（不含 pdb/xml）
   复制到主程序仓库根的 `updater-dist\`。
2. 构建主程序，`CopyUpdater` 目标自动把它复制到输出的 `Update\` 下。

只拷 `Updater.exe` 一个文件不行——框架依赖单文件夹部署，旁边必须有
`Updater.dll`、`Updater.deps.json`、`Updater.runtimeconfig.json` 和依赖 dll。

## 已知的关键坑（都踩过）

- **封送回调是延迟执行的**：`Ui(u => ...)` 里的 lambda 不能捕获 ZipArchive 等会被释放的对象，
  任何要进界面的值都在后台线程先算好，否则 UI 线程上炸 ODE。
- **引擎回调在 Task.Run 线程**：必须经 `SynchronizationContext` 封送，漏一处就是线程冲突。
- **主程序锁存**：更新器退出后主程序要解除"更新进行中"锁存，否则之后再也不会被调用
  （主程序侧靠 WatchUpdaterProcess 进程监视解除）。
- **App.OnExit 硬退出**：防止任何挂起的 await 把进程变成看不见的僵尸。

## 沙箱测试（改完必跑）

用假安装目录 + 本地 zip（file:// 通道）验证成功 / 坏哈希 / `--rollback` 三条路径，
全程不碰真实程序。job.json 模板见主对话记录。
