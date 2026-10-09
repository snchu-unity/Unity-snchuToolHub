using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace ExOpenSource.Editor
{
    /// <summary>唯一安装入口。后台准备文件，主线程批量提交；窗口只读取任务状态。</summary>
    [InitializeOnLoad]
    public static class PluginInstallService
    {
        private static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        private static readonly string CacheRoot = Path.Combine(ProjectRoot, ExOpenSourceConstParam.CACHE_ROOT);
        private static readonly string StatePath = Path.Combine(CacheRoot, "OperationState.json");
        private static readonly string MenuPath = MenuJsonUtility.ProjectMenuPath(ProjectRoot);
        private static readonly ConcurrentQueue<ProgressMessage> Messages = new ConcurrentQueue<ProgressMessage>();
        private static PluginInstallDatabase _database = new PluginInstallDatabase();
        private static PluginOperationState _state = new PluginOperationState();
        private static Task<PreparedBatch> _task;
        private static Task _cacheCleanupTask;
        private static bool _cleanupRetainsState;
        private static CancellationTokenSource _cancellation;
        private static AddAndRemoveRequest _upmRequest;
        private static string _initializationError;
        private static bool _waitingForPackages;
        private static double _packageWaitStarted;
        private static double _lastStateSave;
        private static List<PluginPackageExpectation> _displayPackages;
        private static double _nextPackageDisplayRefresh;

        public static PluginOperationState State => _state;
        public static bool IsCleaningCache => _cacheCleanupTask != null;
        public static bool IsBusy => _task != null || _upmRequest != null || _waitingForPackages || IsCleaningCache;
        public static bool CanCancel => _task != null && _state.Phase == PluginOperationPhase.Preparing;
        public static bool CanRetryPackages => !IsBusy && _state.FilesCommitted && _state.Phase == PluginOperationPhase.Failed
            && RequiresPackageResolution;

        private static bool RequiresPackageResolution => _state.ExpectedPackages.Count > 0
            || _state.UpmAdd.Length > 0 || _state.UpmRemove.Length > 0;

        static PluginInstallService()
        {
            try
            {
                _database = MenuJsonUtility.ReadInstallationData(ProjectRoot);
                ValidateDatabase(_database);
                if (File.Exists(StatePath)) _state = PluginInstallFiles.ReadJson<PluginOperationState>(StatePath);
                ValidateState(_state);
                if (NormalizeLegacyEmbeddedRequests()) SaveState();
                if (_state.Phase == PluginOperationPhase.Committing || _state.RecoveryRequired)
                    EditorApplication.delayCall += RecoverInterruptedCommit;
                else
                {
                    if (_state.Phase == PluginOperationPhase.Preparing)
                    {
                        _state.Phase = PluginOperationPhase.Interrupted;
                        _state.Message = "准备阶段被编辑器关闭或脚本重载中断，原插件目录未改动；可以重新执行。";
                        SaveState();
                    }
                    else if (_state.Phase == PluginOperationPhase.Resolving)
                        WaitForPackages();
                    EditorApplication.delayCall += ResumeCacheCleanup;
                }
            }
            catch (Exception error)
            {
                _initializationError = error.Message;
                _database = new PluginInstallDatabase(); // 仅供错误界面展示；EnsureAvailable 会阻止所有操作。
                _state = new PluginOperationState { Phase = PluginOperationPhase.Failed, Message = "配置 / 临时任务数据无效: " + error.Message };
                Debug.LogError(_state.Message);
            }
            EditorApplication.update += Update;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;
            EditorApplication.quitting += BeforeReload;
            EditorApplication.projectChanged += InvalidatePackageDisplay;
        }

        public static PluginInstallationRecord FindRecord(string localPath)
        {
            string path = localPath.Trim().Replace('\\', '/').TrimEnd('/');
            return _database.installedPlugins.Find(record => string.Equals(record.installPath, path, StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsInstalled(ExPluginItem plugin) => GetInstallationInfo(plugin).IsInstalled;
        public static string InstallationStatus(ExPluginItem plugin) => GetInstallationInfo(plugin).Label;

        /// <summary>界面本地路径以 Unity 项目根目录为基准；文件操作继续使用原路径。</summary>
        public static string DisplayPath(string path) => PluginInstallFiles.ProjectRelativePath(ProjectRoot, path);

        public static PluginInstallationInfo GetInstallationInfo(ExPluginItem plugin)
        {
            var record = FindRecord(plugin.LocalPath);
            string name = PackageNameHint(plugin, record);
            string target = Path.Combine(ProjectRoot, plugin.LocalPath);
            if (_displayPackages == null || EditorApplication.timeSinceStartup >= _nextPackageDisplayRefresh)
            {
                _displayPackages = CaptureEditorSnapshot().Packages;
                _nextPackageDisplayRefresh = EditorApplication.timeSinceStartup + 0.5;
            }
            var package = _displayPackages.FirstOrDefault(item => item.Name == name || PluginInstallFiles.SamePath(item.LocalPath, target));
            var installation = InspectInstallation(ProjectRoot, plugin, _database, package);
            if (_initializationError != null) installation.OperationError = _initializationError;
            return installation;
        }

        private static void InvalidatePackageDisplay() => _displayPackages = null;

        /// <summary>保存或刷新目录时更新已注册包快照；不下载插件、不扫描项目文件。</summary>
        public static void RefreshInstallationInfo()
        {
            _database = MenuJsonUtility.ReadInstallationData(ProjectRoot);
            ValidateDatabase(_database);
            _displayPackages = CaptureEditorSnapshot().Packages;
            _nextPackageDisplayRefresh = EditorApplication.timeSinceStartup + 0.5;
        }

        /// <summary>输入配置已规范化；结合真实目标、管理记录及可选的已注册包判断状态。其他位置的文件不取得管理权。</summary>
        public static PluginInstallationInfo InspectInstallation(string projectRoot, ExPluginItem plugin,
            PluginInstallDatabase database, PluginPackageExpectation registeredPackage)
        {
            string target = Path.Combine(projectRoot, plugin.LocalPath);
            var record = database.installedPlugins.Find(item => PluginInstallFiles.SamePath(item.installPath, plugin.LocalPath));
            bool occupied = File.Exists(PluginInstallFiles.FileSystemPath(target));
            bool populated = PluginInstallFiles.HasContent(target);
            if (TryFindInstallationElsewhere(projectRoot, plugin, database, registeredPackage, out string existingPath, out _))
            {
                var installation = InstallationInfo(PluginInstallationKind.UnmanagedElsewhere, "已安装(未托管)", target,
                    "该插件已在其他位置安装，当前条目未托管。请开发者自行处理；ToolHub 仅显示信息，所有操作已禁用。", false);
                installation.ExistingPath = existingPath;
                return installation;
            }
            if (!populated)
            {
                var installation = InstallationInfo(PluginInstallationKind.NotInstalled, "未安装", target,
                    "尚未安装，可点击“安装”。", false);
                if (occupied) installation.OperationError = "安装目录被同名文件占用，请处理该文件后安装。";
                return installation;
            }
            bool managed = record != null && RecordMatchesSource(record, plugin) && !string.IsNullOrEmpty(record.contentHash);
            var installed = InstallationInfo(PluginInstallationKind.Installed, "已安装", target,
                managed ? "已安装，可更新、重装或卸载。" : "目标目录已有文件。确认是此插件后，可选择“接管现有目录”。", !managed);
            installed.IsManaged = managed;
            return installed;
        }

        /// <summary>安装入口和准备阶段的实时重复检测；前置状态判断复用同一定位逻辑。registeredPackage 可为空。</summary>
        public static bool TryFindKnownInstallationConflict(string projectRoot, ExPluginItem plugin,
            PluginInstallDatabase database, PluginPackageExpectation registeredPackage, out string reason)
        {
            if (TryFindInstallationElsewhere(projectRoot, plugin, database, registeredPackage, out _, out string description))
            {
                reason = description + "。安装已中止，请由开发者处理原插件及其引用后重试；目标文件和安装记录未改动。";
                return true;
            }
            reason = null;
            return false;
        }

        private static bool TryFindInstallationElsewhere(string projectRoot, ExPluginItem plugin,
            PluginInstallDatabase database, PluginPackageExpectation registeredPackage, out string path, out string description)
        {
            string target = Path.Combine(projectRoot, plugin.LocalPath);
            if (registeredPackage != null && !PluginInstallFiles.SamePath(registeredPackage.LocalPath, target))
            {
                path = registeredPackage.LocalPath;
                description = "项目已通过 UPM 安装同名包 " + registeredPackage.Name + "，实际文件位置: "
                    + PluginInstallFiles.ProjectRelativePath(projectRoot, path);
                return true;
            }
            var elsewhere = database.installedPlugins.FirstOrDefault(item => !PluginInstallFiles.SamePath(item.installPath, plugin.LocalPath)
                && RecordMatchesSource(item, plugin) && PluginInstallFiles.HasContent(Path.Combine(projectRoot, item.installPath)));
            if (elsewhere != null)
            {
                path = Path.Combine(projectRoot, elsewhere.installPath);
                description = "同一来源的插件已安装在其他位置: " + PluginInstallFiles.ProjectRelativePath(projectRoot, path);
                return true;
            }
            path = null;
            description = null;
            return false;
        }

        private static PluginInstallationInfo InstallationInfo(PluginInstallationKind kind, string label, string path, string message, bool canAdopt)
            => new PluginInstallationInfo { Kind = kind, Label = label, ActualPath = path, Message = message, CanAdopt = canAdopt };

        private static bool RecordMatchesSource(PluginInstallationRecord record, ExPluginItem plugin)
            => string.Equals(record.repoUrl, GitPluginUtility.RepositoryUrl(plugin), StringComparison.OrdinalIgnoreCase)
                && record.relativePath == plugin.GitURL_Path;

        private static string PackageNameHint(ExPluginItem plugin, PluginInstallationRecord record)
            => record != null && RecordMatchesSource(record, plugin) && !string.IsNullOrEmpty(record.packageName) ? record.packageName
                : plugin.LocalPath.StartsWith("Packages/", StringComparison.Ordinal) ? plugin.LocalPath.Substring("Packages/".Length) : null;

        public static bool CanOperate(PluginInstallationInfo installation, PluginOperationKind kind, out string reason)
        {
            if (installation.OperationError != null)
            {
                reason = installation.OperationError;
                return false;
            }
            if (installation.Kind == PluginInstallationKind.UnmanagedElsewhere)
            {
                reason = installation.Label + "：" + installation.Message + "\n已有位置：" + DisplayPath(installation.ExistingPath);
                return false;
            }
            bool managed = installation.IsManaged;
            bool allowed = kind == PluginOperationKind.Install
                ? installation.Kind == PluginInstallationKind.NotInstalled
                : kind == PluginOperationKind.Adopt ? installation.CanAdopt
                : kind == PluginOperationKind.Reinstall || kind == PluginOperationKind.Uninstall ? managed
                : kind == PluginOperationKind.Update || kind == PluginOperationKind.CheckUpdates ? managed : false;
            reason = allowed ? null : installation.Label + "：" + installation.Message;
            return allowed;
        }

        public static bool TryStart(PluginOperationKind kind, IEnumerable<ExPluginItem> selection, out string reason)
        {
            reason = null;
            if (IsBusy || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                reason = "请等待当前任务和编辑器导入 / 编译完成，并在非 Play Mode 下操作。";
                return false;
            }
            EnsureAvailable();
            RefreshInstallationInfo();
            var plugins = Clone(selection.ToList());
            ValidateSelection(plugins);
            var snapshot = CaptureEditorSnapshot();
            foreach (var plugin in plugins)
            {
                var record = FindRecord(plugin.LocalPath);
                string name = PackageNameHint(plugin, record);
                var package = snapshot.Packages.FirstOrDefault(item => item.Name == name
                    || PluginInstallFiles.SamePath(item.LocalPath, Path.Combine(ProjectRoot, plugin.LocalPath)));
                if (!CanOperate(InspectInstallation(ProjectRoot, plugin, _database, package), kind, out reason))
                {
                    reason = plugin.Name + "：" + reason;
                    return false;
                }
            }
            string token = ExOpenSourceNetworkHelper.Token() ?? "";
            var database = Clone(_database);
            Begin(kind, plugins);
            string operationDirectory = OperationDirectory(_state.Id);
            var cancellation = _cancellation.Token;
            _task = Task.Run(() => PrepareAsync(kind, plugins, database, snapshot, operationDirectory, token, cancellation));
            return true;
        }

        public static void Scan(ExPluginItem source, string menuName, string scanDirectory, string installationRoot)
        {
            EnsureAvailable();
            MenuJsonUtility.ValidateSource(source);
            string prefix = PluginInstallFiles.NormalizeRelativePath(scanDirectory, true);
            string target = PluginInstallFiles.NormalizeRelativePath(installationRoot, false);
            if (target != "Packages" && !target.StartsWith("Assets/", StringComparison.Ordinal))
                throw new InvalidDataException("扫描安装根目录必须为 Packages 或 Assets 下的目录。");
            if (string.IsNullOrWhiteSpace(menuName)) throw new InvalidDataException("扫描结果需要所属菜单。");
            var plugin = Clone(source);
            string token = ExOpenSourceNetworkHelper.Token() ?? "";
            Begin(PluginOperationKind.Scan, new List<ExPluginItem>());
            var cancellation = _cancellation.Token;
            _task = Task.Run(async () =>
            {
                var batch = new PreparedBatch();
                var repository = await GitPluginUtility.FetchAsync(CacheRoot, plugin, token,
                    message => Report(null, message, 0.1f), cancellation);
                if (prefix.Length > 0) await GitPluginUtility.TreeHashAsync(repository, prefix, token, cancellation);
                var manifests = await GitPluginUtility.CheckoutManifestsAsync(repository, prefix, token, cancellation,
                    message => Report(null, message, 0.25f));
                foreach (string manifest in manifests)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string directory = manifest == "package.json" ? "" : manifest.Substring(0, manifest.Length - "/package.json".Length);
                    var entry = new PluginScanEntry { SourcePath = directory };
                    try
                    {
                        var package = PluginInstallFiles.ReadPackage(Path.Combine(repository.Directory, directory), true);
                        string localPath = target + "/" + package.Name;
                        PluginInstallFiles.InstallationPath(ProjectRoot, localPath);
                        entry.Plugin = new ExPluginItem
                        {
                            Name = package.DisplayName, Version = package.Version, Author = package.Author, Intro = package.Description,
                            MenuPath = menuName + "/" + package.Name, Tags = new[] { "UPM" }, LocalPath = localPath,
                            GitURL_Path = directory, GitURL_Username = plugin.GitURL_Username, GitURL_RepoName = plugin.GitURL_RepoName,
                            GitURL_Branch = plugin.GitURL_Branch, IntroductionURL = repository.Url.Substring(0, repository.Url.Length - 4),
                            Dependencies = Array.Empty<UPMPackage>()
                        };
                    }
                    catch (Exception error) when (error is IOException || error is JsonException || error is ArgumentException)
                    { entry.Error = error.Message; }
                    batch.ScanEntries.Add(entry);
                }
                return batch;
            });
        }

        public static void Cancel()
        {
            if (!CanCancel) return; // 提交和 UPM 原生请求不提供中途取消。
            _cancellation.Cancel();
            _state.Message = "正在终止 Git 并结束文件准备...";
        }

        public static void RetryPackages()
        {
            EnsureAvailable();
            if (!CanRetryPackages) throw new InvalidOperationException("当前没有可重试的 UPM 请求。");
            SubmitPackages();
        }

        public static string CacheDirectory => CacheRoot;

        private static void EnsureAvailable()
        {
            if (_initializationError != null) throw new InvalidOperationException("请先修复安装记录 / 状态文件: " + _initializationError);
            if (_state.RecoveryRequired) throw new InvalidOperationException("上次文件提交尚未恢复，请先处理任务状态中的恢复错误。");
            if (IsBusy) throw new InvalidOperationException("已有插件任务运行中。");
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new InvalidOperationException("请在非 Play Mode 且编辑器导入 / 编译完成后操作插件。");
        }

        private static void Begin(PluginOperationKind kind, List<ExPluginItem> plugins)
        {
            _state = new PluginOperationState
            {
                Id = Guid.NewGuid().ToString("N"), Kind = kind, Phase = PluginOperationPhase.Preparing,
                Message = "准备任务...", Rows = plugins.Select(p => new PluginOperationRow { Name = p.Name, LocalPath = p.LocalPath }).ToList()
            };
            _cancellation = new CancellationTokenSource();
            Directory.CreateDirectory(CacheRoot);
            SaveState();
        }

        private static void ValidateDatabase(PluginInstallDatabase database)
        {
            if (database == null || database.installedPlugins == null) throw new InvalidDataException("安装记录必须包含 installedPlugins。");
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in database.installedPlugins)
            {
                if (record == null || string.IsNullOrWhiteSpace(record.installPath) || record.sourceFiles == null)
                    throw new InvalidDataException("安装记录中的目录 / 文件清单无效。");
                record.installPath = PluginInstallFiles.NormalizeRelativePath(record.installPath, false);
                PluginInstallFiles.InstallationPath(ProjectRoot, record.installPath);
                if (!targets.Add(record.installPath)) throw new InvalidDataException("重复安装记录: " + record.installPath);
            }
        }

        private static void ValidateState(PluginOperationState state)
        {
            if (state == null || state.Rows == null || state.Changes == null || state.ScanEntries == null
                || state.ExpectedPackages == null || state.ExpectedDirectDependencies == null || state.UpmAdd == null || state.UpmRemove == null
                || !Enum.IsDefined(typeof(PluginOperationPhase), state.Phase) || !Enum.IsDefined(typeof(PluginOperationKind), state.Kind))
                throw new InvalidDataException("任务状态的必要字段无效。");
            if (state.Phase != PluginOperationPhase.Idle && !Guid.TryParseExact(state.Id, "N", out _))
                throw new InvalidDataException("任务状态的标识无效。");
            var indices = new HashSet<int>();
            var paths = new List<string>();
            foreach (var change in state.Changes)
            {
                if (change == null || change.Index < 0 || !indices.Add(change.Index)) throw new InvalidDataException("任务恢复清单无效。");
                string path = PluginInstallFiles.InstallationPath(ProjectRoot, change.LocalPath);
                if (paths.Any(p => PluginInstallFiles.Overlaps(p, path))) throw new InvalidDataException("任务恢复目录重叠。");
                paths.Add(path);
            }
        }

        private static void ValidateSelection(List<ExPluginItem> plugins)
        {
            if (plugins.Count == 0) throw new InvalidDataException("请至少选择一个插件。");
            var targets = new List<string>();
            var sources = new HashSet<string>(StringComparer.Ordinal);
            foreach (var plugin in plugins)
            {
                if (plugin == null || string.IsNullOrWhiteSpace(plugin.Name)) throw new InvalidDataException("插件名称不能为空。");
                MenuJsonUtility.ValidateSource(plugin);
                plugin.GitURL_Path = PluginInstallFiles.NormalizeRelativePath(plugin.GitURL_Path, true);
                plugin.LocalPath = PluginInstallFiles.NormalizeRelativePath(plugin.LocalPath, false);
                if (!sources.Add(GitPluginUtility.RepositoryUrl(plugin).ToLowerInvariant() + "|" + plugin.GitURL_Path))
                    throw new InvalidDataException("同一来源不能在同批操作中配置到多个目标目录: " + plugin.Name);
                string target = PluginInstallFiles.InstallationPath(ProjectRoot, plugin.LocalPath);
                if (targets.Any(p => PluginInstallFiles.Overlaps(p, target))) throw new InvalidDataException("所选插件的安装目录重叠: " + plugin.LocalPath);
                if (_database.installedPlugins.Any(r => !string.Equals(r.installPath, plugin.LocalPath, StringComparison.OrdinalIgnoreCase)
                    && PluginInstallFiles.Overlaps(PluginInstallFiles.InstallationPath(ProjectRoot, r.installPath), target)))
                    throw new InvalidDataException("安装目录与其他已托管插件重叠: " + plugin.LocalPath);
                targets.Add(target);
            }
        }

        private static EditorSnapshot CaptureEditorSnapshot()
        {
            var snapshot = new EditorSnapshot();
            foreach (var package in PackageInfo.GetAllRegisteredPackages())
                snapshot.Packages.Add(new PluginPackageExpectation
                {
                    Name = package.name, Version = package.version, LocalPath = package.resolvedPath,
                    IsEmbedded = package.source == PackageSource.Embedded,
                    Dependencies = package.dependencies.ToDictionary(d => d.name, d => d.version, StringComparer.Ordinal)
                });
            return snapshot;
        }

        private static async Task<PreparedBatch> PrepareAsync(PluginOperationKind kind, List<ExPluginItem> plugins,
            PluginInstallDatabase database, EditorSnapshot snapshot, string operationDirectory, string token, CancellationToken cancellation)
        {
            var batch = new PreparedBatch { Database = database, ObservedDirect = UpmInstaller.ReadDirectDependencies(ProjectRoot),
                ObservedMenuJson = MenuJsonUtility.ReadProjectMenuJson(ProjectRoot) };
            var configured = MenuJsonUtility.Parse(batch.ObservedMenuJson, out _).Plugins;
            foreach (var plugin in plugins)
                if (!configured.Any(item => PluginInstallFiles.SamePath(item.LocalPath, plugin.LocalPath)
                    && item.MenuPath == plugin.MenuPath && item.GitURL_Username == plugin.GitURL_Username
                    && item.GitURL_RepoName == plugin.GitURL_RepoName && item.GitURL_Branch == plugin.GitURL_Branch
                    && item.GitURL_Path == plugin.GitURL_Path))
                    throw new InvalidDataException("插件尚未保存到 menu.json，或配置已修改，请刷新后重试: " + plugin.Name);
            for (int i = 0; i < plugins.Count; i++)
            {
                var plugin = plugins[i];
                var previous = database.installedPlugins.Find(r => PluginInstallFiles.SamePath(r.installPath, plugin.LocalPath));
                string name = PackageNameHint(plugin, previous);
                var registered = snapshot.Packages.FirstOrDefault(package => package.Name == name
                    || PluginInstallFiles.SamePath(package.LocalPath, Path.Combine(ProjectRoot, plugin.LocalPath)));
                if (!CanOperate(InspectInstallation(ProjectRoot, plugin, database, registered), kind, out string blocked))
                    return BlockPreparation(batch, plugin, blocked);
                string destination = PluginInstallFiles.InstallationPath(ProjectRoot, plugin.LocalPath);
                bool populated = Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any();
                var item = new PreparedPlugin { Plugin = plugin, Destination = destination, Previous = previous, Index = i,
                    HadDirectory = Directory.Exists(destination) };
                batch.Items.Add(item);
                if (kind == PluginOperationKind.Adopt)
                {
                    if (!populated) throw new InvalidDataException("接管需要已有的非空目录: " + plugin.LocalPath);
                    var files = PluginInstallFiles.ListFiles(destination);
                    item.Package = PluginInstallFiles.ReadPackage(destination, item.IsPackage);
                    item.Next = MakeRecord(item, null, null, files, PluginInstallFiles.ContentHash(destination, files, cancellation));
                    SetRecord(database, item.Next);
                    item.Result = "已接管，来源提交尚未确认";
                    continue;
                }
                if (item.HadDirectory)
                {
                    item.BeforeFiles = previous == null ? PluginInstallFiles.ListFiles(destination) : previous.sourceFiles;
                    item.BeforeHash = PluginInstallFiles.ContentHash(destination, item.BeforeFiles, cancellation);
                }
                item.Populated = populated;
                if (kind == PluginOperationKind.Uninstall)
                {
                    if (previous == null) throw new InvalidDataException("没有该插件的托管记录: " + plugin.Name);
                    item.Package = PluginInstallFiles.ReadPackage(destination, item.IsPackage && populated);
                    item.Changed = Directory.Exists(destination) || File.Exists(destination + ".meta");
                    item.Result = "准备卸载";
                    database.installedPlugins.Remove(previous);
                }
                else if (kind == PluginOperationKind.Install && populated)
                {
                    item.Package = PluginInstallFiles.ReadPackage(destination, item.IsPackage);
                    item.Result = "已安装，跳过";
                }
                else if (kind == PluginOperationKind.CheckUpdates && previous == null)
                    item.Result = "未安装";
                else
                {
                    if (kind != PluginOperationKind.Install && previous == null)
                        throw new InvalidDataException("请先安装该插件: " + plugin.Name);
                    item.NeedsDownload = true;
                }
            }
            if (kind == PluginOperationKind.Adopt) return batch;
            if (kind == PluginOperationKind.Uninstall)
            {
                if (!TryValidateUninstall(batch, snapshot, out string blocked))
                    return BlockPreparation(batch, plugins[0], blocked);
                PrepareUpmRemoval(batch);
                return batch;
            }

            var groups = batch.Items.Where(i => i.NeedsDownload).GroupBy(i => GitPluginUtility.RepositoryKey(i.Plugin)).ToList();
            var dependencySnapshots = new Dictionary<string, GitDependencySnapshot>(StringComparer.Ordinal);
            if (kind != PluginOperationKind.CheckUpdates)
            {
                // 依赖预检走 GitHub API；整批通过前不启动 Git 克隆或获取仓库树。
                for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
                {
                    var group = groups[groupIndex].ToList();
                    float progress = 0.1f + 0.15f * groupIndex / groups.Count;
                    Report(null, "依赖预检：通过 GitHub API 读取 " + group[0].Plugin.GitURL_RepoName + " 的清单...", progress);
                    var dependencies = await GitPluginUtility.ReadDependencyManifestsAsync(group[0].Plugin,
                        group.Select(i => i.Plugin.GitURL_Path), token, cancellation,
                        message => Report(null, message, progress));
                    dependencySnapshots.Add(groups[groupIndex].Key, dependencies);
                    foreach (var item in group)
                    {
                        item.Package = dependencies.Packages[item.Plugin.GitURL_Path];
                        if (item.IsPackage && item.Package == null)
                            throw new InvalidDataException("依赖预检失败：安装到 Packages 的来源必须包含根 package.json: "
                                + item.Plugin.Name + " (" + item.Plugin.GitURL_Path + ")。Git 尚未启动。");
                        if (item.IsPackage && item.Previous != null && !string.IsNullOrEmpty(item.Previous.packageName)
                            && item.Previous.packageName != item.Package.Name)
                            throw new InvalidDataException("上游包名发生变化，请卸载旧条目并重新配置: " + item.Plugin.Name);
                        Report(item.Plugin.LocalPath, "依赖清单已读取，等待整批预检", progress);
                    }
                }
                cancellation.ThrowIfCancellationRequested();
                Report(null, "检查整批插件的依赖及来源（Git 尚未启动）...", 0.3f);
                PrepareDependencies(batch, snapshot);
                cancellation.ThrowIfCancellationRequested();
                Report(null, "依赖预检通过，开始通过 Git 获取仓库和插件文件...", 0.35f);
            }

            for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                var group = groups[groupIndex].ToList();
                float progress = 0.35f + 0.1f * groupIndex / groups.Count;
                string requestedCommit = kind == PluginOperationKind.CheckUpdates ? null
                    : dependencySnapshots[groups[groupIndex].Key].Commit;
                GitRepositorySnapshot repository;
                try
                {
                    repository = await GitPluginUtility.FetchAsync(CacheRoot, group[0].Plugin, token,
                        message => Report(null, message, progress), cancellation, requestedCommit);
                }
                catch (Exception error) when (kind != PluginOperationKind.CheckUpdates && !(error is OperationCanceledException))
                {
                    throw new InvalidOperationException("依赖预检已通过，但 Git 获取仓库失败，插件文件尚未提交。\n"
                        + error.Message, error);
                }
                foreach (var item in group)
                {
                    item.Repository = repository;
                    Report(item.Plugin.LocalPath, "核对目录 " + item.Plugin.GitURL_Path, progress);
                    item.Tree = await GitPluginUtility.TreeHashAsync(repository, item.Plugin.GitURL_Path, token, cancellation);
                    bool sameTree = item.Populated && item.Previous.treeHash == item.Tree;
                    if (kind == PluginOperationKind.CheckUpdates)
                    {
                        bool dirty = item.Populated && item.BeforeHash != item.Previous.contentHash;
                        item.Result = !item.Populated ? "本地目录缺失，需要安装" : sameTree ? "已是最新" : "有更新";
                        if (dirty) item.Result += "；本地有修改";
                    }
                    else
                    {
                        item.Changed = kind == PluginOperationKind.Reinstall || !sameTree;
                        if (item.Changed && item.Populated && kind != PluginOperationKind.Reinstall && item.BeforeHash != item.Previous.contentHash)
                            throw new InvalidOperationException("检测到本地修改，更新已停止；如需覆盖请显式使用重装: " + item.Plugin.Name);
                        item.Result = item.Changed ? "准备安装" : "目录未变化，跳过复制";
                        if (!item.Changed)
                        {
                            item.Package = PluginInstallFiles.ReadPackage(item.Destination, item.IsPackage);
                            item.Next = Clone(item.Previous);
                            item.Next.commit = repository.Commit;
                            item.Next.targetBranch = repository.Branch;
                            SetRecord(database, item.Next);
                        }
                    }
                    Report(item.Plugin.LocalPath, item.Result, progress);
                }
            }
            if (kind == PluginOperationKind.CheckUpdates) return batch;

            for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                var changed = groups[groupIndex].Where(i => i.Changed).ToList();
                if (changed.Count == 0) continue;
                var repository = changed[0].Repository;
                float progress = 0.45f + 0.35f * groupIndex / groups.Count;
                Report(null, "正在下载并检出 " + changed.Count + " 个插件目录...", progress + 0.02f);
                await GitPluginUtility.CheckoutDirectoriesAsync(repository, changed.Select(i => i.Plugin.GitURL_Path), token, cancellation,
                    message => Report(null, message, progress + 0.02f));
                foreach (var item in changed)
                {
                    cancellation.ThrowIfCancellationRequested();
                    Report(item.Plugin.LocalPath, "准备文件 " + item.Plugin.Name, progress + 0.05f);
                    string source = item.Plugin.GitURL_Path.Length == 0 ? repository.Directory
                        : PluginInstallFiles.ContainedPath(repository.Directory, item.Plugin.GitURL_Path);
                    if (!Directory.Exists(PluginInstallFiles.FileSystemPath(source))) throw new DirectoryNotFoundException("仓库目录不存在: " + item.Plugin.GitURL_Path);
                    string staged = PluginInstallFiles.ContainedPath(operationDirectory, "New/" + item.Index);
                    PluginInstallFiles.CopyDirectory(source, staged, cancellation);
                    var files = PluginInstallFiles.ListFiles(staged);
                    if (files.Count == 0) throw new InvalidDataException("插件来源目录没有文件: " + item.Plugin.GitURL_Path);
                    if (item.Populated) PluginInstallFiles.PreserveGeneratedMeta(item.Destination, staged, files);
                    item.Next = MakeRecord(item, repository, item.Tree, files, PluginInstallFiles.ContentHash(staged, files, cancellation));
                    SetRecord(database, item.Next);
                    item.Staged = staged;
                    if (kind == PluginOperationKind.Update && item.Populated
                        && PluginInstallFiles.ContentHash(item.Destination, files, cancellation) == item.Next.contentHash)
                    {
                        item.Changed = false;
                        item.Result = "来源版本已核实，文件相同，跳过复制";
                    }
                }
            }
            var changedItems = batch.Items.Where(item => item.Changed).ToList();
            if (changedItems.Count > 0)
            {
                Report(null, "检测项目其他位置的包、程序集和资源身份...", 0.82f);
                var scriptNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in changedItems)
                    scriptNames.UnionWith(PluginInstallFiles.FingerprintScriptNames(item.Staged, item.Package, cancellation));
                var inventory = PluginInstallFiles.InspectProject(ProjectRoot, snapshot.Packages, cancellation, scriptNames);
                foreach (var item in changedItems)
                    if (PluginInstallFiles.TryFindConflict(inventory, item.Staged, item.Destination, item.Package, cancellation, out string conflict))
                        return BlockPreparation(batch, item.Plugin, conflict);
            }
            return batch;
        }

        private static PluginInstallationRecord MakeRecord(PreparedPlugin item, GitRepositorySnapshot repository,
            string tree, List<string> files, string hash)
        {
            return new PluginInstallationRecord
            {
                name = item.Plugin.Name, repoUrl = GitPluginUtility.RepositoryUrl(item.Plugin), targetBranch = item.Plugin.GitURL_Branch,
                relativePath = item.Plugin.GitURL_Path, installPath = item.Plugin.LocalPath,
                commit = repository == null ? "" : repository.Commit, treeHash = tree ?? "", contentHash = hash,
                sourceFiles = files, packageName = item.Package == null ? "" : item.Package.Name,
                packageVersion = item.Package == null ? "" : item.Package.Version
            };
        }

        private static void SetRecord(PluginInstallDatabase database, PluginInstallationRecord record)
        {
            database.installedPlugins.RemoveAll(r => string.Equals(r.installPath, record.installPath, StringComparison.OrdinalIgnoreCase));
            database.installedPlugins.Add(record);
        }

        private static PreparedBatch BlockPreparation(PreparedBatch batch, ExPluginItem plugin, string reason)
        {
            batch.Error = plugin.Name + "：" + reason;
            foreach (var item in batch.Items) item.Result = item.Plugin == plugin ? reason : "未提交";
            Report(plugin.LocalPath, reason, 0.85f);
            return batch;
        }

        private static void PrepareDependencies(PreparedBatch batch, EditorSnapshot snapshot)
        {
            var direct = batch.ObservedDirect;
            var external = UpmInstaller.MergeDependencies(batch.Items.Select(i => i.Plugin));
            var scopes = UpmInstaller.ReadRegistryScopes(ProjectRoot);
            var packages = new Dictionary<string, PreparedPlugin>(StringComparer.Ordinal);
            foreach (var item in batch.Items.Where(i => i.IsPackage && i.Package != null))
            {
                if (packages.ContainsKey(item.Package.Name)) throw new InvalidDataException("同批存在重复包名: " + item.Package.Name);
                packages.Add(item.Package.Name, item);
                var existing = snapshot.Packages.Find(p => p.Name == item.Package.Name);
                if (existing != null && Path.GetFullPath(existing.LocalPath).StartsWith(Path.Combine(ProjectRoot, "Packages") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(Path.GetFullPath(existing.LocalPath), item.Destination, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("同名包已嵌入另一个目录: " + existing.LocalPath);
                // Packages 下的目录是嵌入包，UPM 自动发现；不能再以 file: 添加同名包。
                batch.Expected.Add(new PluginPackageExpectation
                {
                    Name = item.Package.Name, Version = item.Package.Version, LocalPath = item.Plugin.LocalPath,
                    Dependencies = item.Package.Dependencies
                });
            }
            foreach (string name in packages.Keys) external.Remove(name); // 同批嵌入包已经满足这些依赖。
            var installed = new HashSet<string>(snapshot.Packages.Select(p => p.Name), StringComparer.Ordinal);
            var sourced = new HashSet<string>(direct.Concat(external)
                .Where(dependency => UpmInstaller.HasRegistrySource(scopes, dependency.Key)
                    || UpmInstaller.IsLocationSource(dependency.Value)).Select(dependency => dependency.Key), StringComparer.Ordinal);
            var missing = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var dependency in external)
                if (!sourced.Contains(dependency.Key))
                    missing.Add(dependency.Key, batch.Items.SelectMany(item => (item.Plugin.Dependencies ?? Array.Empty<UPMPackage>())
                        .Where(d => d.name.Trim() == dependency.Key)
                        .Select(d => item.Plugin.Name + "（Dependencies: " + dependency.Value + "，未配置可用仓库或下载地址）")).ToList());
            foreach (var item in batch.Items.Where(i => i.Package != null))
                foreach (var dependency in item.Package.Dependencies)
                {
                    if (packages.ContainsKey(dependency.Key) || installed.Contains(dependency.Key)
                        || sourced.Contains(dependency.Key)) continue;
                    if (!UpmInstaller.HasRegistrySource(scopes, dependency.Key))
                    {
                        if (!missing.TryGetValue(dependency.Key, out var consumers))
                            missing.Add(dependency.Key, consumers = new List<string>());
                        consumers.Add(item.Plugin.Name + "（要求 " + dependency.Value + "）");
                    }
                    else if (!item.IsPackage) external[dependency.Key] = dependency.Value;
                }
            if (missing.Count > 0)
            {
                foreach (var item in batch.Items)
                {
                    // 包清单和显式 Dependencies 都是可选输入；没有清单的 Assets 插件也能报告缺失来源。
                    var names = (item.Package == null ? Enumerable.Empty<string>() : item.Package.Dependencies.Keys)
                        .Concat(item.Plugin.Dependencies == null ? Enumerable.Empty<string>() : item.Plugin.Dependencies.Select(d => d.name.Trim()))
                        .Where(missing.ContainsKey).Distinct(StringComparer.Ordinal).ToList();
                    Report(item.Plugin.LocalPath, names.Count > 0 ? "缺少依赖来源: " + string.Join("、", names)
                        : "未提交（本批依赖预检未通过）", 0.3f);
                }
                throw new InvalidDataException("依赖预检失败：缺少 " + missing.Count + " 个依赖来源，已停止，Git 尚未启动，插件主体尚未下载。\n\n"
                    + string.Join("\n\n", missing.Select((dependency, index) => (index + 1) + ". " + dependency.Key
                        + "\n   来自：" + string.Join("、", dependency.Value.Distinct(StringComparer.Ordinal))))
                    + "\n\n请将对应插件选入本批安装，或在引用插件的 Dependencies 中填写来源；也可先通过 Package Manager 安装。\n"
                    + "插件目录和 menu.json 未改动，临时缓存将自动清理。");
            }
            foreach (var dependency in external)
            {
                if (direct.TryGetValue(dependency.Key, out string current))
                {
                    if (current != dependency.Value)
                        throw new InvalidDataException("项目直接依赖与本批配置冲突: " + dependency.Key + " (" + current + " / " + dependency.Value + ")");
                    continue;
                }
                batch.Direct[dependency.Key] = dependency.Value;
                batch.Add.Add(UpmInstaller.PackageSpecification(dependency.Key, dependency.Value));
            }
        }

        private static bool TryValidateUninstall(PreparedBatch batch, EditorSnapshot snapshot, out string reason)
        {
            var removing = new HashSet<string>(batch.Items.Where(i => i.IsPackage).Select(i => i.Previous.packageName), StringComparer.Ordinal);
            foreach (var package in snapshot.Packages)
                if (!removing.Contains(package.Name))
                    foreach (string dependency in package.Dependencies.Keys)
                        if (removing.Contains(dependency))
                        {
                            reason = "“" + package.Name + "”仍依赖“" + dependency + "”，不能单独卸载；请选择依赖方一起卸载。";
                            return false;
                        }
            reason = null;
            return true;
        }

        private static void PrepareUpmRemoval(PreparedBatch batch)
        {
            var direct = batch.ObservedDirect;
            foreach (var item in batch.Items.Where(i => i.IsPackage))
            {
                string name = item.Previous.packageName;
                if (string.IsNullOrEmpty(name)) throw new InvalidDataException("记录缺少包名，请重新接管: " + item.Plugin.Name);
                if (direct.TryGetValue(name, out string source) && source != UpmInstaller.LegacyEmbeddedSource(item.Plugin.LocalPath))
                    throw new InvalidOperationException("该包的项目直接来源已经改变，请先核对 manifest 再卸载: " + name);
                // 记录所有应消失的包；没有 manifest 引用的嵌入包只需移走目录并解析。
                batch.Remove.Add(name);
            }
        }

        private static void Report(string localPath, string message, float progress)
            => Messages.Enqueue(new ProgressMessage { LocalPath = localPath, Message = message, Progress = progress });

        private static void Update()
        {
            bool received = false;
            while (Messages.TryDequeue(out var message))
            {
                if (_state.Phase != PluginOperationPhase.Preparing) continue;
                _state.Message = message.Message;
                _state.Progress = Mathf.Clamp01(message.Progress);
                var row = _state.Rows.Find(r => r.LocalPath == message.LocalPath);
                if (row != null) row.Result = message.Message;
                received = true;
            }
            if (received && EditorApplication.timeSinceStartup - _lastStateSave > 0.5) SaveState();
            if (_task != null && _task.IsCompleted)
            {
                var completed = _task;
                _task = null;
                try
                {
                    var batch = completed.GetAwaiter().GetResult();
                    _cancellation.Token.ThrowIfCancellationRequested();
                    FinishPreparation(batch);
                }
                catch (OperationCanceledException) { Fail(PluginOperationPhase.Cancelled, "操作已取消，插件目录未改动。"); }
                catch (Exception error)
                {
                    if (_cancellation.IsCancellationRequested) Fail(PluginOperationPhase.Cancelled, "操作已取消，插件目录未改动。");
                    else { Fail(PluginOperationPhase.Failed, error.Message); Debug.LogException(error); }
                }
                finally { _cancellation.Dispose(); _cancellation = null; }
            }
            if (_upmRequest != null && _upmRequest.IsCompleted)
            {
                var request = _upmRequest;
                _upmRequest = null;
                try
                {
                    if (request.Status == StatusCode.Success)
                        WaitForPackages();
                    else Fail(PluginOperationPhase.Failed, "文件已提交，UPM 处理失败: " + request.Error.message + "。修正依赖后可以重试包解析。");
                }
                catch (Exception error)
                {
                    Fail(PluginOperationPhase.Failed, "文件已提交，核对 UPM 结果失败: " + error.Message);
                    Debug.LogException(error);
                }
            }
            if (_waitingForPackages && !EditorApplication.isCompiling && !EditorApplication.isUpdating)
            {
                try
                {
                    if (PackagesMatch())
                    {
                        _waitingForPackages = false;
                        Complete("包解析完成，已核对嵌入包来源、声明依赖和注册结果。");
                    }
                    else if (EditorApplication.timeSinceStartup - _packageWaitStarted > 120)
                    {
                        _waitingForPackages = false;
                        Fail(PluginOperationPhase.Failed, "文件已提交，包解析结果尚未一致。请检查 UPM 错误并重试包解析。");
                    }
                }
                catch (Exception error)
                {
                    _waitingForPackages = false;
                    Fail(PluginOperationPhase.Failed, "核对包注册结果失败: " + error.Message);
                    Debug.LogException(error);
                }
            }
            if (_cacheCleanupTask != null && _cacheCleanupTask.IsCompleted)
            {
                var completed = _cacheCleanupTask;
                _cacheCleanupTask = null;
                try
                {
                    completed.GetAwaiter().GetResult();
                    _state.Message += _state.RecoveryRequired
                        ? " 下载及其他任务缓存已清理；当前恢复目录暂留，恢复完成后清理。"
                        : _state.Phase == PluginOperationPhase.Resolving ? " 源码缓存已清理，仅暂留本次包解析任务。" : " 工作缓存已清理。";
                    if (_cleanupRetainsState && !_state.RecoveryRequired && _state.Phase != PluginOperationPhase.Resolving)
                        StartCacheCleanup();
                }
                catch (Exception error)
                {
                    _state.Message += " 缓存清理失败: " + error.Message + "。下次启动或任务结束时将重试清理。";
                    Debug.LogWarning("snchuToolHub 缓存清理失败: " + error.Message);
                }
                SaveState();
            }
        }

        private static void FinishPreparation(PreparedBatch batch)
        {
            if (batch.Error != null)
            {
                foreach (var row in _state.Rows)
                    row.Result = batch.Items.FirstOrDefault(item => item.Plugin.LocalPath == row.LocalPath)?.Result ?? "未提交";
                Fail(PluginOperationPhase.Failed, batch.Error);
                Debug.LogError(batch.Error);
                return;
            }
            if (_state.Kind == PluginOperationKind.Scan)
            {
                _state.ScanEntries = batch.ScanEntries;
                Complete("扫描完成，共 " + batch.ScanEntries.Count + " 个清单；保存配置后才会安装。");
                return;
            }
            _state.Rows = batch.Items.Select(i => new PluginOperationRow
            {
                Name = i.Plugin.Name, LocalPath = i.Plugin.LocalPath, Result = i.Result,
                Commit = i.Repository == null ? "" : i.Repository.Commit
            }).ToList();
            if (_state.Kind == PluginOperationKind.CheckUpdates) { Complete("检查更新完成，插件目录未改动。"); return; }
            _state.UpmAdd = batch.Add.Distinct(StringComparer.Ordinal).ToArray();
            _state.UpmRemove = batch.Remove.Distinct(StringComparer.Ordinal).ToArray();
            _state.ExpectedDirectDependencies = batch.Direct;
            _state.ExpectedPackages = batch.Expected;
            CommitFiles(batch);
            // 已提交配置写入 menu.json；包解析期间只暂留缓存中的任务信息，结束后一起删除。
            StartCacheCleanup();
            if (RequiresPackageResolution) SubmitPackages();
            else Complete("操作完成。");
        }

        private static void CommitFiles(PreparedBatch batch)
        {
            string operation = OperationDirectory(_state.Id);
            Directory.CreateDirectory(operation);
            _state.MenuExisted = File.Exists(MenuPath);
            if (_state.MenuExisted) File.Copy(MenuPath, Path.Combine(operation, "menu.before.json"), false);
            _state.Changes = batch.Items.Where(i => i.Changed).Select(i => new PluginFileChange
            {
                LocalPath = i.Plugin.LocalPath, Index = i.Index, HadDirectory = i.HadDirectory,
                HadMeta = File.Exists(i.Destination + ".meta"), Remove = _state.Kind == PluginOperationKind.Uninstall
            }).ToList();
            _state.Phase = PluginOperationPhase.Committing;
            _state.Message = "提交文件，更新 menu.json...";
            _state.RecoveryRequired = true;
            SaveState();
            EditorApplication.LockReloadAssemblies();
            bool editingStarted = false;
            try
            {
                AssetDatabase.StartAssetEditing();
                editingStarted = true;
                // 下载期间的外部修改在提交边界再次核对，避免覆盖准备阶段之后的编辑。
                var direct = UpmInstaller.ReadDirectDependencies(ProjectRoot);
                if (MenuJsonUtility.ReadProjectMenuJson(ProjectRoot) != batch.ObservedMenuJson)
                    throw new InvalidOperationException("准备期间 menu.json 已修改，请刷新配置后重新执行；插件文件未提交。");
                if (direct.Count != batch.ObservedDirect.Count || batch.ObservedDirect.Any(p => !direct.TryGetValue(p.Key, out string value) || value != p.Value))
                    throw new InvalidOperationException("准备期间项目直接依赖发生变化，请重新执行；插件文件未提交。");
                foreach (var item in batch.Items.Where(i => i.Changed))
                {
                    if (Directory.Exists(item.Destination) != item.HadDirectory || File.Exists(item.Destination)
                        || (item.HadDirectory && PluginInstallFiles.ContentHash(item.Destination, item.BeforeFiles, CancellationToken.None) != item.BeforeHash))
                        throw new InvalidOperationException("准备期间本地文件发生变化，提交已停止: " + item.Plugin.Name);
                }
                foreach (var change in _state.Changes)
                {
                    string destination = PluginInstallFiles.InstallationPath(ProjectRoot, change.LocalPath);
                    string backup = PluginInstallFiles.ContainedPath(operation, "Backup/" + change.Index);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup));
                    if (change.HadDirectory) Directory.Move(destination, backup);
                    if (change.Remove)
                    {
                        if (change.HadMeta) File.Move(destination + ".meta", backup + ".meta");
                    }
                    else
                    {
                        string staged = PluginInstallFiles.ContainedPath(operation, "New/" + change.Index);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        Directory.Move(staged, destination);
                    }
                }
                MenuJsonUtility.WriteProjectMenu(ProjectRoot, MenuJsonUtility.ApplyInstallationData(batch.ObservedMenuJson, batch.Database));
                _database = batch.Database;
                _state.FilesCommitted = true;
                _state.RecoveryRequired = false;
                _state.Phase = PluginOperationPhase.Resolving;
                _state.Message = "文件已提交，等待包解析和资源导入...";
                foreach (var row in _state.Rows)
                    if (batch.Items.Any(i => i.Changed && i.Plugin.LocalPath == row.LocalPath))
                        row.Result = _state.Kind == PluginOperationKind.Uninstall ? "文件已卸载" : "文件已安装";
                SaveState();
                EditorApplication.delayCall += ExOpenSourcePluginManagerWindow.RefreshMenuTree;
            }
            catch
            {
                RollbackFiles();
                throw;
            }
            finally
            {
                try { if (editingStarted) AssetDatabase.StopAssetEditing(); }
                finally { EditorApplication.UnlockReloadAssemblies(); }
            }
            if (_state.Changes.Count > 0) AssetDatabase.Refresh();
        }

        private static void SubmitPackages()
        {
            _state.Phase = PluginOperationPhase.Resolving;
            _state.Message = "正在统一提交 UPM 请求...";
            _state.Progress = 0.9f;
            SaveState();
            try
            {
                var direct = UpmInstaller.ReadDirectDependencies(ProjectRoot);
                string[] remove = _state.UpmRemove.Where(direct.ContainsKey).ToArray();
                if (_state.UpmAdd.Length > 0 || remove.Length > 0)
                    _upmRequest = UpmInstaller.Submit(_state.UpmAdd, remove);
                else if (PackagesMatch())
                    Complete("嵌入包已被 Unity 识别，来源、声明依赖和注册结果已核对。");
                else
                {
                    UpmInstaller.Resolve();
                    WaitForPackages();
                }
            }
            catch (Exception error)
            {
                Fail(PluginOperationPhase.Failed, "文件已提交，UPM 请求提交失败: " + error.Message);
                throw;
            }
        }

        private static void WaitForPackages()
        {
            _waitingForPackages = true;
            _packageWaitStarted = EditorApplication.timeSinceStartup;
        }

        // 只移除旧工具为已提交的嵌入包生成的请求 / 预期，不改项目 manifest 或包文件。
        private static bool NormalizeLegacyEmbeddedRequests()
        {
            var legacySources = new HashSet<string>(StringComparer.Ordinal);
            bool changed = false;
            foreach (var package in _state.ExpectedPackages)
            {
                string source = UpmInstaller.LegacyEmbeddedSource(package.LocalPath);
                legacySources.Add(source);
                if (_state.ExpectedDirectDependencies.TryGetValue(package.Name, out string expected) && expected == source)
                {
                    _state.ExpectedDirectDependencies.Remove(package.Name);
                    changed = true;
                }
            }
            string[] add = _state.UpmAdd.Where(source => !legacySources.Contains(source)).ToArray();
            if (add.Length != _state.UpmAdd.Length)
            {
                _state.UpmAdd = add;
                changed = true;
            }
            return changed;
        }

        private static bool PackagesMatch()
        {
            var direct = UpmInstaller.ReadDirectDependencies(ProjectRoot);
            if (_state.ExpectedDirectDependencies.Any(p => !direct.TryGetValue(p.Key, out string value) || value != p.Value)
                || _state.UpmRemove.Any(direct.ContainsKey)) return false;
            var packages = PackageInfo.GetAllRegisteredPackages();
            foreach (var expected in _state.ExpectedPackages)
            {
                var actual = packages.FirstOrDefault(p => p.name == expected.Name);
                if (actual == null || actual.source != PackageSource.Embedded || actual.version != expected.Version
                    || !string.Equals(Path.GetFullPath(actual.resolvedPath), PluginInstallFiles.InstallationPath(ProjectRoot, expected.LocalPath), StringComparison.OrdinalIgnoreCase)) return false;
                var dependencies = actual.dependencies.ToDictionary(d => d.name, d => d.version);
                if (expected.Dependencies.Count != dependencies.Count
                    || expected.Dependencies.Any(p => !dependencies.TryGetValue(p.Key, out string value) || value != p.Value)
                    || expected.Dependencies.Keys.Any(name => packages.All(p => p.name != name))) return false;
            }
            return _state.ExpectedDirectDependencies.Keys.All(name => packages.Any(p => p.name == name))
                && _state.UpmRemove.All(name => packages.All(p => p.name != name));
        }

        private static void RecoverInterruptedCommit()
        {
            try
            {
                EditorApplication.LockReloadAssemblies();
                bool editingStarted = false;
                try
                {
                    AssetDatabase.StartAssetEditing();
                    editingStarted = true;
                    RollbackFiles();
                }
                finally
                {
                    try { if (editingStarted) AssetDatabase.StopAssetEditing(); }
                    finally { EditorApplication.UnlockReloadAssemblies(); }
                }
            }
            catch (Exception error)
            {
                _state.RecoveryRequired = true;
                Fail(PluginOperationPhase.Failed, "恢复未完成，请保留缓存备份并处理错误: " + error.Message);
                Debug.LogException(error);
                return;
            }
            Fail(PluginOperationPhase.Interrupted, "上次提交被中断，已恢复原插件目录和 menu.json；可以重新执行。");
            AssetDatabase.Refresh();
        }

        private static void RollbackFiles()
        {
            string operation = OperationDirectory(_state.Id);
            foreach (var change in _state.Changes.AsEnumerable().Reverse())
            {
                string destination = PluginInstallFiles.InstallationPath(ProjectRoot, change.LocalPath);
                string backup = PluginInstallFiles.ContainedPath(operation, "Backup/" + change.Index);
                string staged = PluginInstallFiles.ContainedPath(operation, "New/" + change.Index);
                if (Directory.Exists(backup))
                {
                    if (Directory.Exists(destination)) Directory.Delete(destination, true);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    Directory.Move(backup, destination);
                }
                else if (!change.HadDirectory && !change.Remove && !Directory.Exists(staged) && Directory.Exists(destination))
                    Directory.Delete(destination, true);
                if (File.Exists(backup + ".meta"))
                {
                    if (File.Exists(destination + ".meta")) File.Delete(destination + ".meta");
                    File.Move(backup + ".meta", destination + ".meta");
                }
                else if (!change.HadMeta && !change.HadDirectory && !Directory.Exists(staged) && File.Exists(destination + ".meta"))
                    File.Delete(destination + ".meta");
            }
            if (_state.MenuExisted)
            {
                string backup = PluginInstallFiles.ContainedPath(operation, "menu.before.json");
                if (!File.Exists(backup)) throw new FileNotFoundException("缺少本次提交前的 menu.json 临时副本。", backup);
                MenuJsonUtility.WriteProjectMenu(ProjectRoot, Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(backup, System.Text.Encoding.UTF8)));
                _database = MenuJsonUtility.ReadInstallationData(ProjectRoot);
            }
            else
            {
                if (File.Exists(MenuPath)) File.Delete(MenuPath);
                _database = new PluginInstallDatabase();
            }
            _state.FilesCommitted = false;
            _state.RecoveryRequired = false;
            SaveState();
        }

        private static void BeforeReload()
        {
            if (_task != null)
            {
                _cancellation.Cancel();
                _state.Phase = PluginOperationPhase.Interrupted;
                _state.Message = "编辑器退出 / 脚本重载中断了准备阶段，原插件目录未改动。";
                SaveState();
                // Git 取消会终止并等待子进程退出；不与仍在写入的准备工作并发删除。
                try { _task.Wait(5000); }
                catch (AggregateException) { /* 已停止的准备任务错误由中断状态表示。 */ }
                if (!_task.IsCompleted) return; // 启动后清理尚未停止的任务残留。
                _task = null;
                _cancellation.Dispose();
                _cancellation = null;
            }
            ResumeCacheCleanup();
            if (_cacheCleanupTask != null)
            {
                try { _cacheCleanupTask.Wait(5000); }
                catch (AggregateException error)
                {
                    Debug.LogWarning("snchuToolHub 缓存清理失败，下次启动时重试: " + error.GetBaseException().Message);
                }
            }
        }

        private static string OperationDirectory(string id)
        {
            if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("无效任务标识。");
            return PluginInstallFiles.ContainedPath(CacheRoot, "Operations/" + id);
        }

        private static void SaveState()
        {
            bool active = _state.Phase == PluginOperationPhase.Preparing || _state.Phase == PluginOperationPhase.Committing
                || _state.Phase == PluginOperationPhase.Resolving || _state.RecoveryRequired;
            if (!active) return;
            PluginInstallFiles.WriteJson(StatePath, _state);
            _lastStateSave = EditorApplication.timeSinceStartup;
        }

        private static void Fail(PluginOperationPhase phase, string message)
        {
            _state.Phase = phase;
            _state.Message = message;
            foreach (var row in _state.Rows.Where(r => r.Result == "待处理" || r.Result.StartsWith("准备", StringComparison.Ordinal))) row.Result = "未提交";
            SaveState();
            StartCacheCleanup();
        }

        private static void Complete(string message)
        {
            _state.Phase = PluginOperationPhase.Completed;
            _state.Message = message;
            _state.Progress = 1f;
            SaveState();
            StartCacheCleanup();
        }

        private static void ResumeCacheCleanup()
        {
            // 不清理无效状态的未知恢复数据，或延迟回调之前开始的新任务。
            if (_initializationError != null || _state.Phase == PluginOperationPhase.Preparing
                || _state.Phase == PluginOperationPhase.Committing
                || (_state.Phase == PluginOperationPhase.Resolving && !_state.FilesCommitted)) return;
            StartCacheCleanup();
        }

        /// <summary>准备工作停止、提交结束或启动恢复后调用；仅未完成回滚的当前目录需要保留。</summary>
        private static void StartCacheCleanup()
        {
            if (_task != null || IsCleaningCache || !Directory.Exists(PluginInstallFiles.FileSystemPath(CacheRoot))) return;
            // 任务标识已在创建 / 加载状态时验证；保留整个目录供 RollbackFiles 判断提交进度。
            string retained = _state.RecoveryRequired ? Path.Combine(CacheRoot, "Operations", _state.Id) : null;
            _cleanupRetainsState = _state.RecoveryRequired || _state.Phase == PluginOperationPhase.Resolving;
            bool retainState = _cleanupRetainsState;
            _cacheCleanupTask = Task.Run(() => PluginInstallFiles.ClearTaskCache(CacheRoot, retained, retainState));
        }

        private static T Clone<T>(T value) => JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value));

        private class EditorSnapshot
        {
            public List<PluginPackageExpectation> Packages = new List<PluginPackageExpectation>();
        }

        private class PreparedPlugin
        {
            public ExPluginItem Plugin;
            public string Destination;
            public int Index;
            public PluginInstallationRecord Previous;
            public PluginInstallationRecord Next;
            public PluginPackageDefinition Package;
            public GitRepositorySnapshot Repository;
            public string Tree;
            public string BeforeHash;
            public List<string> BeforeFiles;
            public string Staged;
            public string Result;
            public bool Changed;
            public bool NeedsDownload;
            public bool Populated;
            public bool HadDirectory;
            public bool IsPackage => Plugin.LocalPath.StartsWith("Packages/", StringComparison.Ordinal);
        }

        private class PreparedBatch
        {
            public string Error;
            public PluginInstallDatabase Database;
            public Dictionary<string, string> ObservedDirect;
            public string ObservedMenuJson;
            public List<PreparedPlugin> Items = new List<PreparedPlugin>();
            public List<PluginScanEntry> ScanEntries = new List<PluginScanEntry>();
            public List<PluginPackageExpectation> Expected = new List<PluginPackageExpectation>();
            public Dictionary<string, string> Direct = new Dictionary<string, string>();
            public List<string> Add = new List<string>();
            public List<string> Remove = new List<string>();
        }

        private class ProgressMessage
        {
            public string LocalPath;
            public string Message;
            public float Progress;
        }
    }
}
