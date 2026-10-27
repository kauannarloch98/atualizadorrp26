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

echo Compilando em Release (win-x64, Self-Contained, Single-File)...
"%DOTNET_CMD%" publish AtualizadorERP.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "%~dp0Publicacao"

if %errorlevel% equ 0 (
    copy /y "%~dp0Publicacao\AtualizadorERP.exe" "%~dp0AtualizadorERP.exe" >nul
    echo.
    echo ================================================================
    echo [SUCESSO] Executavel gerado em:
    echo "%~dp0AtualizadorERP.exe"
    echo ================================================================
) else (
    echo.
    echo [ERRO] Falha durante a compilacao!
)

echo.
pause
