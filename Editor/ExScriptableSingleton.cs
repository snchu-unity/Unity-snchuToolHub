#if UNITY_EDITOR
using System.Linq;
using UnityEngine;

namespace ExOpenSource.Editor
{
    /// <summary>只在编辑器会话中保留状态，不写入设置文件。</summary>
    public class ExScriptableSingleton<T> : ScriptableObject where T : ScriptableObject
    {
        private static T s_Instance;

        public static T Instance
        {
            get
            {
                if (!s_Instance)
                {
                    // 隐藏对象可跨脚本重载保留会话字段，但不会保存到项目。
                    s_Instance = Resources.FindObjectsOfTypeAll<T>().FirstOrDefault();
                    if (!s_Instance) s_Instance = CreateInstance<T>();
                    s_Instance.hideFlags = HideFlags.HideAndDontSave;
                }
                return s_Instance;
            }
        }
    }
}
#endif
