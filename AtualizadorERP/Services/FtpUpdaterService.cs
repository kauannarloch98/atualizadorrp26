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
        public string FormattedSize => SizeBytes > 0 ? $"{(SizeBytes / (1024.0 * 1024.0)):0.0} MB" : "--";
    }

    public class FtpUpdaterService
    {
        public event Action<LogMessage>? OnLog;
        public event Action<UpdateProgressReport>? OnProgress;

        public Func<string, int, Task<bool>>? RequestCloseProcessAsync { get; set; }

        private void Log(string message, LogLevel level = LogLevel.Info)
        {
            OnLog?.Invoke(new LogMessage
            {
                Timestamp = DateTime.Now,
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

                ReportProgress(10, "Verificando arquivo remoto...");
                bool exists = await client.FileExists(option.RemoteFile, cancellationToken);
                if (!exists)
                {
                    throw new FileNotFoundException($"O arquivo '{option.RemoteFile}' não foi encontrado no servidor FTP.");
                }

                string finalExeName;
                if (option.IsBeta)
                {
                    ReportProgress(12, "Consultando carimbo de data/hora no FTP...");
                    DateTime remoteModified = await client.GetModifiedTime(option.RemoteFile, cancellationToken);
                    if (remoteModified == default)
                    {
                        remoteModified = DateTime.Now;
                    }

                    string timestamp = remoteModified.ToString(@"dd.MM - HH\hmm");
                    finalExeName = $"{option.BaseName} {timestamp}.exe";
                    Log($"Versão Beta identificada. Carimbo FTP: {timestamp}", LogLevel.Info);
                }
                else
                {
                    finalExeName = $"{option.BaseName}.exe";
                }

                Log($"Nome do executável final: {finalExeName}", LogLevel.Info);
                cancellationToken.ThrowIfCancellationRequested();

                // Download
                Log($"Baixando arquivo: {option.RemoteFile}...", LogLevel.Info);
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

                var status = await client.DownloadFile(tempRar, option.RemoteFile, FtpLocalExists.Overwrite, FtpVerify.None, progressHandler, cancellationToken);
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

                var exeFiles = Directory.GetFiles(tempExtract, "*.exe", SearchOption.AllDirectories);
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

                    string remoteDll = option.DllRemoteFile;
                    try
                    {
                        if (!await client.FileExists(remoteDll, cancellationToken))
                        {
                            string[] fallbacks = new[]
                            {
                                "install/erp/delphi_13/SecureBlackbox24.dll",
                                "install/erp/delphi_13/secureblackbox24.dll",
                                "install/erp/Delphi_13/SecureBlackbox24.dll"
                            };
                            foreach (var fb in fallbacks)
                            {
                                if (await client.FileExists(fb, cancellationToken))
                                {
                                    remoteDll = fb;
                                    break;
                                }
                            }
                        }

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
                BackupFiles(destDir, app.BackupFolderName, $"*{app.StandardExePrefix}*.exe");

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

                var items = await client.GetListing(app.RemoteFolder, cancellationToken);
                foreach (var item in items)
                {
                    if (item.Type == FtpObjectType.File && item.Name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrEmpty(app.FilePrefix) &&
                            !item.Name.StartsWith(app.FilePrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var match = Regex.Match(item.Name, @"\d+(\.\d+)+");
                        string versionStr = match.Success ? match.Value : "";

                        result.Add(new RemoteFileInfo
                        {
                            RemotePath = item.FullName,
                            FileName = item.Name,
                            VersionNumber = versionStr,
                            SizeBytes = item.Size,
                            Modified = item.Modified
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

                var options = ErpOption.GetDefaultOptions();
                foreach (var opt in options)
                {
                    try
                    {
                        DateTime dt = await client.GetModifiedTime(opt.RemoteFile, cancellationToken);
                        long size = 0;
                        try
                        {
                            size = await client.GetFileSize(opt.RemoteFile, -1, cancellationToken);
                        }
                        catch { }

                        result[opt.Id] = new ErpVersionServerInfo
                        {
                            OptionId = opt.Id,
                            Modified = dt,
                            SizeBytes = size
                        };
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

                    if (!string.IsNullOrEmpty(excludePattern))
                    {
                        string fileNameOnly = Path.GetFileName(file);
                        if (fileNameOnly.Contains("Beta", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }

                    if (!Directory.Exists(backupDir))
                    {
                        Directory.CreateDirectory(backupDir);
                    }

                    string fileName = Path.GetFileName(file);
                    string targetPath = Path.Combine(backupDir, fileName);

                    if (File.Exists(targetPath))
                    {
                        string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                        string ext = Path.GetExtension(fileName);
                        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                        targetPath = Path.Combine(backupDir, $"{nameWithoutExt}_{timestamp}{ext}");
                    }

                    File.Move(file, targetPath);
                    Log($"Arquivo anterior movido para backup: {Path.GetFileName(file)} -> {backupFolderName}", LogLevel.Info);
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
                                name.StartsWith("ERP ", StringComparison.OrdinalIgnoreCase) ||
                                name.StartsWith("ERP_", StringComparison.OrdinalIgnoreCase))
                            {
                                return true;
                            }
                        }
                        else
                        {
                            // Para outras aplicações (ex: WRpdv, ServerMatriz, etc)
                            if (name.Equals(processPrefix, StringComparison.OrdinalIgnoreCase) ||
                                name.StartsWith(processPrefix + " ", StringComparison.OrdinalIgnoreCase) ||
                                name.StartsWith(processPrefix + "_", StringComparison.OrdinalIgnoreCase) ||
                                name.StartsWith(processPrefix + ".", StringComparison.OrdinalIgnoreCase))
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
