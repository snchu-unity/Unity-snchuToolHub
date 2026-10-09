using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace ExOpenSource.Editor
{
    public class ExOpenSourcePluginManagerWindow : EditorWindow
    {
        private readonly PluginFilterState _filterState = new PluginFilterState();
        private readonly TreeViewState _treeState = new TreeViewState();
        private List<ExMenuConfig> _cachedConfigs;
        private readonly Dictionary<ExMenuConfig, MenuFile> _menuFiles = new Dictionary<ExMenuConfig, MenuFile>();
        private readonly Dictionary<string, PluginBatchPage> _collectionPages = new Dictionary<string, PluginBatchPage>(StringComparer.Ordinal);
        private List<string> _cachedTags = new List<string>();
        private PluginMenuTree _tree;
        private readonly ExOpenSourceHostPage _homePage = new ExOpenSourceHostPage();
        private PluginBatchPage _batchPage;
        private PluginRepositoryScanPage _scanPage;
        private object _selectedPage;
        private Vector2 _detailScroll;
        private string _search = "";

        [MenuItem("SnchuTools/" + ExOpenSourceConstParam.POOF_LIB_MGR)]
        public static void OpenWindow()
        {
            var window = GetWindow<ExOpenSourcePluginManagerWindow>();
            window.titleContent = new GUIContent(ExOpenSourceConstParam.POOF_LIB_MGR);
            window.minSize = new Vector2(850, 600);
            window.Show();
        }

        public static void RefreshMenuTree()
        {
            GetWindow<ExOpenSourcePluginManagerWindow>().ReloadMenu();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent(ExOpenSourceConstParam.POOF_LIB_MGR);
            _filterState.OnChanged += BuildMenuTree;
            NetworkStatusMonitor.OnStatusChanged += OnNetworkStatusChanged;
            ReloadMenu();
        }

        private void OnDisable()
        {
            _filterState.OnChanged -= BuildMenuTree;
            NetworkStatusMonitor.OnStatusChanged -= OnNetworkStatusChanged;
        }

        private void OnInspectorUpdate()
        {
            Repaint();
        }

        private void OnNetworkStatusChanged(bool online)
        {
            Repaint();
        }

        private void ReloadMenu()
        {
            // 编辑期间保留草稿和文件快照；保存时再检查磁盘上是否有新修改。
            if (_selectedPage is PluginConfigurationPage) return;
            _cachedConfigs = LoadMenuConfigs();
            PluginInstallService.RefreshInstallationInfo();
            _collectionPages.Clear();
            _cachedTags = PluginFilterState.CollectAllTags(_cachedConfigs);
            _batchPage = new PluginBatchPage(_cachedConfigs);
            _scanPage = new PluginRepositoryScanPage(_cachedConfigs, SaveScannedPlugins);
            if (_selectedPage is PluginBatchPage batch)
            {
                if (batch.MenuPath == null) _selectedPage = _batchPage;
            }
            else if (_selectedPage is PluginRepositoryScanPage) _selectedPage = _scanPage;
            else if (!(_selectedPage is ExOpenSourcePluginManagerSetting)) _selectedPage = _homePage;
            BuildMenuTree();
        }

        private void BuildMenuTree()
        {
            var root = new MenuNode(0, -1, "Root");
            var folders = new Dictionary<string, MenuNode>();
            int nextId = 1;
            foreach (var config in _cachedConfigs)
            {
                AddMenu(root, folders, config.Name, config, ref nextId);
                foreach (var plugin in config.Plugins)
                {
                    if (_filterState.Matches(plugin))
                        AddMenu(root, folders, plugin.MenuPath, new PluginInformationPage(plugin, config), ref nextId);
                }
            }

            var allPlugins = _cachedConfigs.SelectMany(config => config.Plugins).ToList();
            foreach (var folder in folders.Where(pair => pair.Value.Page == null))
            {
                string prefix = folder.Key + "/";
                var plugins = allPlugins.Where(plugin => plugin.MenuPath.StartsWith(prefix, StringComparison.Ordinal)).ToList();
                if (plugins.Count == 0) continue;
                if (!_collectionPages.TryGetValue(folder.Key, out var collection))
                {
                    collection = new PluginBatchPage(folder.Key, plugins);
                    _collectionPages.Add(folder.Key, collection);
                }
                folder.Value.Page = collection;
            }

            _tree = new PluginMenuTree(_treeState, root, page =>
            {
                RunAfterGUI(() =>
                {
                    if (_selectedPage is PluginConfigurationPage) return;
                    SelectPage(page);
                });
            });
            string selectedPath = _selectedPage is PluginInformationPage selectedPlugin ? selectedPlugin.PluginItem.MenuPath
                : _selectedPage is ExMenuConfig selectedMenu ? selectedMenu.Name
                : _selectedPage is PluginBatchPage selectedBatch ? selectedBatch.MenuPath : null;
            if (selectedPath != null && folders.TryGetValue(selectedPath, out var selectedNode))
            {
                if (_selectedPage is PluginBatchPage) _selectedPage = selectedNode.Page;
                _tree.SetSelection(new[] { selectedNode.id });
            }
            else
            {
                _tree.SetSelection(Array.Empty<int>());
                if (_selectedPage is PluginBatchPage missingBatch && missingBatch.MenuPath != null) _selectedPage = null;
            }
            _tree.searchString = _search;
        }

        private static void AddMenu(MenuNode root, Dictionary<string, MenuNode> folders,
            string path, object page, ref int nextId)
        {
            var parts = path.Split('/');
            var parent = root;
            string key = "";
            for (int i = 0; i < parts.Length; i++)
            {
                key = key.Length == 0 ? parts[i] : key + "/" + parts[i];
                if (!folders.TryGetValue(key, out var node))
                {
                    node = new MenuNode(nextId++, i, parts[i]);
                    parent.AddChild(node);
                    folders.Add(key, node);
                }
                parent = node;
            }
            parent.Page = page;
        }

        private void OnGUI()
        {
            bool editing = _selectedPage is PluginConfigurationPage;
            EditorGUI.BeginDisabledGroup(editing);
            DrawToolbar();
            EditorGUI.EndDisabledGroup();
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(260));
            EditorGUI.BeginDisabledGroup(editing);
            DrawSidebar();
            EditorGUI.EndDisabledGroup();
            GUILayout.EndVertical();

            GUILayout.BeginVertical(EditorStyles.helpBox);
            _detailScroll = EditorGUILayout.BeginScrollView(_detailScroll);
            if (_selectedPage is PluginConfigurationPage configuration)
                configuration.DrawGUI();
            else if (_selectedPage is ExOpenSourcePluginManagerSetting setting)
                setting.DrawGUI();
            else if (_selectedPage is PluginInformationPage plugin)
            {
                GUILayout.BeginHorizontal();
                EditorGUI.BeginDisabledGroup(PluginInstallService.IsBusy
                    || PluginInstallService.GetInstallationInfo(plugin.PluginItem).Kind == PluginInstallationKind.UnmanagedElsewhere);
                if (GUILayout.Button("编辑配置")) RunAfterGUI(() => EditPlugin(plugin.MenuConfig, plugin.PluginItem));
                if (GUILayout.Button("删除配置")) RunAfterGUI(() => DeletePluginConfiguration(plugin.MenuConfig, plugin.PluginItem));
                EditorGUI.EndDisabledGroup();
                GUILayout.EndHorizontal();
                GUILayout.Space(8);
                plugin.DrawGUI();
            }
            else if (_selectedPage is ExMenuConfig config)
                DrawConfig(config);
            else if (_selectedPage is PluginBatchPage batch)
                batch.DrawGUI();
            else if (_selectedPage is PluginRepositoryScanPage scan)
                scan.DrawGUI();
            else if (_selectedPage is ExOpenSourceHostPage host)
                host.DrawGUI(_cachedConfigs);
            else if (_selectedPage == null)
                EditorGUILayout.HelpBox("该目录没有可显示的内容或操作，请选择其他目录或插件。", MessageType.Info);
            EditorGUILayout.EndScrollView();
            PluginOperationGUI.DrawTask();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        private void DrawToolbar()
        {
            GUILayout.BeginHorizontal(EditorStyles.toolbar);
            DrawNavigationButton("首页", _homePage, _selectedPage is ExOpenSourceHostPage);
            DrawNavigationButton("批量管理", _batchPage, ReferenceEquals(_selectedPage, _batchPage));
            DrawNavigationButton("仓库扫描", _scanPage, _selectedPage is PluginRepositoryScanPage);
            DrawNavigationButton("设置", ExOpenSourcePluginManagerSetting.Instance, _selectedPage is ExOpenSourcePluginManagerSetting);
            GUILayout.Space(16);
            if (GUILayout.Button("刷新目录", EditorStyles.toolbarButton, GUILayout.Width(90))) RunAfterGUI(ReloadMenu);
            EditorGUI.BeginDisabledGroup(_cachedConfigs.Count == 0 || PluginInstallService.IsBusy);
            if (GUILayout.Button("新增插件", EditorStyles.toolbarButton, GUILayout.Width(90))) RunAfterGUI(AddPlugin);
            EditorGUI.EndDisabledGroup();
            GUILayout.FlexibleSpace();
            GUILayout.Label(NetworkStatusMonitor.HasChecked ? (NetworkStatusMonitor.IsOnline ? "在线" : "离线") : "网络检测中",
                EditorStyles.miniLabel);
            GUILayout.EndHorizontal();
        }

        private void DrawSidebar()
        {
            GUILayout.Label("插件目录", EditorStyles.boldLabel);
            _search = EditorGUILayout.TextField("搜索", _search);
            _tree.searchString = _search;
            GUILayout.BeginHorizontal();
            GUILayout.Label("状态", GUILayout.Width(36));
            var installFilter = (InstallFilter)EditorGUILayout.Popup((int)_filterState.InstallFilter, new[] { "全部", "已安装", "未安装", "已安装(未托管)" });
            if (installFilter != _filterState.InstallFilter) _filterState.SetInstallFilter(installFilter);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("标签", GUILayout.Width(36));
            var tags = new List<string> { "全部" };
            tags.AddRange(_cachedTags);
            int currentTag = string.IsNullOrEmpty(_filterState.ActiveTag) ? 0 : _cachedTags.IndexOf(_filterState.ActiveTag) + 1;
            int selectedTag = EditorGUILayout.Popup(currentTag, tags.ToArray());
            if (selectedTag != currentTag) _filterState.SetTag(selectedTag == 0 ? null : _cachedTags[selectedTag - 1]);
            GUILayout.EndHorizontal();
            if (GUILayout.Button("重置筛选", EditorStyles.miniButton)) _filterState.Reset();
            if (_cachedConfigs.Count == 0)
                EditorGUILayout.HelpBox("尚无菜单配置，请在设置中添加本地菜单或远端清单。", MessageType.Info);
            _tree.OnGUI(GUILayoutUtility.GetRect(260, 260, 0, 10000, GUILayout.ExpandHeight(true)));
        }

        private void DrawNavigationButton(string label, object page, bool selected)
        {
            if (GUILayout.Toggle(selected, label, EditorStyles.toolbarButton, GUILayout.Width(78)) && !selected)
                RunAfterGUI(() => SelectPage(page));
        }

        private void SelectPage(object page)
        {
            _selectedPage = page;
            if (page != null && !(page is ExMenuConfig) && !(page is PluginInformationPage)
                && !(page is PluginBatchPage batch && batch.MenuPath != null)) _tree.SetSelection(Array.Empty<int>());
            _detailScroll = Vector2.zero;
            Repaint();
        }

        private void DrawConfig(ExMenuConfig config)
        {
            GUILayout.Label(config.Name, ExGUIStyleHelper.TitleStyle);
            EditorGUILayout.LabelField("版本", config.Version);
            EditorGUILayout.LabelField("维护者", config.Owner);
            GUILayout.Label(config.Intro, ExGUIStyleHelper.BodyStyle);
            EditorGUILayout.LabelField("插件数量", config.Plugins.Count.ToString());
            EditorGUILayout.HelpBox("批量操作请进入顶部“批量管理”，选择本目录的插件。", MessageType.Info);
        }

        private List<ExMenuConfig> LoadMenuConfigs()
        {
            _menuFiles.Clear();
            var configs = new List<ExMenuConfig>();
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string fullPath = MenuJsonUtility.ProjectMenuPath(projectRoot);
            try
            {
                string json = MenuJsonUtility.ReadProjectMenuJson(projectRoot);
                var config = MenuJsonUtility.Parse(json, out string convertedJson);
                if (convertedJson != null)
                {
                    MenuJsonUtility.WriteProjectMenu(projectRoot, JObject.Parse(convertedJson));
                    json = File.ReadAllText(fullPath, Encoding.UTF8);
                }
                _menuFiles.Add(config, new MenuFile { Path = fullPath, Json = json });
                configs.Add(config);
            }
            catch (Exception e)
            {
                Debug.LogError($"解析 menu.json 失败: {fullPath}\n{e.Message}");
            }
            return configs;
        }

        private void AddPlugin()
        {
            ExMenuConfig menu;
            if (_selectedPage is ExMenuConfig selectedMenu)
                menu = selectedMenu;
            else if (_selectedPage is PluginInformationPage selectedPlugin)
                menu = selectedPlugin.MenuConfig;
            else
                menu = _cachedConfigs[0];
            EditPlugin(menu, null);
        }

        // source 为 null 表示新增，非 null 必须是该菜单当前加载的条目。
        private void EditPlugin(ExMenuConfig menu, ExPluginItem source)
        {
            object previousPage = _selectedPage;
            _selectedPage = new PluginConfigurationPage(menu, source,
                draft => SavePluginConfiguration(menu, source, draft),
                () => { _selectedPage = previousPage; _detailScroll = Vector2.zero; Repaint(); });
            _detailScroll = Vector2.zero;
            Repaint();
        }

        private void SavePluginConfiguration(ExMenuConfig menu, ExPluginItem source, ExPluginItem draft)
        {
            if (draft.MenuPath == "首页" || draft.MenuPath == "设置" || draft.MenuPath == "批量管理" || draft.MenuPath == "仓库扫描"
                || _cachedConfigs.Exists(config => config.Name == draft.MenuPath))
                throw new InvalidDataException("菜单位置需要包含插件节点，不能直接占用首页、设置或菜单名称。");
            foreach (var config in _cachedConfigs)
            {
                foreach (var item in config.Plugins)
                {
                    if (ReferenceEquals(item, source)) continue;
                    if (item.MenuPath == draft.MenuPath)
                        throw new InvalidDataException("该菜单位置已经存在插件，请使用不同的菜单位置。");
                    if (string.Equals(item.LocalPath.Trim().Replace('\\', '/').TrimEnd('/'), draft.LocalPath,
                        StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("该安装目录已被其他插件配置使用，请编辑原条目或选择另一目录。");
                }
            }

            var document = ReadMenuForChange(menu);
            var plugins = (JArray)document["Plugins"];
            if (source != null && (source.LocalPath != draft.LocalPath
                || source.GitURL_Username != draft.GitURL_Username || source.GitURL_RepoName != draft.GitURL_RepoName
                || source.GitURL_Path != draft.GitURL_Path || source.GitURL_Branch != draft.GitURL_Branch))
                draft.Installation = null;
            var replacement = JObject.FromObject(draft);
            if (source == null)
                plugins.Add(replacement);
            else
            {
                int index = menu.Plugins.IndexOf(source);
                // 合并原版已知字段，保留 JSON 中额外的自定义字段。
                var existing = (JObject)plugins[index];
                foreach (var property in replacement.Properties()) existing[property.Name] = property.Value;
                if (draft.Installation == null) existing.Remove("Installation");
            }
            string path = _menuFiles[menu].Path;
            WriteMenu(menu, document);
            _selectedPage = null;
            ReloadMenu();
            _search = "";
            _filterState.Reset();
            foreach (var config in _cachedConfigs)
            {
                if (_menuFiles[config].Path != path) continue;
                var savedPlugin = config.Plugins.Find(item => item.MenuPath == draft.MenuPath);
                _selectedPage = new PluginInformationPage(savedPlugin, config);
                break;
            }
            _detailScroll = Vector2.zero;
            var installation = PluginInstallService.GetInstallationInfo(draft);
            ShowNotification(new GUIContent(installation.Kind == PluginInstallationKind.UnmanagedElsewhere
                ? "配置已保存：已安装(未托管)，请开发者自行处理"
                : "配置已保存，已检查安装状态，插件文件未改动"));
            Repaint();
        }

        private void DeletePluginConfiguration(ExMenuConfig menu, ExPluginItem plugin)
        {
            if (!EditorUtility.DisplayDialog("删除插件配置",
                $"从菜单移除“{plugin.Name}”？\n这里只删除配置，已安装的插件文件会保留。", "删除配置", "取消")) return;
            var document = ReadMenuForChange(menu);
            ((JArray)document["Plugins"]).RemoveAt(menu.Plugins.IndexOf(plugin));
            WriteMenu(menu, document);
            ReloadMenu();
            ShowNotification(new GUIContent("配置已删除，插件文件保留"));
        }

        private void SaveScannedPlugins(ExMenuConfig menu, List<ExPluginItem> additions)
        {
            var paths = new HashSet<string>(_cachedConfigs.SelectMany(c => c.Plugins).Select(p => p.MenuPath), StringComparer.Ordinal);
            var destinations = new HashSet<string>(_cachedConfigs.SelectMany(c => c.Plugins)
                .Select(p => p.LocalPath.Trim().Replace('\\', '/').TrimEnd('/')), StringComparer.OrdinalIgnoreCase);
            var document = ReadMenuForChange(menu);
            var plugins = (JArray)document["Plugins"];
            foreach (var source in additions)
            {
                var draft = JsonConvert.DeserializeObject<ExPluginItem>(JsonConvert.SerializeObject(source));
                draft.MenuPath = menu.Name + "/" + draft.LocalPath.Substring(draft.LocalPath.LastIndexOf('/') + 1);
                if (!paths.Add(draft.MenuPath)) throw new InvalidDataException("菜单位置重复，请取消重复项或编辑已有条目: " + draft.MenuPath);
                if (!destinations.Add(draft.LocalPath)) throw new InvalidDataException("安装目录已被配置使用: " + draft.LocalPath);
                plugins.Add(JObject.FromObject(draft));
            }
            MenuJsonUtility.Parse(document.ToString(Formatting.None), out _);
            WriteMenu(menu, document);
            ReloadMenu();
            SelectPage(_batchPage);
            int unmanaged = additions.Count(plugin => PluginInstallService.GetInstallationInfo(plugin).Kind == PluginInstallationKind.UnmanagedElsewhere);
            ShowNotification(new GUIContent("已保存 " + additions.Count + " 个独立插件配置"
                + (unmanaged > 0 ? "；其中 " + unmanaged + " 个已安装(未托管)，需自行处理" : "；已检查安装状态，未执行安装")));
            Repaint();
        }

        private JObject ReadMenuForChange(ExMenuConfig menu)
        {
            var file = _menuFiles[menu];
            string configRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "../", ExOpenSourceConstParam.CONFIG_ROOT))
                + Path.DirectorySeparatorChar;
            if (!file.Path.StartsWith(configRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("UI 编辑只保存到管理器自身的 Config~ 目录。");
            if (MenuJsonUtility.ReadProjectMenuJson(Path.GetFullPath(Path.Combine(Application.dataPath, ".."))) != file.Json)
                throw new InvalidOperationException("菜单文件已经被修改，请取消编辑并刷新目录后重试。");
            var document = JObject.Parse(file.Json);
            if (!(document["Plugins"] is JArray)) throw new InvalidDataException("菜单必须包含 Plugins 数组。");
            return document;
        }

        // 文件路径和磁盘快照由 ReadMenuForChange 检查，当前调用链在编辑器主线程完成。
        private void WriteMenu(ExMenuConfig menu, JObject document)
        {
            MenuJsonUtility.WriteProjectMenu(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), document);
        }

        public static void RunAfterGUI(Action action)
        {
            EditorApplication.delayCall += () =>
            {
                try { action(); }
                catch (Exception e) { Debug.LogException(e); }
            };
        }

        private class MenuFile
        {
            public string Path;
            public string Json;
        }

        private class MenuNode : TreeViewItem
        {
            public object Page;

            public MenuNode(int id, int depth, string label) : base(id, depth, label) { }
        }

        private class PluginMenuTree : TreeView
        {
            private readonly MenuNode _root;
            private readonly Action<object> _selectPage;

            public PluginMenuTree(TreeViewState state, MenuNode root, Action<object> selectPage) : base(state)
            {
                _root = root;
                _selectPage = selectPage;
                Reload();
                // 顶层菜单展开，合集默认收起，避免大量插件占满侧栏；空菜单没有可展开节点。
                SetExpanded(root.hasChildren ? root.children.Select(item => item.id).ToArray() : Array.Empty<int>());
            }

            protected override TreeViewItem BuildRoot()
            {
                SetupDepthsFromParentsAndChildren(_root);
                return _root;
            }

            protected override void SelectionChanged(IList<int> selectedIds)
            {
                if (selectedIds.Count == 0) return;
                var node = (MenuNode)FindItem(selectedIds[0], rootItem);
                _selectPage(node == null ? null : node.Page);
            }
        }
    }
}
