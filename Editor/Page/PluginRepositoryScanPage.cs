using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ExOpenSource.Editor
{
    public class PluginRepositoryScanPage
    {
        private readonly List<ExMenuConfig> _menus;
        private readonly Action<ExMenuConfig, List<ExPluginItem>> _save;
        private readonly HashSet<string> _selectedPaths = new HashSet<string>(StringComparer.Ordinal);
        private string _user = "";
        private string _repository = "";
        private string _branch = "";
        private string _directory = "";
        private string _installationRoot = "Packages";
        private string _search = "";
        private string _error;
        private string _resultId;
        private int _menuIndex;

        public PluginRepositoryScanPage(List<ExMenuConfig> menus, Action<ExMenuConfig, List<ExPluginItem>> save)
        {
            _menus = menus;
            _save = save;
        }

        public void DrawGUI()
        {
            GUILayout.Label("仓库包扫描", ExGUIStyleHelper.TitleStyle);
            EditorGUILayout.HelpBox("扫描指定仓库目录中的 package.json，预览后生成完整的独立插件条目。此处不安装插件，也不修改项目包配置。", MessageType.Info);
            EditorGUI.BeginDisabledGroup(PluginInstallService.IsBusy);
            _user = EditorGUILayout.TextField("仓库所有者 *", _user);
            _repository = EditorGUILayout.TextField("仓库名称 *", _repository);
            _branch = EditorGUILayout.TextField("分支 / 标签 *", _branch);
            _directory = EditorGUILayout.TextField(new GUIContent("扫描目录", "仓库内的相对目录；留空扫描整个仓库的包清单"), _directory);
            _installationRoot = EditorGUILayout.TextField("安装根目录 *", _installationRoot);
            _menuIndex = EditorGUILayout.Popup("保存到菜单", _menuIndex, _menus.Select(m => m.Name).ToArray());
            EditorGUI.BeginDisabledGroup(_menus.Count == 0);
            if (GUILayout.Button("扫描包清单"))
                ExOpenSourcePluginManagerWindow.RunAfterGUI(() =>
                {
                    try
                    {
                        _error = null;
                        PluginInstallService.Scan(new ExPluginItem { GitURL_Username = _user.Trim(), GitURL_RepoName = _repository.Trim(), GitURL_Branch = _branch.Trim() },
                            _menus[_menuIndex].Name, _directory, _installationRoot);
                    }
                    catch (Exception error) { _error = error.Message; }
                });
            EditorGUI.EndDisabledGroup();
            EditorGUI.EndDisabledGroup();
            if (_error != null) EditorGUILayout.HelpBox(_error, MessageType.Error);

            var state = PluginInstallService.State;
            if (state.Kind != PluginOperationKind.Scan || state.Phase != PluginOperationPhase.Completed) return;
            if (_resultId != state.Id)
            {
                _resultId = state.Id;
                _selectedPaths.Clear();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in state.ScanEntries)
                    if (entry.Plugin != null && names.Add(entry.Plugin.LocalPath)) _selectedPaths.Add(entry.SourcePath);
            }
            GUILayout.Space(10);
            _search = EditorGUILayout.TextField("筛选扫描结果", _search);
            var visible = state.ScanEntries.Where(e => string.IsNullOrEmpty(_search)
                || e.SourcePath.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0
                || (e.Plugin != null && e.Plugin.Name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("选择可见有效项")) foreach (var entry in visible.Where(e => e.Plugin != null)) _selectedPaths.Add(entry.SourcePath);
            if (GUILayout.Button("清空选择")) _selectedPaths.Clear();
            EditorGUI.BeginDisabledGroup(PluginInstallService.IsBusy || _selectedPaths.Count == 0 || _menus.Count == 0);
            if (GUILayout.Button("保存选中配置（" + _selectedPaths.Count + "）"))
            {
                var plugins = state.ScanEntries.Where(e => e.Plugin != null && _selectedPaths.Contains(e.SourcePath)).Select(e => e.Plugin).ToList();
                ExOpenSourcePluginManagerWindow.RunAfterGUI(() =>
                {
                    try { _error = null; _save(_menus[_menuIndex], plugins); }
                    catch (Exception error) { _error = error.Message; }
                });
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
            foreach (var entry in visible)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                if (entry.Plugin == null) EditorGUILayout.HelpBox(entry.SourcePath + "\n" + entry.Error, MessageType.Warning);
                else
                {
                    bool selected = EditorGUILayout.ToggleLeft(entry.Plugin.Name + " · " + entry.Plugin.Version, _selectedPaths.Contains(entry.SourcePath));
                    if (selected) _selectedPaths.Add(entry.SourcePath); else _selectedPaths.Remove(entry.SourcePath);
                    EditorGUILayout.LabelField("包名", entry.Plugin.LocalPath.Substring(entry.Plugin.LocalPath.LastIndexOf('/') + 1));
                    EditorGUILayout.LabelField("来源目录", entry.SourcePath);
                    EditorGUILayout.LabelField("安装目录", entry.Plugin.LocalPath);
                }
                EditorGUILayout.EndVertical();
            }
        }
    }
}
