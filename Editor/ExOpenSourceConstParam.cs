using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace ExOpenSource.Editor
{
    public static class ExOpenSourceConstParam
    {
        // 首次在编辑器主线程使用时定位；后续后台安装直接复用已解析的路径。
        public static readonly string PROJECT_ROOT = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        public static readonly string TOOL_ROOT = ResolveToolRoot();
        public static readonly string CONFIG_ROOT = TOOL_ROOT + "/Config~";
        public static readonly string CACHE_ROOT = TOOL_ROOT + "/Cache~";
        public static readonly string PROJECT_MENU_PATH = CONFIG_ROOT + "/menu.json";
        public const string GITHUB_CONNECTION_TEST_URL = "https://api.github.com/rate_limit";

        private static string ResolveToolRoot()
        {
            // 使用已有脚本 GUID 作为定位锚点，移动或改名文件夹时无需修改代码。
            string scriptPath = AssetDatabase.GUIDToAssetPath("766db21c60824b9faf87c912cbdf4f34");
            if (string.IsNullOrEmpty(scriptPath))
                throw new InvalidOperationException("无法定位 snchuToolHub，请保留工具脚本及其原有 .meta 文件。");

            string projectRoot = PROJECT_ROOT;
            var package = PackageInfo.FindForAssetPath(scriptPath);
            string toolRoot = package != null
                ? package.resolvedPath
                : Path.Combine(projectRoot, Path.GetDirectoryName(Path.GetDirectoryName(scriptPath)));
            string fullRoot = Path.GetFullPath(toolRoot).Replace('\\', '/').TrimEnd('/');
            string projectPrefix = projectRoot.Replace('\\', '/').TrimEnd('/') + "/";
            return fullRoot.StartsWith(projectPrefix, StringComparison.OrdinalIgnoreCase)
                ? fullRoot.Substring(projectPrefix.Length) : fullRoot;
        }

        #region text

        public const string POOF_LIB_MGR = "snchuToolHub";
        public const string POOF_LIB_HOST_TITLE = POOF_LIB_MGR;
        public const string POOF_LIB_HOST_MSG = "新增插件或扫描仓库 → 保存配置 → 选择插件或合集 → 执行安装。";
        public const string POOF_LIB_HOST_INTRO = "配置一次，长期复用；统一管理项目中的 GitHub 插件。";

        public const string REPO_SETTING = "仓库设置";
        public const string REPO_TOKEN_GROUP = "GitHub令牌设置";
        public const string REPO_TOKEN = "GitHub 令牌";
        public const string REPO_SETTING_GROUP_SUB_1 = "仓库设置/网络检测";
        public const string REPO_SETTING_GROUP_SUB_CONNECTION = "仓库设置/网络检测/connection";

        public const string REPO_CONNECTION_TITLE = "连接的仓库配置";
        #endregion
    }
}
