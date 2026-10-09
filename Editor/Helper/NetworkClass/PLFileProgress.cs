namespace ExOpenSource.Editor
{
    /// <summary>
    /// 文件下载进度
    /// </summary>
    public class PLFileProgress
    {
        public string FileName;
        public float Progress;
        public long DownloadedBytes;
        public long TotalBytes;
        public double Speed; // bytes per second
    }
}