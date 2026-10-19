@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo.
  echo  Falta el SDK de .NET 8. Instalalo con este comando y vuelve a ejecutar compilar.bat:
  echo.
  echo     winget install Microsoft.DotNet.SDK.8
  echo.
  pause
  exit /b 1
)

echo Compilando Gajo... (la primera vez descarga FFmpeg y tarda un par de minutos)
dotnet publish src\Gajo.App\Gajo.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
if errorlevel 1 (
  echo.
  echo  La compilacion fallo. Copia el texto de arriba y pasaselo a Claude.
  pause
  exit /b 1
)

echo.
echo  Listo: publish\Gajo.exe
explorer publish
pause
