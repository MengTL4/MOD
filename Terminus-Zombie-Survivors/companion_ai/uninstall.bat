@echo off
rem Uninstall the Companion AI mod (restores base_library.zip, or only disables this mod if other mods still use the loader)
cd /d "%~dp0"
where uv >nul 2>nul
if %errorlevel%==0 (
    uv run --no-project --python 3.12 install.py --uninstall
) else (
    py -3.12 install.py --uninstall
)
pause
