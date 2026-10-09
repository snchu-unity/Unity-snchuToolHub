using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ExOpenSource.Editor
{
    /// <summary>snchuToolHub 首页，展示安装规则、使用流程和制作理念。</summary>
    public class ExOpenSourceHostPage
    {
        // 首页外观集中在这里调整；不会改变其他页面的通用样式。
        private const int TitleFontSize = 30;
        private const int SectionTitleFontSize = 18;
        private const int BodyFontSize = 13;
        private const int StatisticFontSize = 24;
        private const int CardPadding = 16;
        private const float SectionSpacing = 12f;

        private GUIStyle _titleStyle;
        private GUIStyle _sectionTitleStyle;
        private GUIStyle _bodyStyle;
        private GUIStyle _subtitleStyle;
        private GUIStyle _statisticStyle;
        private GUIStyle _statisticLabelStyle;
        private GUIStyle _cardStyle;
        private GUIStyle _statisticCardStyle;
        private bool _darkTheme;
        private Color _accentColor;

        public const string InstallationRules =
            "1. 已安装\n"
            + "插件位于配置的目标目录，安装按钮禁用。已由 ToolHub 管理的可以更新、重装或卸载；尚未接管的真实目录，确认内容和来源后可选择“接管现有目录”。\n\n"
            + "2. 未安装\n"
            + "前置检查未发现已知安装，且目标目录不存在或为空，允许点击“安装”。安装入口和准备阶段会再次检查重复，发现同一插件即中断并提示位置。\n\n"
            + "3. 已安装(未托管)\n"
            + "已发现插件安装在其他位置，包括由 UPM 管理的包。当前条目不受 ToolHub 管理，全部操作按钮禁用；这里只显示已有位置和提示，由开发者自行处理。\n\n"
            + "保存配置、加载或刷新目录时进行前置检查；目录占用或管理记录异常时，以条目的具体提示为准。";

        /// <summary>菜单由窗口加载并校验，首页直接使用窗口缓存。</summary>
        public void DrawGUI(IEnumerable<ExMenuConfig> menus)
        {
            EnsureStyles();
            var plugins = menus.SelectMany(menu => menu.Plugins).ToList();
            var installations = plugins.Select(PluginInstallService.GetInstallationInfo).ToList();
            int installed = installations.Count(installation => installation.IsInstalled
                && installation.Kind != PluginInstallationKind.UnmanagedElsewhere);
            int uninstalled = installations.Count(installation => PluginInstallService.CanOperate(installation, PluginOperationKind.Install, out _));
            int unmanaged = installations.Count(installation => installation.Kind == PluginInstallationKind.UnmanagedElsewhere);
            GUILayout.Space(8);
            GUILayout.BeginVertical(_cardStyle);
            GUILayout.Label(ExOpenSourceConstParam.POOF_LIB_MGR, _titleStyle);
            GUILayout.Space(4);
            GUILayout.Label("管理项目中的 GitHub 插件：配置、安装、更新与卸载。", _subtitleStyle);
            GUILayout.Space(12);
            EditorGUI.DrawRect(EditorGUILayout.GetControlRect(false, 2f), _accentColor);
            GUILayout.EndVertical();

            GUILayout.Space(SectionSpacing);
            GUILayout.BeginHorizontal();
            DrawStatistic("目录插件", plugins.Count);
            GUILayout.Space(8);
            DrawStatistic("已安装", installed);
            GUILayout.Space(8);
            DrawStatistic("未安装", uninstalled);
            GUILayout.Space(8);
            DrawStatistic("已安装(未托管)", unmanaged);
            GUILayout.EndHorizontal();

            DrawSection("三种安装状态", InstallationRules);

            DrawSection("使用流程",
                "1. 单个插件用“新增插件”填写来源和安装目录；同仓库多包用“仓库扫描”保存配置。\n"
                + "2. 左侧选择插件或目录合集；跨目录多选使用“批量管理”。\n"
                + "3. 点击“安装”；批量安装只处理允许安装的条目，没有可处理项时按钮变灰。\n"
                + "4. “设置”管理菜单与 GitHub 令牌；保存后的配置可以重复使用。\n"
                + "安装位置：Assets 用于普通插件，Packages 用于嵌入包并由 Unity 解析依赖。");

            DrawSection("日常维护",
                "• 检查更新：查看远端变化和本地修改。\n"
                + "• 更新：只替换有变化的插件；本地修改会阻止普通更新。\n"
                + "• 重装：确认后重新准备当前分支内容，原文件先存入临时备份。\n"
                + "• 卸载：先检查依赖，再将文件移入临时备份。删除配置只移除条目，保留已安装文件。\n"
                + "• 缓存清理：成功、失败、取消后均清理下载、暂存和旧备份；提交失败先回滚。启动时清理中断残留，只有未完成回滚的当前恢复目录暂留。下次更新重新获取远端，安装记录保留。\n"
                + "• 接管：登记目标的现有文件及基线，来源提交暂记未知；没有文件可接管时按钮禁用。\n"
                + "• 管理记录异常：文件缺失可安装恢复；来源变更或旧记录缺少基线时，按条目提示处理。\n"
                + "• 批量操作：只处理具备权限的所选项；出现重复时整批停止，目标文件和安装记录不提交。");

            DrawSection("制作理念",
                "• 配置一次，长期复用：每个插件独立记录完整来源和安装目录。\n"
                + "• 同仓库共享下载：按仓库和分支获取一次，再安装选中的目录。\n"
                + "• 管理权来自安装记录或主动接管；外部插件只检测，由开发者管理。\n"
                + "• 单包与批量使用同一流程，统一处理状态、权限、依赖和冲突。\n"
                + "• 工具自有配置、记录和缓存集中保存；工具代码仅在编辑器运行。");
            GUILayout.Space(8);
        }

        private void DrawStatistic(string label, int value)
        {
            GUILayout.BeginVertical(_statisticCardStyle, GUILayout.MinWidth(0), GUILayout.ExpandWidth(true));
            GUILayout.Label(value.ToString(), _statisticStyle);
            GUILayout.Label(label, _statisticLabelStyle, GUILayout.Height(34));
            GUILayout.EndVertical();
        }

        private void DrawSection(string title, string text)
        {
            GUILayout.Space(SectionSpacing);
            GUILayout.BeginVertical(_cardStyle);
            GUILayout.Label(title, _sectionTitleStyle);
            GUILayout.Space(8);
            GUILayout.Label(text, _bodyStyle);
            GUILayout.EndVertical();
        }

        /// <summary>仅在 OnGUI 中创建并缓存样式；编辑器主题变化时重新创建。</summary>
        private void EnsureStyles()
        {
            bool darkTheme = EditorGUIUtility.isProSkin;
            if (_titleStyle != null && _darkTheme == darkTheme) return;
            _darkTheme = darkTheme;

            // 品牌强调色与说明文字色；可自行修改这里的 RGB 值。
            _accentColor = darkTheme ? new Color(0.42f, 0.70f, 1f) : ExBrandStyle.Primary;
            Color secondaryColor = darkTheme ? new Color(0.72f, 0.75f, 0.80f) : new Color(0.38f, 0.42f, 0.47f);

            _titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = TitleFontSize,
                wordWrap = true,
                fixedHeight = 0
            };
            _titleStyle.normal.textColor = _accentColor;
            _sectionTitleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = SectionTitleFontSize,
                wordWrap = true,
                fixedHeight = 0
            };
            _bodyStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = BodyFontSize,
                wordWrap = true,
                richText = true,
                alignment = TextAnchor.UpperLeft
            };
            _subtitleStyle = new GUIStyle(_bodyStyle);
            _subtitleStyle.normal.textColor = secondaryColor;
            _statisticStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = StatisticFontSize,
                fixedHeight = 0
            };
            _statisticStyle.normal.textColor = _accentColor;
            _statisticLabelStyle = new GUIStyle(_bodyStyle) { fontSize = 12 };
            _statisticLabelStyle.normal.textColor = secondaryColor;
            _cardStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(CardPadding, CardPadding, 14, 16),
                margin = new RectOffset(0, 0, 0, 0)
            };
            _statisticCardStyle = new GUIStyle(_cardStyle)
            {
                padding = new RectOffset(12, 12, 10, 10)
            };
        }
    }
}
