@echo off
REM Startet heyDanis direkt aus dem Quellcode (braucht das .NET 8 SDK).
dotnet run --project "%~dp0src\HeyDanis.App\HeyDanis.App.csproj" -c Release
