using System;
using System.Windows;
using System.Windows.Threading;

namespace AtualizadorERP
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                var ex = args.ExceptionObject as Exception;
                MessageBox.Show(
                    $"Erro inesperado na aplicação:\n\n{ex?.Message}\n\n{ex?.StackTrace}",
                    "Erro Crítico - Atualizador ERP",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            };

            DispatcherUnhandledException += (s, args) =>
            {
                MessageBox.Show(
                    $"Erro de interface:\n\n{args.Exception.Message}\n\n{args.Exception.StackTrace}",
                    "Aviso de Erro - Atualizador ERP",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                args.Handled = true;
            };
        }
    }
}
