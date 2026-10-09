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
        public static readonly string TOOL_ROOT = ResolveToolRoot();
        public static readonly string CONFIG_ROOT = TOOL_ROOT + "/Config~";
        public static readonly string CACHE_ROOT = TOOL_ROOT + "/Cache~";
        public static readonly string TOKEN_FILE_PATH = CONFIG_ROOT + "/github-token.txt";
        // 单例属性需要编译期常量，路径相对于工具根目录。
        public const string SETTING_ASSET_PATH = "Config~/ExOpenSourceSetting.asset";
        public static readonly string PROJECT_MENU_PATH = CONFIG_ROOT + "/Menus/menu_ex.json";
        public const string GITHUB_CONNECTION_TEST_URL = "https://api.github.com/rate_limit";

        private static string ResolveToolRoot()
        {
            // 使用已有脚本 GUID 作为定位锚点，移动或改名文件夹时无需修改代码。
            string scriptPath = AssetDatabase.GUIDToAssetPath("766db21c60824b9faf87c912cbdf4f34");
            if (string.IsNullOrEmpty(scriptPath))
                throw new InvalidOperationException("无法定位 snchuToolHub，请保留工具脚本及其原有 .meta 文件。");

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
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
        public const string REPO_TOKEN_FILE_PATH = "令牌缓存文件路径";
        public const string REPO_SETTING_GROUP_SUB_1 = "仓库设置/网络检测";
        public const string REPO_SETTING_GROUP_SUB_CONNECTION = "仓库设置/网络检测/connection";

        public const string REPO_TOKEN_INTRO =
            "访问令牌(可选):\n" +
            "• 私有仓库必须提供令牌，令牌可以避免GitHub速率限制\n" +
            "• 令牌创建地址: https://github.com/settings/tokens \n" +
            "• 本项目用到的token权限只需要 `repo` 权限即可。（如果你只访问public仓库，只需要勾选public_repo）\n" +
            "• <color=" + ExBrandStyle.HexWarning + "> 注意：请勿泄露令牌信息！ 如需要使用令牌，请将其存入一个text文件，然后选择读取该text文件路径。</color>";

        public const string REPO_CONNECTION_TITLE = "连接的仓库配置";
        #endregion
    }
}
