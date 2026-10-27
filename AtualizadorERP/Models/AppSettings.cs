using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AtualizadorERP.Models
{
    public class AppSettings
    {
        public string FtpHost { get; set; } = "ftp.rpinfo.com.br";
        public int FtpPort { get; set; } = 21;
        public string FtpUser { get; set; } = string.Empty;
        public string FtpPass { get; set; } = string.Empty;

        // Diretórios individuais de cada aplicação: "ERP", "WRpdv", "ServerMatriz", etc.
        public Dictionary<string, string> AppDirectories { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // Propriedade mantida para compatibilidade direta com ERP
        public string DestinationDir
        {
            get => GetDirectory("ERP");
            set => SetDirectory("ERP", value);
        }

        public bool OpenFolderOnFinish { get; set; } = true;
        public bool CheckRunningProcesses { get; set; } = true;

        public bool HasFtpCredentials =>
            !string.IsNullOrWhiteSpace(FtpHost) &&
            !string.IsNullOrWhiteSpace(FtpUser) &&
            !string.IsNullOrWhiteSpace(FtpPass);

        // Verifica se ao menos as credenciais FTP foram configuradas
        public bool IsConfigured => HasFtpCredentials;

        public string GetDirectory(string appId)
        {
            if (AppDirectories.TryGetValue(appId, out var dir) && !string.IsNullOrWhiteSpace(dir))
            {
                return dir;
            }
            return string.Empty;
        }

        public void SetDirectory(string appId, string path)
        {
            AppDirectories[appId] = path?.Trim() ?? string.Empty;
        }

        private static string GetConfigFilePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folder = Path.Combine(appData, "AtualizadorERP");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, "settings.json");
        }

        public static AppSettings Load()
        {
            try
            {
                string path = GetConfigFilePath();
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        if (settings.AppDirectories == null)
                        {
                            settings.AppDirectories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        }
                        if (!settings.AppDirectories.ContainsKey("ERP") && !string.IsNullOrWhiteSpace(settings.DestinationDir))
                        {
                            settings.AppDirectories["ERP"] = settings.DestinationDir;
                        }
                        return settings;
                    }
                }
            }
            catch
            {
                // Em caso de falha de leitura, retorna defaults em branco
            }

            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                string path = GetConfigFilePath();
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(this, options);
                File.WriteAllText(path, json);
            }
            catch
            {
                // Ignora falhas de escrita silenciosamente
            }
        }
    }
}
