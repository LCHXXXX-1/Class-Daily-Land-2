@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion
cd /d "%~dp0"

REM ============================================================
REM  一键上传源码到 Gitee
REM  用法：
REM    upload-gitee.bat                 使用默认提交信息
REM    upload-gitee.bat "提交说明"       自定义提交信息
REM  首次使用：把下面的 GITEE_URL 改成你自己的 Gitee 仓库地址
REM ============================================================
set "GITEE_REMOTE=gitee"
set "GITEE_URL=https://gitee.com/lchxxxx/class-daily-land-2.git"

set "MSG=%~1"
if "%MSG%"=="" set "MSG=chore: 同步源码 %date% %time%"

where git >nul 2>nul
if errorlevel 1 (
    echo [错误] 未找到 git，请先安装 Git for Windows 并加入 PATH。
    pause
    exit /b 1
)

git rev-parse --is-inside-work-tree >nul 2>nul
if errorlevel 1 (
    echo [错误] 当前目录不是 Git 仓库：%CD%
    pause
    exit /b 1
)

for /f "delims=" %%b in ('git rev-parse --abbrev-ref HEAD') do set "BRANCH=%%b"

echo.
echo 仓库：%CD%
echo 分支：!BRANCH!
echo 远端：%GITEE_REMOTE%  %GITEE_URL%
echo 提交：%MSG%
echo.

git remote get-url %GITEE_REMOTE% >nul 2>nul
if errorlevel 1 (
    echo [信息] 未发现远端 %GITEE_REMOTE%，正在添加...
    git remote add %GITEE_REMOTE% "%GITEE_URL%"
    if errorlevel 1 goto :fail
) else (
    for /f "delims=" %%u in ('git remote get-url %GITEE_REMOTE%') do set "CUR_URL=%%u"
    if /i not "!CUR_URL!"=="%GITEE_URL%" (
        echo [信息] 远端地址更新为 %GITEE_URL%
        git remote set-url %GITEE_REMOTE% "%GITEE_URL%"
        if errorlevel 1 goto :fail
    )
)

git add -A
if errorlevel 1 goto :fail

git diff --cached --quiet
if errorlevel 1 (
    git commit -m "%MSG%"
    if errorlevel 1 goto :fail
) else (
    echo [信息] 没有文件改动，跳过提交。
)

echo.
echo [信息] 正在推送到 Gitee...
git push -u %GITEE_REMOTE% !BRANCH!
if errorlevel 1 (
    echo.
    echo [失败] 推送失败，请检查：
    echo   1. Gitee 仓库已创建，且 GITEE_URL 正确
    echo   2. 账号有写权限；HTTPS 首次推送会弹出登录窗口
    echo   3. 若用 SSH，把 GITEE_URL 改为 git@gitee.com:用户名/仓库.git
    goto :fail
)

echo.
echo [完成] 已上传到 Gitee：%GITEE_URL%
pause
exit /b 0

:fail
echo.
echo [中断] 操作失败，未完成上传。
pause
exit /b 1
