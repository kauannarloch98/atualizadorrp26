using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace AtualizadorERP.Services
{
    public class UpdateCheckResult
    {
        public bool IsAvailable { get; set; }
        public string CurrentVersion { get; set; } = string.Empty;
        public string RemoteVersion { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public string AssetName { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string ReleaseNotes { get; set; } = string.Empty;
        public string HtmlUrl { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;

        public string FormattedSize
        {
            get
            {
                if (SizeBytes <= 0) return "";
                double mb = SizeBytes / (1024.0 * 1024.0);
                return $"{mb:0.1} MB";
            }
        }
    }

    public class UpdateProgressInfo
    {
        public double Percent { get; set; }
        public long DownloadedBytes { get; set; }
        public long TotalBytes { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    public class GitHubUpdateService
    {
        private const string RepoOwner = "kauannarloch98";
        private const string RepoName = "atualizadorrp26";
        private const string UserAgent = "AtualizadorERP-Updater";

        public static Version GetCurrentVersion()
        {
            var asmVer = Assembly.GetExecutingAssembly().GetName().Version;
            if (asmVer != null)
            {
                return new Version(
                    Math.Max(0, asmVer.Major),
                    Math.Max(0, asmVer.Minor),
                    Math.Max(0, asmVer.Build),
                    Math.Max(0, asmVer.Revision));
            }
            return new Version(1, 0, 0, 0);
        }

        public static string GetCurrentVersionString()
        {
            var ver = GetCurrentVersion();
            if (ver.Revision > 0)
                return $"v{ver.Major}.{ver.Minor}.{ver.Build}.{ver.Revision}";
            return $"v{ver.Major}.{ver.Minor}.{ver.Build}";
        }

        public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
        {
            var currentVer = GetCurrentVersion();
            var result = new UpdateCheckResult
            {
                CurrentVersion = GetCurrentVersionString(),
                IsAvailable = false
            };

            // 1. Tentar via GitHub REST API
            try
            {
                using var handler = new HttpClientHandler { AllowAutoRedirect = true };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(UserAgent, "1.0"));
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

                string apiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
                var response = await client.GetAsync(apiUrl, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    string tagName = root.TryGetProperty("tag_name", out var tProp) ? tProp.GetString() ?? "" : "";
                    string htmlUrl = root.TryGetProperty("html_url", out var hProp) ? hProp.GetString() ?? "" : "";
                    string body = root.TryGetProperty("body", out var bProp) ? bProp.GetString() ?? "" : "";

                    string downloadUrl = "";
                    string assetName = "";
                    long assetSize = 0;

                    if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            string name = asset.TryGetProperty("name", out var nProp) ? nProp.GetString() ?? "" : "";
                            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            {
                                assetName = name;
                                downloadUrl = asset.TryGetProperty("browser_download_url", out var dProp) ? dProp.GetString() ?? "" : "";
                                assetSize = asset.TryGetProperty("size", out var sProp) ? sProp.GetInt64() : 0;
                                if (name.Equals("AtualizadorERP.exe", StringComparison.OrdinalIgnoreCase))
                                {
                                    break;
                                }
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(downloadUrl) && !string.IsNullOrEmpty(tagName))
                    {
                        downloadUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases/download/{tagName}/AtualizadorERP.exe";
                        assetName = "AtualizadorERP.exe";
                    }

                    if (IsNewer(tagName, currentVer, out var remoteVer))
                    {
                        result.IsAvailable = true;
                        result.RemoteVersion = tagName.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tagName : $"v{tagName}";
                        result.DownloadUrl = downloadUrl;
                        result.AssetName = assetName;
                        result.SizeBytes = assetSize;
                        result.ReleaseNotes = body;
                        result.HtmlUrl = htmlUrl;
                        return result;
                    }
                    else
                    {
                        result.RemoteVersion = tagName;
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
            }

            // 2. Fallback de Redirecionamento Web (não sofre com rate-limit de IP da API)
            try
            {
                using var handler = new HttpClientHandler { AllowAutoRedirect = false };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(UserAgent, "1.0"));

                string fullUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases/latest";
                var response = await client.GetAsync(fullUrl, cancellationToken);

                if (((int)response.StatusCode >= 300 && (int)response.StatusCode < 400) && response.Headers.Location != null)
                {
                    var location = response.Headers.Location.ToString();
                    // Exemplo: https://github.com/kauannarloch98/atualizadorrp26/releases/tag/v1.0.1 ou v.1.2.0.0
                    int tagIdx = location.IndexOf("/releases/tag/", StringComparison.OrdinalIgnoreCase);
                    if (tagIdx >= 0)
                    {
                        string tagName = location.Substring(tagIdx + "/releases/tag/".Length).Trim().TrimEnd('/');
                        if (IsNewer(tagName, currentVer, out var remoteVer))
                        {
                            result.IsAvailable = true;
                            result.RemoteVersion = tagName.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tagName : $"v{tagName}";
                            result.DownloadUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases/download/{tagName}/AtualizadorERP.exe";
                            result.AssetName = "AtualizadorERP.exe";
                            result.HtmlUrl = location;
                            return result;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (string.IsNullOrEmpty(result.ErrorMessage))
                    result.ErrorMessage = ex.Message;
            }

            // 3. Fallback de Scraping da página de Releases (se a API e o redirect não funcionarem)
            if (!result.IsAvailable)
            {
                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
                    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(UserAgent, "1.0"));
                    string releasesHtml = await client.GetStringAsync($"https://github.com/{RepoOwner}/{RepoName}/releases", cancellationToken);
                    var match = System.Text.RegularExpressions.Regex.Match(releasesHtml, @"/releases/tag/([^""'\s>]+)");
                    if (match.Success)
                    {
                        string tagName = match.Groups[1].Value.Trim().TrimEnd('/');
                        if (IsNewer(tagName, currentVer, out var remoteVer))
                        {
                            result.IsAvailable = true;
                            result.RemoteVersion = tagName.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tagName : $"v{tagName}";
                            result.DownloadUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases/download/{tagName}/AtualizadorERP.exe";
                            result.AssetName = "AtualizadorERP.exe";
                            result.HtmlUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases/tag/{tagName}";
                            return result;
                        }
                    }
                }
                catch
                {
                    // Ignora falha silenciosamente
                }
            }

            return result;
        }

        private static bool IsNewer(string remoteTag, Version currentVer, out Version? parsedRemote)
        {
            parsedRemote = null;
            if (string.IsNullOrWhiteSpace(remoteTag)) return false;

            // Remove quaisquer prefixos não numéricos no início (ex: 'v', 'v.', 'ver-', etc.)
            string clean = remoteTag.Trim();
            int firstDigit = -1;
            for (int i = 0; i < clean.Length; i++)
            {
                if (char.IsDigit(clean[i]))
                {
                    firstDigit = i;
                    break;
                }
            }
            if (firstDigit > 0)
            {
                clean = clean.Substring(firstDigit);
            }

            // Suporta formatos como 1.0.1 ou 1.1.0.0
            var parts = clean.Split(new[] { '.', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            int major = 0, minor = 0, build = 0, revision = 0;
            if (parts.Length > 0) int.TryParse(parts[0], out major);
            if (parts.Length > 1) int.TryParse(parts[1], out minor);
            if (parts.Length > 2) int.TryParse(parts[2], out build);
            if (parts.Length > 3) int.TryParse(parts[3], out revision);

            parsedRemote = new Version(major, minor, build, revision);
            return parsedRemote > currentVer;
        }

        public async Task DownloadAndApplyUpdateAsync(string downloadUrl, IProgress<UpdateProgressInfo> progress, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(downloadUrl))
                throw new ArgumentException("URL de download da atualização não foi informada.");

            string tempFile = Path.Combine(Path.GetTempPath(), $"AtualizadorERP_Novo_{Guid.NewGuid():N}.exe");

            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromMinutes(10);
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(UserAgent, "1.0"));

                using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                long totalBytes = response.Content.Headers.ContentLength ?? -1;

                using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                var buffer = new byte[81920];
                long totalRead = 0;
                int read;
                var sw = Stopwatch.StartNew();

                while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, read, cancellationToken);
                    totalRead += read;

                    if (totalBytes > 0)
                    {
                        double percent = (double)totalRead / totalBytes * 100.0;
                        double speedMb = (totalRead / (1024.0 * 1024.0)) / Math.Max(sw.Elapsed.TotalSeconds, 0.1);
                        progress?.Report(new UpdateProgressInfo
                        {
                            Percent = Math.Min(percent, 100.0),
                            DownloadedBytes = totalRead,
                            TotalBytes = totalBytes,
                            Status = $"Baixando atualização: {percent:0.0}% ({totalRead / (1024 * 1024):0.0} MB de {totalBytes / (1024 * 1024):0.0} MB - {speedMb:0.1} MB/s)"
                        });
                    }
                    else
                    {
                        progress?.Report(new UpdateProgressInfo
                        {
                            Percent = 50,
                            DownloadedBytes = totalRead,
                            TotalBytes = -1,
                            Status = $"Baixando atualização: {totalRead / (1024 * 1024):0.0} MB transferidos..."
                        });
                    }
                }
            }

            var fi = new FileInfo(tempFile);
            if (!fi.Exists || fi.Length < 100000)
            {
                throw new InvalidOperationException("O arquivo de atualização baixado é inválido ou está corrompido.");
            }

            // Descobrir o executável atual em execução
            string? currentExe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe))
            {
                currentExe = Process.GetCurrentProcess().MainModule?.FileName;
            }

            if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe))
            {
                throw new InvalidOperationException("Não foi possível determinar o caminho do executável atual para substituição.");
            }

            string? exeDir = Path.GetDirectoryName(currentExe);
            if (string.IsNullOrEmpty(exeDir)) exeDir = AppDomain.CurrentDomain.BaseDirectory;

            // Criar script batch temporário para aguardar o processo fechar, substituir o arquivo e reiniciar
            string batPath = Path.Combine(Path.GetTempPath(), $"atualizar_app_{Guid.NewGuid():N}.bat");
            string batContent = $@"@echo off
chcp 65001 >nul
set ""SRC={tempFile}""
set ""DST={currentExe}""
set ""DST_DIR={exeDir}""
set ""PID={Environment.ProcessId}""

set /a WAIT_COUNT=0
:wait_close
tasklist /fi ""PID eq %PID%"" 2>nul | findstr /i ""%PID%"" >nul
if not errorlevel 1 (
    set /a WAIT_COUNT+=1
    if %WAIT_COUNT% gtr 10 (
        taskkill /f /pid %PID% >nul 2>&1
    )
    timeout /t 1 /nobreak >nul
    goto wait_close
)

timeout /t 1 /nobreak >nul

set /a RETRY_COUNT=0
:copy_loop
copy /y ""%SRC%"" ""%DST%"" >nul 2>&1
if errorlevel 1 (
    set /a RETRY_COUNT+=1
    if %RETRY_COUNT% leq 20 (
        timeout /t 1 /nobreak >nul
        goto copy_loop
    )
)

del ""%SRC%"" >nul 2>&1
cd /d ""%DST_DIR%""
start """" ""%DST%""
(goto) 2>nul & del ""%~f0""
";

            File.WriteAllText(batPath, batContent);

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{batPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };

            Process.Start(psi);
            Application.Current.Dispatcher.Invoke(() =>
            {
                Application.Current.Shutdown();
            });
        }
    }
}
