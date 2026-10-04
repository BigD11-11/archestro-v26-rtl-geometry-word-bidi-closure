@echo off
setlocal EnableExtensions
cd /d "%~dp0"
title Archestro V27 Owner Visual RTL + Word RTL Closure
echo.
echo ====================================================================================================
echo  ARCHESTRO MEETING VAULT - V27 OWNER VISUAL RTL + WORD RTL CLOSURE
echo  Embedded runner runtime. Self-test first, then one safe owner run. Aims: NO TOUCH.
echo ====================================================================================================
echo.
set "PYEXE=%CD%\.runtime\python\python.exe"
if not exist "%PYEXE%" (
  if not exist "%CD%\Result" mkdir "%CD%\Result" >nul 2>nul
  >"%CD%\Result\RESULT_TO_UPLOAD.txt" echo ARCHESTRO V27 BOOTSTRAP FAIL: packaged Python runtime is missing. Nothing was changed.
  echo [FAIL] Packaged Python runtime is missing. Nothing was changed.
  start "" explorer.exe /select,"%CD%\Result\RESULT_TO_UPLOAD.txt" >nul 2>nul
  if not "%ARCHESTRO_CI%"=="1" pause
  exit /b 10
)
"%PYEXE%" "%CD%\RUNNER.py" --self-test
if errorlevel 1 (
  if not exist "%CD%\Result" mkdir "%CD%\Result" >nul 2>nul
  >"%CD%\Result\RESULT_TO_UPLOAD.txt" echo ARCHESTRO V27 BOOTSTRAP FAIL: package self-test failed. Nothing was changed.
  echo [FAIL] Package self-test failed. Product was not changed.
  start "" explorer.exe /select,"%CD%\Result\RESULT_TO_UPLOAD.txt" >nul 2>nul
  if not "%ARCHESTRO_CI%"=="1" pause
  exit /b 20
)
if "%ARCHESTRO_LAUNCHER_SMOKE_ONLY%"=="1" (
  echo [PASS] Launcher/runtime smoke completed. Main mutation pipeline was not started.
  exit /b 0
)
"%PYEXE%" "%CD%\RUNNER.py"
set "RC=%ERRORLEVEL%"
echo.
if "%RC%"=="0" (echo [PASS] Run completed.) else (echo [HOLD] Run completed with blockers. Upload Result\RESULT_TO_UPLOAD.zip.)
if not "%ARCHESTRO_CI%"=="1" pause
exit /b %RC%