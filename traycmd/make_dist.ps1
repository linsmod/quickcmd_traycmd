# make_dist.ps1 - Build the solution and copy every exe project's output directory into a flat dist\
param(
    [string]$Configuration = "Release"
)

Write-Host "Building solution..." -ForegroundColor Green

$slnFile = Get-ChildItem -Filter "*.sln" | Select-Object -First 1
if (-not $slnFile) {
    Write-Error "No .sln file found in current directory."
    exit 1
}

# 清理各项目 bin 目录，避免残留旧产物混入 dist
$slnDir = $slnFile.DirectoryName
$projects = dotnet sln $slnFile.FullName list | Where-Object { $_ -match '\.(csproj|vbproj|fsproj)$' } | ForEach-Object {
    [System.IO.Path]::GetFullPath((Join-Path $slnDir $_.Trim()))
}
foreach ($proj in $projects) {
    $binDir = Join-Path (Split-Path $proj -Parent) "bin"
    if (Test-Path $binDir) {
        Remove-Item $binDir -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path $binDir) { Write-Warning "Could not fully clean $binDir (files in use); stale outputs may be copied." }
    }
}

dotnet build $slnFile.FullName -c $Configuration
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

# 收集解决方案中所有可执行(Exe)项目
$exeProjects = $projects | Where-Object { Select-String -Path $_ -Pattern '<OutputType>\s*(Exe|WinExe)\s*</OutputType>' -Quiet }
if (-not $exeProjects) {
    Write-Error "No executable (Exe) projects found in solution."
    exit 1
}

$distRoot = Join-Path $PSScriptRoot "dist"
if (Test-Path $distRoot) {
    Remove-Item $distRoot -Recurse -Force
}

# 每个 exe 项目的输出目录内容全部平铺合并拷贝到 dist\
New-Item -ItemType Directory -Path $distRoot | Out-Null
foreach ($proj in $exeProjects) {
    $propsJson = dotnet msbuild $proj -getProperty:TargetDir,TargetName -p:Configuration=$Configuration -nologo | ConvertFrom-Json
    $targetDir = $propsJson.Properties.TargetDir
    $targetName = $propsJson.Properties.TargetName

    if (-not (Test-Path $targetDir)) {
        Write-Warning "Output directory not found for $proj : $targetDir"
        continue
    }

    Write-Host "Copying '$targetDir' -> 'dist\'..." -ForegroundColor Yellow
    Copy-Item -Path (Join-Path $targetDir "*") -Destination $distRoot -Recurse -Force

    $exePath = Join-Path $distRoot "$targetName.exe"
    if (Test-Path $exePath) {
        $size = (Get-Item $exePath).Length / 1KB
        Write-Host "  OK: dist\$targetName.exe ($([math]::Round($size, 2)) KB)" -ForegroundColor Green
    } else {
        Write-Warning "  Expected executable '$targetName.exe' not found in dist\."
    }
}

Write-Host "Done. Output root: dist\" -ForegroundColor Green
