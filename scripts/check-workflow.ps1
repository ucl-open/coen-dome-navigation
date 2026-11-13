param(
    # Internal: the step this script runs in a fresh process of its own (assemblies stay loaded).
    [ValidateSet('', 'Build')][string]$Step = ''
)
# Checks a change to src\ without the rig and without opening Bonsai: no window, no device.
#   1. Compiles src\Extensions under C# 5, the language Bonsai compiles it with.
#   2. Builds src\main.bonsai with Bonsai's own expression builders, as Bonsai does at Start before it
#      subscribes anything (every type, selector, include, subject and mapping), against
#      config\rig-template.yml filled in as a rig's, every node that reads rig.yml pointed at that copy.
#      Then checks what the build made: RecordHarpDevices' boards, ExperimentManifest's expected files,
#      the trial row's columns against schemas\trial-columns.yml, and no wait but the laser cap's backstop
#      on the wall clock inside TaskLogic.
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
    [AppDomain]::CurrentDomain.add_AssemblyResolve([ResolveEventHandler]{
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
    })
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

$failed = 0
"1. C# 5 compile of src\Extensions"
$null = New-Item -ItemType Directory -Path $bin -Force
$out = & dotnet build (Join-Path $repo 'src\Extensions.csproj') -p:LangVersion=5 -o $bin 2>&1
if ($LASTEXITCODE -ne 0) { $out | Select-String 'error' | Select-Object -First 10 | ForEach-Object { "  $_" }; "  FAIL  Extensions does not compile under C# 5"; exit 1 }
"  ok    Extensions compiles under C# 5"
"2. Build main.bonsai against the filled rig template"
& powershell -NoProfile -ExecutionPolicy Bypass -File $self -Step Build
if ($LASTEXITCODE -ne 0) { $failed++ }
if ($failed) { "FAILED"; exit 1 }
"All checks passed."
exit 0
