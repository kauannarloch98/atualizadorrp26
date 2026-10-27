using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using AtualizadorERP.Models;
using AtualizadorERP.Services;

namespace AtualizadorERP
{
    public partial class SettingsWindow : Window
    {
        private readonly AppSettings _settings;
        private readonly FtpUpdaterService _updaterService;
        private readonly bool _isFirstRun;
        private readonly Dictionary<string, TextBox> _dirInputs = new();

        public SettingsWindow(AppSettings settings, bool isFirstRun = false)
        {
            InitializeComponent();
            _settings = settings;
            _isFirstRun = isFirstRun;
            _updaterService = new FtpUpdaterService();

            if (_isFirstRun)
            {
                BannerFirstRun.Visibility = Visibility.Visible;
                TxtHeaderTitle.Text = "Configuração Inicial do Sistema";
            }

            LoadSettingsIntoUi();
            BuildAppDirectoryControls();
        }

        private void LoadSettingsIntoUi()
        {
            TxtHost.Text = string.IsNullOrWhiteSpace(_settings.FtpHost) ? "ftp.rpinfo.com.br" : _settings.FtpHost;
            TxtPort.Text = _settings.FtpPort <= 0 ? "21" : _settings.FtpPort.ToString();
            TxtUser.Text = _settings.FtpUser;
            TxtPass.Password = _settings.FtpPass;
            ChkAutoKill.IsChecked = _settings.CheckRunningProcesses;
            ChkOpenFolder.IsChecked = _settings.OpenFolderOnFinish;
        }

