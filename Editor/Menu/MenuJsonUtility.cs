using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ExOpenSource.Editor
{
    /// <summary>菜单文件输入边界。旧格式在这里展开，业务数据不保留菜单级 Git 默认值。</summary>
    public static class MenuJsonUtility
    {
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

            var config = JsonUtility.FromJson<ExMenuConfig>(document.ToString(Formatting.None));
            if (config == null || string.IsNullOrWhiteSpace(config.Name) || config.Plugins == null)
                throw new InvalidDataException("菜单必须包含 Name 和 Plugins。");
            foreach (var plugin in config.Plugins)
            {
                if (plugin == null || string.IsNullOrWhiteSpace(plugin.Name)
                    || string.IsNullOrWhiteSpace(plugin.MenuPath) || string.IsNullOrWhiteSpace(plugin.LocalPath))
                    throw new InvalidDataException("插件必须包含 Name、MenuPath 和 LocalPath。");
                ValidateSource(plugin);
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
