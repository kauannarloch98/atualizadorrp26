using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AtualizadorERP.Models
{
    public class RemoteFileInfo
    {
        public string RemotePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string VersionNumber { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public DateTime Modified { get; set; }

        public string FormattedSize => $"{(SizeBytes / (1024.0 * 1024.0)):0.0} MB";
        public string FormattedDate => Modified.ToString("dd/MM/yyyy HH:mm");
    }

    public class AppDefinition
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string IconGlyph { get; set; } = "📦";
        public string RemoteFolder { get; set; } = string.Empty;
        public string BackupFolderName { get; set; } = string.Empty;
        public string FilePrefix { get; set; } = string.Empty;
        public string StandardExePrefix { get; set; } = string.Empty;
        public bool IsErpSuite { get; set; }

        // Informações dinâmicas obtidas em tempo real do FTP
        public List<RemoteFileInfo> AvailableFiles { get; set; } = new();
        public RemoteFileInfo? LatestFile { get; set; }
        public string StatusSummary { get; set; } = "Aguardando verificação do servidor...";
        public bool IsLoading { get; set; }

        public static List<AppDefinition> GetAllDefinitions()
        {
            return new List<AppDefinition>
            {
                // 1. ERP
                new AppDefinition
                {
                    Id = "ERP",
                    Name = "ERP (RP One)",
                    Category = "Sistema Central",
                    IconGlyph = "🏢",
                    RemoteFolder = "install/erp",
                    BackupFolderName = "fix bkp", // e beta bkp
                    IsErpSuite = true,
                    StatusSummary = "4 versões disponíveis (Fix/Beta Delphi 10 e 13)"
                },
                // 2. WRpdv
                new AppDefinition
                {
                    Id = "WRpdv",
                    Name = "WRpdv",
                    Category = "Frente de Caixa",
                    IconGlyph = "🛒",
                    RemoteFolder = "install/frente/WRpdv",
                    BackupFolderName = "WRpdv bkp",
                    StandardExePrefix = "WRpdv"
                },
                // 3. ServerMatriz
                new AppDefinition
                {
                    Id = "ServerMatriz",
                    Name = "ServerMatriz",
                    Category = "Servidores",
                    IconGlyph = "🖥️",
                    RemoteFolder = "install/frente/ServerMatriz",
                    BackupFolderName = "ServerMatriz bkp",
                    StandardExePrefix = "ServerMatriz"
                },
                // 4. ServerUnidade
                new AppDefinition
                {
                    Id = "ServerUnidade",
                    Name = "ServerUnidade",
                    Category = "Servidores",
                    IconGlyph = "🌐",
                    RemoteFolder = "install/frente/ServerUn",
                    BackupFolderName = "Server UN bkp",
                    StandardExePrefix = "ServerUN"
                },
                // 5. Frente
                new AppDefinition
                {
                    Id = "Frente",
                    Name = "Frente - NFCe",
                    Category = "Frente de Caixa",
                    IconGlyph = "🧾",
                    RemoteFolder = "install/frente/Frente - NFCe",
                    BackupFolderName = "Frente bkp",
                    FilePrefix = "frente_",
                    StandardExePrefix = "Frente"
                },
                // 6. CredRP
                new AppDefinition
                {
                    Id = "CredRP",
                    Name = "CredRP",
                    Category = "Serviços",
                    IconGlyph = "💳",
                    RemoteFolder = "install/frente/CredRP",
                    BackupFolderName = "Cred RP bkp",
                    StandardExePrefix = "CredRP"
                },
                // 7. ClientRP
                new AppDefinition
                {
                    Id = "ClientRP",
                    Name = "ClientRP",
                    Category = "Serviços",
                    IconGlyph = "👤",
                    RemoteFolder = "install/frente/ClientRP",
                    BackupFolderName = "ClientRP bkp",
                    StandardExePrefix = "ClienrRP"
                },
                // 8. EnvLog
                new AppDefinition
                {
                    Id = "EnvLog",
                    Name = "EnvLog",
                    Category = "Utilitários",
                    IconGlyph = "📜",
                    RemoteFolder = "install/frente/EnvLog",
                    BackupFolderName = "EnvLog bkp",
                    StandardExePrefix = "EnvLog"
                },
                // 9. ImpArq
                new AppDefinition
                {
                    Id = "ImpArq",
                    Name = "ImpArq",
                    Category = "Utilitários",
                    IconGlyph = "🖨️",
                    RemoteFolder = "install/frente/ImpArq",
                    BackupFolderName = "ImpArq bkp",
                    StandardExePrefix = "ImpArq"
                },
                // 10. ImpFlex
                new AppDefinition
                {
                    Id = "ImpFlex",
                    Name = "ImpFlex",
                    Category = "Utilitários",
                    IconGlyph = "📄",
                    RemoteFolder = "install/frente/ImpFlex",
                    BackupFolderName = "ImpFlex bkp",
                    StandardExePrefix = "ImpFlex"
                },
                // 11. ProcArq
                new AppDefinition
                {
                    Id = "ProcArq",
                    Name = "ProcArq",
                    Category = "Utilitários",
                    IconGlyph = "⚙️",
                    RemoteFolder = "install/frente/ProcArq",
                    BackupFolderName = "ProcArq bkp",
                    StandardExePrefix = "ProcArq"
                }
            };
        }

        public string GenerateFinalExeName(string rarFileName = "")
        {
            return $"{StandardExePrefix}.exe";
        }
    }
}
