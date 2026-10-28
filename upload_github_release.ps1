param (
    [Parameter(Mandatory=$true)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'

# Limpa o prefixo se fornecido
$cleanVer = $Version.Trim()
if ($cleanVer.StartsWith('v', [System.StringComparison]::OrdinalIgnoreCase)) {
    $cleanVer = $cleanVer.Substring(1).TrimStart('.')
}
$tagName = "v$cleanVer"

$raw = "protocol=https`nhost=github.com" | git credential fill
$token = ""
foreach ($l in $raw) {
    if ($l -match '^password=(.+)$') {
        $token = $matches[1].Trim()
    }
}

if (-not $token) {
    Write-Host "[ERRO] Nao foi possivel obter o token do GitHub pelo Git Credential Manager." -ForegroundColor Red
    exit 1
}

$repoOwner = "kauannarloch98"
$repoName = "atualizadorrp26"
$exePath = Join-Path $PSScriptRoot "Publicacao\AtualizadorERP.exe"

if (-not (Test-Path $exePath)) {
    $exePath = Join-Path $PSScriptRoot "AtualizadorERP.exe"
}

if (-not (Test-Path $exePath)) {
    Write-Host "[ERRO] Executavel nao encontrado em: $exePath" -ForegroundColor Red
    exit 1
}

$headers = @{
    "Authorization" = "Bearer $token"
    "User-Agent" = "AtualizadorERP-Publisher"
    "Accept" = "application/vnd.github.v3+json"
}

Write-Host "Verificando se a release '$tagName' ja existe no GitHub..." -ForegroundColor Cyan

$release = $null
try {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repoOwner/$repoName/releases/tags/$tagName" -Headers $headers -ErrorAction Stop
    Write-Host "Release '$tagName' encontrada (ID: $($release.id))." -ForegroundColor Green
} catch {
    Write-Host "Release '$tagName' ainda nao existe. Criando nova Release no GitHub..." -ForegroundColor Yellow
    $createBody = @{
        tag_name = $tagName
        target_commitish = "main"
        name = "Release $tagName"
        body = "Nova versao $tagName do Atualizador de Sistemas RP Info."
        draft = $false
        prerelease = $false
    } | ConvertTo-Json

    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repoOwner/$repoName/releases" -Method Post -Headers $headers -Body $createBody -ContentType "application/json" -ErrorAction Stop
    Write-Host "Release '$tagName' criada com sucesso! (ID: $($release.id))" -ForegroundColor Green
}

# Se ja existir um asset AtualizadorERP.exe, exclui o anterior
if ($release.assets) {
    foreach ($a in $release.assets) {
        if ($a.name -eq "AtualizadorERP.exe") {
            Write-Host "Excluindo binario antigo da Release (ID: $($a.id))..." -ForegroundColor Yellow
            try {
                Invoke-RestMethod -Uri "https://api.github.com/repos/$repoOwner/$repoName/releases/assets/$($a.id)" -Method Delete -Headers $headers
                Write-Host "Binario antigo removido com sucesso!" -ForegroundColor Green
            } catch {
                Write-Host "Aviso ao remover binario antigo: $_" -ForegroundColor DarkYellow
            }
        }
    }
}

# Upload do novo binario compilado
Write-Host "Enviando novo executavel compilado ($exePath)..." -ForegroundColor Cyan
$uploadUri = "https://uploads.github.com/repos/$repoOwner/$repoName/releases/$($release.id)/assets?name=AtualizadorERP.exe"
$bytes = [System.IO.File]::ReadAllBytes($exePath)

$uploadHeaders = @{
    "Authorization" = "Bearer $token"
    "User-Agent" = "AtualizadorERP-Publisher"
    "Content-Type" = "application/octet-stream"
}

$uploaded = Invoke-RestMethod -Uri $uploadUri -Method Post -Headers $uploadHeaders -Body $bytes
$sizeMb = [Math]::Round($uploaded.size / (1024 * 1024), 2)
Write-Host "[SUCESSO] AtualizadorERP.exe ($sizeMb MB) anexado a Release '$tagName' no GitHub!" -ForegroundColor Green
