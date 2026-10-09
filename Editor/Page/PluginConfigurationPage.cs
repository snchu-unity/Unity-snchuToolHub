using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ExOpenSource.Editor
{
    /// <summary>插件配置的 IMGUI 表单。source 为 null 时新增；保存前只修改草稿。</summary>
    public class PluginConfigurationPage
    {
        private readonly ExMenuConfig _menu;
        private readonly Action<ExPluginItem> _onSave;
        private readonly Action _onCancel;
        private readonly bool _isNew;
        private readonly ExMenuConfig _examples;
        private readonly string[] _exampleLabels;
        private ExPluginItem _draft;
        private List<UPMPackage> _dependencies;
        private string _tagsText;
        private string _repositoryAddress = "";
        private string _error;
        private int _exampleIndex;

        public PluginConfigurationPage(ExMenuConfig menu, ExPluginItem source,
            Action<ExPluginItem> onSave, Action onCancel)
        {
            _menu = menu;
            _onSave = onSave;
            _onCancel = onCancel;
            _isNew = source == null;
            SetDraft(source ?? new ExPluginItem { Name = "", MenuPath = menu.Name + "/" });

            // 示例是可选参考文件，不是活动菜单；缺失时仍可手工填写。
            var examplePath = Path.Combine(Application.dataPath, "../", ExOpenSourceConstParam.TOOL_ROOT,
                "Examples~/menu.example.json");
            if (File.Exists(examplePath))
            {
                _examples = MenuJsonUtility.Parse(File.ReadAllText(examplePath, System.Text.Encoding.UTF8), out _);
                _exampleLabels = new string[_examples.Plugins.Count];
                for (int i = 0; i < _exampleLabels.Length; i++)
                    _exampleLabels[i] = _examples.Plugins[i].MenuPath;
            }
            else
                _exampleLabels = Array.Empty<string>();
        }

        private void SetDraft(ExPluginItem source)
        {
            _draft = JsonUtility.FromJson<ExPluginItem>(JsonUtility.ToJson(source));
            // 可选文本和列表统一为空字符串 / 空列表。
            _draft.Version = _draft.Version ?? "";
            _draft.Author = _draft.Author ?? "";
            _draft.Intro = _draft.Intro ?? "";
            _draft.LocalPath = _draft.LocalPath ?? "";
            _draft.GitURL_Username = _draft.GitURL_Username ?? "";
            _draft.GitURL_RepoName = _draft.GitURL_RepoName ?? "";
            _draft.GitURL_Branch = _draft.GitURL_Branch ?? "";
            _draft.GitURL_Path = _draft.GitURL_Path ?? "";
            _draft.IntroductionURL = _draft.IntroductionURL ?? "";
            _tagsText = _draft.Tags == null ? "" : string.Join(", ", _draft.Tags);
            _dependencies = new List<UPMPackage>(_draft.Dependencies ?? Array.Empty<UPMPackage>());
            if (_dependencies.Contains(null)) throw new InvalidDataException("UPM 依赖不能包含空条目。");
        }

        public void DrawGUI()
        {
            GUILayout.Label(_isNew ? "新增插件配置" : "编辑插件配置", ExGUIStyleHelper.TitleStyle);
            EditorGUILayout.LabelField("保存到菜单", _menu.Name);
            EditorGUILayout.HelpBox("保存后加入插件列表，安装由你另行点击。编辑完成后请选择“保存配置”或“取消”。", MessageType.Info);

            if (_isNew && _exampleLabels.Length > 0)
            {
                GUILayout.BeginHorizontal();
                _exampleIndex = EditorGUILayout.Popup("配置示例", _exampleIndex, _exampleLabels);
                if (GUILayout.Button("填入示例", GUILayout.Width(80)))
                    ExOpenSourcePluginManagerWindow.RunAfterGUI(FillExample);
                GUILayout.EndHorizontal();
                EditorGUILayout.HelpBox("示例使用占位仓库和路径，请替换为实际插件信息后保存。", MessageType.Info);
            }

            GUILayout.Space(8);
            GUILayout.Label("基本信息", EditorStyles.boldLabel);
            string automaticMenuPath = _menu.Name + "/" + _draft.Name;
            _draft.Name = EditorGUILayout.TextField("名称 *", _draft.Name);
            if (_isNew && _draft.MenuPath == automaticMenuPath) _draft.MenuPath = _menu.Name + "/" + _draft.Name;
            _draft.MenuPath = EditorGUILayout.TextField(new GUIContent("菜单位置 *", "用 / 划分类别，例如：项目插件目录/编辑器/工具名称"), _draft.MenuPath);
            _draft.Version = EditorGUILayout.TextField("版本说明", _draft.Version);
            _draft.Author = EditorGUILayout.TextField("作者", _draft.Author);
            _tagsText = EditorGUILayout.TextField(new GUIContent("标签", "多个标签用逗号分隔"), _tagsText);
            GUILayout.Label("简介");
            _draft.Intro = EditorGUILayout.TextArea(_draft.Intro, GUILayout.MinHeight(55));
            _draft.IntroductionURL = EditorGUILayout.TextField("说明书网址", _draft.IntroductionURL);

            GUILayout.Space(12);
            GUILayout.Label("下载来源", EditorStyles.boldLabel);
            GUILayout.BeginHorizontal();
            _repositoryAddress = EditorGUILayout.TextField(new GUIContent("GitHub 仓库网址", "可粘贴仓库网址，点击识别以填写所有者和仓库名"), _repositoryAddress);
            if (GUILayout.Button("识别仓库", GUILayout.Width(80)))
                ExOpenSourcePluginManagerWindow.RunAfterGUI(ReadRepositoryAddress);
            GUILayout.EndHorizontal();
            _draft.GitURL_Username = EditorGUILayout.TextField("仓库所有者 *", _draft.GitURL_Username);
            _draft.GitURL_RepoName = EditorGUILayout.TextField("仓库名称 *", _draft.GitURL_RepoName);
            _draft.GitURL_Branch = EditorGUILayout.TextField("分支 *", _draft.GitURL_Branch);
            _draft.GitURL_Path = EditorGUILayout.TextField(new GUIContent("仓库内下载目录", "相对仓库根目录；留空表示下载整个仓库"), _draft.GitURL_Path);

            GUILayout.Space(12);
            GUILayout.Label("安装位置", EditorStyles.boldLabel);
            GUILayout.BeginHorizontal();
            _draft.LocalPath = EditorGUILayout.TextField(new GUIContent("项目内安装目录 *", "相对于 Unity 项目根目录（Assets 与 Packages 的上一级）；例如 Assets/ThirdParty/MyPlugin 或 Packages/com.example.plugin"), _draft.LocalPath);
            if (GUILayout.Button("选择目录", GUILayout.Width(80)))
                ExOpenSourcePluginManagerWindow.RunAfterGUI(ChooseInstallDirectory);
            GUILayout.EndHorizontal();

            GUILayout.Space(12);
            GUILayout.Label("UPM 依赖", EditorStyles.boldLabel);
            for (int i = 0; i < _dependencies.Count; i++)
            {
                var dependency = _dependencies[i];
                GUILayout.BeginVertical(EditorStyles.helpBox);
                dependency.name = EditorGUILayout.TextField("包名", dependency.name);
                dependency.version = EditorGUILayout.TextField("版本 / Git URL", dependency.version);
                if (GUILayout.Button("移除此依赖"))
                {
                    int index = i;
                    ExOpenSourcePluginManagerWindow.RunAfterGUI(() => _dependencies.RemoveAt(index));
                }
                GUILayout.EndVertical();
            }
            if (GUILayout.Button("添加 UPM 依赖"))
                ExOpenSourcePluginManagerWindow.RunAfterGUI(() => _dependencies.Add(new UPMPackage { name = "", version = "" }));

            GUILayout.Space(12);
            if (!string.IsNullOrEmpty(_error)) EditorGUILayout.HelpBox(_error, MessageType.Error);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("保存配置")) ExOpenSourcePluginManagerWindow.RunAfterGUI(Save);
            if (GUILayout.Button("取消")) ExOpenSourcePluginManagerWindow.RunAfterGUI(_onCancel);
            GUILayout.EndHorizontal();
        }

        private void FillExample()
        {
            var example = _examples.Plugins[_exampleIndex];
            SetDraft(example);
            string prefix = _examples.Name + "/";
            _draft.MenuPath = _draft.MenuPath.StartsWith(prefix, StringComparison.Ordinal)
                ? _menu.Name + "/" + _draft.MenuPath.Substring(prefix.Length) : _menu.Name + "/" + _draft.Name;
            _error = null;
        }

        private void ReadRepositoryAddress()
        {
            if (!Uri.TryCreate(_repositoryAddress.Trim(), UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" || !string.IsNullOrEmpty(uri.UserInfo))
            {
                _error = "请填写 https://github.com/所有者/仓库名 形式的网址，不要包含令牌。";
                return;
            }
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            if (parts.Length < 2 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1]))
            {
                _error = "仓库网址必须包含所有者和仓库名称。";
                return;
            }
            _draft.GitURL_Username = parts[0];
            _draft.GitURL_RepoName = parts[1].EndsWith(".git", StringComparison.Ordinal)
                ? parts[1].Substring(0, parts[1].Length - 4) : parts[1];
            _error = null;
        }

        private void ChooseInstallDirectory()
        {
            var selected = EditorUtility.OpenFolderPanel("选择项目内的插件安装目录", Application.dataPath, "");
            if (string.IsNullOrEmpty(selected)) return;
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/') + "/";
            string fullPath = Path.GetFullPath(selected).Replace('\\', '/');
            if (!fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                _error = "请选择当前项目 Assets 或 Packages 中的插件目录。";
                return;
            }
            _draft.LocalPath = fullPath.Substring(projectRoot.Length);
            _error = null;
        }

        private void Save()
        {
            try
            {
                NormalizeAndValidate();
                _onSave(_draft);
            }
            catch (Exception e)
            {
                _error = e.Message;
                Debug.LogException(e);
            }
        }

        private void NormalizeAndValidate()
        {
            _draft.Name = _draft.Name.Trim();
            _draft.MenuPath = _draft.MenuPath.Trim();
            if (_draft.Name.Length == 0) throw new InvalidDataException("名称不能为空。");
            if (_draft.MenuPath.Length == 0 || Array.Exists(_draft.MenuPath.Split('/'), string.IsNullOrWhiteSpace))
                throw new InvalidDataException("菜单位置不能为空，也不能包含空分类。");

            _draft.GitURL_Username = _draft.GitURL_Username.Trim();
            _draft.GitURL_RepoName = _draft.GitURL_RepoName.Trim();
            _draft.GitURL_Branch = _draft.GitURL_Branch.Trim();
            MenuJsonUtility.ValidateSource(_draft);

            _draft.GitURL_Path = PluginInstallFiles.NormalizeRelativePath(_draft.GitURL_Path, true);
            _draft.LocalPath = PluginInstallFiles.NormalizeRelativePath(_draft.LocalPath, false);
            PluginInstallFiles.InstallationPath(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), _draft.LocalPath);

            var tags = new List<string>();
            var uniqueTags = new HashSet<string>();
            foreach (var value in _tagsText.Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string tag = value.Trim();
                if (tag.Length > 0 && uniqueTags.Add(tag)) tags.Add(tag);
            }
            _draft.Tags = tags.ToArray();
            foreach (var dependency in _dependencies)
            {
                dependency.name = (dependency.name ?? "").Trim();
                dependency.version = (dependency.version ?? "").Trim();
                if (dependency.name.Length == 0 || dependency.version.Length == 0)
                    throw new InvalidDataException("每条 UPM 依赖都需要包名及版本 / Git URL。");
            }
            _draft.Dependencies = _dependencies.ToArray();
        }

    }
}
