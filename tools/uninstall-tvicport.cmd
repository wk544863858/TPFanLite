@echo off
rem Uninstall TVicPort driver (requires admin)
sc stop TVicPort64
sc delete TVicPort64
echo [OK] TVicPort64 service removed. Driver file may remain in System32\drivers (safe to delete).
pause
