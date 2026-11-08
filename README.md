# Pebble（卵石）

Android 优先的 Git 文件浏览器。技术栈：.NET MAUI + LibGit2Sharp，支持亮/暗双主题。

## 构建与运行

前置：.NET SDK 10、`maui-android` / `maui-windows` workload、Android SDK、JDK 17。

源码统一放在 `src/` 下；`Pebble.csproj` 已把本机 SDK 路径写进 `AndroidSdkDirectory` / `JavaSdkDirectory`，换机器删掉那两行、改用 `ANDROID_HOME` / `JAVA_HOME` 环境变量即可。

```bash
cd src                                          # 工程目录
dotnet build                                    # 全部目标
dotnet run   -f net10.0-windows10.0.19041.0     # 桌面调试
dotnet build -f net10.0-android                 # 出 APK
# 产物：src/bin/Debug/net10.0-android/com.pebble.app-Signed.apk
```

## 当前状态

| 项 | 状态 |
|---|---|
| Windows 构建 / 运行 | 通过，0 警告 0 错误 |
| Android 构建（arm64-v8a） | 通过，0 警告 0 错误 |
| Android 运行期 Git | 原生库已自编并验证架构；**未在真机跑过**（无设备） |

### Android 原生库：已用 NDK 自编

`LibGit2Sharp.NativeBinaries 2.0.324` 没有 android RID。.NET Android 的 RID 兜底会把 `linux-arm64` 的 **glibc** 产物复制成 `lib/arm64-v8a/libgit2-5853918.so`（`e_machine` 也是 AArch64，光看架构分辨不出来），真机 `dlopen` 必失败。所以：

- csproj 里用 `PebbleDropForeignGit2` 这个 Target 在 `ResolvePackageAssets` 之后把 NuGet 的 `libgit2-5853918` 从 `NativeCopyLocalItems` / `RuntimeCopyLocalItems` 剔除，否则它会先占位（XA4301）
- 真正的库来自 `src/Platforms/Android/libs/arm64-v8a/libgit2-5853918.so`（NDK r30 交叉编译）

当前产物：libgit2 **1.9.1** + mbedTLS 2.28.9 静态链接，`e_machine=0xB7`，只依赖 `libc.so` / `libdl.so`，无 `GLIBC_` 符号。

复现编译（cmake 用 Android SDK 自带的那份，NDK 用 r30）：

```bash
cmake -S libgit2-1.9.1 -B out-arm64 -G Ninja \
  -DCMAKE_TOOLCHAIN_FILE=$NDK/build/cmake/android.toolchain.cmake \
  -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-24 \
  -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON \
  -DBUILD_TESTS=OFF -DBUILD_CLI=OFF -DBUILD_EXAMPLES=OFF \
  -DUSE_HTTPS=mbedTLS \
  -DMBEDTLS_INCLUDE_DIR=<mbedtls>/include \
  -DMBEDTLS_LIBRARY=<build>/libmbedtls.a \
  -DMBEDX509_LIBRARY=<build>/libmbedx509.a \
  -DMBEDCRYPTO_LIBRARY=<build>/libmbedcrypto.a \
  -DUSE_BUNDLED_ZLIB=ON
cmake --build out-arm64 --parallel 8
llvm-strip --strip-unneeded out-arm64/libgit2.so   # 7.8MB → 2.0MB
```

mbedTLS 3.6 的 tarball 缺 framework 子模块编不了，用 2.28.9。

### HTTPS 与 CA

Android 没有 `/etc/ssl/certs`，mbedTLS 不会自动找到根证书。`TlsBootstrap` 首次启动把 `src/Resources/Raw/cacert.pem`（Mozilla 118 个根证书）复制到私有目录，再通过 `NativeGit` P/Invoke 调 `git_libgit2_opts(GIT_OPT_SET_SSL_CERT_LOCATIONS=12, file, NULL)` 交给 libgit2。拿不到 CA 不阻塞启动，只影响远程拉取。

## 交互

| 动作 | 行为 |
|---|---|
| 短按目录 | 进入下一级 |
| 短按文件 | 操作菜单：用编辑器打开 / 查看属性 |
| 长按任意项 | 进入多选，顶部换成上下文栏（已选 N 项 · 全选 · 复制 · 移动 · 删除 · 重命名 · 压缩） |
| 多选态短按 | 切换该项选中，全取消则自动退出多选 |
| 复制 / 移动 | 写入应用内剪贴板，底部出现「粘贴到此处」，此时 FAB 自动上移让位 |
| 右下 Git FAB | 底部滑出抽屉：全部 M/U/D 文件 + 复选框 + Commit Message + Commit & Push；角标显示变更数 |
| 右下 + FAB | 新建文件，支持 `docs/a.md` 直接建子目录 |

实现要点：

