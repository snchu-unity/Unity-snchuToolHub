#if UNITY_EDITOR
namespace ExOpenSource.Editor
{
    using System;
    using System.IO;
    using System.Linq;
    using UnityEditorInternal;
    using UnityEngine;

    public class ExScriptableSingleton<T> : ScriptableObject where T : ScriptableObject
    {
        private static T s_Instance;
        public static T Instance
        {
            get
            {
                if (!s_Instance)
                {
                    LoadOrCreate();
                }
                return s_Instance;
            }
        }
        public static T LoadOrCreate()
        {
            string filePath = GetFilePath();
            if (string.IsNullOrEmpty(filePath))
                throw new InvalidOperationException($"save location of {typeof(T).Name} is invalid");
            // 首次使用允许没有设置文件；已存在但损坏或类型不符的设置必须明确报错。
            if (!File.Exists(filePath))
            {
                s_Instance = CreateInstance<T>();
                return s_Instance;
            }
            var objects = InternalEditorUtility.LoadSerializedFileAndForget(filePath);
            if (objects.Length != 1 || !(objects[0] is T))
                throw new InvalidDataException("设置文件内容或类型无效: " + filePath);
            s_Instance = (T)objects[0];
            return s_Instance;
        }

        public static void Save(bool saveAsText = true)
        {
            if (!s_Instance) return;

            string filePath = GetFilePath();
            if (!string.IsNullOrEmpty(filePath))
            {
                string directoryName = Path.GetDirectoryName(filePath);
                if (!Directory.Exists(directoryName))
                {
                    Directory.CreateDirectory(directoryName);
                }
                UnityEngine.Object[] obj = new T[1] { s_Instance };
                InternalEditorUtility.SaveToSerializedFileAndForget(obj, filePath, saveAsText);
                //Debug.Log($"Saved ScriptableSingleton to {filePath}");
            }
        }

        public static void UpdateAsset(T asset)
        {
            if (asset == null) return;
            s_Instance = asset;
        }

        protected static string GetFilePath()
        {
            var location = typeof(T).GetCustomAttributes(true)
                .Where(v => v is PLFilePathAttribute)
                .Cast<PLFilePathAttribute>()
                .FirstOrDefault()
                ?.filepath;
            return location == null ? null : Path.GetFullPath(Path.Combine(Application.dataPath, "..",
                ExOpenSourceConstParam.TOOL_ROOT, location));
        }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public class PLFilePathAttribute : Attribute
    {
        public readonly string filepath;
        /// <summary>
        /// 单例存放路径
        /// </summary>
        /// <param name="path">相对工具根目录的路径</param>
        public PLFilePathAttribute(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("Invalid relative path (it is empty)");
            }
            if (path[0] == '/')
            {
                path = path.Substring(1);
            }
            filepath = path;
        }
    }
}
#endif
