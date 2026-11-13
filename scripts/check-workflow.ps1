param(
    # Internal: the step this script runs in a fresh process of its own (assemblies stay loaded).
    [ValidateSet('', 'Build', 'Checks')][string]$Step = ''
)
# Checks a change to src\ without the rig and without opening Bonsai: no window, no device.
#   1. Compiles src\Extensions under C# 5, the language Bonsai compiles it with.
#   2. Runs tests\Extensions.Tests (the corridor's draw, interval, run and outcome, the Zapit command, rig-template.yml
#      through the generated deserializer).
#   3. Builds src\main.bonsai with Bonsai's own expression builders, as Bonsai does at Start before it
#      subscribes anything (every type, selector, include, subject and mapping), against
#      config\rig-template.yml filled in as a rig's, every node that reads rig.yml pointed at that copy.
#      Then checks what the build made: RecordHarpDevices' boards, ExperimentManifest's expected files and
#      the trial row's columns against schemas\trial-columns.yml.
#   4. Each config\task*.yml reads as the workflow reads it at Go, TaskPath and MissingCalibration are exposed,
#      and every key in the template reaches the Settings_Rig JSON the experiment logs.
# TaskLogic's waits (all on the Harp clock but the laser cap's backstop) are tests\test_workflow.py's.
# Needs the environment set up (.bonsai\Setup.ps1: .bonsai\Packages). Exit 0 when all pass.
$ErrorActionPreference = 'Stop'
$self = $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent (Split-Path -Parent $self)
$bin = Join-Path ([IO.Path]::GetTempPath()) 'coen-dome-navigation-check'
$bonsaiDir = Join-Path $repo '.bonsai'

function Initialize-Assemblies {
    [xml]$cfg = Get-Content (Join-Path $bonsaiDir 'Bonsai.config') -Raw
    $map = @{}
    foreach ($a in $cfg.PackageConfiguration.AssemblyLocations.AssemblyLocation) {
        if ($a.processorArchitecture -in 'MSIL', 'Amd64') { $map[$a.assemblyName] = Join-Path $bonsaiDir $a.location }
    }
    $map['Extensions'] = Join-Path $bin 'Extensions.dll'
    foreach ($n in 'YamlDotNet', 'Newtonsoft.Json') { $map[$n] = Join-Path $bin "$n.dll" }
    foreach ($f in $cfg.PackageConfiguration.LibraryFolders.LibraryFolder) {
        if ($f.platform -eq 'x64') { $env:PATH = (Join-Path $bonsaiDir $f.path) + ';' + $env:PATH }
    }
    $script:resolveMap = $map; $script:resolvePkgs = Join-Path $bonsaiDir 'Packages'
    $script:resolver = [ResolveEventHandler]{
        param($s, $e)
        $n = (New-Object Reflection.AssemblyName $e.Name).Name
        $loaded = [AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq $n } | Select-Object -First 1
        if ($loaded) { return $loaded }
        $p = $script:resolveMap[$n]
        if (-not $p) {
            $p = Get-ChildItem $script:resolvePkgs -Recurse -Filter "$n.dll" -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '\\lib\\net4' } | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
        }
        if ($p -and (Test-Path $p)) { return [Reflection.Assembly]::LoadFrom($p) }
        return $null
    }
    [AppDomain]::CurrentDomain.add_AssemblyResolve($script:resolver)
    foreach ($n in 'Bonsai.Core', 'Bonsai', 'Extensions') { $null = [Reflection.Assembly]::LoadFrom($map[$n]) }
}

