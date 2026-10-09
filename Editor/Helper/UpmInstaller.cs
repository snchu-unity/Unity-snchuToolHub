using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;

namespace ExOpenSource.Editor
{
    /// <summary>合并外部依赖并提交 UPM 请求；嵌入包由解析发现，不直接改写项目 manifest。</summary>
    public static class UpmInstaller
    {
        public static Dictionary<string, string> MergeDependencies(IEnumerable<ExPluginItem> plugins)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var plugin in plugins)
            {
                if (plugin.Dependencies == null) continue; // 配置允许没有外部依赖。
                foreach (var dependency in plugin.Dependencies)
                {
                    if (dependency == null || string.IsNullOrWhiteSpace(dependency.name)
                        || string.IsNullOrWhiteSpace(dependency.version))
                        throw new InvalidDataException("插件“" + plugin.Name + "”的依赖必须填写包名和版本 / Git URL。");
                    string name = dependency.name.Trim();
                    string version = dependency.version.Trim();
                    if (result.TryGetValue(name, out var existing) && existing != version)
                        throw new InvalidDataException("同批插件要求了不同的依赖来源: " + name + " (" + existing + " / " + version + ")");
                    result[name] = version;
                }
            }
            return result;
        }

        public static Dictionary<string, string> ReadDirectDependencies(string projectRoot)
        {
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(projectRoot, "Packages/manifest.json"), Encoding.UTF8));
            if (!(manifest["dependencies"] is JObject dependencies))
                throw new InvalidDataException("项目 manifest 必须包含 dependencies 对象。");
            return dependencies.Properties().ToDictionary(p => p.Name, p =>
            {
                if (p.Value.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)p.Value))
                    throw new InvalidDataException("项目 manifest 的依赖必须填写版本或来源: " + p.Name);
                return (string)p.Value;
            }, StringComparer.Ordinal);
        }

        public static string[] ReadRegistryScopes(string projectRoot)
        {
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(projectRoot, "Packages/manifest.json"), Encoding.UTF8));
            if (!(manifest["scopedRegistries"] is JArray registries)) return Array.Empty<string>(); // scopedRegistries 是可选项。
            return registries.OfType<JObject>().Where(r => r["scopes"] is JArray)
                .SelectMany(r => ((JArray)r["scopes"]).Values<string>()).ToArray();
        }

        public static bool HasRegistrySource(string[] scopes, string name)
            => name.StartsWith("com.unity.", StringComparison.Ordinal)
                || scopes.Any(scope => name == scope || name.StartsWith(scope + ".", StringComparison.Ordinal));

        public static bool IsLocationSource(string source)
            => source.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                || source.IndexOf("://", StringComparison.Ordinal) >= 0
                || source.StartsWith("git@", StringComparison.Ordinal);

        public static string PackageSpecification(string name, string source)
        {
            if (IsLocationSource(source))
                return source;
            return name + "@" + source;
        }

        /// <summary>仅用于兼容旧任务和卸载旧版本写入的直接引用，新安装不生成此引用。</summary>
        public static string LegacyEmbeddedSource(string localPath) => "file:" + localPath.Substring("Packages/".Length);

        /// <summary>调用前由安装服务保证本工具的其他 UPM 请求已经结束。</summary>
        public static void Resolve() => Client.Resolve();

        /// <summary>调用前由安装服务保证没有其他由本工具发起的 UPM 操作。</summary>
        public static AddAndRemoveRequest Submit(string[] add, string[] remove)
        {
            if (add.Length == 0 && remove.Length == 0)
                throw new InvalidOperationException("UPM 请求不能为空。");
            return Client.AddAndRemove(add, remove);
        }
    }
}
