using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ExOpenSource.Editor
{
    /// <summary>单插件页和批量页共享的操作入口、任务展示。</summary>
    public static class PluginOperationGUI
    {
        private static bool _showResults;
        private static Vector2 _resultsScroll;

        public static string Label(PluginOperationKind kind)
        {
            switch (kind)
            {
                case PluginOperationKind.Install: return "安装";
                case PluginOperationKind.CheckUpdates: return "检查更新";
                case PluginOperationKind.Update: return "更新";
                case PluginOperationKind.Reinstall: return "重装";
                case PluginOperationKind.Uninstall: return "卸载";
                case PluginOperationKind.Adopt: return "接管现有目录";
                default: return "仓库扫描";
            }
        }

        public static void Request(PluginOperationKind kind, IEnumerable<ExPluginItem> selection)
        {
            var plugins = selection.ToList();
            if (plugins.Count == 0) { EditorUtility.DisplayDialog("无法执行", "没有符合操作条件的插件。", "确定"); return; }
            foreach (var plugin in plugins)
                if (!PluginInstallService.CanOperate(PluginInstallService.GetInstallationInfo(plugin), kind, out string reason))
                {
                    EditorUtility.DisplayDialog("无法执行" + Label(kind), plugin.Name + "：" + reason, "确定");
                    return;
                }
            string explanation;
            switch (kind)
            {
                case PluginOperationKind.CheckUpdates: explanation = null; break;
                case PluginOperationKind.Adopt:
                    explanation = "登记所选目录的当前文件，接受配置中填写的来源。不会下载或修改插件文件；当前来源提交记为未知。";
                    break;
                case PluginOperationKind.Uninstall:
                    explanation = "所选插件文件（含本地改动）将先移入临时备份，随后移除对应本地包入口。成功后清理；文件提交失败时先回滚，再清理。只有回滚未完成的恢复数据暂留。共享外部依赖会保留。";
                    break;
                case PluginOperationKind.Reinstall:
                    explanation = "重新下载当前分支，并覆盖所选插件的本地修改。旧文件先保存在临时备份，成功后清理；文件提交失败时先回滚，再清理。只有回滚未完成的恢复数据暂留。";
                    break;
                default:
                    explanation = "按配置处理所选插件。Packages 下的包由 Unity 作为嵌入包识别，并统一解析依赖。";
                    break;
            }
            if (explanation != null && !EditorUtility.DisplayDialog(Label(kind) + "插件",
                "选中 " + plugins.Count + " 个插件。\n\n" + explanation + "\n\n"
                + string.Join("\n", plugins.Take(8).Select(p => p.Name + " → " + p.LocalPath)), Label(kind), "取消")) return;
            try
            {
                if (!PluginInstallService.TryStart(kind, plugins, out string startError))
                    EditorUtility.DisplayDialog("无法执行" + Label(kind), startError, "确定");
            }
            catch (Exception error)
            {
                EditorUtility.DisplayDialog("无法执行" + Label(kind), error.Message, "确定");
                Debug.LogException(error);
            }
        }

        public static void DrawTask()
        {
            var state = PluginInstallService.State;
            if (state.Phase == PluginOperationPhase.Idle) return;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(Label(state.Kind) + " · "
                + (PluginInstallService.IsCleaningCache ? "清理缓存" : PhaseLabel(state.Phase)), EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (PluginInstallService.CanCancel && GUILayout.Button("取消", GUILayout.Width(65)))
                ExOpenSourcePluginManagerWindow.RunAfterGUI(PluginInstallService.Cancel);
            if (PluginInstallService.CanRetryPackages && GUILayout.Button("重试包解析", GUILayout.Width(100)))
                ExOpenSourcePluginManagerWindow.RunAfterGUI(PluginInstallService.RetryPackages);
            EditorGUI.BeginDisabledGroup(PluginInstallService.IsCleaningCache || !Directory.Exists(PluginInstallService.CacheDirectory));
            if (GUILayout.Button("打开缓存 / 备份", GUILayout.Width(130)))
                ExOpenSourcePluginManagerWindow.RunAfterGUI(() =>
                {
                    if (Directory.Exists(PluginInstallService.CacheDirectory))
                        EditorUtility.RevealInFinder(PluginInstallService.CacheDirectory);
                });
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
            if (PluginInstallService.IsBusy)
                EditorGUI.ProgressBar(GUILayoutUtility.GetRect(0, 18, GUILayout.ExpandWidth(true)), state.Progress,
                    PluginInstallService.IsCleaningCache ? "正在清理任务工作缓存..." : state.Message);
            else
                EditorGUILayout.HelpBox(state.Message ?? "", state.Phase == PluginOperationPhase.Failed ? MessageType.Error : MessageType.Info);
            if (state.Rows.Count > 0)
            {
                _showResults = EditorGUILayout.Foldout(_showResults, "各插件结果（" + state.Rows.Count + "）", true);
                if (_showResults)
                {
                    _resultsScroll = EditorGUILayout.BeginScrollView(_resultsScroll, GUILayout.MaxHeight(160));
                    foreach (var row in state.Rows)
                        EditorGUILayout.LabelField(new GUIContent(row.Name, row.LocalPath), new GUIContent(row.Result, row.Commit));
                    EditorGUILayout.EndScrollView();
                }
            }
            EditorGUILayout.EndVertical();
        }

        private static string PhaseLabel(PluginOperationPhase phase)
        {
            switch (phase)
            {
                case PluginOperationPhase.Preparing: return "准备文件";
                case PluginOperationPhase.Committing: return "提交文件";
                case PluginOperationPhase.Resolving: return "解析依赖";
                case PluginOperationPhase.Completed: return "已完成";
                case PluginOperationPhase.Failed: return "失败";
                case PluginOperationPhase.Cancelled: return "已取消";
                case PluginOperationPhase.Interrupted: return "已中断";
                default: return "空闲";
            }
        }
    }
}
