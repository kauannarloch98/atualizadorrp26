using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using AtualizadorERP.Models;
using AtualizadorERP.Services;

namespace AtualizadorERP
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<LogMessage> _logs = new();
        private readonly ObservableCollection<AppDefinition> _apps = new();
        private readonly AppSettings _settings;
        private readonly ErpOption[] _options;
        private ErpOption _selectedOption;
        private AppDefinition _currentApp;
        private CancellationTokenSource? _cts;
        private string? _lastInstalledExePath;
        private readonly FtpUpdaterService _updaterService;

        public MainWindow()
        {
            _settings = AppSettings.Load();
            _options = ErpOption.GetDefaultOptions();
            _selectedOption = _options[0]; // ERP Fix Delphi 10 padrão
            _updaterService = new FtpUpdaterService();

            foreach (var app in AppDefinition.GetAllDefinitions())
            {
                _apps.Add(app);
            }
            _currentApp = _apps[0];

            InitializeComponent();

            LbApps.ItemsSource = _apps;
            IcLogs.ItemsSource = _logs;

            // Seleciona ERP por padrão
            LbApps.SelectedIndex = 0;
            UpdateCurrentAppUi();

            Loaded += MainWindow_Loaded;

            TxtAppVersionSidebar.Text = GitHubUpdateService.GetCurrentVersionString();

            AddLog("Aplicativo iniciado. RP Info pronta para operação.", LogLevel.Info);
        }

        private System.Windows.Threading.DispatcherTimer? _appUpdateTimer;

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 1. Inicia checagem silenciosa de atualizações no GitHub imediatamente
            _ = CheckForAppUpdatesAsync(silent: true);

            // 2. Timer para checagem automática periódica em segundo plano (a cada 5 minutos)
            _appUpdateTimer?.Stop();
            _appUpdateTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(5)
            };
            _appUpdateTimer.Tick += async (s, ev) => await CheckForAppUpdatesAsync(silent: true);
            _appUpdateTimer.Start();

            // 3. Inicia consulta das versões no FTP em segundo plano (sem bloquear o GitHub updater)
            if (!_settings.IsConfigured)
            {
                AddLog("Atenção: Credenciais do servidor FTP pendentes de configuração.", LogLevel.Warning);

                var win = new SettingsWindow(_settings, isFirstRun: true) { Owner = this };
                if (win.ShowDialog() == true && _settings.IsConfigured)
                {
                    UpdateCurrentAppUi();
                    AddLog("Configurações iniciais salvas com sucesso!", LogLevel.Success);
                    _ = RefreshAllVersionsFromFtpAsync();
                }
                else
                {
                    AddLog("Aviso: O sistema necessita de Usuário e Senha do FTP para baixar arquivos.", LogLevel.Warning);
                }
            }
            else
            {
                _ = RefreshAllVersionsFromFtpAsync();
            }
        }

        // =====================================================================
        // ATUALIZAÇÃO DA INTERFACE BASEADA NO SISTEMA SELECIONADO NA SIDEBAR
        // =====================================================================
        private void LbApps_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (LbApps.SelectedItem is AppDefinition selected)
            {
                _currentApp = selected;
                UpdateCurrentAppUi();
                AddLog($"Aplicação ativa: {_currentApp.Name}", LogLevel.Info);
            }
        }

        private void UpdateCurrentAppUi()
        {
            if (_currentApp == null || TxtAppTitle == null) return;

            // 1. Cabeçalho
            TxtAppTitle.Text = _currentApp.Name;
            TxtAppCategory.Text = _currentApp.Category;
            TxtRemotePathInfo.Text = $"FTP: /{_currentApp.RemoteFolder}";

            // 2. Bloco da pasta de instalação
            string currentDir = _settings.GetDirectory(_currentApp.Id);
            if (string.IsNullOrWhiteSpace(currentDir))
            {
                TxtCurrentDest.Text = "(Pasta não configurada - será solicitada antes do download)";
                TxtCurrentDest.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // Amber
            }
            else
            {
                TxtCurrentDest.Text = currentDir;
                TxtCurrentDest.Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)); // Slate 200
            }

            // 3. Regra de backup
            if (_currentApp.IsErpSuite)
            {
                TxtBackupRuleInfo.Text = "Regra de backup: Versões antigas são salvas em 'fix bkp' (versão) e 'beta bkp' (versão + data)";
            }
            else
            {
                TxtBackupRuleInfo.Text = $"Regra de backup: Versões anteriores são movidas para '{_currentApp.BackupFolderName}' com a versão do arquivo";
            }

            // 4. Alterna painéis de versão (ERP vs Genérico)
            if (_currentApp.IsErpSuite)
            {
                PanelErpVersions.Visibility = Visibility.Visible;
                PanelGenericApp.Visibility = Visibility.Collapsed;
            }
            else
            {
                PanelErpVersions.Visibility = Visibility.Collapsed;
                PanelGenericApp.Visibility = Visibility.Visible;
                TxtGenericAppIcon.Text = _currentApp.IconGlyph;

                UpdateGenericVersionsDisplay();
            }
        }

        private void UpdateGenericVersionsDisplay()
        {
            if (_currentApp.AvailableFiles.Count > 0)
            {
                CmbGenericVersions.ItemsSource = null;
                CmbGenericVersions.ItemsSource = _currentApp.AvailableFiles;
                CmbGenericVersions.DisplayMemberPath = "FileName";

                var target = _currentApp.LatestFile ?? _currentApp.AvailableFiles[0];
                CmbGenericVersions.SelectedItem = target;
                UpdateGenericDetailsCard(target);
            }
            else
            {
                CmbGenericVersions.ItemsSource = null;
                TxtGenericTargetVersion.Text = _currentApp.StatusSummary;
                TxtGenericExeRename.Text = $"Será instalado como: {_currentApp.StandardExePrefix}.exe";
                TxtGenericFileSize.Text = "Tamanho: --";
                TxtGenericFileDate.Text = "Modificado em: --";
            }
        }

        private void UpdateGenericDetailsCard(RemoteFileInfo file)
        {
            if (file == null) return;
            string finalExe = _currentApp.GenerateFinalExeName(file.FileName);
            TxtGenericTargetVersion.Text = string.IsNullOrEmpty(file.VersionNumber) ? file.FileName : $"Versão {file.VersionNumber}";
            TxtGenericExeRename.Text = $"Será instalado como: {finalExe}";
            TxtGenericFileSize.Text = $"Tamanho: {file.FormattedSize}";
            TxtGenericFileDate.Text = $"Modificado em: {file.FormattedDate}";
        }

        private void CmbGenericVersions_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (CmbGenericVersions.SelectedItem is RemoteFileInfo file)
            {
                UpdateGenericDetailsCard(file);
            }
        }

        // =====================================================================
        // CONSULTA DE VERSÕES EM TEMPO REAL NO FTP
        // =====================================================================
        private async void BtnRefreshFtp_Click(object sender, RoutedEventArgs e)
        {
            await RefreshAllVersionsFromFtpAsync();
        }

        private async Task RefreshAllVersionsFromFtpAsync()
        {
            if (!_settings.IsConfigured) return;

            BtnRefreshFtp.IsEnabled = false;
            TxtRefreshBtn.Text = "Consultando FTP...";
            AddLog("Verificando versões e atualizações disponíveis no servidor FTP...", LogLevel.Info);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            try
            {
                // 1. ERP - Detalhes, Carimbos de data/hora e Tamanhos
                var erpDetails = await Task.Run(() => _updaterService.FetchErpVersionsDetailsAsync(_settings, cts.Token));
                if (erpDetails.Count > 0)
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (erpDetails.TryGetValue(1, out var info1))
                        {
                            TxtErpFixD10Sub.Text = $"Estável oficial • FTP: {info1.Modified:dd/MM/yyyy HH:mm}";
                            TxtErpFixD10Size.Text = $"Tamanho: {info1.FormattedSize}";
                        }

                        if (erpDetails.TryGetValue(2, out var info2))
                        {
                            TxtErpBetaD10Sub.Text = $"Testes Delphi 10 • FTP: {info2.Modified:dd/MM/yyyy HH:mm}";
                            TxtErpBetaD10Exe.Text = "Será instalado como: ERPBeta.exe";
                            TxtErpBetaD10Size.Text = $"Tamanho: {info2.FormattedSize}";
                        }

                        if (erpDetails.TryGetValue(3, out var info3))
                        {
                            TxtErpFixD13Sub.Text = $"Estável Delphi 13 • FTP: {info3.Modified:dd/MM/yyyy HH:mm}";
                            TxtErpFixD13Exe.Text = "Será instalado como: ERP.exe (+ DLL)";
                            TxtErpFixD13Size.Text = $"Tamanho: {info3.FormattedSize}";
                        }

                        if (erpDetails.TryGetValue(4, out var info4))
                        {
                            TxtErpBetaD13Sub.Text = $"Testes Delphi 13 • FTP: {info4.Modified:dd/MM/yyyy HH:mm}";
                            TxtErpBetaD13Exe.Text = "Será instalado como: ERPBeta.exe (+ DLL)";
                            TxtErpBetaD13Size.Text = $"Tamanho: {info4.FormattedSize}";
                        }

                        var latestErp = erpDetails.Values.OrderByDescending(d => d.Modified).FirstOrDefault();
                        if (latestErp != null && latestErp.Modified != default && _apps.Count > 0)
                        {
                            _apps[0].StatusSummary = $"Atualizado em {latestErp.Modified:dd/MM HH:mm}";
                        }
                    });
                }

                // 2. Demais 10 aplicações
                for (int i = 1; i < _apps.Count; i++)
                {
                    var app = _apps[i];
                    try
                    {
                        var files = await Task.Run(() => _updaterService.FetchAppRemoteFilesAsync(app, _settings, cts.Token));
                        app.AvailableFiles = files;
                        if (files.Count > 0)
                        {
                            app.LatestFile = files[0];
                            string vStr = !string.IsNullOrEmpty(files[0].VersionNumber) ? $"v{files[0].VersionNumber}" : files[0].FileName;
                            app.StatusSummary = $"{vStr} • {files[0].Modified:dd/MM}";
                        }
                        else
                        {
                            app.StatusSummary = "Nenhum pacote no FTP";
                        }
                    }
                    catch
                    {
                        app.StatusSummary = "Falha ao consultar";
                    }
                }

                // Atualiza a visualização
                LbApps.Items.Refresh();
                UpdateCurrentAppUi();

                AddLog("Consulta ao FTP concluída com êxito!", LogLevel.Success);
            }
            catch (Exception ex)
            {
                AddLog($"Aviso na verificação do FTP: {ex.Message}", LogLevel.Warning);
            }
            finally
            {
                BtnRefreshFtp.IsEnabled = true;
                TxtRefreshBtn.Text = "Consultar Versões no FTP";
            }
        }

        // =====================================================================
        // EXECUÇÃO DO DOWNLOAD E ATUALIZAÇÃO
        // =====================================================================
        private async void BtnStartUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (!_settings.IsConfigured)
            {
                MessageBox.Show(
                    this,
                    "Antes de baixar, é necessário configurar as credenciais de acesso ao FTP.\n\nA janela de configurações será aberta.",
                    "Configuração Necessária",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                var win = new SettingsWindow(_settings, isFirstRun: true) { Owner = this };
                if (win.ShowDialog() != true || !_settings.IsConfigured)
                {
                    AddLog("Atualização cancelada: Credenciais não informadas.", LogLevel.Warning);
                    return;
                }
            }

            // Validação da pasta de destino da aplicação selecionada
            string appDir = _settings.GetDirectory(_currentApp.Id);
            if (string.IsNullOrWhiteSpace(appDir))
            {
                var askDir = MessageBox.Show(
                    this,
                    $"A pasta de instalação de '{_currentApp.Name}' ainda não foi informada.\n\n" +
                    "Deseja selecionar a pasta agora para continuar o download e instalação?",
                    "Pasta Não Informada",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (askDir != MessageBoxResult.Yes)
                {
                    AddLog($"Download de {_currentApp.Name} cancelado: pasta de destino não informada.", LogLevel.Warning);
                    return;
                }

                var dialog = new OpenFolderDialog
                {
                    Title = $"Selecione a pasta de instalação para {_currentApp.Name}",
                    InitialDirectory = Directory.Exists(@"C:\ERP") ? @"C:\ERP" : @"C:\"
                };

                if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
                {
                    appDir = dialog.FolderName;
                    _settings.SetDirectory(_currentApp.Id, appDir);
                    if (_currentApp.IsErpSuite)
                    {
                        _settings.DestinationDir = appDir;
                    }
                    _settings.Save();
                    UpdateCurrentAppUi();
                    AddLog($"Pasta de {_currentApp.Name} configurada para: {appDir}", LogLevel.Info);
                }
                else
                {
                    AddLog($"Operação cancelada: Nenhuma pasta foi selecionada para {_currentApp.Name}.", LogLevel.Warning);
                    return;
                }
            }

            // Preparação dos controles visuais
            SetUpdatingUiState(isUpdating: true);
            PanelSuccess.Visibility = Visibility.Collapsed;
            _cts = new CancellationTokenSource();

            var updater = new FtpUpdaterService();

            updater.OnLog += logMsg =>
            {
                AddLog(logMsg.Text, logMsg.Level);
            };

            updater.OnProgress += report =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    PbProgress.Value = report.Percentage;
                    TxtPercent.Text = $"{report.Percentage:0}%";
                    TxtStatus.Text = report.Status;
                    TxtSpeed.Text = string.IsNullOrEmpty(report.Speed) ? "Velocidade: --" : $"Velocidade: {report.Speed}";
                    TxtTransferred.Text = string.IsNullOrEmpty(report.Transferred) ? "Tamanho: --" : report.Transferred;
                    TxtEta.Text = string.IsNullOrEmpty(report.Eta) ? "Tempo: --" : report.Eta;
                });
            };

            updater.RequestCloseProcessAsync = async (procName, pid) =>
            {
                return await Dispatcher.InvokeAsync(() =>
                {
                    var result = MessageBox.Show(
                        this,
                        $"O processo '{procName}' (PID: {pid}) está em execução.\n\n" +
                        "Deseja fechá-lo automaticamente agora para que a atualização possa substituir o arquivo executável?",
                        "Aplicação em Execução",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    return result == MessageBoxResult.Yes;
                });
            };

            try
            {
                if (_currentApp.IsErpSuite)
                {
                    _lastInstalledExePath = await Task.Run(() => updater.ExecuteUpdateAsync(_selectedOption, _settings, _cts.Token));
                }
                else
                {
                    // Aplicação genérica
                    RemoteFileInfo? target = (RemoteFileInfo?)CmbGenericVersions.SelectedItem ?? _currentApp.LatestFile;
                    if (target == null)
                    {
                        // Busca se ainda não buscou
                        AddLog($"Consultando arquivos de {_currentApp.Name} no FTP...", LogLevel.Info);
                        var files = await Task.Run(() => updater.FetchAppRemoteFilesAsync(_currentApp, _settings, _cts.Token));
                        if (files.Count == 0)
                        {
                            throw new FileNotFoundException($"Nenhum pacote .rar foi encontrado no FTP para {_currentApp.Name}.");
                        }
                        target = files[0];
                    }

                    _lastInstalledExePath = await Task.Run(() => updater.ExecuteGenericAppUpdateAsync(_currentApp, target, appDir, _settings, _cts.Token));
                }

                // Exibição de Sucesso
                PanelSuccess.Visibility = Visibility.Visible;
                TxtSuccessFilePath.Text = $"Arquivo pronto para uso em: {_lastInstalledExePath}";

                if (_settings.OpenFolderOnFinish)
                {
                    OpenFolderInExplorer(appDir);
                }
            }
            catch (OperationCanceledException)
            {
                AddLog("A atualização foi cancelada pelo usuário.", LogLevel.Warning);
                TxtStatus.Text = "Atualização cancelada.";
            }
            catch (Exception ex)
            {
                AddLog($"Falha durante a atualização de {_currentApp.Name}: {ex.Message}", LogLevel.Error);
                TxtStatus.Text = "Ocorreu um erro na atualização.";
                MessageBox.Show(
                    this,
                    $"Não foi possível concluir a atualização de '{_currentApp.Name}'.\n\nDetalhes:\n{ex.Message}",
                    "Falha na Atualização",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                SetUpdatingUiState(isUpdating: false);
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void BtnCancelUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                AddLog("Solicitando cancelamento da operação...", LogLevel.Warning);
                BtnCancelUpdate.IsEnabled = false;
                _cts.Cancel();
            }
        }

        private void SetUpdatingUiState(bool isUpdating)
        {
            BtnStartUpdate.IsEnabled = !isUpdating;
            BtnCancelUpdate.Visibility = isUpdating ? Visibility.Visible : Visibility.Collapsed;
            BtnCancelUpdate.IsEnabled = true;

            // Mantém as cores e tema escuro sem disparar estilos cinzas/brancos padrão do Windows
            LbApps.IsHitTestVisible = !isUpdating;
            LbApps.Opacity = isUpdating ? 0.65 : 1.0;

            RbOpcao1.IsEnabled = !isUpdating;
            RbOpcao2.IsEnabled = !isUpdating;
            RbOpcao3.IsEnabled = !isUpdating;
            RbOpcao4.IsEnabled = !isUpdating;
            CmbGenericVersions.IsEnabled = !isUpdating;
            BtnChangeDest.IsEnabled = !isUpdating;
            BtnSidebarSettings.IsEnabled = !isUpdating;
            BtnRefreshFtp.IsEnabled = !isUpdating;
        }

        // =====================================================================
        // TRATAMENTO DAS OPÇÕES DO ERP (RADIO BUTTONS)
        // =====================================================================
        private void RbOption_Checked(object sender, RoutedEventArgs e)
        {
            if (_options == null || RbOpcao1 == null) return;

            if (RbOpcao1.IsChecked == true) _selectedOption = _options[0];
            else if (RbOpcao2?.IsChecked == true) _selectedOption = _options[1];
            else if (RbOpcao3?.IsChecked == true) _selectedOption = _options[2];
            else if (RbOpcao4?.IsChecked == true) _selectedOption = _options[3];

            if (IsLoaded && _selectedOption != null)
            {
                AddLog($"Opção do ERP selecionada: {_selectedOption.Title}", LogLevel.Info);
            }
        }

        // =====================================================================
        // BOTÕES DE DIRETÓRIOS E BACKUP
        // =====================================================================
        private void BtnChangeDest_Click(object sender, RoutedEventArgs e)
        {
            string current = _settings.GetDirectory(_currentApp.Id);
            var dialog = new OpenFolderDialog
            {
                Title = $"Selecione a pasta de instalação de {_currentApp.Name}",
                InitialDirectory = Directory.Exists(current) ? current : @"C:\"
            };

            if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                _settings.SetDirectory(_currentApp.Id, dialog.FolderName);
                if (_currentApp.IsErpSuite)
                {
                    _settings.DestinationDir = dialog.FolderName;
                }
                _settings.Save();
                UpdateCurrentAppUi();
                AddLog($"Pasta de {_currentApp.Name} alterada para: {dialog.FolderName}", LogLevel.Info);
            }
        }

        private void BtnQuickOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            string dir = _settings.GetDirectory(_currentApp.Id);
            if (string.IsNullOrWhiteSpace(dir))
            {
                MessageBox.Show(this, $"A pasta de {_currentApp.Name} ainda não foi configurada.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            OpenFolderInExplorer(dir);
        }

        private void BtnQuickOpenBackup_Click(object sender, RoutedEventArgs e)
        {
            string dir = _settings.GetDirectory(_currentApp.Id);
            if (string.IsNullOrWhiteSpace(dir))
            {
                MessageBox.Show(this, $"A pasta de {_currentApp.Name} ainda não foi configurada.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string bkpDir;
            if (_currentApp.IsErpSuite)
            {
                bkpDir = Path.Combine(dir, "fix bkp");
                if (!Directory.Exists(bkpDir))
                {
                    string betaBkp = Path.Combine(dir, "beta bkp");
                    if (Directory.Exists(betaBkp)) bkpDir = betaBkp;
                }
            }
            else
            {
                bkpDir = Path.Combine(dir, _currentApp.BackupFolderName);
            }

            if (!Directory.Exists(bkpDir))
            {
                try { Directory.CreateDirectory(bkpDir); } catch { }
            }

            OpenFolderInExplorer(bkpDir);
        }

        private void OpenFolderInExplorer(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AddLog($"Não foi possível abrir a pasta no Explorer: {ex.Message}", LogLevel.Warning);
            }
        }

        private void BtnOpenSettings_Click(object sender, RoutedEventArgs e)
        {
            var win = new SettingsWindow(_settings) { Owner = this };
            if (win.ShowDialog() == true)
            {
                UpdateCurrentAppUi();
                AddLog("Configurações atualizadas com sucesso.", LogLevel.Info);
            }
        }

        private void BtnRunErp_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_lastInstalledExePath) || !File.Exists(_lastInstalledExePath))
            {
                MessageBox.Show(this, "O arquivo executável não foi localizado.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                string workDir = Path.GetDirectoryName(_lastInstalledExePath) ?? _settings.GetDirectory(_currentApp.Id);
                Process.Start(new ProcessStartInfo
                {
                    FileName = _lastInstalledExePath,
                    WorkingDirectory = workDir,
                    UseShellExecute = true
                });

                AddLog($"Iniciando aplicação: {_lastInstalledExePath}", LogLevel.Success);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Erro ao iniciar a aplicação: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // =====================================================================
        // LOGS E TERMINAL
        // =====================================================================
        private void AddLog(string message, LogLevel level)
        {
            Dispatcher.InvokeAsync(() =>
            {
                var entry = new LogMessage
                {
                    Timestamp = FtpUpdaterService.GetBrasiliaNow(),
                    Level = level,
                    Text = message
                };

                _logs.Add(entry);
                if (TxtLogCount != null)
                {
                    TxtLogCount.Text = $"({_logs.Count} eventos)";
                }
                ScrollLogs?.ScrollToBottom();
            });
        }

        private void BtnCopyLogs_Click(object sender, RoutedEventArgs e)
        {
            if (_logs.Count == 0) return;

            var sb = new StringBuilder();
            foreach (var log in _logs)
            {
                sb.AppendLine($"{log.FormattedTime} {log.Tag} {log.Text}");
            }

            try
            {
                Clipboard.SetText(sb.ToString());
                AddLog("Logs copiados para a área de transferência.", LogLevel.Info);
            }
            catch
            {
                // Tratamento se clipboard estiver bloqueado por outro app
            }
        }

        private void BtnClearLogs_Click(object sender, RoutedEventArgs e)
        {
            _logs.Clear();
            TxtLogCount.Text = "(0 eventos)";
        }

        // =====================================================================
        // ATUALIZAÇÃO AUTOMÁTICA VIA GITHUB RELEASES
        // =====================================================================
        private readonly GitHubUpdateService _gitHubUpdateService = new();
        private UpdateCheckResult? _pendingAppUpdate;

        private async Task CheckForAppUpdatesAsync(bool silent)
        {
            try
            {
                if (silent)
                {
                    await Task.Delay(300); // Inicialização suave rápida
                }
                else
                {
                    AddLog("Consultando repositório GitHub (kauannarloch98/atualizadorrp26) por novas versões...", LogLevel.Info);
                }

                var update = await _gitHubUpdateService.CheckForUpdatesAsync();

                Dispatcher.Invoke(() =>
                {
                    if (update.IsAvailable)
                    {
                        _pendingAppUpdate = update;
                        TxtAppUpdateVersion.Text = update.RemoteVersion;
                        TxtAppUpdateSize.Text = !string.IsNullOrEmpty(update.FormattedSize) ? $"({update.FormattedSize})" : "";
                        if (!string.IsNullOrWhiteSpace(update.ReleaseNotes))
                        {
                            TxtAppUpdateNotes.Text = update.ReleaseNotes.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() 
                                                     ?? "Nova atualização pronta para download.";
                        }
                        BannerAppUpdate.Visibility = Visibility.Visible;
                        AddLog($"🚀 Nova versão do Atualizador encontrada no GitHub: {update.RemoteVersion} (Atual: {update.CurrentVersion})", LogLevel.Success);
                    }
                    else if (!silent)
                    {
                        MessageBox.Show(this, 
                            $"Você já está utilizando a versão mais recente do Atualizador ({update.CurrentVersion}).\nNenhuma atualização necessária no momento.", 
                            "Atualizador de Sistemas", 
                            MessageBoxButton.OK, 
                            MessageBoxImage.Information);
                        AddLog($"O Atualizador já está na versão mais recente ({update.CurrentVersion}).", LogLevel.Success);
                    }
                });
            }
            catch (Exception ex)
            {
                if (!silent)
                {
                    AddLog($"Não foi possível verificar atualizações no GitHub: {ex.Message}", LogLevel.Warning);
                    MessageBox.Show(this, $"Erro ao verificar atualizações no GitHub: {ex.Message}", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private async void BtnCheckAppUpdateManual_Click(object sender, RoutedEventArgs e)
        {
            BtnCheckAppUpdateManual.IsEnabled = false;
            try
            {
                await CheckForAppUpdatesAsync(silent: false);
            }
            finally
            {
                BtnCheckAppUpdateManual.IsEnabled = true;
            }
        }

        private void BtnDismissAppUpdate_Click(object sender, RoutedEventArgs e)
        {
            BannerAppUpdate.Visibility = Visibility.Collapsed;
        }

        private async void BtnApplyAppUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingAppUpdate == null || string.IsNullOrWhiteSpace(_pendingAppUpdate.DownloadUrl))
            {
                MessageBox.Show(this, "Nenhuma URL de atualização válida foi localizada.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = MessageBox.Show(this,
                $"Deseja baixar e instalar a versão {_pendingAppUpdate.RemoteVersion} agora?\n\nO aplicativo será reiniciado automaticamente com a nova versão.",
                "Atualizar Aplicativo",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            BtnApplyAppUpdate.IsEnabled = false;
            PbAppUpdate.Visibility = Visibility.Visible;
            TxtAppUpdateStatus.Visibility = Visibility.Visible;
            TxtAppUpdateStatus.Text = "Iniciando download da atualização...";
            PbAppUpdate.Value = 0;

            AddLog($"Iniciando download da versão {_pendingAppUpdate.RemoteVersion} via GitHub...", LogLevel.Info);

            try
            {
                var progress = new Progress<UpdateProgressInfo>(info =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        PbAppUpdate.Value = info.Percent;
                        TxtAppUpdateStatus.Text = info.Status;
                    });
                });

                await _gitHubUpdateService.DownloadAndApplyUpdateAsync(_pendingAppUpdate.DownloadUrl, progress);
            }
            catch (Exception ex)
            {
                BtnApplyAppUpdate.IsEnabled = true;
                PbAppUpdate.Visibility = Visibility.Collapsed;
                TxtAppUpdateStatus.Text = $"Erro na atualização: {ex.Message}";
                AddLog($"Falha ao atualizar aplicativo: {ex.Message}", LogLevel.Error);
                MessageBox.Show(this, $"Erro ao aplicar atualização: {ex.Message}", "Erro de Atualização", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}