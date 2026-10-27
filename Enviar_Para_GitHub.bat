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

echo Adicionando e commitando alteracoes...
git add .
git commit -m "Versao 1.0.0 - Atualizador de Sistemas RP Info com auto-update do GitHub e suporte Delphi 13" >nul 2>&1

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
