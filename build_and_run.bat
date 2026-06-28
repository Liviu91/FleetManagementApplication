@echo off
REM ============================================================================
REM  Fleet Management - build & test helper
REM
REM  Usage:
REM    build_and_run.bat          Build web + worker + tests, then run the tests.
REM    build_and_run.bat run      Same as above, then launch the Worker and Web
REM                               app locally (needs RabbitMQ + SQL Server up).
REM
REM  The default (build + test) needs NO database, RabbitMQ or car/phone, so it is
REM  the safe way to verify code changes locally before a real-world drive.
REM ============================================================================
setlocal EnableExtensions
cd /d "%~dp0"

set "WEB=web application 1\web application 1\WebApplication1\WebApplication1.csproj"
set "WORKER=WorkerService\WorkerService\WorkerService\WorkerService.csproj"
set "TESTS=web application 1\web application 1\WebApplication1.Tests\WebApplication1.Tests.csproj"

echo ============================================================
echo  Fleet Management - build ^& test
echo ============================================================

echo.
echo [1/3] Building Web application...
dotnet build "%WEB%" -c Debug --nologo
if errorlevel 1 goto :error

echo.
echo [2/3] Building Worker service...
dotnet build "%WORKER%" -c Debug --nologo
if errorlevel 1 goto :error

echo.
echo [3/3] Building and running automated tests...
dotnet test "%TESTS%" -c Debug --nologo
if errorlevel 1 goto :error

echo.
echo ------------------------------------------------------------
echo  BUILD AND TESTS SUCCEEDED
echo ------------------------------------------------------------
echo  Note: the MAUI Android app (MauiApp1) is built/deployed from
echo  Visual Studio because it needs the MAUI/Android workloads.

if /I "%~1"=="run" goto :run
echo.
echo  To also launch the Worker + Web app locally, run:
echo      build_and_run.bat run
goto :done

:run
echo.
echo Launching Worker service and Web app in separate windows...
echo (Requires RabbitMQ on localhost:5672 and SQL Server per appsettings.json)
start "Fleet Worker" cmd /k dotnet run --project "%WORKER%"
start "Fleet Web"    cmd /k dotnet run --project "%WEB%"
echo.
echo  Web app will be available at:  http://localhost:7292
goto :done

:error
echo.
echo *** BUILD/TEST FAILED ***
endlocal
exit /b 1

:done
endlocal
exit /b 0
