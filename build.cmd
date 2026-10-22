@echo off
rem Requer o .NET 8 SDK (https://dotnet.microsoft.com/download/dotnet/8.0)
rem Gera dist\WslSyncMonitor.exe (pequeno; precisa do .NET 8 Desktop Runtime instalado)
dotnet publish "%~dp0src" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o "%~dp0dist"
rem Versão que não precisa de runtime instalado (~70 MB):
rem dotnet publish "%~dp0src" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "%~dp0dist-standalone"
