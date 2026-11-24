# 编译、打包并部署到游戏的 Dev mod 目录（免订阅、自动启用）。等同于 package.ps1 -Deploy。
#
#   pwsh tools/deploy-dev.ps1
#
& (Join-Path $PSScriptRoot "package.ps1") -Deploy @args
