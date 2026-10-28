using System;
using AtualizadorERP.Services;

namespace AtualizadorERP.Models
{
    public enum LogLevel
    {
        Info,
        Success,
        Warning,
        Error
    }

    public class LogMessage
    {
        public DateTime Timestamp { get; set; } = FtpUpdaterService.GetBrasiliaNow();
        public LogLevel Level { get; set; } = LogLevel.Info;
        public string Text { get; set; } = string.Empty;

        public string FormattedTime => Timestamp.ToString("HH:mm:ss");

        public string Tag => Level switch
        {
            LogLevel.Success => "[OK]",
            LogLevel.Warning => "[AVISO]",
            LogLevel.Error => "[ERRO]",
            _ => "[INFO]"
        };

        public string ColorHex => Level switch
        {
            LogLevel.Success => "#10B981", // Emerald Green
            LogLevel.Warning => "#F59E0B", // Amber
            LogLevel.Error => "#EF4444",   // Red
            _ => "#38BDF8"                 // Sky Blue / Cyan
        };
    }
}
