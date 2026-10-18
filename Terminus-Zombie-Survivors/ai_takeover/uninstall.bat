@echo off
rem Uninstall the AI Takeover mod (restores the original base_library.zip contents)
cd /d "%~dp0"
where uv >nul 2>nul
if %errorlevel%==0 (
    uv run --no-project --python 3.12 install.py --uninstall
) else (
    py -3.12 install.py --uninstall
)
pause
