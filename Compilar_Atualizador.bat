@echo off
setlocal
title Compilar Atualizador ERP - Release
cd /d "%~dp0AtualizadorERP"

echo ================================================================
echo       COMPILANDO ATUALIZADOR DE ERP (EXECUTAVEL UNICO)
echo ================================================================
echo.

set "DOTNET_CMD=dotnet"
where dotnet >nul 2>nul
if %errorlevel% neq 0 (
    if exist "C:\Program Files\dotnet\dotnet.exe" (
        set "DOTNET_CMD=C:\Program Files\dotnet\dotnet.exe"
    ) else (
        echo [ERRO] .NET 8 SDK nao foi encontrado no sistema!
        pause
        exit /b 1
    )
)

for /f "tokens=3 delims=<>" %%v in ('findstr "<Version>" AtualizadorERP.csproj') do set "APP_VER=%%v"
echo Versao configurada no projeto: %APP_VER%
echo.

echo Compilando em Release (win-x64, Self-Contained, Single-File)...
"%DOTNET_CMD%" publish AtualizadorERP.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "%~dp0Publicacao"

if %errorlevel% equ 0 (
    copy /y "%~dp0Publicacao\AtualizadorERP.exe" "%~dp0AtualizadorERP.exe" >nul
    echo.
    echo ================================================================
    echo [SUCESSO] Executavel v%APP_VER% gerado em:
    echo "%~dp0AtualizadorERP.exe"
    echo ================================================================
) else (
    echo.
    echo [ERRO] Falha durante a compilacao!
)

echo.
pause
