param([switch]$Apply, [string]$ListPath = 'K:\config\data-dictionary.json')
# Adds this workflow's trial columns (schemas\trial-columns.yml) and its own streams to the lab's
# names list (K:\config\data-dictionary.json), which rig-control checks before it launches the
# workflow, and tags each name this workflow uses with coen-dome-navigation (usedBy; every other
# repo's tags are kept). Names it no longer writes ($retired) lose its tag, and leave the list when
# no other repo is tagged on them. Without -Apply it only says what it would change. -Apply saves through
# rig-setup's own Save-NamesDictionary, which refuses if the file changed since it was read, backs it
# up and logs the change. The list is production config: apply only on Pip's word.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\add-names.ps1 [-Apply]
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$me = 'coen-dome-navigation'
$rigSetup = 'C:\Github\Coen-Lab\rig-setup'
. "$rigSetup\engine\detect.ps1"
. "$rigSetup\engine\device-names.ps1"
$path = $ListPath  # another copy only to try the script
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
# This workflow's own streams: BonsaiData_StimulusState, and zapit-v2's copy of the Zapit stimulus config at Go.
$ownStreams = @('BonsaiData_StimulusState', 'Settings_ZapitStimConfig')
# Names this workflow wrote once and no longer does (Pip, 9 Nov 2026): optoStimulated became laserPowerMw,
# StimulusState moved into BonsaiData.
$retired = @{ trialColumns = @('optoStimulated'); streams = @('StimulusState') }
foreach ($s in $ownStreams) { if ($streams -notcontains $s) { $streams += $s; $added += "stream $s" } }
$es = [ordered]@{}; foreach ($p in $prev.experimentSettings.PSObject.Properties) { $es[$p.Name] = $p.Value }
# usedBy as it was, with this repo on each of its names (its streams: StimulusState and the lab's
# Settings_* and calibration streams it already writes are tagged by whoever saves the list).
$usedBy = [ordered]@{}
foreach ($section in 'streams', 'experimentSettings', 'trialColumns') {
    $usedBy[$section] = [ordered]@{}
    $old = Get-NamesMember (Get-NamesMember $prev 'usedBy') $section
    if ($old) { foreach ($p in $old.PSObject.Properties) { $usedBy[$section][$p.Name] = @($p.Value) } }
}
$tagged = @()
function Add-Tag([string]$Section, [string]$Name) {
    $tags = @($usedBy[$Section][$Name] | Where-Object { $_ })
    if ($tags -notcontains $me) { $usedBy[$Section][$Name] = @(@($tags) + $me | Sort-Object); $script:tagged += $Name }
}
foreach ($name in $cols.PSObject.Properties.Name) { Add-Tag 'trialColumns' $name }
foreach ($s in $ownStreams) { Add-Tag 'streams' $s }
$removed = @()
foreach ($section in $retired.Keys) {
    foreach ($name in $retired[$section]) {
        if (-not $usedBy[$section].Contains($name)) { $others = @() } else { $others = @($usedBy[$section][$name] | Where-Object { $_ -and $_ -ne $me }) }
        if ($others) {
            if (@($usedBy[$section][$name]) -contains $me) { $usedBy[$section][$name] = $others; $removed += "$me tag on $name" }
            continue
        }
        if ($usedBy[$section].Contains($name)) { $usedBy[$section].Remove($name) }
        if ($section -eq 'trialColumns' -and $tc.Contains($name)) { $tc.Remove($name); $removed += "column $name" }
        if ($section -eq 'streams' -and $streams -contains $name) { $streams = @($streams | Where-Object { $_ -ne $name }); $removed += "stream $name" }
    }
}
$body = [ordered]@{ streams = $streams; experimentSettings = $es; trialColumns = $tc; usedBy = $usedBy }
"would add $($added.Count): $($added -join ', ')"
"would tag $($tagged.Count) with ${me}: $($tagged -join ', ')"
"would remove $($removed.Count): $($removed -join ', ')"
if ($Apply -and ($added -or $tagged -or $removed)) {
    $what = @($(if ($added) { "added $($added -join ', ')" }), $(if ($tagged) { "tagged $($tagged -join ', ') with $me" }),
              $(if ($removed) { "removed $($removed -join ', ')" })) -join '; '
    $r = Save-NamesDictionary -Path $path -Body $body -Previous $prev -Was $was -By 'PC' -What "$what (coen-dome-navigation, Tom Childs' auditory VR corridor on open-rigs)"
    "saved: $($r.Ok) $($r.Detail)"
}
