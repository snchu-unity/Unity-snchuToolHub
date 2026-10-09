using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExOpenSource.Editor
{
    /// <summary>纯文件操作；后台准备阶段不调用 Unity API。</summary>
    public static class PluginInstallFiles
    {
        /// <summary>将已规范化的绝对路径转换为 System.IO 路径；记录和 Unity API 仍使用普通路径。</summary>
        public static string FileSystemPath(string absolutePath)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT
                || absolutePath.StartsWith(@"\\?\", StringComparison.Ordinal)) return absolutePath;
            absolutePath = absolutePath.Replace('/', '\\');
            return absolutePath.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + absolutePath.Substring(2) : @"\\?\" + absolutePath;
        }

        /// <summary>将有效的本地路径转换为项目相对显示；项目外路径保留完整位置。返回值不用于文件操作。</summary>
        public static string ProjectRelativePath(string projectRoot, string path)
        {
            string root = Path.GetFullPath(projectRoot).Replace('\\', '/').TrimEnd('/');
            string fullPath = Path.GetFullPath(Path.Combine(projectRoot, path)).Replace('\\', '/').TrimEnd('/');
            if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)) return ".";
            string prefix = root + "/";
            return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? fullPath.Substring(prefix.Length) : fullPath;
        }

        public static string NormalizeRelativePath(string value, bool allowEmpty)
        {
            string path = (value ?? "").Trim().Replace('\\', '/').TrimEnd('/');
            if (allowEmpty && path.Length == 0) return path;
            if (path.Length == 0 || Path.IsPathRooted(path)
                || path.IndexOfAny(new[] { ':', '"', '<', '>', '|', '?', '*' }) >= 0
                || path.Any(char.IsControl) || path.StartsWith("-", StringComparison.Ordinal)
                || path.Split('/').Any(p => p.Length == 0 || p == "." || p == ".."
                    || p.EndsWith(".", StringComparison.Ordinal) || p.EndsWith(" ", StringComparison.Ordinal)
                    || Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase)))
                throw new InvalidDataException("无效相对目录: " + value);
            return path;
        }

        public static string InstallationPath(string projectRoot, string relativePath)
        {
            string path = NormalizeRelativePath(relativePath, false);
            if (!path.StartsWith("Assets/", StringComparison.Ordinal)
                && !path.StartsWith("Packages/", StringComparison.Ordinal))
                throw new InvalidDataException("安装目录必须在 Assets 或 Packages 下: " + path);
            if (path.StartsWith("Packages/", StringComparison.Ordinal) && path.Split('/').Length != 2)
                throw new InvalidDataException("嵌入包必须直接放在 Packages 的一个子目录中: " + path);
            if (path == "Packages/manifest.json" || path == "Packages/packages-lock.json")
                throw new InvalidDataException("安装目录不能使用项目包配置文件路径。");
            string fullPath = ContainedPath(projectRoot, path);
            string tool = Path.GetFullPath(Path.Combine(projectRoot, ExOpenSourceConstParam.TOOL_ROOT));
            if (Overlaps(fullPath, tool)) throw new InvalidDataException("安装目录不能覆盖插件管理器: " + path);
            return fullPath;
        }

        public static string ContainedPath(string root, string relativePath)
        {
            string absoluteRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(Path.Combine(absoluteRoot, relativePath));
            if (!full.StartsWith(absoluteRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("目录越界: " + relativePath);
            string cursor = full;
            while (cursor != null && cursor.Length >= absoluteRoot.Length)
            {
                string fileSystemCursor = FileSystemPath(cursor);
                if ((File.Exists(fileSystemCursor) || Directory.Exists(fileSystemCursor))
                    && (File.GetAttributes(fileSystemCursor) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("不支持符号链接或目录联接: " + cursor);
                cursor = Path.GetDirectoryName(cursor);
            }
            return full;
        }

        public static bool Overlaps(string first, string second)
        {
            return string.Equals(first, second, StringComparison.OrdinalIgnoreCase)
                || first.StartsWith(second + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || second.StartsWith(first + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        public static bool SamePath(string first, string second)
            => string.Equals(first.Replace('\\', '/').TrimEnd('/'), second.Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

        public static bool HasContent(string directory)
        {
            string path = FileSystemPath(directory);
            return Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
        }

        /// <summary>主线程已提供包快照；只读索引 Assets、Packages 和已注册包实际目录。
        /// scriptNames 可选，null 检查全部脚本，空集合跳过脚本指纹；正式安装提供本批必要的文件名。</summary>
        public static PluginProjectInventory InspectProject(string projectRoot, IEnumerable<PluginPackageExpectation> registeredPackages,
            CancellationToken cancellation, HashSet<string> scriptNames = null)
        {
            var inventory = new PluginProjectInventory { ScriptNames = scriptNames };
            var roots = new List<string> { Path.Combine(projectRoot, "Assets"), Path.Combine(projectRoot, "Packages") };
            foreach (var package in registeredPackages)
            {
                AddIdentity(inventory.Packages, package.Name, package.LocalPath);
                roots.Add(package.LocalPath);
            }
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(FileSystemPath(root))) continue; // 内置包的注册信息不一定对应可枚举目录。
                foreach (string file in ImportedFiles(root, visited, cancellation))
                    IndexFile(inventory, file);
            }
            return inventory;
        }

        /// <summary>只有缺少根包清单和导入范围 asmdef 的插件，才需要原样脚本比较。</summary>
        public static HashSet<string> FingerprintScriptNames(string sourceDirectory, PluginPackageDefinition package, CancellationToken cancellation)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (package != null) return names;
            var files = ImportedFiles(sourceDirectory, new HashSet<string>(StringComparer.OrdinalIgnoreCase), cancellation).ToList();
            if (!files.Any(file => file.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase)))
                names.UnionWith(files.Where(IsFingerprintScript).Select(Path.GetFileName));
            return names;
        }

        /// <summary>检查冲突并将本批文件加入索引。目标目录自身排除；无冲突时才登记本批身份。</summary>
        public static bool TryFindConflict(PluginProjectInventory inventory, string sourceDirectory, string destination,
            PluginPackageDefinition package, CancellationToken cancellation, out string conflict)
        {
            var files = ImportedFiles(sourceDirectory, new HashSet<string>(StringComparer.OrdinalIgnoreCase), cancellation).ToList();
            if (package != null && FindIdentityConflict(inventory.Packages, package.Name, destination, "包名", out conflict)) return true;
            bool compareScripts = package == null && !files.Any(file => file.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase));
            foreach (string file in files)
            {
                cancellation.ThrowIfCancellationRequested();
                if (file.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase))
                {
                    string name = ReadAssemblyDefinitionName(file);
                    if (FindIdentityConflict(inventory.Assemblies, name, destination, "程序集", out conflict)) return true;
                }
                else if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    string name = ManagedAssemblyName(file);
                    if (name != null && FindIdentityConflict(inventory.Assemblies, name, destination, "DLL 程序集", out conflict)) return true;
                }
                else if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    string guid = AssetGuid(file);
                    if (guid != null && FindIdentityConflict(inventory.AssetGuids, guid, destination, "资源 GUID", out conflict)) return true;
                }
                else if (compareScripts && IsFingerprintScript(file))
                {
                    string fingerprint = ScriptFingerprint(file);
                    if (FindIdentityConflict(inventory.Scripts, fingerprint, destination, "原样重复的脚本 " + Path.GetFileName(file), out conflict)) return true;
                }
            }
            // 同一批两个插件之间也必须检查；以安装后的路径登记，不使用暂存目录作为冲突位置。
            if (package != null) AddIdentity(inventory.Packages, package.Name, destination);
            foreach (string file in files)
            {
                string installedPath = Path.Combine(destination, file.Substring(sourceDirectory.Length + 1));
                IndexFile(inventory, file, installedPath);
            }
            conflict = null;
            return false;
        }

        private static IEnumerable<string> ImportedFiles(string root, HashSet<string> visited, CancellationToken cancellation)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                string directory = pending.Pop();
                if (!visited.Add(directory)) continue;
                foreach (var entry in new DirectoryInfo(FileSystemPath(directory)).EnumerateFileSystemInfos())
                {
                    string name = entry.Name;
                    string assetName = name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 5) : name;
                    if (assetName.StartsWith(".", StringComparison.Ordinal) || assetName.EndsWith("~", StringComparison.Ordinal)) continue;
                    string path = Path.Combine(directory, name);
                    var attributes = entry.Attributes;
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("重复检测范围包含符号链接 / 目录联接，无法安全枚举: " + path);
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                    else yield return path;
                }
            }
        }

        private static void IndexFile(PluginProjectInventory inventory, string file, string identityPath = null)
        {
            string location = identityPath ?? file; // 只在同批暂存文件映射到目标位置时提供 identityPath。
            if (Path.GetFileName(file) == "package.json")
            {
                var json = JObject.Parse(File.ReadAllText(FileSystemPath(file), Encoding.UTF8));
                string name = (string)json["name"];
                if (!string.IsNullOrWhiteSpace(name)) AddIdentity(inventory.Packages, name, Path.GetDirectoryName(location));
            }
            else if (file.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase))
                AddIdentity(inventory.Assemblies, ReadAssemblyDefinitionName(file), location);
            else if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                string name = ManagedAssemblyName(file);
                if (name != null) AddIdentity(inventory.Assemblies, name, location);
            }
            else if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                string guid = AssetGuid(file);
                if (guid != null) AddIdentity(inventory.AssetGuids, guid, location.Substring(0, location.Length - 5));
            }
            else if ((inventory.ScriptNames == null || inventory.ScriptNames.Contains(Path.GetFileName(file))) && IsFingerprintScript(file))
                AddIdentity(inventory.Scripts, ScriptFingerprint(file), location);
        }

        private static void AddIdentity(Dictionary<string, List<string>> index, string identity, string location)
        {
            if (!index.TryGetValue(identity, out var paths)) index[identity] = paths = new List<string>();
            if (!paths.Any(path => SamePath(path, location))) paths.Add(location);
        }

        private static bool FindIdentityConflict(Dictionary<string, List<string>> index, string identity, string destination,
            string kind, out string conflict)
        {
            if (index.TryGetValue(identity, out var paths))
            {
                string path = paths.FirstOrDefault(item => !SamePath(item, destination)
                    && !item.Replace('\\', '/').StartsWith(destination.Replace('\\', '/').TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
                if (path != null)
                {
                    conflict = kind + "已存在于其他位置: " + path + "。安装已中止，请由开发者处理原插件后重试；目标文件未提交。";
                    return true;
                }
            }
            conflict = null;
            return false;
        }

        private static string ReadAssemblyDefinitionName(string file)
        {
            var json = JObject.Parse(File.ReadAllText(FileSystemPath(file), Encoding.UTF8));
            string name = (string)json["name"];
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("asmdef 缺少名称: " + file);
            return name;
        }

        // 原生 DLL 没有 .NET 程序集身份，仍可通过资源 GUID 检测重复。
        private static string ManagedAssemblyName(string file)
        {
            if (!HasManagedAssemblyHeader(file)) return null;
            return AssemblyName.GetAssemblyName(FileSystemPath(file)).Name;
        }

        // 先读取 PE 的 CLR 数据目录，避免 Mono 尝试读取原生 DLL 时向 Console 输出加载错误。
        private static bool HasManagedAssemblyHeader(string file)
        {
            using (var stream = File.OpenRead(FileSystemPath(file)))
            using (var reader = new BinaryReader(stream))
            {
                if (stream.Length < 64 || reader.ReadUInt16() != 0x5a4d) return false;
                stream.Position = 0x3c;
                uint header = reader.ReadUInt32();
                if (header > stream.Length - 24) return false;
                stream.Position = header;
                if (reader.ReadUInt32() != 0x00004550) return false;
                stream.Position = header + 20;
                ushort optionalSize = reader.ReadUInt16();
                long optional = header + 24L;
                if (optionalSize < 2 || optional + optionalSize > stream.Length) return false;
                stream.Position = optional;
                ushort magic = reader.ReadUInt16();
                int directoryOffset = magic == 0x10b ? 96 : magic == 0x20b ? 112 : 0;
                // CLR Runtime Header 为第 15 个数据目录；PE32 / PE32+ 的目录起点不同。
                if (directoryOffset == 0 || optionalSize < directoryOffset + 15 * 8) return false;
                stream.Position = optional + directoryOffset - 4;
                if (reader.ReadUInt32() < 15) return false;
                stream.Position = optional + directoryOffset + 14 * 8;
                return reader.ReadUInt32() != 0 && reader.ReadUInt32() >= 72;
            }
        }

        // GUID 和脚本指纹用于补充识别；没有这些稳定标识的普通资源不能仅凭名称判定插件身份。
        private static string AssetGuid(string file)
        {
            var match = Regex.Match(File.ReadAllText(FileSystemPath(file), Encoding.UTF8), @"(?m)^guid:[ \t]*([0-9a-fA-F]{32})[ \t]*\r?$");
            return match.Success ? match.Groups[1].Value : null;
        }

        private static bool IsFingerprintScript(string file)
            => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase)
                && new FileInfo(FileSystemPath(file)).Length > 0;

        private static string ScriptFingerprint(string file)
        {
            using (var hash = SHA256.Create())
            using (var stream = File.OpenRead(FileSystemPath(file)))
                return Path.GetFileName(file) + ":" + Hex(hash.ComputeHash(stream));
        }

        public static List<string> ListFiles(string root)
        {
            var result = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                foreach (string entry in Directory.GetFileSystemEntries(FileSystemPath(directory)))
                {
                    string child = Path.Combine(directory, Path.GetFileName(entry));
                    var attributes = File.GetAttributes(FileSystemPath(child));
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("不支持符号链接: " + child);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (Path.GetFileName(child) != ".git") pending.Push(child);
                    }
                    else result.Add(child.Substring(root.Length + 1).Replace('\\', '/'));
                }
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        public static void CopyDirectory(string source, string destination, CancellationToken cancellation)
        {
            Directory.CreateDirectory(FileSystemPath(destination));
            foreach (string relative in ListFiles(source))
            {
                cancellation.ThrowIfCancellationRequested();
                string target = Path.Combine(destination, relative);
                Directory.CreateDirectory(FileSystemPath(Path.GetDirectoryName(target)));
                File.Copy(FileSystemPath(Path.Combine(source, relative)), FileSystemPath(target), false);
            }
        }

        // 没有上游 .meta 的文件继续使用 Unity 已生成的元数据；已经删除的文件不保留孤立 .meta。
        public static void PreserveGeneratedMeta(string installed, string staged, List<string> sourceFiles)
        {
            var upstream = new HashSet<string>(sourceFiles, StringComparer.OrdinalIgnoreCase);
            foreach (string relative in ListFiles(installed))
            {
                if (!relative.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) || upstream.Contains(relative)) continue;
                string target = Path.Combine(staged, relative);
                string asset = target.Substring(0, target.Length - 5);
                if (!File.Exists(FileSystemPath(asset)) && !Directory.Exists(FileSystemPath(asset))) continue;
                Directory.CreateDirectory(FileSystemPath(Path.GetDirectoryName(target)));
                File.Copy(FileSystemPath(Path.Combine(installed, relative)), FileSystemPath(target), false);
            }
        }

        public static string ContentHash(string root, List<string> sourceFiles, CancellationToken cancellation)
        {
            var tracked = new HashSet<string>(sourceFiles, StringComparer.OrdinalIgnoreCase);
            var inventory = new StringBuilder();
            using (var hash = SHA256.Create())
            {
                foreach (string relative in ListFiles(root))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (relative.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) && !tracked.Contains(relative)) continue;
                    using (var stream = File.OpenRead(FileSystemPath(Path.Combine(root, relative))))
                        inventory.Append(relative).Append('\0').Append(Hex(hash.ComputeHash(stream))).Append('\n');
                }
                return Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(inventory.ToString())));
            }
        }

        public static string HashText(string value)
        {
            using (var hash = SHA256.Create()) return Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(value)));
        }

        private static string Hex(byte[] value) => BitConverter.ToString(value).Replace("-", "").ToLowerInvariant();

        public static PluginPackageDefinition ReadPackage(string directory, bool required)
        {
            string path = Path.Combine(directory, "package.json");
            if (!File.Exists(FileSystemPath(path)))
            {
                if (required) throw new InvalidDataException("安装到 Packages 的来源必须包含根 package.json: " + directory);
                return null;
            }
            return ParsePackage(File.ReadAllText(FileSystemPath(path), Encoding.UTF8), path);
        }

        /// <summary>本地清单与远端依赖预检使用同一套包配置校验。</summary>
        public static PluginPackageDefinition ParsePackage(string text, string path)
        {
            var json = JObject.Parse(text);
            string name = PackageText(json, "name", false);
            string version = PackageText(json, "version", false);
            if (string.IsNullOrWhiteSpace(name) || !Regex.IsMatch(name, @"^[a-z0-9][a-z0-9._-]*$")
                || string.IsNullOrWhiteSpace(version))
                throw new InvalidDataException("package.json 必须包含有效的 name 和 version: " + path);
            string displayName = PackageText(json, "displayName", true);
            var result = new PluginPackageDefinition
            {
                Name = name, Version = version,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? name : displayName,
                Description = PackageText(json, "description", true) ?? "",
                Author = json["author"] is JObject author ? PackageText(author, "name", true) ?? "" : PackageText(json, "author", true) ?? ""
            };
            if (json["dependencies"] != null && !(json["dependencies"] is JObject))
                throw new InvalidDataException("package.json 的 dependencies 必须是对象: " + path);
            if (json["dependencies"] is JObject dependencies)
                foreach (var property in dependencies.Properties())
                {
                    if (property.Value.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)property.Value))
                        throw new InvalidDataException("无效包依赖: " + property.Name);
                    result.Dependencies.Add(property.Name, (string)property.Value);
                }
            return result;
        }

        // 元数据可选文本允许省略；name/version 是必填字符串。
        private static string PackageText(JObject json, string field, bool optional)
        {
            var value = json[field];
            if (optional && (value == null || value.Type == JTokenType.Null)) return null;
            if (value == null || value.Type != JTokenType.String)
                throw new InvalidDataException("package.json 的 " + field + " 必须是字符串。");
            return (string)value;
        }

        public static void WriteJson(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".writing";
            try
            {
                File.WriteAllText(temporary, JsonConvert.SerializeObject(value, Formatting.Indented) + Environment.NewLine,
                    new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public static T ReadJson<T>(string path) => JsonConvert.DeserializeObject<T>(File.ReadAllText(path, Encoding.UTF8));

        /// <summary>工作线程已停止后清理整个 Cache~。retainedOperationDirectory 可为 null；
        /// 仅回滚未完成时传入当前 Operations 子目录，完整保留其恢复数据。</summary>
        public static void ClearTaskCache(string cacheRoot, string retainedOperationDirectory, bool retainTaskState = false)
        {
            string fullRoot = Path.GetFullPath(cacheRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(Path.GetFileName(fullRoot), "Cache~", StringComparison.Ordinal))
                throw new InvalidDataException("任务缓存清理只能操作工具的 Cache~ 目录。");
            string parent = Path.GetDirectoryName(fullRoot);
            ContainedPath(parent, "Cache~");
            string operations = Path.Combine(fullRoot, "Operations");
            string retained = null;
            if (retainedOperationDirectory != null)
            {
                retained = Path.GetFullPath(retainedOperationDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!SamePath(Path.GetDirectoryName(retained), operations))
                    throw new InvalidDataException("恢复数据必须位于 Cache~/Operations 的直接子目录。");
                ContainedPath(fullRoot, retained.Substring(fullRoot.Length + 1));
            }
            if (!Directory.Exists(FileSystemPath(fullRoot))) return;
            if (retained == null && !retainTaskState)
            {
                DeleteCacheDirectory(parent, fullRoot);
                return;
            }

            foreach (string entry in Directory.GetFileSystemEntries(FileSystemPath(fullRoot)))
            {
                string child = Path.Combine(fullRoot, Path.GetFileName(entry));
                if (retainTaskState && SamePath(child, Path.Combine(fullRoot, "OperationState.json"))) continue;
                if (!SamePath(child, operations))
                {
                    DeleteCacheEntry(fullRoot, child);
                    continue;
                }
                foreach (string operationEntry in Directory.GetFileSystemEntries(FileSystemPath(operations)))
                {
                    string operation = Path.Combine(operations, Path.GetFileName(operationEntry));
                    if (retained == null || !SamePath(operation, retained)) DeleteCacheEntry(fullRoot, operation);
                }
                if (!Directory.EnumerateFileSystemEntries(FileSystemPath(operations)).Any())
                    DeleteCacheDirectory(fullRoot, operations);
            }
            if (!Directory.EnumerateFileSystemEntries(FileSystemPath(fullRoot)).Any())
                DeleteCacheDirectory(parent, fullRoot);
        }

        private static void DeleteCacheEntry(string cacheRoot, string entry)
        {
            string checkedPath = ContainedPath(cacheRoot, entry.Substring(cacheRoot.Length + 1));
            string fileSystemPath = FileSystemPath(checkedPath);
            var attributes = File.GetAttributes(fileSystemPath);
            if ((attributes & FileAttributes.Directory) != 0)
                DeleteCacheDirectory(cacheRoot, checkedPath);
            else
            {
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(fileSystemPath, attributes & ~FileAttributes.ReadOnly);
                File.Delete(fileSystemPath);
            }
        }

        public static void DeleteCacheDirectory(string cacheRoot, string directory)
        {
            string fullRoot = Path.GetFullPath(cacheRoot).TrimEnd(Path.DirectorySeparatorChar);
            string fullDirectory = Path.GetFullPath(directory);
            if (!fullDirectory.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("缓存清理路径越界。");
            string checkedPath = ContainedPath(fullRoot, fullDirectory.Substring(fullRoot.Length + 1));
            string fileSystemPath = FileSystemPath(checkedPath);
            if (!Directory.Exists(fileSystemPath)) return;

            // Git 对象文件可能只读；先检查整个子树，拒绝目录联接和符号链接，再解除只读。
            var pending = new Stack<string>();
            var readOnlyEntries = new List<string>();
            pending.Push(fileSystemPath);
            if ((File.GetAttributes(fileSystemPath) & FileAttributes.ReadOnly) != 0)
                readOnlyEntries.Add(fileSystemPath);
            while (pending.Count > 0)
            {
                foreach (string entry in Directory.GetFileSystemEntries(pending.Pop()))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("缓存清理不支持符号链接或目录联接: " + entry);
                    if ((attributes & FileAttributes.ReadOnly) != 0) readOnlyEntries.Add(entry);
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                }
            }
            foreach (string entry in readOnlyEntries)
                File.SetAttributes(entry, File.GetAttributes(entry) & ~FileAttributes.ReadOnly);
            Directory.Delete(fileSystemPath, true);
        }
    }
}
