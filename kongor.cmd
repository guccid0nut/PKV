@echo off
REM SPDX-License-Identifier: AGPL-3.0-or-later
REM SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
REM Project KONGOR V (https://github.com/guccidonut/PKV)
setlocal
set "REPO_ROOT=%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%REPO_ROOT%kongor.ps1" %*
