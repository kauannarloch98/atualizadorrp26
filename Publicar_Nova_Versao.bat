@echo off
setlocal enabledelayedexpansion
title Publicar Nova Versao do Atualizador ERP
cd /d "%~dp0"

echo ================================================================
echo       PUBLICADOR AUTOMATICO DE VERSOES NO GITHUB
echo ================================================================
echo.

set "APP_VER=1.0.0"
for /f "tokens=3 delims=<>" %%v in ('findstr "<Version>" "%~dp0AtualizadorERP\AtualizadorERP.csproj" 2^>nul') do set "APP_VER=%%v"

echo Versao atual do projeto: v%APP_VER%
echo.
set /p NEW_VER="Digite o numero da nova versao a lancar (ex: 1.2.2 ou Enter para v%APP_VER%): "
if "%NEW_VER%"=="" set "NEW_VER=%APP_VER%"

:: Limpa prefixo 'v' se o usuario digitou 'v1.2.2'
if "%NEW_VER:~0,1%"=="v" set "NEW_VER=%NEW_VER:~1%"
if "%NEW_VER:~0,1%"=="V" set "NEW_VER=%NEW_VER:~1%"
if "%NEW_VER:~0,1%"=="." set "NEW_VER=%NEW_VER:~1%"

echo.
echo ================================================================
echo   [1/5] Atualizando AtualizadorERP.csproj para versao %NEW_VER%...
echo ================================================================

powershell -NoProfile -Command ^
    "$path = '%~dp0AtualizadorERP\AtualizadorERP.csproj'; " ^
    "$content = [System.IO.File]::ReadAllText($path); " ^
    "$content = [System.Text.RegularExpressions.Regex]::Replace($content, '<Version>[^<]+</Version>', '<Version>%NEW_VER%</Version>'); " ^
    "$content = [System.Text.RegularExpressions.Regex]::Replace($content, '<AssemblyVersion>[^<]+</AssemblyVersion>', '<AssemblyVersion>%NEW_VER%.0</AssemblyVersion>'); " ^
    "$content = [System.Text.RegularExpressions.Regex]::Replace($content, '<FileVersion>[^<]+</FileVersion>', '<FileVersion>%NEW_VER%.0</FileVersion>'); " ^
    "[System.IO.File]::WriteAllText($path, $content);"

echo [OK] Versao atualizada no arquivo do projeto!
echo.

echo ================================================================
echo   [2/5] Compilando executavel em Release (Win-x64 SingleFile)...
echo ================================================================
cd /d "%~dp0AtualizadorERP"
dotnet publish AtualizadorERP.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:Version=%NEW_VER% -p:AssemblyVersion=%NEW_VER%.0 -p:FileVersion=%NEW_VER%.0 -o "%~dp0Publicacao"

if %errorlevel% neq 0 (
    echo.
    echo [ERRO] Falha na compilacao do projeto! Corrija os erros e tente novamente.
    cd /d "%~dp0"
    pause
    exit /b 1
)

cd /d "%~dp0"
copy /y "%~dp0Publicacao\AtualizadorERP.exe" "%~dp0AtualizadorERP.exe" >nul 2>&1
echo [OK] Executavel compilado com sucesso!
echo.

echo ================================================================
echo   [3/5] Comitando alteracoes no Git...
echo ================================================================
git add .
git commit -m "Lancamento versao %NEW_VER% - Atualizador de Sistemas RP Info" >nul 2>&1
echo [OK] Alteracoes comitadas!
echo.

echo ================================================================
echo   [4/5] Criando tag v%NEW_VER% e enviando para o GitHub...
echo ================================================================
git tag -d "v%NEW_VER%" >nul 2>&1
git push origin ":refs/tags/v%NEW_VER%" >nul 2>&1
git tag -a "v%NEW_VER%" -m "Release v%NEW_VER%"
git push origin main --tags

if %errorlevel% neq 0 (
    echo [AVISO] Falha ao enviar tags para o GitHub. Verifique a conexao e credenciais.
)
echo.

echo ================================================================
echo   [5/5] Publicando Release e Binario v%NEW_VER% no GitHub...
echo ================================================================
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0upload_github_release.ps1" "%NEW_VER%"

echo.
echo ================================================================
echo      PROCESSO CONCLUIDO! VERSAO v%NEW_VER% PRONTA NO GITHUB
echo ================================================================
echo.
pause
