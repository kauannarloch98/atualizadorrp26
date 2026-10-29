@echo off
setlocal
title Atualizador de ERP - RP Info

:: ============================================================================
:: CONFIGURACOES DO SERVIDOR FTP E DIRETORIOS
:: ============================================================================
set "FTP_HOST=ftp.rpinfo.com.br"
set "FTP_PORT=21"
set "FTP_USER="
set "FTP_PASS="
set "DEST_DIR=C:\ERP"

if not defined FTP_USER set /p "FTP_USER=Informe seu usuario FTP RP Info: "
if not defined FTP_PASS set /p "FTP_PASS=Informe sua senha FTP RP Info: "

:: ============================================================================
:: LOCALIZACAO DO WINRAR
:: ============================================================================
set "WINRAR="
if exist "%ProgramFiles%\WinRAR\WinRAR.exe" set "WINRAR=%ProgramFiles%\WinRAR\WinRAR.exe"
if not defined WINRAR if exist "%ProgramFiles(x86)%\WinRAR\WinRAR.exe" set "WINRAR=%ProgramFiles(x86)%\WinRAR\WinRAR.exe"
if not defined WINRAR (
    for /f "delims=" %%I in ('where winrar.exe 2^>nul') do set "WINRAR=%%I"
)

if not defined WINRAR (
    echo.
    echo ================================================================
    echo [ERRO] O WinRAR nao foi encontrado no caminho padrao!
    echo Instale o WinRAR ou edite o script informando o caminho correto.
    echo ================================================================
    echo.
    pause
    exit /b 1
)

:: Garante que a pasta destino C:\ERP exista
if not exist "%DEST_DIR%" (
    mkdir "%DEST_DIR%" 2>nul
)

:: ============================================================================
:: MENU PRINCIPAL
:: ============================================================================
:MENU
cls
echo ================================================================
echo               DOWNLOAD E ATUALIZACAO DO ERP - RP INFO
echo ================================================================
echo   Destino: %DEST_DIR%
echo ----------------------------------------------------------------
echo.
echo   [1] ERP Fix Delphi 10
echo   [2] ERP Beta Delphi 10
echo   [3] ERP Fix Delphi 13
echo   [4] ERP Beta Delphi 13
echo   [0] Sair
echo.
echo ================================================================
choice /c 12340 /n /m "Escolha uma opcao [1-4 ou 0 para sair]: "

if errorlevel 5 goto SAIR
if errorlevel 4 goto OPCAO4
if errorlevel 3 goto OPCAO3
if errorlevel 2 goto OPCAO2
if errorlevel 1 goto OPCAO1

:: ----------------------------------------------------------------------------
:: DEFINICAO DAS OPCOES
:: ----------------------------------------------------------------------------
:OPCAO1
set "TITULO=ERP Fix Delphi 10"
set "REMOTE_FILE=install/erp/ERP.rar"
set "IS_BETA=0"
set "IS_DELPHI13=0"
set "BASE_NAME=ERP"
goto EXECUTAR

:OPCAO2
set "TITULO=ERP Beta Delphi 10"
set "REMOTE_FILE=install/erp/ERP-Beta.rar"
set "IS_BETA=1"
set "IS_DELPHI13=0"
set "BASE_NAME=ERP Beta"
goto EXECUTAR

:OPCAO3
set "TITULO=ERP Fix Delphi 13"
set "REMOTE_FILE=install/erp/delphi_13/erp_fix.rar"
set "IS_BETA=0"
set "IS_DELPHI13=1"
set "BASE_NAME=ERP Delphi 13"
goto EXECUTAR

:OPCAO4
set "TITULO=ERP Beta Delphi 13"
set "REMOTE_FILE=install/erp/delphi_13/erp_beta.rar"
set "IS_BETA=1"
set "IS_DELPHI13=1"
set "BASE_NAME=ERP Delphi 13 Beta -"
goto EXECUTAR

:: ============================================================================
:: ROTINA DE DOWNLOAD, EXTRACAO E RENOMEACAO
:: ============================================================================
:EXECUTAR
cls
echo ================================================================
echo INICIANDO PROCESSO: %TITULO%
echo ================================================================
echo.

:: Cria pasta temporaria unica para esta operacao
set "TEMP_WORK=%TEMP%\erp_update_%RANDOM%"
mkdir "%TEMP_WORK%" 2>nul
set "TEMP_RAR=%TEMP_WORK%\pacote.rar"
set "TEMP_EXTRACT=%TEMP_WORK%\extraidos"
mkdir "%TEMP_EXTRACT%" 2>nul

