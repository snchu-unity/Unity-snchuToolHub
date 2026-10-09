using System.Collections.Generic;

namespace ExOpenSource.Editor
{
    /// <summary>
    /// 下载结果
    /// </summary>
    public class PLDownloadResult
    {
        public bool IsSuccess;
        public string Message;
        public int TotalFiles;
        public int SuccessCount;
        public int FailCount;
        public double TotalTime; // in seconds
        public List<PLFileResult> FileResults = new List<PLFileResult>();
    }
}