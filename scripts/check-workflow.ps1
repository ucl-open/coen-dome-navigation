# The lab's workflow check, one copy for every workflow on RigHardware: lib-open-rigs' open_rigs\scripts\check-workflow.ps1,
# from the open-rigs this repo pins (uv sync puts it in .venv). Needs .bonsai\Setup.ps1 run first. Exit 0 when all pass.
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
& uv sync --project $repo -q
if ($LASTEXITCODE -ne 0) { throw 'uv sync failed.' }
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo '.venv\Lib\site-packages\open_rigs\scripts\check-workflow.ps1') -Repo $repo
exit $LASTEXITCODE
