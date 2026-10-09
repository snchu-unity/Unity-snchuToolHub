using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ExOpenSource.Editor
{
    [Serializable]
    public struct RepoInfo
    {

        public string userName;


        public string repoName;


        public string branch;


        public string remoteMenuPath;

        [Space]
        [Delayed]






        public string localMenuPath;





        public string rawContentGitRepoUrl
        {
            get
            {
                var gitRepoUrl = ExOpenSourceNetworkHelper.FormatGitHubUrl(userName, repoName, branch, remoteMenuPath);
                return $"<color={ExBrandStyle.HexTextPrimary}>下载内容地址(Raw URL):{gitRepoUrl}</color>";
            }
        }






        private string MenuVersion
        {
            get
            {
                var version = "--";
                var fullPath = Path.Combine(Application.dataPath, "../", localMenuPath);
                if (!File.Exists(fullPath)) Debug.LogWarning($"找不到目录配置文件: {fullPath}");
                try
                {
                    var json = File.ReadAllText(fullPath);
                    var config = JsonUtility.FromJson<ExMenuConfig>(json);
                    version = config.Version;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"解析配置文件失败: {e.Message}");
                }

                return $"<color={ExBrandStyle.HexTextPrimary}>菜单目录版本: {version}</color>";
            }
        }

        public void LoadMenu()
        {
            ExOpenSourceNetworkHelper.DownloadFile(GetGitFileDownloadConfig());
        }

        public void UpdateMenu()
        {
            LoadMenu();
        }

        /// <summary>
        ///     打开菜单目录在资源管理器中
        /// </summary>
        public void OpenMenuInExplore()
        {
            var fullPath = Path.Combine(Application.dataPath, "../", localMenuPath);
            if (File.Exists(fullPath))
                EditorUtility.RevealInFinder(fullPath);
            else
                Debug.LogWarning($"菜单文件不存在: {fullPath}");
        }

        private bool ExistMenuJson()
        {
            var isExist = File.Exists(Path.Combine(Application.dataPath, "../", localMenuPath));
            return isExist;
        }

        private GitFileDownloadConfig GetGitFileDownloadConfig()
        {
            return new GitFileDownloadConfig
            {
                Username = userName,
                Repository = repoName,
                Branch = branch,
                RemoteFilePath = remoteMenuPath,
                LocalFilePath = localMenuPath,
                GitToken = ExOpenSourcePluginManagerSetting.Instance.ReadToken()
            };
        }

        public static bool Equal(RepoInfo a,RepoInfo b)
        {
            return a.repoName == b.repoName
                   && a.userName == b.userName
                   && a.branch == b.branch
                   && a.remoteMenuPath == b.remoteMenuPath;
        }
    }
}
