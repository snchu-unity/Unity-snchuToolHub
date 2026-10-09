using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ExOpenSource.Editor
{
    /// <summary>异步 Git 与仓库缓存。调用方按来源分组，并串行使用同一缓存。</summary>
    public static class GitPluginUtility
    {
        public static string RepositoryUrl(ExPluginItem plugin)
            => $"https://github.com/{plugin.GitURL_Username}/{plugin.GitURL_RepoName}.git";

        public static string RepositoryKey(ExPluginItem plugin)
            => RepositoryUrl(plugin).ToLowerInvariant() + "\n" + plugin.GitURL_Branch;

        public static async Task<GitRepositorySnapshot> FetchAsync(string cacheRoot, ExPluginItem plugin,
            string token, Action<string> report, CancellationToken cancellation)
        {
            string url = RepositoryUrl(plugin);
            string branch = plugin.GitURL_Branch;
            string cacheHash = PluginInstallFiles.HashText(RepositoryKey(plugin));
            string directory = PluginInstallFiles.ContainedPath(cacheRoot,
                "Repositories/" + cacheHash.Substring(0, 24));
            Directory.CreateDirectory(Path.GetDirectoryName(directory));
            // 缩短工作树路径；已有的完整哈希缓存原地迁移，继续复用仓库内容。
            string legacyDirectory = PluginInstallFiles.ContainedPath(cacheRoot, "Repositories/" + cacheHash);
            if (!Directory.Exists(directory) && Directory.Exists(legacyDirectory))
                Directory.Move(legacyDirectory, directory);
            string commitReference;
            if (!Directory.Exists(directory))
            {
                string temporary = directory + ".new-" + Guid.NewGuid().ToString("N").Substring(0, 16);
                report("首次克隆 " + plugin.GitURL_Username + "/" + plugin.GitURL_RepoName);
                try
                {
                    await RunAsync("clone --config core.longpaths=true --depth 1 --filter=blob:none --no-checkout --no-tags --branch "
                        + Quote(branch) + " " + Quote(url) + " " + Quote(temporary), cacheRoot, token, null, cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    Directory.Move(PluginInstallFiles.ContainedPath(cacheRoot, temporary.Substring(cacheRoot.Length + 1)), directory);
                }
                finally { PluginInstallFiles.DeleteCacheDirectory(cacheRoot, temporary); }
                commitReference = "HEAD";
            }
            else
            {
                // 读取保存的来源；remote get-url 会应用用户的 insteadOf URL 改写，不能用于此校验。
                string remote = (await RunAsync("config --local --get remote.origin.url", directory, token, null, cancellation)).Trim();
                if (!string.Equals(remote, url, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("缓存仓库来源不匹配，请检查该缓存目录: " + directory);
                // 检出期间的部分路径操作需要在缓存仓库初始化配置时就读到此选项。
                await RunAsync("config --local core.longpaths true", directory, token, null, cancellation);
                report("获取仓库变化 " + plugin.GitURL_Username + "/" + plugin.GitURL_RepoName);
                await RunAsync("fetch --depth 1 --filter=blob:none --no-tags origin " + Quote(branch),
                    directory, token, null, cancellation);
                commitReference = "FETCH_HEAD";
            }
            string commit = (await RunAsync("rev-parse " + commitReference, directory, token, null, cancellation)).Trim();
            return new GitRepositorySnapshot { Directory = directory, Url = url, Branch = branch, Commit = commit };
        }

        public static async Task CheckoutDirectoriesAsync(GitRepositorySnapshot repository, IEnumerable<string> paths,
            string token, CancellationToken cancellation)
        {
            string[] selected = paths.Distinct(StringComparer.Ordinal).ToArray();
            if (selected.Contains(""))
                await RunAsync("sparse-checkout disable", repository.Directory, token, null, cancellation);
            else
            {
                // --stdin 避免几十个目录超过 Windows 命令行长度限制。
                string input = string.Join("\n", selected.Select(Quote)) + "\n";
                await RunAsync("sparse-checkout set --cone --stdin", repository.Directory, token, input, cancellation);
            }
            await RunAsync("checkout --detach --force " + repository.Commit, repository.Directory, token, null, cancellation);
        }

        public static async Task<string> TreeHashAsync(GitRepositorySnapshot repository, string path,
            string token, CancellationToken cancellation)
        {
            string revision = path.Length == 0 ? repository.Commit + "^{tree}" : repository.Commit + ":" + path;
            string result = (await RunAsync("rev-parse --verify " + Quote(revision),
                repository.Directory, token, null, cancellation)).Trim();
            string type = (await RunAsync("cat-file -t " + result, repository.Directory, token, null, cancellation)).Trim();
            if (type != "tree") throw new InvalidDataException("插件来源必须是仓库目录: " + path);
            return result;
        }

        /// <summary>只检出清单文件，扫描不会下载仓库里所有插件的源码和资源。</summary>
        public static async Task<List<string>> CheckoutManifestsAsync(GitRepositorySnapshot repository,
            string prefix, string token, CancellationToken cancellation)
        {
            string command = "ls-tree -r --name-only -z " + repository.Commit;
            if (prefix.Length > 0) command += " -- " + Quote(prefix);
            string files = await RunAsync(command, repository.Directory, token, null, cancellation);
            var manifests = files.Split('\0').Where(p => p == "package.json" || p.EndsWith("/package.json", StringComparison.Ordinal)).ToList();
            if (manifests.Count == 0) return manifests;
            string input = string.Join("\n", manifests.Select(p => "/" + EscapePattern(p))) + "\n";
            await RunAsync("sparse-checkout set --no-cone --stdin", repository.Directory, token, input, cancellation);
            await RunAsync("checkout --detach --force " + repository.Commit, repository.Directory, token, null, cancellation);
            return manifests;
        }

        private static string EscapePattern(string path)
            => path.Replace("\\", "\\\\").Replace("*", "\\*").Replace("?", "\\?").Replace("[", "\\[");

        private static string Quote(string value)
        {
            if (value.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) >= 0)
                throw new InvalidDataException("Git 参数包含无效字符。");
            return "\"" + value + "\"";
        }

        private static async Task<string> RunAsync(string arguments, string workingDirectory, string token,
            string input, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Directory.CreateDirectory(workingDirectory);
            using (var process = new Process())
            {
                // 命令参数与缓存仓库配置共同启用长路径，不修改用户的全局配置。
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = "git", Arguments = "-c core.longpaths=true " + arguments, WorkingDirectory = workingDirectory,
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input != null,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                };
                process.StartInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
                string authorization = "";
                if (!string.IsNullOrEmpty(token))
                {
                    // 凭据只进入本次进程环境，不进入远端 URL、缓存配置或安装记录。
                    authorization = Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + token));
                    process.StartInfo.EnvironmentVariables["GIT_CONFIG_COUNT"] = "1";
                    process.StartInfo.EnvironmentVariables["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraheader";
                    process.StartInfo.EnvironmentVariables["GIT_CONFIG_VALUE_0"] = "AUTHORIZATION: basic " + authorization;
                }
                process.Start();
                using (cancellation.Register(() => Terminate(process)))
                {
                    // 两个输出管道并行读取，避免缓冲区填满导致 Git 等待。
                    Task<string> output = process.StandardOutput.ReadToEndAsync();
                    Task<string> error = process.StandardError.ReadToEndAsync();
                    if (input != null)
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(input);
                        await process.StandardInput.BaseStream.WriteAsync(bytes, 0, bytes.Length);
                        process.StandardInput.Close();
                    }
                    await Task.WhenAll(output, error, Task.Run(() => process.WaitForExit()));
                    cancellation.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0)
                    {
                        string message = Regex.Replace(error.Result, @"(https?://)[^/\s""@]+@", "$1<redacted>@");
                        if (token.Length > 0) message = message.Replace(token, "<redacted>");
                        if (authorization.Length > 0) message = message.Replace(authorization, "<redacted>");
                        throw new InvalidOperationException("Git 操作失败（" + process.ExitCode + "）: " + message.Trim());
                    }
                    return output.Result;
                }
            }
        }

        private static void Terminate(Process process)
        {
            try
            {
                if (process.HasExited) return;
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    using (var kill = Process.Start(new ProcessStartInfo
                    {
                        FileName = "taskkill.exe", Arguments = "/PID " + process.Id + " /T /F",
                        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardOutput = true, RedirectStandardError = true
                    })) kill.WaitForExit(3000);
                }
                else process.Kill();
            }
            catch (InvalidOperationException) { /* Git 已退出。 */ }
            catch (System.ComponentModel.Win32Exception) { try { process.Kill(); } catch (InvalidOperationException) { } }
        }
    }
}
