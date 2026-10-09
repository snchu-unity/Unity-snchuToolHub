using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ExOpenSource.Editor
{
    /// <summary>GitHub 清单预检与异步 Git 下载。调用方按来源分组，并串行使用同一缓存。</summary>
    public static class GitPluginUtility
    {
        private const int CommandTimeoutSeconds = 600;

        public static string RepositoryUrl(ExPluginItem plugin)
            => $"https://github.com/{plugin.GitURL_Username}/{plugin.GitURL_RepoName}.git";

        public static string RepositoryKey(ExPluginItem plugin)
            => RepositoryUrl(plugin).ToLowerInvariant() + "\n" + plugin.GitURL_Branch;

        /// <summary>通过 GitHub API 读取固定提交的根清单；不启动 Git，也不写下载缓存。report 可选。</summary>
        public static async Task<GitDependencySnapshot> ReadDependencyManifestsAsync(ExPluginItem source,
            IEnumerable<string> paths, string token, CancellationToken cancellation, Action<string> report = null)
        {
            string api = "https://api.github.com/repos/" + Uri.EscapeDataString(source.GitURL_Username)
                + "/" + Uri.EscapeDataString(source.GitURL_RepoName);
            string[] selected = paths.Distinct(StringComparer.Ordinal).ToArray();
            using (var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }))
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("snchuToolHub");
                client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
                if (!string.IsNullOrEmpty(token))
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                string commit = (await ReadPreflightTextAsync(client,
                    api + "/commits/" + Uri.EscapeDataString(source.GitURL_Branch),
                    "application/vnd.github.sha", false, cancellation, report)).Trim();
                if (!Regex.IsMatch(commit, "^[a-f0-9]{40}$"))
                    throw new InvalidDataException("依赖预检未完成：GitHub 返回了无效的提交标识。" + api);
                var result = new GitDependencySnapshot { Commit = commit };
                // 限制并发，避免逐个等待 54 个清单，也避免同时发出过多 API 请求。
                for (int offset = 0; offset < selected.Length; offset += 4)
                {
                    var entries = await Task.WhenAll(selected.Skip(offset).Take(4).Select(async path =>
                    {
                        string manifest = path.Length == 0 ? "package.json" : path + "/package.json";
                        string encoded = string.Join("/", manifest.Split('/').Select(Uri.EscapeDataString));
                        string url = api + "/contents/" + encoded + "?ref=" + commit;
                        string response = await ReadPreflightTextAsync(client, url,
                            "application/vnd.github+json", true, cancellation, report);
                        PluginPackageDefinition package = null;
                        if (response != null)
                        {
                            var json = JObject.Parse(response);
                            if ((string)json["type"] != "file" || (string)json["encoding"] != "base64"
                                || json["content"]?.Type != JTokenType.String)
                                throw new InvalidDataException("依赖预检未完成：GitHub 未返回有效的包清单。" + manifest);
                            string text = Encoding.UTF8.GetString(Convert.FromBase64String((string)json["content"]));
                            package = PluginInstallFiles.ParsePackage(text, manifest);
                        }
                        return new KeyValuePair<string, PluginPackageDefinition>(path, package);
                    }));
                    foreach (var entry in entries) result.Packages.Add(entry.Key, entry.Value);
                    if (report != null) report("依赖预检：已读取 " + result.Packages.Count + "/" + selected.Length + " 个清单（Git 尚未启动）");
                }
                return result;
            }
        }

        private static async Task<string> ReadPreflightTextAsync(HttpClient client, string url,
            string accept, bool allowMissing, CancellationToken cancellation, Action<string> report)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
                        using (var response = await client.SendAsync(request, cancellation))
                        {
                            if (allowMissing && response.StatusCode == HttpStatusCode.NotFound) return null;
                            if ((int)response.StatusCode >= 500 && attempt < 2)
                            {
                                if (report != null) report("依赖预检：GitHub API 暂时不可用，正在重试...");
                            }
                            else
                            {
                                if (!response.IsSuccessStatusCode)
                                    throw new InvalidOperationException("依赖预检未完成：读取 GitHub 清单失败（HTTP "
                                        + (int)response.StatusCode + "）。请检查仓库 / 分支、令牌的仓库读取权限及 API 限额。\n"
                                        + url + "\nGit 尚未启动，不能据此判断依赖是否齐全。");
                                return await response.Content.ReadAsStringAsync();
                            }
                        }
                    }
                }
                catch (HttpRequestException)
                {
                    if (attempt == 2)
                        throw new InvalidOperationException("依赖预检未完成：GitHub API 网络连接失败，已重试 2 次。\n"
                            + url + "\n请检查本机网络与代理。Git 尚未启动，不能据此判断依赖是否齐全。");
                    if (report != null) report("依赖预检：API 连接中断，正在重试...");
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    if (attempt == 2)
                        throw new TimeoutException("依赖预检未完成：GitHub API 请求超时，已重试 2 次。\n"
                            + url + "\nGit 尚未启动，不能据此判断依赖是否齐全。");
                    if (report != null) report("依赖预检：API 请求超时，正在重试...");
                }
                await Task.Delay(1000 * (attempt + 1), cancellation);
            }
            throw new InvalidOperationException("依赖预检未完成：GitHub API 重试次数已耗尽。" + url);
        }

        public static async Task<GitRepositorySnapshot> FetchAsync(string cacheRoot, ExPluginItem plugin,
            string token, Action<string> report, CancellationToken cancellation, string requestedCommit = null)
        {
            string url = RepositoryUrl(plugin);
            string branch = plugin.GitURL_Branch;
            string effectiveUrl = (await RunAsync("ls-remote --get-url " + Quote(url),
                cacheRoot, url, token, null, cancellation)).Trim();
            if (!string.Equals(effectiveUrl, url, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Git 的 URL 改写与 ToolHub 的 HTTPS 下载冲突，请检查该仓库对应的 url.*.insteadOf 配置: " + url);
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
                    await RunAsync("clone --progress --config core.longpaths=true --depth 1 --filter=blob:none --no-checkout --no-tags --branch "
                        + Quote(branch) + " " + Quote(url) + " " + Quote(temporary), cacheRoot, url, token, null, cancellation, report);
                    cancellation.ThrowIfCancellationRequested();
                    Directory.Move(PluginInstallFiles.ContainedPath(cacheRoot, temporary.Substring(cacheRoot.Length + 1)), directory);
                }
                finally { PluginInstallFiles.DeleteCacheDirectory(cacheRoot, temporary); }
                commitReference = "HEAD";
            }
            else
            {
                // 读取保存的来源；remote get-url 会应用用户的 insteadOf URL 改写，不能用于此校验。
                string remote = (await RunAsync("config --local --get remote.origin.url", directory, url, token, null, cancellation)).Trim();
                if (!string.Equals(remote, url, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("缓存仓库来源不匹配，请检查该缓存目录: " + directory);
                // 检出期间的部分路径操作需要在缓存仓库初始化配置时就读到此选项。
                await RunAsync("config --local core.longpaths true", directory, url, token, null, cancellation);
                report("获取仓库变化 " + plugin.GitURL_Username + "/" + plugin.GitURL_RepoName);
                await RunAsync("fetch --progress --depth 1 --filter=blob:none --no-tags origin " + Quote(branch),
                    directory, url, token, null, cancellation, report);
                commitReference = "FETCH_HEAD";
            }
            string commit = (await RunAsync("rev-parse " + commitReference, directory, url, token, null, cancellation)).Trim();
            // 分支可能在预检期间推进；主体必须使用预检清单对应的提交。
            if (requestedCommit != null && commit != requestedCommit)
            {
                report("获取依赖预检时锁定的提交...");
                await RunAsync("fetch --progress --depth 1 --filter=blob:none --no-tags origin " + Quote(requestedCommit),
                    directory, url, token, null, cancellation, report);
                commit = (await RunAsync("rev-parse FETCH_HEAD", directory, url, token, null, cancellation)).Trim();
                if (commit != requestedCommit)
                    throw new InvalidDataException("Git 下载的提交与依赖预检不一致，已停止，插件目录未改动。");
            }
            return new GitRepositorySnapshot { Directory = directory, Url = url, Branch = branch, Commit = commit };
        }

        /// <summary>同仓库选中目录统一下载后检出。report 可选，仅用于进度展示。</summary>
        public static async Task CheckoutDirectoriesAsync(GitRepositorySnapshot repository, IEnumerable<string> paths,
            string token, CancellationToken cancellation, Action<string> report = null)
        {
            string[] selected = paths.Distinct(StringComparer.Ordinal).ToArray();
            await DownloadBlobsAsync(repository, path => selected.Any(directory => directory.Length == 0
                || path.StartsWith(directory + "/", StringComparison.Ordinal)), token, cancellation, report);
            if (selected.Contains(""))
                await RunAsync("sparse-checkout disable", repository.Directory, repository.Url, token, null, cancellation, report);
            else
            {
                // 精确选中目录，避免 cone 模式连带下载祖先目录中的无关文件；stdin 避免命令行长度限制。
                string input = string.Join("\n", selected.Select(path => "/" + EscapePattern(path) + "/")) + "\n";
                await RunAsync("sparse-checkout set --no-cone --stdin", repository.Directory, repository.Url, token, input, cancellation, report);
            }
            if (report != null) report("文件下载完成，正在检出 " + selected.Length + " 个插件目录...");
            await RunAsync("checkout --progress --detach --force " + repository.Commit,
                repository.Directory, repository.Url, token, null, cancellation, report);
        }

        public static async Task<string> TreeHashAsync(GitRepositorySnapshot repository, string path,
            string token, CancellationToken cancellation)
        {
            string revision = path.Length == 0 ? repository.Commit + "^{tree}" : repository.Commit + ":" + path;
            string result = (await RunAsync("rev-parse --verify " + Quote(revision),
                repository.Directory, repository.Url, token, null, cancellation)).Trim();
            string type = (await RunAsync("cat-file -t " + result, repository.Directory, repository.Url, token, null, cancellation)).Trim();
            if (type != "tree") throw new InvalidDataException("插件来源必须是仓库目录: " + path);
            return result;
        }

        /// <summary>只检出清单文件，扫描不会下载仓库里所有插件的源码和资源。</summary>
        public static async Task<List<string>> CheckoutManifestsAsync(GitRepositorySnapshot repository,
            string prefix, string token, CancellationToken cancellation, Action<string> report = null)
        {
            string command = "ls-tree -r --name-only -z " + repository.Commit;
            if (prefix.Length > 0) command += " -- " + Quote(prefix);
            string files = await RunAsync(command, repository.Directory, repository.Url, token, null, cancellation);
            var manifests = files.Split('\0').Where(p => p == "package.json" || p.EndsWith("/package.json", StringComparison.Ordinal)).ToList();
            await CheckoutManifestFilesAsync(repository, manifests, token, cancellation, report);
            return manifests;
        }

        private static async Task CheckoutManifestFilesAsync(GitRepositorySnapshot repository, List<string> manifests,
            string token, CancellationToken cancellation, Action<string> report)
        {
            if (manifests.Count == 0) return;
            var selected = new HashSet<string>(manifests, StringComparer.Ordinal);
            await DownloadBlobsAsync(repository, selected.Contains, token, cancellation, report);
            string input = string.Join("\n", manifests.Select(p => "/" + EscapePattern(p))) + "\n";
            await RunAsync("sparse-checkout set --no-cone --stdin", repository.Directory, repository.Url, token, input, cancellation, report);
            await RunAsync("checkout --progress --detach --force " + repository.Commit,
                repository.Directory, repository.Url, token, null, cancellation, report);
        }

        // 将 partial clone 的隐式文件下载提前为一次显式 fetch，使批量下载可报告真实进度。
        private static async Task DownloadBlobsAsync(GitRepositorySnapshot repository, Func<string, bool> include,
            string token, CancellationToken cancellation, Action<string> report)
        {
            string tree = await RunAsync("ls-tree -r -z " + repository.Commit, repository.Directory, repository.Url, token, null, cancellation);
            var blobs = new HashSet<string>(StringComparer.Ordinal);
            foreach (string entry in tree.Split('\0'))
            {
                cancellation.ThrowIfCancellationRequested();
                if (entry.Length == 0) continue;
                int separator = entry.IndexOf('\t');
                string[] identity = entry.Substring(0, separator).Split(' ');
                if (identity[1] == "blob" && include(entry.Substring(separator + 1))) blobs.Add(identity[2]);
            }
            if (blobs.Count == 0) return;
            if (report != null) report("正在统一下载 " + blobs.Count + " 个 Git 文件对象...");
            // 与 Git 的 promisor 下载一致：不以已有提交作为 have，否则服务器可能省略尚未下载的 blob。
            await RunAsync("-c fetch.negotiationAlgorithm=noop fetch --progress --no-tags --no-write-fetch-head --recurse-submodules=no --filter=blob:none --stdin origin",
                repository.Directory, repository.Url, token, string.Join("\n", blobs) + "\n", cancellation, report);
        }

        private static string EscapePattern(string path)
            => path.Replace("\\", "\\\\").Replace("*", "\\*").Replace("?", "\\?").Replace("[", "\\[");

        private static string Quote(string value)
        {
            if (value.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) >= 0)
                throw new InvalidDataException("Git 参数包含无效字符。");
            return "\"" + value + "\"";
        }

        private static async Task<string> RunAsync(string arguments, string workingDirectory, string repositoryUrl, string token,
            string input, CancellationToken cancellation, Action<string> report = null)
        {
            cancellation.ThrowIfCancellationRequested();
            Directory.CreateDirectory(workingDirectory);
            using (var process = new Process())
            {
                // 命令参数与缓存仓库配置共同启用长路径，不修改用户的全局配置。
                // 完整仓库地址比常见的 GitHub -> SSH 前缀更具体；规则仅对当前 Git 进程及子进程生效。
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = "git", Arguments = "-c core.longpaths=true -c http.lowSpeedLimit=1 -c http.lowSpeedTime=120 -c "
                        + Quote("url." + repositoryUrl + ".insteadOf=" + repositoryUrl) + " " + arguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input != null,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                };
                process.StartInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
                process.StartInfo.EnvironmentVariables["GCM_INTERACTIVE"] = "never";
                ConfigureSystemProxy(process.StartInfo, repositoryUrl, report);
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
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(CommandTimeoutSeconds)))
                using (cancellation.Register(() => Terminate(process)))
                using (timeout.Token.Register(() => Terminate(process)))
                {
                    // 两个输出管道并行读取，避免缓冲区填满导致 Git 等待。
                    string progress = "等待 Git 返回数据";
                    Task<string> output = process.StandardOutput.ReadToEndAsync();
                    Task<string> error = ReadErrorsAsync(process.StandardError, token, authorization,
                        message => progress = message);
                    Task completion = Task.WhenAll(output, error, Task.Run(() => process.WaitForExit()));
                    var elapsed = Stopwatch.StartNew();
                    try
                    {
                        if (input != null)
                        {
                            byte[] bytes = Encoding.UTF8.GetBytes(input);
                            await process.StandardInput.BaseStream.WriteAsync(bytes, 0, bytes.Length);
                            process.StandardInput.Close();
                        }
                        while (!completion.IsCompleted)
                        {
                            await Task.WhenAny(completion, Task.Delay(1000));
                            if (!completion.IsCompleted && report != null)
                                report(progress + "（本步骤已等待 " + elapsed.Elapsed.ToString(@"mm\:ss") + "）");
                        }
                        await completion;
                    }
                    catch (IOException) when (process.HasExited && process.ExitCode != 0)
                    {
                        // Git 连接失败时可能提前关闭 stdin；保留 stderr 中真正的失败原因。
                        await completion;
                    }
                    catch (Exception) when (cancellation.IsCancellationRequested || timeout.IsCancellationRequested)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        throw CommandTimeout();
                    }
                    finally
                    {
                        if (!completion.IsCompleted)
                        {
                            Terminate(process);
                            try { await completion; }
                            catch (IOException) { /* 已记录的输入输出错误由当前异常报告。 */ }
                        }
                    }
                    cancellation.ThrowIfCancellationRequested();
                    if (timeout.IsCancellationRequested) throw CommandTimeout();
                    if (process.ExitCode != 0)
                    {
                        throw new InvalidOperationException("Git 操作失败（" + process.ExitCode + "）: " + error.Result.Trim());
                    }
                    return output.Result;
                }
            }
        }

        private static TimeoutException CommandTimeout()
            => new TimeoutException("Git 操作超过 10 分钟，已终止进程。请检查 Git 网络与代理配置后重试；本次准备文件不会提交。");

        private static void ConfigureSystemProxy(ProcessStartInfo start, string repositoryUrl, Action<string> report)
        {
            // 系统代理是可选来源；已有代理环境变量和 Git 自身的 http.proxy 配置优先。
            if (new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" }
                .Any(name => !string.IsNullOrWhiteSpace(start.EnvironmentVariables[name]))) return;
            var proxy = WebRequest.DefaultWebProxy;
            if (proxy == null) return;
            var repository = new Uri(repositoryUrl);
            Uri address = proxy.GetProxy(repository);
            if (address == repository) return;
            start.EnvironmentVariables["HTTPS_PROXY"] = address.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
            if (report != null) report("Git 使用本机系统代理：" + address.Host + ":" + address.Port);
        }

        private static async Task<string> ReadErrorsAsync(StreamReader reader, string token, string authorization, Action<string> progress)
        {
            var errors = new StringBuilder();
            string line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                string message = Regex.Replace(line, @"(https?://)[^/\s""@]+@", "$1<redacted>@");
                if (token.Length > 0) message = message.Replace(token, "<redacted>");
                if (authorization.Length > 0) message = message.Replace(authorization, "<redacted>");
                errors.AppendLine(message);
                if (errors.Length > 16384) errors.Remove(0, errors.Length - 16384);
                if (message.Length > 0) progress(message);
            }
            return errors.ToString();
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