# The template filled in as a rig's, at <bin>\config\<MACHINE>\rig.yml.
function Write-FilledTemplate {
    $rigDir = Join-Path $bin "config\$([Environment]::MachineName)"
    $null = New-Item -ItemType Directory -Path $rigDir -Force
    $template = [IO.File]::ReadAllText((Join-Path $repo 'config\rig-template.yml'))
    $serials = 'BG01WA0E', 'AQ03WH45', 'AQ03WRBG', 'AQ03WW0D', 'E664C856C360B937', 'E664C856C31F4637'
    foreach ($s in $serials) { $template = ([regex]'usbSerial: CHANGE-ME').Replace($template, "usbSerial: $s", 1) }
    $template = $template.Replace('rigId: XX', 'rigId: D1').Replace('serialNumber: "00000000"', "serialNumber: '1'").Replace("rootPath: C:\Coen-Lab-Local\Acquired-Data", "rootPath: $bin").Replace('RigConfigs\CHANGE-ME\', "RigConfigs\$([Environment]::MachineName)\")
    foreach ($left in 'CHANGE-ME', 'rigId: XX', 'serialNumber: "00000000"') {
        if ($template.Contains($left)) { throw "check-workflow: the template still holds '$left' after filling; update Write-FilledTemplate." }
    }
    $rigYml = Join-Path $rigDir 'rig.yml'
    [IO.File]::WriteAllText($rigYml, $template)
    return $rigYml
}

if ($Step -eq 'Build') {
    Initialize-Assemblies
    $workflowPath = Join-Path $repo 'src\main.bonsai'
    foreach ($m in [regex]::Matches((Get-Content $workflowPath -Raw), 'assembly=([A-Za-z0-9\.]+)"')) { $null = [Reflection.Assembly]::Load($m.Groups[1].Value) }
    Set-Location (Split-Path $workflowPath); [Environment]::CurrentDirectory = (Split-Path $workflowPath)
    $reader = [Xml.XmlReader]::Create($workflowPath)
    try { $wb = [Bonsai.WorkflowBuilder]::Serializer.Deserialize($reader) } finally { $reader.Close() }
    $rigYml = Write-FilledTemplate
    $configRoot = Join-Path $bin 'config'
    $script:roots = New-Object System.Collections.Generic.List[string]
    function Redirect-ConfigRoots($graph) {
        foreach ($n in $graph) {
            $b = [Bonsai.Expressions.ExpressionBuilder]::Unwrap($n.Value)
            $owner = if ($b -is [Bonsai.Expressions.CombinatorBuilder]) { $b.Combinator } else { $b }
            if ($owner -and $owner.GetType().Namespace -like 'OpenRigs.*' -and $owner.GetType().Name -notlike '*Settings' -and $owner.PSObject.Properties['ConfigRoot']) {
                $script:roots.Add("$($owner.GetType().Name)=$($owner.ConfigRoot)"); $owner.ConfigRoot = $configRoot
            }
            if ($b.GetType().Name -eq 'ExperimentManifest') { $b.ConfigFiles = $rigYml }
            if ($b -is [Bonsai.Expressions.IWorkflowExpressionBuilder] -and $b.Workflow) { Redirect-ConfigRoots $b.Workflow }
        }
    }
    Redirect-ConfigRoots $wb.Workflow
    $failed = 0
    $distinct = @($script:roots | ForEach-Object { ($_ -split '=', 2)[1].TrimEnd('\') } | Sort-Object -Unique)
    if ($script:roots.Count -ge 3 -and $distinct.Count -eq 1) { "  ok    every node reads rig.yml under the same root, $($distinct[0]) ($($script:roots.Count) nodes)" }
    else { "  FAIL  the nodes' rig.yml roots differ, or too few were found: $($script:roots -join '; ')"; $failed++ }
    try {
        $built = [Bonsai.Expressions.ExpressionBuilderGraphExtensions]::Build($wb.Workflow)
    } catch {
        $x = $_.Exception
        while ($x) { $b = $null; try { $b = $x.Builder } catch {}; $nm = if ($b) { [Bonsai.Expressions.ExpressionBuilder]::GetElementDisplayName($b) } else { '' }; "  FAIL  $($x.GetType().Name): $nm :: $($x.Message)"; $x = $x.InnerException }
        exit 1
    }
    "  ok    main.bonsai builds"
    Add-Type -ReferencedAssemblies System.Core -TypeDefinition @'
public class BuiltObjects : System.Linq.Expressions.ExpressionVisitor {
    public System.Collections.Generic.List<object> Found = new System.Collections.Generic.List<object>();
    protected override System.Linq.Expressions.Expression VisitConstant(System.Linq.Expressions.ConstantExpression node) {
        if (node.Value != null) Found.Add(node.Value);
        return base.VisitConstant(node);
    }
}
'@
    $objects = New-Object BuiltObjects
    $null = $objects.Visit($built)
    $records = @($objects.Found | Where-Object { $_.GetType().FullName -eq 'OpenRigs.HarpDevices.RecordHarpDevice' })
    $got = @($records | ForEach-Object { "$($_.Name) $($_.PortName) $($_.UsbSerial)" } | Sort-Object)
    $want = @('BehaviorBoard-Main COM1 BG01WA0E', 'LicketySplit-Left COM5 E664C856C360B937', 'LicketySplit-Right COM6 E664C856C31F4637', 'StepperDriver-A COM3 AQ03WRBG', 'StepperDriver-B COM4 AQ03WW0D', 'TimestampGeneratorGen3-Main COM2 AQ03WH45')
    if (($got -join '; ') -eq ($want -join '; ')) { "  ok    RecordHarpDevices records the six Harp boards from rig.yml" }
    else { "  FAIL  RecordHarpDevices made: $($got -join '; ')"; $failed++ }
    $recorder = @($objects.Found | Where-Object { $_.GetType().Name -eq 'ManifestRecorder' })
    $planField = if ($recorder.Count) { $recorder[0].GetType().GetFields([Reflection.BindingFlags]'Instance,NonPublic,Public') | Where-Object { $_.FieldType.Name -eq 'ExpectedPlan' } | Select-Object -First 1 }
    $expected = if ($planField) { @($planField.GetValue($recorder[0]).Entries | ForEach-Object { $_.Path }) } else { @() }
    $wantFiles = 'BehaviorBoard-Main/', 'TimestampGeneratorGen3-Main/', 'StepperDriver-A/', 'StepperDriver-B/', 'LicketySplit-Left/', 'LicketySplit-Right/', 'Camera-Face/', 'Camera-Pupil/', 'Camera-Body/',
        'BehaviorBoard-Main/BehaviorBoard-Main_device.json', 'BehaviorBoard-Main/BehaviorBoard-Main.yml',
        'TimestampGeneratorGen3-Main/TimestampGeneratorGen3-Main_device.json', 'TimestampGeneratorGen3-Main/TimestampGeneratorGen3-Main.yml',
        'StepperDriver-A/StepperDriver-A_device.json', 'StepperDriver-A/StepperDriver-A.yml',
        'StepperDriver-B/StepperDriver-B_device.json', 'StepperDriver-B/StepperDriver-B.yml',
        'LicketySplit-Left/LicketySplit-Left_device.json', 'LicketySplit-Right/LicketySplit-Right_device.json',
        'LicketySplit-Left/LicketySplit-Left.yml', 'LicketySplit-Right/LicketySplit-Right.yml',
        'Settings/Settings_Rig_', 'Settings/Settings_Task_', 'Settings/Settings_Experiment_', 'BonsaiData/'
    $missing = @($wantFiles | Where-Object { $expected -notcontains $_ })
    $extra = @($expected | Where-Object { $wantFiles -notcontains $_ })
    if ($missing.Count -eq 0 -and $extra.Count -eq 0) { "  ok    ExperimentManifest expects the $($expected.Count) folders and files of an experiment" }
    else { "  FAIL  ExperimentManifest expects '$($expected -join ', ')'; missing: $($missing -join ', '); extra: $($extra -join ', ')"; $failed++ }
    # The trial row's columns, as LogTrial's writer names them, against schemas\trial-columns.yml.
    $columns = [OpenRigs.Logging.CsvWriter]::ColumnNames([Bonsai.Harp.Timestamped`1].MakeGenericType([TrialRow]))
    $schema = @((Get-Content (Join-Path $repo 'schemas\trial-columns.yml')) | Where-Object { $_ -match '^([A-Za-z]\w*):\s*$' } | ForEach-Object { $Matches[1] })
    if (($columns -join ',') -eq ($schema -join ',')) { "  ok    BonsaiData_Trial's $($columns.Count) columns are schemas\trial-columns.yml's, in its order" }
    else { "  FAIL  BonsaiData_Trial writes $($columns -join ','); schemas\trial-columns.yml lists $($schema -join ',')"; $failed++ }
    exit $(if ($failed) { 1 } else { 0 })
}

if ($Step -eq 'Checks') {
    Initialize-Assemblies
    foreach ($p in $script:resolveMap.Values) { if (Test-Path $p) { try { $null = [Reflection.Assembly]::LoadFrom($p) } catch {} } }
    [AppDomain]::CurrentDomain.remove_AssemblyResolve($script:resolver)
    $failed = 0
    # Each task file as the workflow reads it at Go (the generated deserializer: no unknown key).
    foreach ($file in Get-ChildItem (Join-Path $repo 'config') -Filter 'task*.yml') {
        try {
            $d = (New-Object YamlDotNet.Serialization.DeserializerBuilder).Build()
            $c = $d.Deserialize((New-Object IO.StringReader ([IO.File]::ReadAllText($file.FullName))), [UclOpenCoenDomeNavigationTask.UclOpenCoenDomeNavigationTaskLogic])
            if ($c.TaskParameters.TrainingStages.Count -gt 0) { "  ok    config\$($file.Name) reads as the workflow reads it at Go" }
            else { "  FAIL  config\$($file.Name) has no trainingStages"; $failed++ }
        } catch { $x = $_.Exception; while ($x.InnerException) { $x = $x.InnerException }; "  FAIL  config\$($file.Name): $($x.Message)"; $failed++ }
    }
    $wf = Get-Content (Join-Path $repo 'src\main.bonsai') -Raw
    if ($wf -match '<Property Name="TaskPath"' -and $wf -match '<Property Name="MissingCalibration"') { "  ok    main.bonsai exposes TaskPath and MissingCalibration" }
    else { "  FAIL  main.bonsai does not expose TaskPath and MissingCalibration"; $failed++ }
    $yaml = [IO.File]::ReadAllText((Write-FilledTemplate))
    $E = [Linq.Expressions.Expression]
    $wait = [System.Reactive.Linq.Observable].GetMethods() | Where-Object { $_.Name -eq 'Wait' -and $_.GetParameters().Count -eq 1 } | Select-Object -First 1
    function New-RigYamlExpression {
        $x = $E::Constant([System.Reactive.Linq.Observable]::Return($yaml), [IObservable[string]])
        $des = New-Object UclOpenCoenDomeNavigation.DeserializeFromYaml
        $des.Type = [Activator]::CreateInstance([Bonsai.Expressions.TypeMapping`1].MakeGenericType([UclOpenCoenDomeNavigation.DomeRig]))
        return $des.Build([Linq.Expressions.Expression[]]@($x))
    }
    # The experiment's Settings_Rig JSON is rig.yml as parsed, and the manifest keeps only rig.yml's hash,
    # so every key in rig.yml must reach the JSON.
    try {
        $x = New-RigYamlExpression
        $cb = New-Object Bonsai.Expressions.CombinatorBuilder
        $ser = New-Object UclOpenCoenDomeNavigation.SerializeToJson; $ser.Formatting = 'Indented'; $cb.Combinator = $ser
        $x = $cb.Build([Linq.Expressions.Expression[]]@($x))
        $json = $E::Lambda($E::Call($wait.MakeGenericMethod([string]), $x)).Compile().DynamicInvoke()
        $jsonKeys = New-Object System.Collections.Generic.List[string]
        foreach ($t in [Newtonsoft.Json.Linq.JToken]::Parse($json).Descendants()) {
            if ($t -is [Newtonsoft.Json.Linq.JProperty]) { $jsonKeys.Add(($t.Path -replace '\[\d+\]', '[]')) }
        }
        $yamlKeys = New-Object System.Collections.Generic.List[string]
        function Add-YamlKeys($node, [string]$at) {
            if ($node -is [System.Collections.IDictionary]) { foreach ($k in $node.Keys) { $p = if ($at) { "$at.$k" } else { "$k" }; $yamlKeys.Add($p); Add-YamlKeys $node[$k] $p } }
            elseif ($node -is [System.Collections.IList]) { foreach ($i in $node) { Add-YamlKeys $i "$at[]" } }
        }
        Add-YamlKeys ((New-Object YamlDotNet.Serialization.Deserializer).Deserialize((New-Object IO.StringReader $yaml))) ''
        $lost = @($yamlKeys | Where-Object { $jsonKeys -notcontains $_ })
        if ($yamlKeys.Count -gt 0 -and $lost.Count -eq 0) { "  ok    Settings_Rig JSON keeps all $($yamlKeys.Count) keys of rig-template.yml" }
        else { "  FAIL  Settings_Rig JSON lost $($lost.Count) of $($yamlKeys.Count) keys of rig-template.yml: $($lost -join ', ')"; $failed++ }
    } catch { $x = $_.Exception; while ($x.InnerException) { $x = $x.InnerException }; "  FAIL  Settings_Rig JSON: $($x.Message)"; $failed++ }
    exit $(if ($failed) { 1 } else { 0 })
}

if (-not (Test-Path (Join-Path $bonsaiDir 'Packages'))) { throw "No .bonsai\Packages in $repo. Run .bonsai\Setup.ps1 first." }
$failed = 0
"1. C# 5 compile of src\Extensions"
$null = New-Item -ItemType Directory -Path $bin -Force
$out = & dotnet build (Join-Path $repo 'src\Extensions.csproj') -p:LangVersion=5 -o $bin 2>&1
if ($LASTEXITCODE -ne 0) { $out | Select-String 'error' | Select-Object -First 10 | ForEach-Object { "  $_" }; "  FAIL  Extensions does not compile under C# 5"; exit 1 }
"  ok    Extensions compiles under C# 5"
"2. tests\Extensions.Tests"
$log = & { $ErrorActionPreference = 'Continue'; & dotnet test (Join-Path $repo 'tests\Extensions.Tests') -nologo 2>&1 | ForEach-Object { "$_" } }
$testExit = $LASTEXITCODE
$log | Where-Object { $_ -match 'Passed!|Failed|error' } | ForEach-Object { "  $_" }
if ($testExit -ne 0) { "  FAIL  tests\Extensions.Tests"; $failed++ }
"3. Build main.bonsai against the filled rig template"
& powershell -NoProfile -ExecutionPolicy Bypass -File $self -Step Build
if ($LASTEXITCODE -ne 0) { $failed++ }
"4. The task files, the launch properties and Settings_Rig"
& powershell -NoProfile -ExecutionPolicy Bypass -File $self -Step Checks
if ($LASTEXITCODE -ne 0) { $failed++ }
if ($failed) { "FAILED"; exit 1 }
"All checks passed."
exit 0
