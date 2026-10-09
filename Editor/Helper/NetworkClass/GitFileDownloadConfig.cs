namespace ExOpenSource.Editor
{
    /// <summary>
    /// 文件下载配置
    /// </summary>
    public class GitFileDownloadConfig
    {
        public string Username;
        public string Repository;
        public string Branch;
        public string RemoteFilePath;
        public string LocalFilePath;
        public string GitToken = "";
    }
}