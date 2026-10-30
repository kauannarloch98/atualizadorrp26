using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FluentFTP;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Common;
using AtualizadorERP.Models;

namespace AtualizadorERP.Services
{
    public class UpdateProgressReport
    {
        public double Percentage { get; set; }
        public string Status { get; set; } = string.Empty;
        public string Speed { get; set; } = string.Empty;
        public string Transferred { get; set; } = string.Empty;
        public string Eta { get; set; } = string.Empty;
    }

    public class ErpVersionServerInfo
    {
        public int OptionId { get; set; }
        public DateTime Modified { get; set; }
        public long SizeBytes { get; set; }
        public string RemoteFileName { get; set; } = string.Empty;
        public string RemoteFullPath { get; set; } = string.Empty;
        public string FormattedSize => SizeBytes > 0 ? $"{(SizeBytes / (1024.0 * 1024.0)):0.0} MB" : "--";
    }

    public class FtpUpdaterService
    {
        public event Action<LogMessage>? OnLog;
        public event Action<UpdateProgressReport>? OnProgress;

        public Func<string, int, Task<bool>>? RequestCloseProcessAsync { get; set; }

        private static readonly TimeZoneInfo BrasiliaTimeZone = GetBrasiliaTimeZone();

        private static TimeZoneInfo GetBrasiliaTimeZone()
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("E. South America Standard Time");
            }
            catch
            {
                return TimeZoneInfo.CreateCustomTimeZone("BRT", TimeSpan.FromHours(-3), "Horário de Brasília", "Horário de Brasília");
            }
        }

        public static DateTime GetBrasiliaNow()
        {
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, BrasiliaTimeZone);
        }

        public static DateTime ConvertFtpTimeToBrasilia(DateTime ftpTime)
        {
            if (ftpTime == default || ftpTime == DateTime.MinValue)
                return default;

            // O protocolo FTP (MDTM / MLSD) retorna os horários em UTC (+00:00) com DateTimeKind.Unspecified.
            // Convertemos sempre de UTC para o Horário Oficial de Brasília (UTC-3).
            DateTime utcTime = ftpTime.Kind == DateTimeKind.Local
                ? ftpTime.ToUniversalTime()
                : DateTime.SpecifyKind(ftpTime, DateTimeKind.Utc);

            return TimeZoneInfo.ConvertTimeFromUtc(utcTime, BrasiliaTimeZone);
        }

        private void Log(string message, LogLevel level = LogLevel.Info)
        {
            OnLog?.Invoke(new LogMessage
            {
                Timestamp = GetBrasiliaNow(),
                Level = level,
                Text = message
            });
        }

        private void ReportProgress(double percentage, string status, string speed = "", string transferred = "", string eta = "")
        {
            OnProgress?.Invoke(new UpdateProgressReport
            {
                Percentage = Math.Clamp(percentage, 0, 100),
                Status = status,
                Speed = speed,
                Transferred = transferred,
                Eta = eta
            });
        }

        private AsyncFtpClient CreateFtpClient(AppSettings settings)
        {
            var client = new AsyncFtpClient(settings.FtpHost, settings.FtpUser, settings.FtpPass, settings.FtpPort);
            client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
            client.Config.ValidateAnyCertificate = true;
            client.Config.ConnectTimeout = 15000;
            client.Config.ReadTimeout = 30000;
            return client;
        }

        // =====================================================================
        // RESOLUÇÃO DINÂMICA E CASE-INSENSITIVE NO SERVIDOR FTP
        // =====================================================================
        public async Task<List<FtpListItem>> GetDirectoryListingCaseInsensitiveAsync(AsyncFtpClient client, string remoteFolder, CancellationToken cancellationToken)
        {
            // 1. Tenta listar diretamente
            try
            {
                var items = await client.GetListing(remoteFolder, cancellationToken);
                if (items != null && items.Length > 0)
                {
                    return items.ToList();
                }
            }
            catch
            {
                // Falhou na tentativa direta (ex: diferença de maiúsculas/minúsculas no caminho)
            }

            // 2. Resolução case-insensitive segmento a segmento
            string resolvedPath = await ResolveRemoteDirectoryPathAsync(client, remoteFolder, cancellationToken);
            try
            {
                var items = await client.GetListing(resolvedPath, cancellationToken);
                return items?.ToList() ?? new List<FtpListItem>();
            }
            catch (Exception ex)
            {
                Log($"Aviso ao consultar pasta '{remoteFolder}': {ex.Message}", LogLevel.Warning);
                return new List<FtpListItem>();
            }
        }

        private async Task<string> ResolveRemoteDirectoryPathAsync(AsyncFtpClient client, string remoteFolder, CancellationToken cancellationToken)
        {
            string clean = remoteFolder.Trim().Trim('/').Replace('\\', '/');
            if (string.IsNullOrEmpty(clean)) return "/";

            string[] parts = clean.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string current = "";

            foreach (string part in parts)
            {
                string parent = string.IsNullOrEmpty(current) ? "/" : current;
                try
                {
                    var listing = await client.GetListing(parent, cancellationToken);
                    var match = listing.FirstOrDefault(i =>
                        (i.Type == FtpObjectType.Directory || i.Type == FtpObjectType.Link) &&
                        i.Name.Equals(part, StringComparison.OrdinalIgnoreCase));

                    if (match != null)
                    {
                        current = string.IsNullOrEmpty(current) ? match.Name : $"{current}/{match.Name}";
                        continue;
                    }
                }
                catch
                {
                }

                current = string.IsNullOrEmpty(current) ? part : $"{current}/{part}";
            }

            return current;
        }

        /// <summary>
        /// Regra de identificação dos pacotes do ERP:
        /// - Se tiver "ERP" e "BETA" no mesmo nome (independente de maiúsculas ou minúsculas) = Beta
        /// - Onde tiver somente "ERP" (independente de maiúsculas ou minúsculas, sem 'beta') = Fix
        /// </summary>
        public static FtpListItem? FindErpPackageInListing(IEnumerable<FtpListItem> items, bool isBeta)
        {
            var rarFiles = items
                .Where(i => i.Type == FtpObjectType.File && i.Name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (rarFiles.Count == 0) return null;

            if (isBeta)
            {
                // Se tiver ERP e BETA no mesmo nome = Beta
                var betaWithErp = rarFiles
                    .Where(i => i.Name.Contains("erp", StringComparison.OrdinalIgnoreCase) &&
                                i.Name.Contains("beta", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(i => i.Modified)
                    .ToList();

                if (betaWithErp.Count > 0) return betaWithErp[0];

                // Fallback: qualquer .rar que contenha 'beta'
                return rarFiles
                    .Where(i => i.Name.Contains("beta", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(i => i.Modified)
                    .FirstOrDefault();
            }
            else
            {
                // Onde tiver somente ERP (sem 'beta') = Fix (ex: erp.rar, ERP.rar, erp_fix.rar, ERP_Fix.rar)
                var fixWithErp = rarFiles
                    .Where(i => i.Name.Contains("erp", StringComparison.OrdinalIgnoreCase) &&
                                !i.Name.Contains("beta", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(i => i.Modified)
                    .ToList();

                if (fixWithErp.Count > 0) return fixWithErp[0];

                // Fallback: qualquer .rar que não contenha 'beta'
                return rarFiles
                    .Where(i => !i.Name.Contains("beta", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(i => i.Modified)
                    .FirstOrDefault();
            }
        }

        public static FtpListItem? FindSecureBlackboxDll(IEnumerable<FtpListItem> items)
        {
            return items.FirstOrDefault(i =>
                i.Type == FtpObjectType.File &&
                (i.Name.Equals("SecureBlackbox24.dll", StringComparison.OrdinalIgnoreCase) ||
                 (i.Name.Contains("secureblackbox", StringComparison.OrdinalIgnoreCase) && i.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))));
        }

        // =====================================================================
        // ATUALIZAÇÃO DO ERP (SUITE DELPHI 10 / DELPHI 13)
        // =====================================================================
        public async Task<string> ExecuteUpdateAsync(ErpOption option, AppSettings settings, CancellationToken cancellationToken)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ERP_Update_" + Guid.NewGuid().ToString("N"));
            string tempRar = Path.Combine(tempDir, "pacote.rar");
            string tempExtract = Path.Combine(tempDir, "extraidos");

            try
            {
                Directory.CreateDirectory(tempDir);
                Directory.CreateDirectory(tempExtract);

                string destDir = settings.GetDirectory("ERP");
                if (string.IsNullOrWhiteSpace(destDir))
                {
                    destDir = settings.DestinationDir;
                }
                if (string.IsNullOrWhiteSpace(destDir))
                {
                    throw new InvalidOperationException("A pasta de destino do ERP não foi configurada.");
                }

                Log($"Iniciando atualização do ERP: {option.Title}", LogLevel.Info);
                ReportProgress(0, "Verificando ambiente...");

                if (!Directory.Exists(destDir))
                {
                    Log($"Criando pasta de destino: {destDir}", LogLevel.Info);
                    Directory.CreateDirectory(destDir);
                }

                if (settings.CheckRunningProcesses)
                {
                    await CheckAndHandleRunningProcessesAsync("ERP", destDir);
                }

                cancellationToken.ThrowIfCancellationRequested();

                ReportProgress(5, "Conectando ao servidor FTP...");
                Log($"Conectando a {settings.FtpHost}:{settings.FtpPort}...", LogLevel.Info);

                using var client = CreateFtpClient(settings);
                await client.AutoConnect(cancellationToken);
                Log("Conexão segura (FTPS) estabelecida com sucesso!", LogLevel.Success);

                cancellationToken.ThrowIfCancellationRequested();

                ReportProgress(10, "Localizando pacote no servidor FTP...");
                string remoteFolder = option.RemoteFolder;
                var folderItems = await GetDirectoryListingCaseInsensitiveAsync(client, remoteFolder, cancellationToken);
                var package = FindErpPackageInListing(folderItems, option.IsBeta);

                if (package == null)
                {
                    string tipo = option.IsBeta ? "Beta" : "Fix";
                    throw new FileNotFoundException($"Nenhum pacote {tipo} (.rar) do ERP foi encontrado na pasta '{remoteFolder}' do servidor FTP.");
                }

                string remoteFile = package.FullName;
                Log($"Pacote remoto identificado: {package.Name} (Caminho: {remoteFile})", LogLevel.Info);

                string finalExeName = $"{option.BaseName}.exe";
                if (option.IsBeta)
                {
                    ReportProgress(12, "Consultando carimbo de data/hora no FTP...");
                    DateTime remoteModified = ConvertFtpTimeToBrasilia(package.Modified);
                    if (remoteModified == default)
                    {
                        remoteModified = ConvertFtpTimeToBrasilia(await client.GetModifiedTime(remoteFile, cancellationToken));
                    }
                    if (remoteModified == default)
                    {
                        remoteModified = GetBrasiliaNow();
                    }

                    string timestamp = remoteModified.ToString(@"dd.MM.yyyy - HH\hmm");
                    Log($"Versão Beta identificada ({package.Name}). Carimbo FTP (Brasília): {timestamp}", LogLevel.Info);
                }
                else
                {
                    DateTime remoteModified = ConvertFtpTimeToBrasilia(package.Modified);
                    string timestamp = remoteModified != default ? remoteModified.ToString(@"dd.MM.yyyy - HH\hmm") : "";
                    Log($"Versão Fix identificada ({package.Name}). Carimbo FTP (Brasília): {timestamp}", LogLevel.Info);
                }

                Log($"Nome do executável final: {finalExeName}", LogLevel.Info);
                cancellationToken.ThrowIfCancellationRequested();

                // Download
                Log($"Baixando arquivo: {package.Name}...", LogLevel.Info);
                ReportProgress(15, "Iniciando download...");

                var progressHandler = new Progress<FtpProgress>(p =>
                {
                    double overallPercent = 15 + (p.Progress * 0.60);
                    double mbTransferred = p.TransferredBytes / (1024.0 * 1024.0);
                    string speedStr = p.TransferSpeedToString();
                    string etaStr = p.ETA != TimeSpan.Zero ? $"ETA: {p.ETA:mm\\:ss}" : "";
                    string transferredStr = $"{mbTransferred:0.0} MB transferidos";

                    ReportProgress(overallPercent, $"Baixando: {p.Progress:0.0}% ({transferredStr})", speedStr, transferredStr, etaStr);
                });

                var status = await client.DownloadFile(tempRar, remoteFile, FtpLocalExists.Overwrite, FtpVerify.None, progressHandler, cancellationToken);
                if (status != FtpStatus.Success && !File.Exists(tempRar))
                {
                    throw new Exception("Falha no download do pacote do servidor FTP.");
                }

                long fileSizeMb = new FileInfo(tempRar).Length / (1024 * 1024);
                Log($"Download concluído ({fileSizeMb} MB)!", LogLevel.Success);

                cancellationToken.ThrowIfCancellationRequested();

                // Extração
                ReportProgress(78, "Extraindo pacote comprimido...");
                Log("Extraindo arquivos do pacote RAR...", LogLevel.Info);

                bool extracted = ExtractRarPackage(tempRar, tempExtract);
                if (!extracted)
                {
                    throw new Exception("Falha ao descompactar o arquivo RAR baixado.");
                }

                var exeFiles = Directory.GetFiles(tempExtract, "*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (exeFiles.Length == 0)
                {
                    throw new FileNotFoundException("Nenhum arquivo executável (.exe) foi encontrado dentro do pacote RAR.");
                }

                string sourceExe = exeFiles[0];
                string destExe = Path.Combine(destDir, finalExeName);

                ReportProgress(86, "Criando backup das versões anteriores...");

                // REGRA DE BACKUP DO ERP:
                // Fix vai para 'fix bkp', Beta vai para 'beta bkp'
                if (option.IsBeta)
                {
                    BackupFiles(destDir, "beta bkp", "*Beta*.exe");
                }
                else
                {
                    BackupFiles(destDir, "fix bkp", "ERP*.exe", excludePattern: "*Beta*");
                }

                ReportProgress(90, "Instalando executável...");
                Log($"Instalando executável em: {destExe}", LogLevel.Info);

                if (File.Exists(destExe))
                {
                    File.Delete(destExe);
                }
                File.Move(sourceExe, destExe);
                Log("Executável instalado com sucesso!", LogLevel.Success);

                // DLL SecureBlackbox para Delphi 13
                if (option.IsDelphi13)
                {
                    ReportProgress(92, "Baixando DLL SecureBlackbox24...");
                    Log("Baixando biblioteca complementar: SecureBlackbox24.dll...", LogLevel.Info);

                    string destDll = Path.Combine(destDir, option.DllLocalName);
                    var dllProgress = new Progress<FtpProgress>(p =>
                    {
                        double overallPercent = 92 + (p.Progress * 0.06);
                        ReportProgress(overallPercent, $"Baixando DLL: {p.Progress:0.0}%", p.TransferSpeedToString());
                    });

                    var dllItem = FindSecureBlackboxDll(folderItems);
                    string remoteDll = dllItem != null ? dllItem.FullName : option.DllRemoteFile;

                    try
                    {
                        await client.DownloadFile(destDll, remoteDll, FtpLocalExists.Overwrite, FtpVerify.None, dllProgress, cancellationToken);
                        Log($"SecureBlackbox24.dll atualizada com sucesso em: {destDll}", LogLevel.Success);
                    }
                    catch (Exception ex)
                    {
                        Log($"Aviso ao baixar SecureBlackbox24.dll: {ex.Message}. O executável principal foi instalado.", LogLevel.Warning);
                    }
                }

                await client.Disconnect(cancellationToken);

                ReportProgress(100, "Atualização concluída com sucesso!", "", "", "");
                Log($"Processo concluído com êxito! Arquivo pronto em: {destExe}", LogLevel.Success);

                return destExe;
            }
            finally
            {
                CleanupTempDir(tempDir);
            }
        }

        // =====================================================================
        // ATUALIZAÇÃO GENÉRICA DE DEMAIS APLICAÇÕES (WRPDV, FRENTE, SERVIDORES, ETC)
        // =====================================================================
        public async Task<string> ExecuteGenericAppUpdateAsync(AppDefinition app, RemoteFileInfo targetFile, string destDir, AppSettings settings, CancellationToken cancellationToken)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"{app.Id}_Update_" + Guid.NewGuid().ToString("N"));
            string tempRar = Path.Combine(tempDir, "pacote.rar");
            string tempExtract = Path.Combine(tempDir, "extraidos");

            try
            {
                Directory.CreateDirectory(tempDir);
                Directory.CreateDirectory(tempExtract);

                Log($"Iniciando atualização de {app.Name}...", LogLevel.Info);
                ReportProgress(0, "Verificando ambiente...");

                if (!Directory.Exists(destDir))
                {
                    Log($"Criando pasta de destino: {destDir}", LogLevel.Info);
                    Directory.CreateDirectory(destDir);
                }

                if (settings.CheckRunningProcesses)
                {
                    await CheckAndHandleRunningProcessesAsync(app.StandardExePrefix, destDir);
                }

                cancellationToken.ThrowIfCancellationRequested();

                ReportProgress(5, "Conectando ao servidor FTP...");
                using var client = CreateFtpClient(settings);
                await client.AutoConnect(cancellationToken);

                string finalExeName = app.GenerateFinalExeName(targetFile.FileName);
                Log($"Pacote remoto selecionado: {targetFile.FileName}", LogLevel.Info);
                Log($"Nome do executável final: {finalExeName}", LogLevel.Info);

                // Download com progresso
                ReportProgress(15, $"Baixando {targetFile.FileName}...");
                var progressHandler = new Progress<FtpProgress>(p =>
                {
                    double overallPercent = 15 + (p.Progress * 0.65);
                    double mbTransferred = p.TransferredBytes / (1024.0 * 1024.0);
                    string speedStr = p.TransferSpeedToString();
                    string etaStr = p.ETA != TimeSpan.Zero ? $"ETA: {p.ETA:mm\\:ss}" : "";
                    string transferredStr = $"{mbTransferred:0.0} MB transferidos";

                    ReportProgress(overallPercent, $"Baixando: {p.Progress:0.0}% ({transferredStr})", speedStr, transferredStr, etaStr);
                });

                await client.DownloadFile(tempRar, targetFile.RemotePath, FtpLocalExists.Overwrite, FtpVerify.None, progressHandler, cancellationToken);
                Log($"Download de {targetFile.FileName} concluído com sucesso!", LogLevel.Success);

                cancellationToken.ThrowIfCancellationRequested();

                // Extração
                ReportProgress(82, "Extraindo pacote comprimido...");
                Log("Extraindo arquivos do pacote...", LogLevel.Info);

                bool extracted = ExtractRarPackage(tempRar, tempExtract);
                if (!extracted)
                {
                    throw new Exception("Falha ao descompactar o arquivo RAR baixado.");
                }

                var exeFiles = Directory.GetFiles(tempExtract, "*.exe", SearchOption.AllDirectories);
                if (exeFiles.Length == 0)
                {
                    throw new FileNotFoundException($"Nenhum arquivo .exe foi encontrado dentro do pacote {targetFile.FileName}.");
                }

                string sourceExe = exeFiles[0];
                string destExe = Path.Combine(destDir, finalExeName);

                // REGRA DE BACKUP: Mover versões antigas para a pasta '[Nome] bkp'
                ReportProgress(88, $"Criando backup em '{app.BackupFolderName}'...");
                if (app.Id.Equals("ClientRP", StringComparison.OrdinalIgnoreCase))
                {
                    BackupFiles(destDir, app.BackupFolderName, "*Clien*RP*.exe");
                }
                else
                {
                    BackupFiles(destDir, app.BackupFolderName, $"*{app.StandardExePrefix}*.exe");
                }

                ReportProgress(94, "Instalando novo executável...");
                Log($"Instalando em: {destExe}", LogLevel.Info);

                if (File.Exists(destExe))
                {
                    File.Delete(destExe);
                }
                File.Move(sourceExe, destExe);

                await client.Disconnect(cancellationToken);

                ReportProgress(100, $"{app.Name} atualizado com sucesso!", "", "", "");
                Log($"[OK] {app.Name} atualizado com êxito! Arquivo instalado: {destExe}", LogLevel.Success);

                return destExe;
            }
            finally
            {
                CleanupTempDir(tempDir);
            }
        }

        // =====================================================================
        // CONSULTA DE ARQUIVOS EM TEMPO REAL NO FTP
        // =====================================================================
        public async Task<List<RemoteFileInfo>> FetchAppRemoteFilesAsync(AppDefinition app, AppSettings settings, CancellationToken cancellationToken)
        {
            var result = new List<RemoteFileInfo>();
            try
            {
                using var client = CreateFtpClient(settings);
                await client.AutoConnect(cancellationToken);

                var items = await GetDirectoryListingCaseInsensitiveAsync(client, app.RemoteFolder, cancellationToken);
                foreach (var item in items)
                {
                    if (item.Type == FtpObjectType.File && item.Name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrEmpty(app.FilePrefix))
                        {
                            string cleanPrefix = app.FilePrefix.TrimEnd('_', '-');
                            if (!item.Name.StartsWith(app.FilePrefix, StringComparison.OrdinalIgnoreCase) &&
                                !item.Name.Contains(cleanPrefix, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                        }

                        var match = Regex.Match(item.Name, @"\d+(\.\d+)+");
                        string versionStr = match.Success ? match.Value : "";

                        result.Add(new RemoteFileInfo
                        {
                            RemotePath = item.FullName,
                            FileName = item.Name,
                            VersionNumber = versionStr,
                            SizeBytes = item.Size,
                            Modified = ConvertFtpTimeToBrasilia(item.Modified)
                        });
                    }
                }

                await client.Disconnect(cancellationToken);
            }
            catch (Exception ex)
            {
                Log($"Aviso ao consultar arquivos de {app.Name}: {ex.Message}", LogLevel.Warning);
            }

            // Ordena os arquivos pela data mais recente
            return result.OrderByDescending(f => f.Modified).ToList();
        }

        // =====================================================================
        // CONSULTA DE CARIMBO E TAMANHO DAS VERSÕES DO ERP
        // =====================================================================
        public async Task<Dictionary<int, ErpVersionServerInfo>> FetchErpVersionsDetailsAsync(AppSettings settings, CancellationToken cancellationToken)
        {
            var result = new Dictionary<int, ErpVersionServerInfo>();
            try
            {
                using var client = CreateFtpClient(settings);
                await client.AutoConnect(cancellationToken);

                // Obtém a listagem das pastas do Delphi 10 e Delphi 13 de forma case-insensitive
                var d10Items = await GetDirectoryListingCaseInsensitiveAsync(client, "install/erp", cancellationToken);
                var d13Items = await GetDirectoryListingCaseInsensitiveAsync(client, "install/erp/delphi_13", cancellationToken);

                var options = ErpOption.GetDefaultOptions();
                foreach (var opt in options)
                {
                    try
                    {
                        var folderItems = opt.IsDelphi13 ? d13Items : d10Items;
                        var package = FindErpPackageInListing(folderItems, opt.IsBeta);

                        if (package != null)
                        {
                            DateTime dt = ConvertFtpTimeToBrasilia(package.Modified);
                            if (dt == default)
                            {
                                try
                                {
                                    dt = ConvertFtpTimeToBrasilia(await client.GetModifiedTime(package.FullName, cancellationToken));
                                }
                                catch { }
                            }

                            long size = package.Size;
                            if (size <= 0)
                            {
                                try
                                {
                                    size = await client.GetFileSize(package.FullName, -1, cancellationToken);
                                }
                                catch { }
                            }

                            result[opt.Id] = new ErpVersionServerInfo
                            {
                                OptionId = opt.Id,
                                Modified = dt,
                                SizeBytes = size,
                                RemoteFileName = package.Name,
                                RemoteFullPath = package.FullName
                            };
                        }
                    }
                    catch
                    {
                        // Se falhar em uma versão específica, prossegue
                    }
                }

                await client.Disconnect(cancellationToken);
            }
            catch (Exception ex)
            {
                Log($"Aviso ao consultar detalhes do ERP no servidor: {ex.Message}", LogLevel.Warning);
            }
            return result;
        }

        public async Task<Dictionary<int, DateTime>> FetchErpVersionsDatesAsync(AppSettings settings, CancellationToken cancellationToken)
        {
            var details = await FetchErpVersionsDetailsAsync(settings, cancellationToken);
            return details.ToDictionary(k => k.Key, v => v.Value.Modified);
        }

        private static string? GetInternalExeVersion(string filePath)
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(filePath);
                string? ver = vi.FileVersion?.Trim();
                if (string.IsNullOrWhiteSpace(ver) || ver == "0.0.0.0")
                {
                    ver = vi.ProductVersion?.Trim();
                }

                if (!string.IsNullOrWhiteSpace(ver) && ver != "0.0.0.0")
                {
                    ver = ver.Replace(',', '.').Replace(' ', '_').Trim();
                    foreach (char c in Path.GetInvalidFileNameChars())
                    {
                        ver = ver.Replace(c, '_');
                    }
                    return ver;
                }
            }
            catch
            {
                // Silencioso se não conseguir ler informações de versão
            }
            return null;
        }

        // =====================================================================
        // ROTINA DE BACKUP: MOVE ARQUIVOS ANTIGOS PARA PASTA DE BKP
        // =====================================================================
        private void BackupFiles(string destDir, string backupFolderName, string searchPattern, string? excludePattern = null)
        {
            try
            {
                if (!Directory.Exists(destDir)) return;

                string backupDir = Path.Combine(destDir, backupFolderName);
                var existingFiles = Directory.GetFiles(destDir, searchPattern, SearchOption.TopDirectoryOnly);

                foreach (var file in existingFiles)
                {
                    string fileNameOnly = Path.GetFileName(file);
                    if (!string.IsNullOrEmpty(excludePattern))
                    {
                        if (fileNameOnly.Contains("Beta", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }

                    if (!Directory.Exists(backupDir))
                    {
                        Directory.CreateDirectory(backupDir);
                    }

                    var fi = new FileInfo(file);
                    DateTime fileModTime = ConvertFtpTimeToBrasilia(fi.LastWriteTimeUtc);
                    if (fileModTime == default || fileModTime <= DateTime.MinValue)
                    {
                        fileModTime = GetBrasiliaNow();
                    }

                    string nameWithoutExt = Path.GetFileNameWithoutExtension(file);
                    string ext = Path.GetExtension(file);
                    bool isBetaFile = fileNameOnly.Contains("Beta", StringComparison.OrdinalIgnoreCase);
                    string? internalVersion = GetInternalExeVersion(file);
                    string dateStr = fileModTime.ToString(@"dd.MM.yyyy - HH\hmm");

                    // Limpa sufixos caso o arquivo antigo já contenha a versão no próprio nome
                    string cleanBase = nameWithoutExt;
                    if (!string.IsNullOrWhiteSpace(internalVersion) && cleanBase.EndsWith(internalVersion, StringComparison.OrdinalIgnoreCase))
                    {
                        cleanBase = cleanBase.Substring(0, cleanBase.Length - internalVersion.Length).TrimEnd(' ', '_', '-');
                    }

                    string targetFileName;
                    if (isBetaFile)
                    {
                        // Para ERP Beta: ERP + versão + data de modificação
                        // Exemplo: ERPBeta 24.1.0.5 29.10.2026 - 08h02.exe
                        if (!string.IsNullOrWhiteSpace(internalVersion))
                        {
                            targetFileName = $"{cleanBase} {internalVersion} {dateStr}{ext}";
                        }
                        else
                        {
                            targetFileName = $"{cleanBase} {dateStr}{ext}";
                        }
                    }
                    else
                    {
                        // Para ERP Fix e demais aplicações: Nome + Versão do arquivo
                        // Exemplo: WRpdv 3.16.59.exe, ERP 24.1.0.12.exe
                        if (!string.IsNullOrWhiteSpace(internalVersion))
                        {
                            targetFileName = $"{cleanBase} {internalVersion}{ext}";
                        }
                        else
                        {
                            // Fallback de segurança se não houver versão interna gravada no .exe
                            targetFileName = $"{cleanBase} {dateStr}{ext}";
                        }
                    }

                    string targetPath = Path.Combine(backupDir, targetFileName);

                    // Se já existir no backup um arquivo com esse mesmo nome, adiciona data de modificação ou contador
                    if (File.Exists(targetPath))
                    {
                        string baseTarget = Path.GetFileNameWithoutExtension(targetFileName);
                        if (!baseTarget.Contains(dateStr))
                        {
                            targetFileName = $"{baseTarget} {dateStr}{ext}";
                            targetPath = Path.Combine(backupDir, targetFileName);
                        }

                        int count = 1;
                        while (File.Exists(targetPath))
                        {
                            string curBase = Path.GetFileNameWithoutExtension(targetFileName);
                            targetPath = Path.Combine(backupDir, $"{curBase}_{count}{ext}");
                            count++;
                        }
                    }

                    File.Move(file, targetPath);
                    Log($"Arquivo anterior movido para backup: {fileNameOnly} -> {Path.GetFileName(targetPath)} em '{backupFolderName}'", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                Log($"Aviso ao mover arquivos antigos para '{backupFolderName}': {ex.Message}", LogLevel.Warning);
            }
        }

        private async Task CheckAndHandleRunningProcessesAsync(string processPrefix, string destinationDir)
        {
            int currentPid = Environment.ProcessId;
            string currentProcName = Process.GetCurrentProcess().ProcessName;

            var running = Process.GetProcesses()
                .Where(p =>
                {
                    try
                    {
                        // 1. Ignora o próprio processo do atualizador
                        if (p.Id == currentPid) return false;

                        string name = p.ProcessName;
                        if (name.Equals("AtualizadorERP", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals(currentProcName, StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }

                        // 2. Filtro específico para o ERP (evita falso positivo com AtualizadorERP)
                        if (processPrefix.Equals("ERP", StringComparison.OrdinalIgnoreCase))
                        {
                            if (name.Equals("ERP", StringComparison.OrdinalIgnoreCase) ||
                                name.Equals("ERPBeta", StringComparison.OrdinalIgnoreCase) ||
                                name.StartsWith("ERP ", StringComparison.OrdinalIgnoreCase) ||
                                name.StartsWith("ERP_", StringComparison.OrdinalIgnoreCase))
                            {
                                return true;
                            }
                        }
                        else
                        {
                            // Para outras aplicações (ex: WRpdv, ServerMatriz, ClienrRP, etc)
                            if (name.Equals(processPrefix, StringComparison.OrdinalIgnoreCase) ||
                                name.StartsWith(processPrefix + " ", StringComparison.OrdinalIgnoreCase) ||
                                name.StartsWith(processPrefix + "_", StringComparison.OrdinalIgnoreCase) ||
                                name.StartsWith(processPrefix + ".", StringComparison.OrdinalIgnoreCase) ||
                                (processPrefix.Equals("ClienrRP", StringComparison.OrdinalIgnoreCase) && name.Equals("ClientRP", StringComparison.OrdinalIgnoreCase)))
                            {
                                return true;
                            }
                        }

                        // 3. Processo ativo apontando para a pasta destino da aplicação (exceto AtualizadorERP)
                        if (!string.IsNullOrWhiteSpace(destinationDir) && p.MainModule?.FileName != null)
                        {
                            if (p.MainModule.FileName.StartsWith(destinationDir, StringComparison.OrdinalIgnoreCase))
                            {
                                string exeName = Path.GetFileName(p.MainModule.FileName);
                                if (!exeName.StartsWith("AtualizadorERP", StringComparison.OrdinalIgnoreCase))
                                {
                                    return true;
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Processos protegidos do sistema (System, svchost, etc)
                    }
                    return false;
                })
                .ToList();

            if (running.Count > 0)
            {
                foreach (var proc in running)
                {
                    string pName = proc.ProcessName;
                    int pId = proc.Id;
                    Log($"Aviso: O processo '{pName}' (PID: {pId}) está em execução.", LogLevel.Warning);

                    if (RequestCloseProcessAsync != null)
                    {
                        bool shouldClose = await RequestCloseProcessAsync(pName, pId);
                        if (shouldClose)
                        {
                            try
                            {
                                proc.Kill();
                                proc.WaitForExit(3000);
                                Log($"Processo '{pName}' finalizado com segurança.", LogLevel.Success);
                            }
                            catch (Exception ex)
                            {
                                Log($"Não foi possível fechar o processo '{pName}': {ex.Message}", LogLevel.Warning);
                            }
                        }
                        else
                        {
                            throw new OperationCanceledException($"Atualização cancelada pelo usuário pois o processo '{pName}' está aberto.");
                        }
                    }
                }
            }
        }

        private bool ExtractRarPackage(string rarPath, string outputDir)
        {
            try
            {
                using var archive = RarArchive.OpenArchive(rarPath);
                archive.WriteToDirectory(outputDir, new ExtractionOptions
                {
                    ExtractFullPath = true,
                    Overwrite = true
                });
                Log("Pacote extraído com sucesso pelo descompactador integrado.", LogLevel.Success);
                return true;
            }
            catch (Exception ex)
            {
                Log($"Aviso na extração interna: {ex.Message}. Tentando WinRAR local...", LogLevel.Warning);
            }

            string? winrarPath = FindWinRarPath();
            if (!string.IsNullOrEmpty(winrarPath) && File.Exists(winrarPath))
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = winrarPath,
                        Arguments = $"e -y -ibck \"{rarPath}\" \"{outputDir}\\\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var proc = Process.Start(psi);
                    proc?.WaitForExit();
                    if (proc?.ExitCode == 0)
                    {
                        Log("Pacote extraído com sucesso pelo WinRAR local.", LogLevel.Success);
                        return true;
                    }
                }
                catch (Exception winrarEx)
                {
                    Log($"Erro no WinRAR: {winrarEx.Message}", LogLevel.Error);
                }
            }

            return false;
        }

        private static string? FindWinRarPath()
        {
            string p1 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinRAR", "WinRAR.exe");
            if (File.Exists(p1)) return p1;

            string p2 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WinRAR", "WinRAR.exe");
            if (File.Exists(p2)) return p2;

            return null;
        }

        private static void CleanupTempDir(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch
            {
                // Silencioso
            }
        }

        public async Task<bool> TestFtpConnectionAsync(string host, int port, string user, string pass)
        {
            try
            {
                using var client = new AsyncFtpClient(host, user, pass, port);
                client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
                client.Config.ValidateAnyCertificate = true;
                client.Config.ConnectTimeout = 10000;
                client.Config.ReadTimeout = 10000;

                await client.AutoConnect();
                await client.Disconnect();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
