using System.Collections.Generic;

namespace ExOpenSource.Editor
{
    /// <summary>菜单元数据与插件列表，下载来源由每个插件独立保存。</summary>
    [System.Serializable]
    public class ExMenuConfig
    {
        public string Name;

        public string Version;

        public string Owner;

        public string Intro;

        public List<ExPluginItem> Plugins = new List<ExPluginItem>();
    }
}
