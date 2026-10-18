@echo off
rem Install / reinstall the AI Takeover mod (needs Python 3.12 via uv or the py launcher)
cd /d "%~dp0"
where uv >nul 2>nul
if %errorlevel%==0 (
    uv run --no-project --python 3.12 install.py %*
) else (
    py -3.12 install.py %*
)
pause
