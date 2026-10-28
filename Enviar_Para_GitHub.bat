@echo off
title Enviar Atualizador para o GitHub
cd /d "%~dp0"
echo ================================================================
echo       ENVIANDO ATUALIZADOR ERP PARA O GITHUB
echo       Repositorio: https://github.com/kauannarloch98/atualizadorrp26
echo ================================================================
echo.

echo Verificando status dos arquivos...
git status
echo.

set "APP_VER=1.0.0"
for /f "tokens=3 delims=<>" %%v in ('findstr "<Version>" "%~dp0AtualizadorERP\AtualizadorERP.csproj" 2^>nul') do set "APP_VER=%%v"

echo Adicionando e commitando alteracoes (v%APP_VER%)...
git add .
git commit -m "Versao %APP_VER% - Atualizador de Sistemas RP Info" >nul 2>&1

echo.
echo Enviando para o GitHub (main e tags)...
git push -u origin main --tags

echo.
if %errorlevel% equ 0 (
    echo ================================================================
    echo [SUCESSO] Codigo e tags enviados para o GitHub com sucesso!
    echo ================================================================
) else (
    echo ================================================================
    echo [AVISO] Verifique a janela de login do GitHub no navegador
    echo ou configure seu Token de Acesso Pessoal (PAT).
    echo ================================================================
)

echo.
pause