- **长按**：MAUI 没有内置 LongPress，用 `PointerGestureRecognizer` 按下起 420ms 计时。松手会补发一次 Tap，用 `_suppressNextTap` 吞掉，否则会误触发菜单。
- **勾选即 stage**：抽屉里勾上就 `StageFile`、取消就 `UnstageFile`；Commit 走 `CommitPaths`，内部先 `Unstage("*")` 再按勾选重新 stage，保证未勾选的不会被顺带提交。
- **压缩**：`System.IO.Compression`，目录按相对路径展开进 zip，不引第三方库。
- **同名冲突**：复制/移动/压缩遇到同名一律追加 ` (n)`，不覆盖。

## API

`GitService`：

- `GetFilesWithStatus(repoPath)` → `IReadOnlyList<FileItem>`
- `GetChildrenAsync(repoRoot, relativePath)` → 分层读取，供文件浏览器下钻
- `GetWorkingChanges(repoRoot)` → 整个工作区的 M/U/D，供 Git 抽屉
- `StageFile / UnstageFile` → 支持 `(repoRoot, relativePath)` 与绝对路径两种重载
- `CommitPaths(repoRoot, message, paths, token?)` → 只提交勾选的文件
- `DeleteFile(path)` / `FileOperations.*` → 删除、复制、移动、重命名、压缩
- `CloneAsync(url, localPath, token?)`
- `CommitAndPush(repoPath, message, token?)` / `CommitAndPushAsync(...)`
- `ProbeNativeLibrary()` → 原生库自检，返回 `libgit2 <ver> (sha ...)` 或异常原因

`FileItem`：`Name` / `FullPath` / `RelativePath` / `IsDirectory` / `GitStatus`，计算属性 `StatusLetter`（M/U/D）、`HasChanges`。

## 双主题

色值直接写在各属性的 `AppThemeBinding` 里。

**不要用 `OnAppTheme` 资源再 `StaticResource` 引用**：`Border.Stroke`、`Path.Stroke` 是 `Brush` 类型，`OnAppTheme<Color>` 混用会在 XAML 解析期直接崩溃，且 WinUI 不打任何日志（进程静默退出，只能靠对照实验定位）。

## 演示仓库

首次启动在 `FileSystem.AppDataDirectory` 下创建：

- `test-repo/` — 含 `/src` `/docs` `/notes`，预置 M / U / D 三种未提交状态
- `test-repo-origin.git` — 本地 bare 仓库，作为 `origin`，因此 Commit & Push 离线也能完整走通

## 目录

```
Pebble/
├─ src/                               MAUI 工程根（Pebble.csproj 所在）
│  ├─ App.xaml(.cs) / MauiProgram.cs  应用入口与初始化
│  ├─ Pages/                         各界面
│  │   ├─ MainPage.xaml(.cs)          文件列表 + 多选栏 + FAB + Git 抽屉
│  │   ├─ EditorPage.xaml(.cs)        编辑器（缩放 / Markdown 预览 / 源码）
│  │   ├─ ArchivePreviewPage.xaml(.cs) 压缩包多层预览
│  │   ├─ ImagePreviewPage / SvgPreviewPage(.xaml.cs)  图片 / SVG 查看
│  │   ├─ MediaPage(.xaml.cs)         音频 / 视频播放
│  │   ├─ FontPreviewPage(.xaml.cs)   字体预览
│  │   ├─ DiffPage(.xaml.cs)          变更 Diff
│  │   └─ SettingsPage(.xaml.cs)      设置
│  ├─ Services/                      业务服务
│  │   ├─ GitService.cs               Clone / 状态 / Stage / Commit+Push / 原生库自检
│  │   ├─ NativeGit.cs                git_libgit2_opts 的 P/Invoke
│  │   ├─ FileOperations.cs           复制 / 移动 / 删除 / 重命名 / 压缩
│  │   ├─ ArchiveService.cs           压缩包不解压列举（SharpCompress）
│  │   ├─ LocalServerService.cs       本机文件共享服务器
│  │   ├─ MarkdownPreview.cs / CodePreview.cs / SvgPreview.cs  各类预览渲染
│  │   ├─ FileKinds.cs / BookmarkStore.cs / AppSettings.cs / AppLog.cs / StatusBar.cs
│  │   ├─ StorageAccess.cs            Android 存储权限适配
│  │   ├─ TlsBootstrap.cs             Android CA bundle 注入
│  │   └─ TestRepoFactory.cs          演示仓库生成
│  ├─ Models/FileItem.cs              文件项 + GitFileStatus + 选中态
│  ├─ Converters/GitStatusColorConverter.cs
│  ├─ Resources/                      AppIcon / Splash / Images(TDesign) / Fonts / Raw(cacert.pem)
│  └─ Platforms/Android|Windows/
└─ README.md / .gitignore / .gitattributes
```

路径约定：仓库内相对路径一律 `/`，落盘时才转平台分隔符，`RepoPath.Combine` 拒绝 `../` 越界。
