namespace ExOpenSource.Editor
{
    /// <summary>
    /// 下载状态
    /// </summary>
    public enum PLDownloadState
    {
        Ready,
        ScanningFolder,
        DownloadingFile,
        Completed,
        Failed
    }
}