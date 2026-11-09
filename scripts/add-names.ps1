param([switch]$Apply)
# Adds this workflow's trial columns (schemas\trial-columns.yml) and its StimulusState stream to the lab's
# names list (K:\config\data-dictionary.json), which rig-control checks before it launches the
# workflow. Without -Apply it only says what it would add. -Apply saves through rig-setup's own
# Save-NamesDictionary, which refuses if the file changed since it was read, backs it up and logs the
# change. The list is production config: apply only on Pip's word.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\add-names.ps1 [-Apply]
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$rigSetup = 'C:\Github\Coen-Lab\rig-setup'
. "$rigSetup\engine\detect.ps1"
. "$rigSetup\engine\device-names.ps1"
$path = 'K:\config\data-dictionary.json'
$was = [IO.File]::ReadAllText($path)
$prev = $was | ConvertFrom-Json
$read = "import json,yaml;d=yaml.safe_load(open(r'$repo\schemas\trial-columns.yml',encoding='utf-8'));print(json.dumps({k:{'type':v['type'],'description':v['description']} for k,v in d.items()}))"
$cols = uv run --project $repo python -c $read | ConvertFrom-Json
if (-not $cols) { throw "could not read $repo\schemas\trial-columns.yml" }
$tc = [ordered]@{}
foreach ($p in $prev.trialColumns.PSObject.Properties) { $tc[$p.Name] = $p.Value }
$added = @()
foreach ($p in $cols.PSObject.Properties) {
    if ($tc.Contains($p.Name)) {
        if ($tc[$p.Name].type -ne $p.Value.type) { throw "column $($p.Name) is $($tc[$p.Name].type) on the list, $($p.Value.type) here" }
        continue
    }
    $tc[$p.Name] = [ordered]@{ type = $p.Value.type; description = $p.Value.description }
    $added += "column $($p.Name)"
}
$streams = @($prev.streams)
if ($streams -notcontains 'StimulusState') { $streams += 'StimulusState'; $added += 'stream StimulusState' }
$es = [ordered]@{}; foreach ($p in $prev.experimentSettings.PSObject.Properties) { $es[$p.Name] = $p.Value }
$body = [ordered]@{ streams = $streams; experimentSettings = $es; trialColumns = $tc }
$what = "added $($added -join ', ') (coen-dome-navigation, Tom Childs' auditory VR corridor on open-rigs)"
"would add $($added.Count): $($added -join ', ')"
if ($Apply -and $added) {
    $r = Save-NamesDictionary -Path $path -Body $body -Previous $prev -Was $was -By 'PC' -What $what
    "saved: $($r.Ok) $($r.Detail)"
}
