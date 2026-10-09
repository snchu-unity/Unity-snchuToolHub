using System;
using System.Collections.Generic;

namespace ExOpenSource.Editor
{
    public enum PluginOperationKind { Install, CheckUpdates, Update, Reinstall, Uninstall, Adopt, Scan }
    public enum PluginOperationPhase { Idle, Preparing, Committing, Resolving, Completed, Failed, Cancelled, Interrupted }

    public enum PluginInstallationKind { NotInstalled, Managed, External, Occupied, NeedsAdoption, Missing, SourceMismatch, Unavailable, UnmanagedElsewhere }

    /// <summary>项目中已确认的安装情况及当前条目的管理权；安装时再进行实时重复检测。</summary>
    public class PluginInstallationInfo
    {
        public PluginInstallationKind Kind;
        public string Label;
        public string ActualPath;
        // 其他位置已存在时提供；ActualPath 始终保留配置目标，不把外部文件当作管理目录。
        public string ExistingPath;
        public string Message;
        public bool CanAdopt;

        public bool IsInstalled => Kind == PluginInstallationKind.Managed || Kind == PluginInstallationKind.External
            || Kind == PluginInstallationKind.NeedsAdoption || Kind == PluginInstallationKind.UnmanagedElsewhere;
    }

    /// <summary>一次安装准备中的项目文件索引；不在 GUI 绘制时扫描项目。</summary>
    public class PluginProjectInventory
    {
        public Dictionary<string, List<string>> Packages = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        public Dictionary<string, List<string>> Assemblies = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        public Dictionary<string, List<string>> AssetGuids = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> Scripts = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        // null 表示检查全部脚本；安装准备阶段提供本批需要比较的文件名集合，空集合不计算脚本指纹。
        public HashSet<string> ScriptNames;
    }

    [Serializable]
    public class PluginInstallDatabase
    {
        public int version = 2;
        public List<PluginInstallationRecord> installedPlugins = new List<PluginInstallationRecord>();
    }

    [Serializable]
    public class PluginInstallationRecord
    {
        // 保留原安装记录的来源字段，旧记录没有校验信息时需要显式接管。
        public string name;
        public string repoUrl;
        public string targetBranch;
        public string relativePath;
        public string installPath;
        public string commit;
        public string treeHash;
        public string contentHash;
        public string packageName;
        public string packageVersion;
        public List<string> sourceFiles = new List<string>();
    }

    [Serializable]
    public class PluginOperationRow
    {
        public string Name;
        public string LocalPath;
        public string Result = "待处理";
        public string Commit;
    }

    [Serializable]
    public class PluginFileChange
    {
        public string LocalPath;
        public int Index;
        public bool HadDirectory;
        public bool HadMeta;
        public bool Remove;
    }

    [Serializable]
    public class PluginScanEntry
    {
        public string SourcePath;
        public string Error;
        public ExPluginItem Plugin;
    }

    [Serializable]
    public class PluginPackageExpectation
    {
        public string Name;
        public string LocalPath;
        public string Version;
        public bool IsEmbedded;
        public Dictionary<string, string> Dependencies = new Dictionary<string, string>();
    }

    [Serializable]
    public class PluginOperationState
    {
        public string Id;
        public PluginOperationKind Kind;
        public PluginOperationPhase Phase;
        public string Message;
        public float Progress;
        public bool FilesCommitted;
        public bool RecoveryRequired;
        public bool RecordsExisted;
        public List<PluginOperationRow> Rows = new List<PluginOperationRow>();
        public List<PluginFileChange> Changes = new List<PluginFileChange>();
        public List<PluginScanEntry> ScanEntries = new List<PluginScanEntry>();
        public List<PluginPackageExpectation> ExpectedPackages = new List<PluginPackageExpectation>();
        public Dictionary<string, string> ExpectedDirectDependencies = new Dictionary<string, string>();
        public string[] UpmAdd = Array.Empty<string>();
        public string[] UpmRemove = Array.Empty<string>();
    }

    public class GitRepositorySnapshot
    {
        public string Directory;
        public string Url;
        public string Branch;
        public string Commit;
    }

    public class PluginPackageDefinition
    {
        public string Name;
        public string Version;
        public string DisplayName;
        public string Description;
        public string Author;
        public Dictionary<string, string> Dependencies = new Dictionary<string, string>();
    }
}
