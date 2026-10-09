using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace ExOpenSource.Editor
{
    public enum ConnectionStatus
    {
         Pending,
         Checking,
         Success,
         Failed
    }

    [PLFilePath(ExOpenSourceConstParam.SETTING_ASSET_PATH)]
    public class ExOpenSourcePluginManagerSetting : ExScriptableSingleton<ExOpenSourcePluginManagerSetting>
    {
        // ════════════════════════════════════════════════════════
        //  Tab: 令牌配置
        // ════════════════════════════════════════════════════════






        public string TokenFilePath = "";






        private string showToken = "";

        // ════════════════════════════════════════════════════════
        //  Tab: 菜单配置。远端菜单地址是可选项，不作为插件的默认来源。
        // ════════════════════════════════════════════════════════




        // 保留原字段名，以兼容已经保存的设置。
        public List<RepoInfo> repoInfos = new List<RepoInfo>
        {
            // ScriptableObject 字段初始化不能调用 AssetDatabase；OnEnable 再定位工具目录。
            new RepoInfo { localMenuPath = "Config~/Menus/menu_ex.json" }
        };
        private readonly HashSet<string> _expandedMenuSources = new HashSet<string>();

        // ════════════════════════════════════════════════════════
        //  Tab: 网络检测
        // ════════════════════════════════════════════════════════




        private string connectionInfo =>
            $"<color={ExBrandStyle.HexAccent}>状态:{connectionStatus}</color>  响应时间:{responseTime:0.00}ms";




        [TextArea(1, 20)]

        private string connectionMessage = "准备测试连接";




        public void CheckConnectionToGitRepo()
        {
            if (isTestingConnection) return;

            connectionStatus = ConnectionStatus.Checking;
            connectionMessage = "正在测试连接...";
            connectionStartTime = EditorApplication.timeSinceStartup;
            debugUrl = ExOpenSourceConstParam.GITHUB_CONNECTION_TEST_URL;

            isTestingConnection = true;
            EditorCoroutineUtility.StartCoroutineOwnerless(TestConnectionCoroutine());
        }

        // ── 内部状态 ─────────────────────────────────────────────
        private double connectionStartTime;
        private ConnectionStatus connectionStatus = ConnectionStatus.Pending;
        private string debugUrl;
        private bool isTestingConnection;
        private double responseTime;

        private void OnEnable()
        {
            // Config~ 文件引用随工具位置更新，插件安装目标保留原配置。
            TokenFilePath = RebaseConfigurationPath(TokenFilePath);
            for (int i = 0; i < repoInfos.Count; i++)
            {
                var source = repoInfos[i];
                source.localMenuPath = RebaseConfigurationPath(source.localMenuPath);
                repoInfos[i] = source;
            }
        }

        private static string RebaseConfigurationPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string normalized = path.Replace('\\', '/');
            if (normalized.StartsWith("Config~/", StringComparison.OrdinalIgnoreCase))
                return ExOpenSourceConstParam.TOOL_ROOT + "/" + normalized;
            int position = normalized.LastIndexOf("/Config~/", StringComparison.OrdinalIgnoreCase);
            return position < 0 ? path : ExOpenSourceConstParam.TOOL_ROOT + normalized.Substring(position);
        }

        private void OnDisable()  => Save();
        private void OnDestroy()  => Save();
        private void OnValidate() => Save();

        // ── 网络检测协程 ─────────────────────────────────────────
        private IEnumerator TestConnectionCoroutine()
        {
            using (var request = UnityWebRequest.Get(debugUrl))
            {
                request.timeout = 10;
                request.SetRequestHeader("User-Agent", "snchuToolHub/UnityEditor-" + Application.unityVersion);
                request.SetRequestHeader("Accept", "application/vnd.github+json");
                string token = ReadToken();
                if (!string.IsNullOrEmpty(token))
                    request.SetRequestHeader("Authorization", $"token {token}");

                var operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    connectionMessage = $"正在连接... ({Mathf.FloorToInt(request.downloadProgress * 100)}%)";
                    yield return null;
                }

                var totalTime = (EditorApplication.timeSinceStartup - connectionStartTime) * 1000;
                responseTime = totalTime;

                if (request.result == UnityWebRequest.Result.Success)
                {
                    connectionStatus = ConnectionStatus.Success;
                    connectionMessage = $"测试地址:{debugUrl}\n连接成功！\nHTTP 状态: {request.responseCode}\n响应时间: {totalTime:0} ms";
                }
                else
                {
                    connectionStatus = ConnectionStatus.Failed;
                    connectionMessage = HandleConnectionError(request, debugUrl);
                }

                isTestingConnection = false;
            }
        }

        private string HandleConnectionError(UnityWebRequest request, string url)
        {
            return request.responseCode switch
            {
                401 or 403 => $"测试地址:{url}\n访问被拒绝 (HTTP {request.responseCode})\n• 检查令牌是否有效以及网络访问限制",
                404 => $"测试地址:{url}\nGitHub 接口不可用 (HTTP 404)\n• 检查网络或代理配置",
                429 => $"测试地址:{url}\n⚠GitHub 速率限制\n• 添加 GitHub 令牌可提高限制",
                _ when request.result == UnityWebRequest.Result.ConnectionError =>
                    $"测试地址:{url}\n网络连接失败\n请检查网络连接，DNS是否正常。",
                _ => $"测试地址:{url}\n连接失败: {request.error} (HTTP {request.responseCode})"
            };
        }

        public string ReadToken()
        {
            if (string.IsNullOrEmpty(TokenFilePath))
            {
                showToken = "--";
                return string.Empty;
            }

            try
            {
                string tokenPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", TokenFilePath));
                if (File.Exists(tokenPath))
                {
                    EditorWindow.focusedWindow?.Repaint();
                    var t = File.ReadAllText(tokenPath, System.Text.Encoding.UTF8).Trim();
                    showToken = string.IsNullOrEmpty(t) ? "--" : "已加载";
                    return t;
                }

                Debug.LogWarning($"令牌文件不存在: {TokenFilePath}");
                EditorWindow.focusedWindow?.ShowNotification(new GUIContent($"令牌文件不存在: {TokenFilePath}"));
                EditorWindow.focusedWindow?.Repaint();
                showToken = "--";
                return string.Empty;
            }
            catch (Exception e)
            {
                Debug.LogError($"读取令牌失败: {e.Message}");
                EditorWindow.focusedWindow?.ShowNotification(new GUIContent($"读取令牌失败: {e.Message}"));
                EditorWindow.focusedWindow?.Repaint();
                showToken = "--";
                return string.Empty;
            }
        }

        public void DrawGUI()
        {
            GUILayout.Label("令牌配置", ExGUIStyleHelper.TitleStyle);
            EditorGUILayout.HelpBox("沿用原版的令牌文件读取方式。导入文件会复制到工具自身的 Config~ 目录；界面只显示加载状态。", MessageType.Info);
            EditorGUILayout.SelectableLabel(string.IsNullOrEmpty(TokenFilePath) ? "未选择令牌文件" : TokenFilePath,
                EditorStyles.textField, GUILayout.Height(38));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("导入令牌文件")) ExOpenSourcePluginManagerWindow.RunAfterGUI(ImportTokenFile);
            if (GUILayout.Button("打开令牌文件")) ExOpenSourcePluginManagerWindow.RunAfterGUI(OpenTokenFile);
            if (GUILayout.Button("加载令牌"))
            {
                var token = ReadToken();
                EditorWindow.focusedWindow.ShowNotification(new GUIContent(string.IsNullOrEmpty(token) ? "未加载令牌" : "令牌已加载"));
            }
            GUILayout.EndHorizontal();
            EditorGUILayout.LabelField("令牌状态", string.IsNullOrEmpty(showToken) || showToken == "--" ? "未加载" : "已加载（隐藏内容）");
            if (GUILayout.Button("打开 GitHub 令牌设置")) Application.OpenURL("https://github.com/settings/tokens");

            GUILayout.Space(16);
            GUILayout.Label("菜单配置", ExGUIStyleHelper.TitleStyle);
            EditorGUILayout.HelpBox("本地菜单 JSON 保存目录分组和插件条目。远端清单只用于下载菜单文件，插件安装来源仍由每个插件独立填写。", MessageType.Info);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("新建本地菜单"))
                ExOpenSourcePluginManagerWindow.RunAfterGUI(CreateLocalMenu);
            if (GUILayout.Button("添加远端清单"))
            {
                ExOpenSourcePluginManagerWindow.RunAfterGUI(() =>
                {
                    var source = new RepoInfo
                    {
                        remoteMenuPath = "menu.json",
                        localMenuPath = ExOpenSourceConstParam.CONFIG_ROOT + "/Menus/menu_" + Guid.NewGuid().ToString("N") + ".json"
                    };
                    repoInfos.Add(source);
                    _expandedMenuSources.Add(source.localMenuPath);
                    Save();
                });
            }
            GUILayout.EndHorizontal();

            for (int i = 0; i < repoInfos.Count; i++)
            {
                var repo = repoInfos[i];
                GUILayout.BeginVertical(EditorStyles.helpBox);
                GUILayout.Label(Path.GetFileName(repo.localMenuPath), EditorStyles.boldLabel);
                EditorGUILayout.LabelField("本地菜单文件");
                EditorGUILayout.SelectableLabel(repo.localMenuPath, EditorStyles.textField, GUILayout.Height(38));
                bool expanded = EditorGUILayout.Foldout(_expandedMenuSources.Contains(repo.localMenuPath), "远端菜单清单（可选）", true);
                if (expanded) _expandedMenuSources.Add(repo.localMenuPath); else _expandedMenuSources.Remove(repo.localMenuPath);
                if (expanded)
                {
                    EditorGUI.BeginChangeCheck();
                    repo.userName = EditorGUILayout.DelayedTextField("清单仓库所有者", repo.userName);
                    repo.repoName = EditorGUILayout.DelayedTextField("清单仓库名称", repo.repoName);
                    repo.branch = EditorGUILayout.DelayedTextField("清单分支 / 标签", repo.branch);
                    repo.remoteMenuPath = EditorGUILayout.DelayedTextField("远端 JSON 路径", repo.remoteMenuPath);
                    if (EditorGUI.EndChangeCheck())
                    {
                        repoInfos[i] = repo;
                        Save();
                    }
                    if (GUILayout.Button("下载 / 更新此菜单文件"))
                    {
                        var menuSource = repo;
                        ExOpenSourcePluginManagerWindow.RunAfterGUI(() =>
                        {
                            try
                            {
                                MenuJsonUtility.ValidateSource(new ExPluginItem
                                {
                                    GitURL_Username = menuSource.userName, GitURL_RepoName = menuSource.repoName,
                                    GitURL_Branch = menuSource.branch
                                });
                                PluginInstallFiles.NormalizeRelativePath(menuSource.remoteMenuPath, false);
                                menuSource.LoadMenu();
                            }
                            catch (Exception error)
                            {
                                EditorUtility.DisplayDialog("无法下载菜单", error.Message, "确定");
                                throw;
                            }
                        });
                    }
                }
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("打开菜单文件")) repo.OpenMenuInExplore();
                bool remove = GUILayout.Button("移除菜单引用");
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
                if (remove)
                {
                    int index = i;
                    ExOpenSourcePluginManagerWindow.RunAfterGUI(() =>
                    {
                        repoInfos.RemoveAt(index);
                        Save();
                        ExOpenSourcePluginManagerWindow.RefreshMenuTree();
                    });
                }
            }
            EditorGUILayout.HelpBox("远端菜单下载完成后会刷新目录。移除引用只取消加载该菜单，JSON 文件和已安装插件会保留。", MessageType.Info);

            GUILayout.Space(16);
            GUILayout.Label("工具文件", ExGUIStyleHelper.TitleStyle);
            if (GUILayout.Button("打开配置目录"))
                ExOpenSourcePluginManagerWindow.RunAfterGUI(() =>
                {
                    string configPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ExOpenSourceConstParam.CONFIG_ROOT));
                    Directory.CreateDirectory(configPath);
                    EditorUtility.RevealInFinder(configPath);
                });

            GUILayout.Space(16);
            GUILayout.Label("网络检测", ExGUIStyleHelper.TitleStyle);
            if (GUILayout.Button("检测 GitHub 连接")) CheckConnectionToGitRepo();
            GUILayout.Label(connectionInfo, ExGUIStyleHelper.BodyStyle);
            GUILayout.Label(connectionMessage, ExGUIStyleHelper.BodyStyle);
        }

        private void ImportTokenFile()
        {
            var selectedPath = EditorUtility.OpenFilePanel("选择 GitHub 令牌文本文件", "", "txt");
            if (string.IsNullOrEmpty(selectedPath)) return;
            var tokenPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ExOpenSourceConstParam.TOKEN_FILE_PATH));
            Directory.CreateDirectory(Path.GetDirectoryName(tokenPath));
            if (!string.Equals(Path.GetFullPath(selectedPath), tokenPath, StringComparison.OrdinalIgnoreCase))
                File.Copy(selectedPath, tokenPath, true);
            TokenFilePath = ExOpenSourceConstParam.TOKEN_FILE_PATH;
            Save();
            ReadToken();
        }

        private void OpenTokenFile()
        {
            var tokenPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ExOpenSourceConstParam.TOKEN_FILE_PATH));
            Directory.CreateDirectory(Path.GetDirectoryName(tokenPath));
            if (!File.Exists(tokenPath)) File.WriteAllText(tokenPath, "", new System.Text.UTF8Encoding(false));
            TokenFilePath = ExOpenSourceConstParam.TOKEN_FILE_PATH;
            Save();
            EditorUtility.OpenWithDefaultApp(tokenPath);
        }

        private void CreateLocalMenu()
        {
            string id = Guid.NewGuid().ToString("N");
            string path = ExOpenSourceConstParam.CONFIG_ROOT + "/Menus/menu_" + id + ".json";
            PluginInstallFiles.WriteJson(Path.GetFullPath(Path.Combine(Application.dataPath, "..", path)), new ExMenuConfig
            {
                Name = "插件目录-" + id.Substring(0, 6), Version = "1.0.0", Owner = "本项目", Intro = "本地插件菜单"
            });
            repoInfos.Add(new RepoInfo { localMenuPath = path });
            Save();
            ExOpenSourcePluginManagerWindow.RefreshMenuTree();
        }
    }
}
