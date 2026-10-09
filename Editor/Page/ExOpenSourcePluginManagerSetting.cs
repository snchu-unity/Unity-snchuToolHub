using System;
using System.Collections;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace ExOpenSource.Editor
{
    public enum ConnectionStatus { Pending, Checking, Success, Failed }

    /// <summary>设置页只编辑 menu.json；令牌只从项目外 txt 读取，内容不参与 Unity 序列化。</summary>
    public class ExOpenSourcePluginManagerSetting : ExScriptableSingleton<ExOpenSourcePluginManagerSetting>
    {
        public const string TokenFileEnvironmentVariable = "SNCHU_TOOLHUB_TOKEN_FILE";
        [NonSerialized] private string _token = "";
        // 仅在隐藏的会话对象中保留路径，脚本重载后可重新读取；不写入项目或工具文件。
        [SerializeField] private string _tokenFilePath = "";
        [SerializeField] private bool _useDefaultTokenFile = true;
        private ConnectionStatus _connectionStatus;
        private string _connectionMessage = "准备测试连接";
        private double _connectionStartTime;
        private double _responseTime;
        private bool _isTestingConnection;

        private void OnEnable()
        {
            _token = "";
            if (_useDefaultTokenFile && string.IsNullOrEmpty(_tokenFilePath))
                _tokenFilePath = FindDefaultTokenFile();
        }

        /// <summary>未配置文件允许匿名访问；已配置但无法读取时明确报错，中断当前请求。</summary>
        public string ReadToken()
        {
            if (string.IsNullOrEmpty(_token) && !string.IsNullOrEmpty(_tokenFilePath)) ReadTokenFile(_tokenFilePath);
            return _token;
        }

        public void DrawGUI()
        {
            GUILayout.Label("目录配置", ExGUIStyleHelper.TitleStyle);
            EditorGUILayout.HelpBox("所有插件配置和更新基线统一保存在 menu.json。实际安装状态由项目文件和 UPM 注册结果判断。", MessageType.Info);
            EditorGUILayout.SelectableLabel(ExOpenSourceConstParam.PROJECT_MENU_PATH,
                EditorStyles.textField, GUILayout.Height(38));
            EditorGUI.BeginDisabledGroup(PluginInstallService.IsBusy);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("打开 menu.json"))
                ExOpenSourcePluginManagerWindow.RunAfterGUI(OpenMenu);
            if (GUILayout.Button("导入配置"))
                ExOpenSourcePluginManagerWindow.RunAfterGUI(ImportMenu);
            GUILayout.EndHorizontal();
            EditorGUI.EndDisabledGroup();

            GUILayout.Space(16);
            GUILayout.Label("GitHub 令牌（建议提前配置）", ExGUIStyleHelper.TitleStyle);
            EditorGUILayout.HelpBox("只读取 Unity 项目和工具目录之外的本机 txt。读取一次后在当前会话复用，关闭窗口无需重选；ToolHub 不复制文件、不保存令牌内容。", MessageType.Info);
            EditorGUILayout.SelectableLabel(string.IsNullOrEmpty(_tokenFilePath) ? "未选择本地令牌文件" : _tokenFilePath,
                EditorStyles.textField, GUILayout.Height(38));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("选择本地 txt"))
                ExOpenSourcePluginManagerWindow.RunAfterGUI(ReadTokenFile);
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_tokenFilePath)))
            {
                if (GUILayout.Button("重新读取"))
                    ExOpenSourcePluginManagerWindow.RunAfterGUI(() => ReadTokenFile(_tokenFilePath));
            }
            if (GUILayout.Button("清除本次读取"))
            {
                _token = "";
                _tokenFilePath = "";
                _useDefaultTokenFile = false;
            }
            GUILayout.EndHorizontal();
            EditorGUILayout.LabelField("令牌状态", !string.IsNullOrEmpty(_token) ? "已读取（内容隐藏）"
                : string.IsNullOrEmpty(_tokenFilePath) ? "未配置" : "已配置文件，使用时读取");
            EditorGUILayout.HelpBox("免重选：在 Windows 用户环境变量中设置 " + TokenFileEnvironmentVariable
                + "，值填写 txt 的绝对路径。ToolHub 只读取该变量，不保存令牌或路径到工具目录。", MessageType.Info);
            if (GUILayout.Button("读取环境变量指定的文件"))
                ExOpenSourcePluginManagerWindow.RunAfterGUI(ReadDefaultTokenFile);
            if (GUILayout.Button("打开 GitHub 令牌设置")) Application.OpenURL("https://github.com/settings/tokens");

            GUILayout.Space(16);
            GUILayout.Label("GitHub API 检测", ExGUIStyleHelper.TitleStyle);
            EditorGUILayout.HelpBox("检测 GitHub API 的连接与认证。插件下载通过本机 Git 执行，使用 Git 自身的网络与代理配置。", MessageType.Info);
            EditorGUI.BeginDisabledGroup(_isTestingConnection);
            if (GUILayout.Button("检测 GitHub API"))
                ExOpenSourcePluginManagerWindow.RunAfterGUI(CheckConnectionToGitRepo);
            EditorGUI.EndDisabledGroup();
            GUILayout.Label($"状态: {_connectionStatus}  响应时间: {_responseTime:0.00}ms", ExGUIStyleHelper.BodyStyle);
            GUILayout.Label(_connectionMessage, ExGUIStyleHelper.BodyStyle);
        }

        private static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static void OpenMenu()
        {
            string path = MenuJsonUtility.ProjectMenuPath(ProjectRoot);
            if (!File.Exists(path))
                MenuJsonUtility.WriteProjectMenu(ProjectRoot, JObject.Parse(MenuJsonUtility.ReadProjectMenuJson(ProjectRoot)));
            EditorUtility.OpenWithDefaultApp(path);
        }

        private static void ImportMenu()
        {
            string selected = EditorUtility.OpenFilePanel("选择插件目录配置", "", "json");
            if (string.IsNullOrEmpty(selected)) return;
            string json = File.ReadAllText(selected, Encoding.UTF8);
            MenuJsonUtility.Parse(json, out string converted);
            var document = JObject.Parse(converted ?? json);
            // 导入别人的配置不等于接管本地文件，管理信息只能由本项目安装或主动接管产生。
            foreach (JObject plugin in (JArray)document["Plugins"]) plugin.Remove("Installation");
            if (!EditorUtility.DisplayDialog("导入插件目录",
                "将替换当前 menu.json 中的插件配置，已安装文件保留。导入后，已有目标目录可重新接管。", "导入", "取消")) return;
            MenuJsonUtility.WriteProjectMenu(ProjectRoot, document);
            ExOpenSourcePluginManagerWindow.RefreshMenuTree();
        }

        private void ReadTokenFile()
        {
            string selected = EditorUtility.OpenFilePanel("选择 GitHub 令牌文本文件", "", "txt");
            if (string.IsNullOrEmpty(selected)) return;
            ReadTokenFile(selected);
        }

        /// <summary>读取项目和工具目录之外的本机 txt；只在会话内保留内容，不保存或复制文件。</summary>
        public void ReadTokenFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("请选择本机令牌 txt 文件。");
            string expanded = Environment.ExpandEnvironmentVariables(path.Trim());
            if (!Path.IsPathRooted(expanded)) throw new InvalidDataException("令牌文件必须使用本机绝对路径。");
            string fullPath = Path.GetFullPath(expanded);
            if (!string.Equals(Path.GetExtension(fullPath), ".txt", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("令牌文件必须是 txt 文本文件。");
            if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
                throw new InvalidDataException("请选择本机磁盘上的令牌 txt，不使用网络共享路径。");
            string toolRoot = Path.GetFullPath(Path.Combine(ProjectRoot, ExOpenSourceConstParam.TOOL_ROOT));
            if (IsWithin(fullPath, ProjectRoot) || IsWithin(fullPath, toolRoot))
                throw new InvalidDataException("令牌 txt 必须放在 Unity 项目和 snchuToolHub 目录之外，避免随项目或工具上传。");
            string token = File.ReadAllText(fullPath, Encoding.UTF8).Trim();
            if (token.Length == 0 || Array.Exists(token.ToCharArray(), char.IsWhiteSpace))
                throw new InvalidDataException("令牌 txt 只能包含一行非空令牌，不要添加说明或内部空白。");
            _tokenFilePath = fullPath;
            _useDefaultTokenFile = true;
            _token = token;
        }

        private static bool IsWithin(string path, string directory)
            => path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        private static string FindDefaultTokenFile()
        {
            string path = Environment.GetEnvironmentVariable(TokenFileEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(path) && Environment.OSVersion.Platform == PlatformID.Win32NT)
                path = Environment.GetEnvironmentVariable(TokenFileEnvironmentVariable, EnvironmentVariableTarget.User);
            return (path ?? "").Trim();
        }

        private void ReadDefaultTokenFile()
        {
            string path = FindDefaultTokenFile();
            if (string.IsNullOrEmpty(path)) throw new InvalidDataException("尚未设置 " + TokenFileEnvironmentVariable + "，变量值应是 txt 文件路径。");
            ReadTokenFile(path);
        }

        public void CheckConnectionToGitRepo()
        {
            if (_isTestingConnection) return;
            string token;
            try { token = ReadToken(); }
            catch (Exception error)
            {
                _connectionStatus = ConnectionStatus.Failed;
                _connectionMessage = "读取令牌文件失败: " + error.Message;
                throw;
            }
            _connectionStatus = ConnectionStatus.Checking;
            _connectionMessage = "正在测试 GitHub API...";
            _connectionStartTime = EditorApplication.timeSinceStartup;
            _isTestingConnection = true;
            EditorCoroutineUtility.StartCoroutineOwnerless(TestConnectionCoroutine(token));
        }

        private IEnumerator TestConnectionCoroutine(string token)
        {
            string url = ExOpenSourceConstParam.GITHUB_CONNECTION_TEST_URL;
            try
            {
                using (var request = UnityWebRequest.Get(url))
                {
                    request.timeout = 10;
                    request.SetRequestHeader("User-Agent", "snchuToolHub/UnityEditor-" + Application.unityVersion);
                    request.SetRequestHeader("Accept", "application/vnd.github+json");
                    if (!string.IsNullOrEmpty(token)) request.SetRequestHeader("Authorization", "token " + token);
                    yield return request.SendWebRequest();
                    _responseTime = (EditorApplication.timeSinceStartup - _connectionStartTime) * 1000;
                    _connectionStatus = request.result == UnityWebRequest.Result.Success ? ConnectionStatus.Success : ConnectionStatus.Failed;
                    _connectionMessage = request.result == UnityWebRequest.Result.Success
                        ? $"GitHub API 连接成功。HTTP {request.responseCode}，响应时间 {_responseTime:0} ms。"
                        : $"GitHub API 连接失败。HTTP {request.responseCode}: {request.error}";
                }
            }
            finally
            {
                _isTestingConnection = false;
                EditorWindow.focusedWindow?.Repaint();
            }
        }
    }
}
