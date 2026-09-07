# make_dist.ps1 - Build and publish quickcmd (supports .sln)
param(
    [string]$Project = ""   # 可选：显式指定要发布的 .csproj 路径
)

Write-Host "Building quickcmd..." -ForegroundColor Green

# 1. 确定要发布的项目文件
if ($Project -and (Test-Path $Project)) {
    $projectFile = $Project
    Write-Host "Using specified project: $projectFile" -ForegroundColor Cyan
} else {
    # 尝试查找 .sln 文件
    $slnFile = Get-ChildItem -Filter "*.sln" | Select-Object -First 1
    if ($slnFile) {
        Write-Host "Found solution: $($slnFile.Name)" -ForegroundColor Cyan
        # 获取解决方案中的所有项目文件路径（解析为绝对路径）
        $slnDir = $slnFile.DirectoryName
        $projectList = dotnet sln $slnFile.FullName list | Where-Object { $_ -match '\.(csproj|vbproj|fsproj)$' } | ForEach-Object {
            [System.IO.Path]::GetFullPath((Join-Path $slnDir $_.Trim()))
        }
        if (-not $projectList -or $projectList.Count -eq 0) {
            Write-Error "No projects found in solution."
            exit 1
        }
        # 优先选择本仓库同名项目，其次是可执行(Exe)项目，最后回退到第一个
        $projectFile = $projectList | Where-Object { $_ -match '\\quickcmd\.csproj$' } | Select-Object -First 1
        if (-not $projectFile) {
            $projectFile = $projectList | Where-Object { Select-String -Path $_ -Pattern '<OutputType>\s*Exe\s*</OutputType>' -Quiet } | Select-Object -First 1
        }
        if (-not $projectFile) {
            Write-Error "No executable (Exe) project found in solution; PublishSingleFile requires an app project."
            exit 1
        }
        Write-Host "Auto-selected project: $projectFile" -ForegroundColor Cyan
    } else {
        # 回退：查找默认的 .csproj
        $defaultProj = Get-ChildItem -Filter "*.csproj" | Select-Object -First 1
        if ($defaultProj) {
            $projectFile = $defaultProj.FullName
            Write-Host "Using default project: $projectFile" -ForegroundColor Cyan
        } else {
            Write-Error "No .sln or .csproj file found in current directory."
            exit 1
        }
    }
}

# 2. 清理（可选，可指定项目文件）
Write-Host "Cleaning previous build..." -ForegroundColor Yellow
dotnet clean $projectFile

# 3. 发布到 dist 目录
Write-Host "Publishing to dist..." -ForegroundColor Yellow
dotnet publish $projectFile `
    -c Release `
    -r win-x64 `
    -p:PublishSingleFile=true `
    -p:DebugType=none `
    -o dist

# 4. 检查结果
if ($LASTEXITCODE -eq 0) {
    Write-Host "Build successful! Output: dist\quickcmd.exe" -ForegroundColor Green
    # 注意：如果发布的项目名不是 quickcmd，请根据实际文件名调整
    $exeName = [System.IO.Path]::GetFileNameWithoutExtension($projectFile) + ".exe"
    $exePath = Join-Path "dist" $exeName
    if (Test-Path $exePath) {
        $size = (Get-Item $exePath).Length / 1KB
        Write-Host "File size: $([math]::Round($size, 2)) KB" -ForegroundColor Cyan
    } else {
        Write-Host "Warning: Expected executable '$exeName' not found in dist." -ForegroundColor Yellow
    }
} else {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}