echo [1/3] Conectando ao FTP e baixando: %REMOTE_FILE%
echo Aguarde...
echo.

:: Download com curl preservando data do FTP (-R), com suporte a FTPS (--ssl)
curl -k --ssl -u "%FTP_USER%:%FTP_PASS%" -R "ftp://%FTP_HOST%/%REMOTE_FILE%" -o "%TEMP_RAR%" --progress-bar

if %errorlevel% neq 0 (
    echo.
    echo [ERRO] Falha ao realizar download do arquivo no FTP.
    echo Verifique sua conexao ou se a senha/usuario estao corretos.
    goto LIMPEZA
)

if not exist "%TEMP_RAR%" (
    echo.
    echo [ERRO] O arquivo nao foi salvo localmente.
    goto LIMPEZA
)

:: Define o nome final do executavel
if "%IS_BETA%"=="1" (
    call :OBTER_TIMESTAMP_FTP
) else (
    set "NOME_FINAL=%BASE_NAME%.exe"
)

echo.
echo [2/3] Extraindo executavel com WinRAR...
"%WINRAR%" e -y -ibck "%TEMP_RAR%" "%TEMP_EXTRACT%\"

if %errorlevel% neq 0 (
    echo.
    echo [ERRO] Falha ao descompactar com o WinRAR.
    goto LIMPEZA
)

:: Procura o executavel descompactado
set "EXE_FILE="
for %%F in ("%TEMP_EXTRACT%\*.exe") do (
    set "EXE_FILE=%%F"
)

if not defined EXE_FILE (
    echo.
    echo [ERRO] Nenhum arquivo .exe foi encontrado dentro do .rar extraido!
    goto LIMPEZA
)

echo.
echo [3/3] Instalando e renomeando executavel...
echo Arquivo final: "%DEST_DIR%\%NOME_FINAL%"

move /y "%EXE_FILE%" "%DEST_DIR%\%NOME_FINAL%" >nul
if %errorlevel% neq 0 (
    echo.
    echo [AVISO] Falha ao substituir o arquivo em "%DEST_DIR%".
    echo O ERP pode estar aberto em execucao. Feche-o e tente novamente.
    goto LIMPEZA
)

echo [OK] Executavel instalado com sucesso!

:: Download da DLL para versoes Delphi 13
if "%IS_DELPHI13%"=="1" (
    echo.
    echo ----------------------------------------------------------------
    echo Atualizando SecureBlackbox24.dll para Delphi 13...
    echo ----------------------------------------------------------------
    curl -k --ssl -u "%FTP_USER%:%FTP_PASS%" -R "ftp://%FTP_HOST%/install/erp/Delphi_13/SecureBlackbox24.dll" -o "%DEST_DIR%\SecureBlackbox24.dll" --progress-bar
    if %errorlevel% equ 0 (
        echo [OK] SecureBlackbox24.dll atualizada com sucesso em "%DEST_DIR%\SecureBlackbox24.dll"!
    ) else (
        echo [AVISO] Falha ao baixar SecureBlackbox24.dll do FTP.
    )
)

echo.
echo ================================================================
echo             PROCESSO CONCLUIDO COM SUCESSO!
echo ================================================================

:LIMPEZA
:: Remove os arquivos temporarios
if exist "%TEMP_WORK%" (
    rmdir /s /q "%TEMP_WORK%" 2>nul
)

echo.
echo Pressione qualquer tecla para voltar ao menu...
pause >nul
goto MENU

:: ============================================================================
:: SUB-ROTINA: CAPTURAR TIMESTAMP ORIGINAL DO ARQUIVO NO FTP
:: ============================================================================
:OBTER_TIMESTAMP_FTP
set "TIMESTAMP="
for /f "usebackq delims=" %%D in (`powershell -NoProfile -Command "(Get-Item -LiteralPath '%TEMP_RAR%').LastWriteTime.ToString('dd.MM - HH\hmm')"`) do (
    set "TIMESTAMP=%%D"
)
set "NOME_FINAL=%BASE_NAME% %TIMESTAMP%.exe"
exit /b

:: ============================================================================
:: SAIDA
:: ============================================================================
:SAIR
cls
echo Encerrando atualizador...
timeout /t 1 >nul 2>nul
exit /b 0
