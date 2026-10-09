using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ExOpenSource.Editor
{
    public class PluginBatchPage
    {
        private readonly List<ExPluginItem> _plugins;
        private readonly HashSet<ExPluginItem> _selected = new HashSet<ExPluginItem>();
        private readonly string[] _repositorySources;
        private string _search = "";

        /// <summary>合集的完整菜单路径；全局批量页为空。</summary>
        public string MenuPath { get; }

        public PluginBatchPage(IEnumerable<ExMenuConfig> menus, ExMenuConfig selectedMenu = null)
            : this(null, menus.SelectMany(m => m.Plugins), false)
        {
            if (selectedMenu != null) foreach (var plugin in selectedMenu.Plugins) _selected.Add(plugin);
        }

        /// <summary>由已校验的目录条目创建合集页，默认选中目录及子目录的全部插件。</summary>
        public PluginBatchPage(string menuPath, IEnumerable<ExPluginItem> plugins)
            : this(menuPath, plugins, true) { }

        private PluginBatchPage(string menuPath, IEnumerable<ExPluginItem> plugins, bool selectAll)
        {
            MenuPath = menuPath;
            _plugins = plugins.ToList();
            if (selectAll) foreach (var plugin in _plugins) _selected.Add(plugin);
            _repositorySources = _plugins.GroupBy(GitPluginUtility.RepositoryKey).Select(group =>
            {
                var source = group.First();
                return source.GitURL_Username + "/" + source.GitURL_RepoName + " · " + source.GitURL_Branch + " · " + group.Count() + " 个插件";
            }).ToArray();
        }

        public void DrawGUI()
        {
            var installations = _plugins.ToDictionary(plugin => plugin, PluginInstallService.GetInstallationInfo);
            GUILayout.Label(MenuPath == null ? "批量管理" : MenuPath.Substring(MenuPath.LastIndexOf('/') + 1) + " · 插件合集", ExGUIStyleHelper.TitleStyle);
            if (MenuPath != null)
            {
                EditorGUILayout.LabelField("目录", MenuPath);
                EditorGUILayout.HelpBox("已默认选中本合集及子目录的全部插件，可直接点击“安装所选”。左侧目录筛选不改变此合集的操作范围；下方列表可调整选择。", MessageType.Info);
            }
            EditorGUILayout.LabelField("合集 / 列表插件数", _plugins.Count.ToString());
            EditorGUILayout.LabelField("仓库与分支分组数", _repositorySources.Length.ToString());
            foreach (string source in _repositorySources) EditorGUILayout.LabelField("来源分组", source);
            EditorGUILayout.HelpBox("同仓库、同分支的插件共用一次仓库获取。单个和批量操作使用同一安装服务。", MessageType.Info);
            EditorGUILayout.HelpBox("各操作只处理所选项中具备权限的插件，按钮数量即实际处理数量。外部插件不参与维护；安装准备发现冲突时，整批停止且不提交目标文件。", MessageType.Info);
            _search = EditorGUILayout.TextField("筛选", _search);
            var visible = _plugins.Where(p => string.IsNullOrEmpty(_search)
                || p.Name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0
                || p.LocalPath.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0
                || p.GitURL_RepoName.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("全选可见")) foreach (var plugin in visible) _selected.Add(plugin);
            if (GUILayout.Button("选择已安装"))
                foreach (var plugin in visible)
                    if (installations[plugin].IsInstalled) _selected.Add(plugin);
            if (GUILayout.Button("清空选择")) _selected.Clear();
            GUILayout.Label("已选 " + _selected.Count + " / " + _plugins.Count, GUILayout.Width(110));
            EditorGUILayout.EndHorizontal();
            EditorGUI.BeginDisabledGroup(PluginInstallService.IsBusy || _selected.Count == 0);
            EditorGUILayout.BeginHorizontal();
            DrawAction(PluginOperationKind.Install, installations);
            DrawAction(PluginOperationKind.CheckUpdates, installations);
            DrawAction(PluginOperationKind.Update, installations);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            DrawAction(PluginOperationKind.Reinstall, installations);
            DrawAction(PluginOperationKind.Uninstall, installations);
            DrawAction(PluginOperationKind.Adopt, installations);
            EditorGUILayout.EndHorizontal();
            EditorGUI.EndDisabledGroup();
            GUILayout.Space(8);
            foreach (var plugin in visible)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                bool selected = GUILayout.Toggle(_selected.Contains(plugin), "", GUILayout.Width(20));
                if (selected) _selected.Add(plugin); else _selected.Remove(plugin);
                GUILayout.Label(plugin.Name, EditorStyles.boldLabel);
                EditorGUILayout.EndHorizontal();
                GUILayout.Label(installations[plugin].Label, ExGUIStyleHelper.BodyStyle);
                EditorGUILayout.LabelField("来源", plugin.GitURL_Username + "/" + plugin.GitURL_RepoName + " · " + plugin.GitURL_Branch);
                EditorGUILayout.LabelField("目录", plugin.GitURL_Path + " → " + plugin.LocalPath);
                if (installations[plugin].Kind == PluginInstallationKind.UnmanagedElsewhere)
                {
                    EditorGUILayout.LabelField("已有位置", PluginInstallService.DisplayPath(installations[plugin].ExistingPath));
                    EditorGUILayout.HelpBox(installations[plugin].Message, MessageType.Warning);
                }
                else if (installations[plugin].Kind != PluginInstallationKind.Managed
                    && installations[plugin].Kind != PluginInstallationKind.NotInstalled)
                    GUILayout.Label(installations[plugin].Message + "\n实际位置："
                        + PluginInstallService.DisplayPath(installations[plugin].ActualPath), ExGUIStyleHelper.BodyStyle);
                EditorGUILayout.EndVertical();
            }
        }

        private void DrawAction(PluginOperationKind kind, Dictionary<ExPluginItem, PluginInstallationInfo> installations)
        {
            var selection = _plugins.Where(plugin => _selected.Contains(plugin)
                && PluginInstallService.CanOperate(installations[plugin], kind, out _)).ToList();
            int excluded = _selected.Count - selection.Count;
            string label = (kind == PluginOperationKind.Install ? "安装所选" : PluginOperationGUI.Label(kind)) + "（" + selection.Count + "）";
            using (new EditorGUI.DisabledScope(selection.Count == 0))
            {
                if (!GUILayout.Button(new GUIContent(label, "实际处理 " + selection.Count + " 个；因状态不符合条件而排除 " + excluded + " 个。"))) return;
                ExOpenSourcePluginManagerWindow.RunAfterGUI(() => PluginOperationGUI.Request(kind, selection));
            }
        }
    }
}
