using System;

namespace AtualizadorERP.Models
{
    public class ErpOption
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty; // "Delphi 10" ou "Delphi 13"
        public string Badge { get; set; } = string.Empty;    // "Fix (Estável)" ou "Beta"
        public string RemoteFolder => IsDelphi13 ? "install/erp/delphi_13" : "install/erp";
        public string RemoteFile { get; set; } = string.Empty;
        public string BaseName { get; set; } = string.Empty;
        public bool IsBeta { get; set; }
        public bool IsDelphi13 { get; set; }
        public string DllRemoteFile { get; set; } = "install/erp/delphi_13/SecureBlackbox24.dll";
        public string DllLocalName { get; set; } = "SecureBlackbox24.dll";
        public string Description { get; set; } = string.Empty;

        public static ErpOption[] GetDefaultOptions()
        {
            return new[]
            {
                new ErpOption
                {
                    Id = 1,
                    Title = "ERP Fix Delphi 10",
                    Category = "Delphi 10",
                    Badge = "Estável",
                    RemoteFile = "install/erp/ERP.rar",
                    BaseName = "ERP",
                    IsBeta = false,
                    IsDelphi13 = false,
                    Description = "Versão oficial estável compilada em Delphi 10. Destino: ERP.exe"
                },
                new ErpOption
                {
                    Id = 2,
                    Title = "ERP Beta Delphi 10",
                    Category = "Delphi 10",
                    Badge = "Beta",
                    RemoteFile = "install/erp/ERP-Beta.rar",
                    BaseName = "ERP Beta",
                    IsBeta = true,
                    IsDelphi13 = false,
                    Description = "Versão de testes Delphi 10 com carimbo de data/hora no nome."
                },
                new ErpOption
                {
                    Id = 3,
                    Title = "ERP Fix Delphi 13",
                    Category = "Delphi 13",
                    Badge = "Estável",
                    RemoteFile = "install/erp/delphi_13/erp_fix.rar",
                    BaseName = "ERP Delphi 13",
                    IsBeta = false,
                    IsDelphi13 = true,
                    Description = "Versão oficial estável Delphi 13 (inclui SecureBlackbox24.dll)."
                },
                new ErpOption
                {
                    Id = 4,
                    Title = "ERP Beta Delphi 13",
                    Category = "Delphi 13",
                    Badge = "Beta",
                    RemoteFile = "install/erp/delphi_13/erp_beta.rar",
                    BaseName = "ERP Delphi 13 Beta",
                    IsBeta = true,
                    IsDelphi13 = true,
                    Description = "Versão de testes Delphi 13 com carimbo de data/hora e DLL."
                }
            };
        }
    }
}