        private void BuildAppDirectoryControls()
        {
            PanelAppDirectories.Children.Clear();
            _dirInputs.Clear();

            var apps = AppDefinition.GetAllDefinitions();

            foreach (var app in apps)
            {
                var card = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)), // #0F172A
                    BorderBrush = new SolidColorBrush(Color.FromRgb(30, 41, 59)), // #1E293B
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 10, 12, 10),
                    Margin = new Thickness(0, 0, 0, 10)
                };

                var sp = new StackPanel();

                // Cabeçalho da aplicação
                var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                var headerLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

                var iconContainer = new Border
                {
                    Width = 26,
                    Height = 26,
                    CornerRadius = new CornerRadius(6),
                    Background = new SolidColorBrush(Color.FromRgb(17, 26, 44)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(34, 48, 74)),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var iconBlock = new TextBlock
                {
                    Text = app.IconGlyph,
                    FontSize = 13,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                iconContainer.Child = iconBlock;

                var nameBlock = new TextBlock
                {
                    Text = app.Name,
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(248, 250, 252))
                };
                var catBlock = new TextBlock
                {
                    Text = $" • {app.Category}",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                    VerticalAlignment = VerticalAlignment.Center
                };

                headerLeft.Children.Add(iconContainer);
                headerLeft.Children.Add(nameBlock);
                headerLeft.Children.Add(catBlock);

                var backupInfo = new TextBlock
                {
                    Text = $"Backup: {app.BackupFolderName}",
                    FontSize = 10.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };

                headerGrid.Children.Add(headerLeft);
                headerGrid.Children.Add(backupInfo);
                sp.Children.Add(headerGrid);

                // Grid com TextBox e Botões
                var rowGrid = new Grid();
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                if (app.Id == "ERP")
                {
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                }
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var txtDir = new TextBox
                {
                    Text = _settings.GetDirectory(app.Id),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(txtDir, 0);
                rowGrid.Children.Add(txtDir);
                _dirInputs[app.Id] = txtDir;

                int nextCol = 1;

                if (app.Id == "ERP")
                {
                    var btnDefault = new Button
                    {
                        Content = "Padrão C:\\ERP",
                        Padding = new Thickness(10, 6, 10, 6),
                        Margin = new Thickness(6, 0, 0, 0),
                        Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                        Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                        BorderThickness = new Thickness(1),
                        Cursor = System.Windows.Input.Cursors.Hand,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    btnDefault.Click += (s, e) => txtDir.Text = @"C:\ERP";
                    Grid.SetColumn(btnDefault, nextCol++);
                    rowGrid.Children.Add(btnDefault);
                }

                var btnBrowse = new Button
                {
                    Content = "Procurar...",
                    Padding = new Thickness(12, 6, 12, 6),
                    Margin = new Thickness(6, 0, 0, 0),
                    Background = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                    Foreground = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
                    BorderThickness = new Thickness(0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center
                };

                string appId = app.Id;
                string appName = app.Name;
                btnBrowse.Click += (s, e) =>
                {
                    var dlg = new OpenFolderDialog
                    {
                        Title = $"Selecione a pasta de instalação de {appName}",
                        InitialDirectory = Directory.Exists(txtDir.Text) ? txtDir.Text : @"C:\"
                    };
                    if (dlg.ShowDialog(this) == true)
                    {
                        txtDir.Text = dlg.FolderName;
                    }
                };

                Grid.SetColumn(btnBrowse, nextCol);
                rowGrid.Children.Add(btnBrowse);

                sp.Children.Add(rowGrid);
                card.Child = sp;
                PanelAppDirectories.Children.Add(card);
            }
        }

        private async void BtnTestFtp_Click(object sender, RoutedEventArgs e)
        {
            string host = TxtHost.Text.Trim();
            if (!int.TryParse(TxtPort.Text.Trim(), out int port)) port = 21;
            string user = TxtUser.Text.Trim();
            string pass = TxtPass.Password;

            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
            {
                MessageBox.Show(this, "Informe o Usuário e a Senha antes de testar a conexão.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnTestFtp.IsEnabled = false;
            TxtTestStatus.Text = "Conectando ao FTP...";
            TxtTestStatus.Foreground = Brushes.Gold;

            bool success = await _updaterService.TestFtpConnectionAsync(host, port, user, pass);

            if (success)
            {
                TxtTestStatus.Text = "✓ Conexão FTPS realizada com sucesso!";
                TxtTestStatus.Foreground = Brushes.LightGreen;
            }
            else
            {
                TxtTestStatus.Text = "✗ Falha ao conectar. Verifique login e senha.";
                TxtTestStatus.Foreground = Brushes.Coral;
            }

            BtnTestFtp.IsEnabled = true;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            string host = TxtHost.Text.Trim();
            string user = TxtUser.Text.Trim();
            string pass = TxtPass.Password;

            if (string.IsNullOrWhiteSpace(host))
            {
                MessageBox.Show(this, "O campo Servidor FTP (Host) é obrigatório.", "Atenção", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtHost.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(user))
            {
                MessageBox.Show(this, "Por favor, informe o seu Usuário do FTP.", "Atenção", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtUser.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(pass))
            {
                MessageBox.Show(this, "Por favor, informe a Senha do FTP.", "Atenção", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtPass.Focus();
                return;
            }

            _settings.FtpHost = host;
            if (int.TryParse(TxtPort.Text.Trim(), out int port))
            {
                _settings.FtpPort = port;
            }
            _settings.FtpUser = user;
            _settings.FtpPass = pass;
            _settings.CheckRunningProcesses = ChkAutoKill.IsChecked ?? true;
            _settings.OpenFolderOnFinish = ChkOpenFolder.IsChecked ?? true;

            // Salva os diretórios informados
            foreach (var kvp in _dirInputs)
            {
                _settings.SetDirectory(kvp.Key, kvp.Value.Text);
            }

            _settings.Save();

            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (_isFirstRun && !_settings.HasFtpCredentials)
            {
                var result = MessageBox.Show(
                    this,
                    "As credenciais de acesso ao servidor FTP são necessárias para que as consultas e downloads funcionem.\n\nDeseja realmente fechar sem salvar agora?",
                    "Configuração Incompleta",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.No)
                {
                    return;
                }
            }

            DialogResult = false;
            Close();
        }
    }
}
