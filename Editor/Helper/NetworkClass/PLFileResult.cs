namespace ExOpenSource.Editor
{
    /// <summary>
    /// 文件下载结果
    /// </summary>
    public class PLFileResult
    {
        public bool IsSuccess;
        public string ErrorMessage;
        public string RemotePath;
        public string LocalPath;
        public long FileSize;
        public double DownloadTime; // in seconds
    }
}