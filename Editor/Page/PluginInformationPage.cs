using UnityEditor;
using UnityEngine;

namespace ExOpenSource.Editor
{
    public class PluginInformationPage
    {
        public ExPluginItem PluginItem { get; }
        public ExMenuConfig MenuConfig { get; }

        public PluginInformationPage(ExPluginItem pluginItem, ExMenuConfig menuConfig)
        {
            PluginItem = pluginItem;
            MenuConfig = menuConfig;
        }

        public void DrawGUI()
        {
            GUILayout.Label(PluginItem.Name, ExGUIStyleHelper.TitleStyle);
            var installation = PluginInstallService.GetInstallationInfo(PluginItem);
            EditorGUILayout.LabelField("状态", installation.Label);
            EditorGUILayout.LabelField("配置目标", PluginInstallService.DisplayPath(installation.ActualPath));
            if (installation.Kind == PluginInstallationKind.UnmanagedElsewhere)
                EditorGUILayout.LabelField("已有位置", PluginInstallService.DisplayPath(installation.ExistingPath));
            EditorGUILayout.HelpBox(installation.Message, installation.Kind == PluginInstallationKind.UnmanagedElsewhere
                || installation.CanAdopt ? MessageType.Warning : MessageType.Info);
            if (!string.IsNullOrEmpty(installation.OperationError))
                EditorGUILayout.HelpBox(installation.OperationError, MessageType.Error);
            EditorGUILayout.LabelField("配置版本", PluginItem.Version);
            EditorGUILayout.LabelField("作者", PluginItem.Author);
            GUILayout.Label(PluginItem.Intro ?? "", ExGUIStyleHelper.BodyStyle);
            EditorGUILayout.LabelField("仓库", PluginItem.GitURL_Username + "/" + PluginItem.GitURL_RepoName);
            EditorGUILayout.LabelField("分支 / 标签", PluginItem.GitURL_Branch);
            EditorGUILayout.LabelField("来源目录", PluginItem.GitURL_Path);
            EditorGUILayout.LabelField("安装目录", PluginItem.LocalPath);
            var record = PluginInstallService.FindRecord(PluginItem.LocalPath);
            if (installation.IsManaged)
            {
                EditorGUILayout.LabelField("实际提交", string.IsNullOrEmpty(record.commit) ? "未知（接管后尚未更新）" : record.commit);
                EditorGUILayout.LabelField("来源目录版本", string.IsNullOrEmpty(record.treeHash) ? "未知" : record.treeHash);
            }
            if (PluginItem.Dependencies != null && PluginItem.Dependencies.Length > 0)
            {
                GUILayout.Label("外部 UPM 依赖", EditorStyles.boldLabel);
                foreach (var dependency in PluginItem.Dependencies)
                    EditorGUILayout.LabelField(dependency.name, dependency.version);
            }
            GUILayout.Space(10);
            EditorGUI.BeginDisabledGroup(PluginInstallService.IsBusy || installation.Kind == PluginInstallationKind.UnmanagedElsewhere);
            EditorGUILayout.BeginHorizontal();
            DrawAction(PluginOperationKind.Install, installation);
            DrawAction(PluginOperationKind.CheckUpdates, installation);
            DrawAction(PluginOperationKind.Update, installation);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            DrawAction(PluginOperationKind.Reinstall, installation);
            DrawAction(PluginOperationKind.Uninstall, installation);
            DrawAction(PluginOperationKind.Adopt, installation);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!System.IO.Directory.Exists(PluginInstallFiles.FileSystemPath(installation.ActualPath))))
            {
                if (GUILayout.Button("打开安装目录"))
                    ExOpenSourcePluginManagerWindow.RunAfterGUI(() => EditorUtility.RevealInFinder(installation.ActualPath));
            }
            if (!string.IsNullOrEmpty(PluginItem.IntroductionURL) && GUILayout.Button("打开说明"))
                ExOpenSourcePluginManagerWindow.RunAfterGUI(() => Application.OpenURL(PluginItem.IntroductionURL));
            EditorGUILayout.EndHorizontal();
            EditorGUI.EndDisabledGroup();
        }

        private void DrawAction(PluginOperationKind kind, PluginInstallationInfo installation)
        {
            bool allowed = PluginInstallService.CanOperate(installation, kind, out string reason);
            using (new EditorGUI.DisabledScope(!allowed))
            {
                if (GUILayout.Button(new GUIContent(PluginOperationGUI.Label(kind), reason)))
                    ExOpenSourcePluginManagerWindow.RunAfterGUI(() => PluginOperationGUI.Request(kind, new[] { PluginItem }));
            }
        }
    }
}
