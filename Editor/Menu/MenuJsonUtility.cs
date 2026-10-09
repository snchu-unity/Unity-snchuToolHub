using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExOpenSource.Editor
{
    /// <summary>菜单文件输入边界。旧格式在这里展开，业务数据不保留菜单级 Git 默认值。</summary>
    public static class MenuJsonUtility
    {
        public static string ProjectMenuPath(string projectRoot)
            => Path.GetFullPath(Path.Combine(projectRoot, ExOpenSourceConstParam.PROJECT_MENU_PATH));

        /// <summary>唯一长期配置为 menu.json。首次读取在编辑器主线程完成旧配置迁移；没有配置时只返回空菜单。</summary>
        public static string ReadProjectMenuJson(string projectRoot)
        {
            string path = ProjectMenuPath(projectRoot);
            if (File.Exists(path))
            {
                RemoveLegacyConfiguration(Path.GetDirectoryName(path));
                return File.ReadAllText(path, System.Text.Encoding.UTF8);
            }
            string legacy = Path.Combine(Path.GetDirectoryName(path), "Menus", "menu_ex.json");
            if (File.Exists(legacy))
            {
                string json = File.ReadAllText(legacy, System.Text.Encoding.UTF8);
                Parse(json, out string converted);
                var document = JObject.Parse(converted ?? json);
                string records = Path.Combine(Path.GetDirectoryName(path), "GitPluginInstallRecords.asset");
                var database = File.Exists(records) ? PluginInstallFiles.ReadJson<PluginInstallDatabase>(records) : new PluginInstallDatabase();
                foreach (JObject plugin in (JArray)document["Plugins"])
                {
                    string target = (string)plugin["LocalPath"];
                    var record = database.installedPlugins.FirstOrDefault(item => PluginInstallFiles.SamePath(item.installPath, target)
                        && item.repoUrl == "https://github.com/" + (string)plugin["GitURL_Username"] + "/" + (string)plugin["GitURL_RepoName"] + ".git"
                        && item.relativePath == (string)plugin["GitURL_Path"] && item.targetBranch == (string)plugin["GitURL_Branch"]);
                    // 复制到新项目时，不把没有真实文件的旧项目管理信息带入新配置。
                    if (record != null && PluginInstallFiles.HasContent(Path.Combine(projectRoot, target)))
                        plugin["Installation"] = JObject.FromObject(record);
                    else plugin.Remove("Installation");
                }
                WriteProjectMenu(projectRoot, document);
                RemoveLegacyConfiguration(Path.GetDirectoryName(path));
                return File.ReadAllText(path, System.Text.Encoding.UTF8);
            }
            return JsonConvert.SerializeObject(new ExMenuConfig
            {
                Name = "项目插件目录", Version = "1.0.0", Owner = "本项目", Intro = "项目插件配置"
            }, Formatting.Indented) + Environment.NewLine;
        }

        public static void WriteProjectMenu(string projectRoot, JObject document)
        {
            Parse(document.ToString(Formatting.None), out _);
            PluginInstallFiles.WriteJson(ProjectMenuPath(projectRoot), document);
        }

        // 确认旧提交没有待恢复文件后，仅删除旧版自身已知的配置文件，不保留迁移备份。
        private static void RemoveLegacyConfiguration(string configRoot)
        {
            string statePath = Path.Combine(configRoot, "PluginOperationState.json");
            if (File.Exists(statePath))
            {
                var state = PluginInstallFiles.ReadJson<PluginOperationState>(statePath);
                if (state.RecoveryRequired || state.Phase == PluginOperationPhase.Committing)
                    throw new InvalidOperationException("旧版工具尚有未恢复的文件提交，请先完成恢复，再迁移为 menu.json。");
            }
            // 旧版内置令牌副本直接清理；新版凭据必须由开发者选择项目外的 txt。
            foreach (string relative in new[] { "Menus/menu_ex.json", "GitPluginInstallRecords.asset",
                "ExOpenSourceSetting.asset", "PluginOperationState.json", "github-token.txt" })
            {
                string file = Path.Combine(configRoot, relative);
                if (File.Exists(file)) File.Delete(file);
            }
            string menus = Path.Combine(configRoot, "Menus");
            if (Directory.Exists(menus) && !Directory.EnumerateFileSystemEntries(menus).Any()) Directory.Delete(menus);
        }

        /// <summary>管理信息只来自 menu.json；没有实际文件的条目不取得管理权。</summary>
        public static PluginInstallDatabase ReadInstallationData(string projectRoot)
        {
            var menu = Parse(ReadProjectMenuJson(projectRoot), out _);
            var database = new PluginInstallDatabase();
            foreach (var plugin in menu.Plugins)
                if (plugin.Installation != null && PluginInstallFiles.SamePath(plugin.Installation.installPath, plugin.LocalPath)
                    && PluginInstallFiles.HasContent(Path.Combine(projectRoot, plugin.LocalPath)))
                    database.installedPlugins.Add(plugin.Installation);
            return database;
        }

        /// <summary>将本次安装 / 接管结果写回同一份配置，不生成独立安装记录。</summary>
        public static JObject ApplyInstallationData(string json, PluginInstallDatabase database)
        {
            var document = JObject.Parse(json);
            foreach (JObject plugin in (JArray)document["Plugins"])
            {
                var record = database.installedPlugins.FirstOrDefault(item => PluginInstallFiles.SamePath(item.installPath, (string)plugin["LocalPath"]));
                if (record == null) plugin.Remove("Installation");
                else plugin["Installation"] = JObject.FromObject(record);
            }
            return document;
        }

        /// <summary>解析及校验菜单；仅旧文件转换时返回 convertedJson，其他情况为 null。</summary>
        public static ExMenuConfig Parse(string json, out string convertedJson)
        {
            var document = JObject.Parse(json);
            if (!(document["Plugins"] is JArray plugins))
                throw new InvalidDataException("菜单必须包含 Plugins 数组。");

            bool legacy = document.Property("DefaultGit_UserName") != null
                || document.Property("DefaultGit_RepoName") != null
                || document.Property("DefaultGit_Branch") != null;
            if (legacy)
            {
                foreach (var token in plugins)
                {
                    if (!(token is JObject plugin)) throw new InvalidDataException("插件条目必须是 JSON 对象。");
                    ExpandLegacySource(plugin, document, "GitURL_Username", "DefaultGit_UserName");
                    ExpandLegacySource(plugin, document, "GitURL_RepoName", "DefaultGit_RepoName");
                    ExpandLegacySource(plugin, document, "GitURL_Branch", "DefaultGit_Branch");
                    // 可选元数据在旧格式中可能省略，展开为统一条目结构。
                    foreach (var field in new[] { "Version", "Author", "Intro", "GitURL_Path", "IntroductionURL" })
                        if (plugin[field] == null || plugin[field].Type == JTokenType.Null) plugin[field] = "";
                    foreach (var field in new[] { "Tags", "Dependencies" })
                        if (plugin[field] == null || plugin[field].Type == JTokenType.Null) plugin[field] = new JArray();
                }
                document.Remove("DefaultGit_UserName");
                document.Remove("DefaultGit_RepoName");
                document.Remove("DefaultGit_Branch");
            }

            foreach (var token in plugins)
            {
                if (!(token is JObject plugin)) throw new InvalidDataException("插件条目必须是 JSON 对象。");
                foreach (var field in new[] { "GitURL_Username", "GitURL_RepoName", "GitURL_Branch" })
                    if (plugin[field] == null || plugin[field].Type != JTokenType.String)
                        throw new InvalidDataException("每个插件都必须独立填写字符串字段 " + field + "。");
            }

            var config = JsonConvert.DeserializeObject<ExMenuConfig>(document.ToString(Formatting.None));
            if (config == null || string.IsNullOrWhiteSpace(config.Name) || config.Plugins == null)
                throw new InvalidDataException("菜单必须包含 Name 和 Plugins。");
            foreach (var plugin in config.Plugins)
            {
                if (plugin == null || string.IsNullOrWhiteSpace(plugin.Name)
                    || string.IsNullOrWhiteSpace(plugin.MenuPath) || string.IsNullOrWhiteSpace(plugin.LocalPath))
                    throw new InvalidDataException("插件必须包含 Name、MenuPath 和 LocalPath。");
                ValidateSource(plugin);
                plugin.LocalPath = PluginInstallFiles.NormalizeRelativePath(plugin.LocalPath, false);
                PluginInstallFiles.InstallationPath(ExOpenSourceConstParam.PROJECT_ROOT, plugin.LocalPath);
                plugin.GitURL_Path = PluginInstallFiles.NormalizeRelativePath(plugin.GitURL_Path, true);
            }

            convertedJson = legacy ? document.ToString(Formatting.Indented) + Environment.NewLine : null;
            return config;
        }

        // 只在接收旧 JSON 时执行一次；不补猜测值，不参与安装时的来源选择。
        private static void ExpandLegacySource(JObject plugin, JObject menu, string field, string legacyField)
        {
            var source = plugin[field];
            if (source != null && source.Type != JTokenType.Null
                && !(source.Type == JTokenType.String && (string)source == "")) return;
            var inherited = menu[legacyField];
            if (inherited == null || inherited.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)inherited))
            {
                string name = (string)plugin["Name"];
                throw new InvalidDataException($"插件“{name}”缺少 {field}，旧菜单也没有可迁移的值。");
            }
            plugin[field] = (string)inherited;
        }

        public static void ValidateSource(ExPluginItem plugin)
        {
            ValidateGitArgument(plugin.GitURL_Username, $"插件“{plugin.Name}”的仓库所有者", false);
            ValidateGitArgument(plugin.GitURL_RepoName, $"插件“{plugin.Name}”的仓库名称", false);
            ValidateGitArgument(plugin.GitURL_Branch, $"插件“{plugin.Name}”的分支", true);
        }

        private static void ValidateGitArgument(string value, string label, bool allowSlash)
        {
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("-", StringComparison.Ordinal)
                || Array.Exists(value.ToCharArray(), c => char.IsWhiteSpace(c) || char.IsControl(c)
                    || c == '"' || c == '\'' || c == '\\' || (!allowSlash && "/:@?#".IndexOf(c) >= 0)))
                throw new InvalidDataException(label + "为插件必填项，不能包含空白、引号或无效路径字符。");
        }
    }
}
