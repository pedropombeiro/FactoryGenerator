param(
	[string]$BuildHostDir,
	[string]$MSBuildBinDir
)

$assembliesToUpdate = @(
	"Microsoft.Bcl.AsyncInterfaces",
	"System.Buffers",
	"System.Collections.Immutable",
	"System.Memory",
	"System.Numerics.Vectors",
	"System.Runtime.CompilerServices.Unsafe",
	"System.Threading.Tasks.Extensions"
)

$configPath = Join-Path $BuildHostDir "Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.exe.config"

if (-not (Test-Path $configPath)) {
	Write-Warning "BuildHost config not found at: $configPath. Skipping binding redirect fix."
	exit 0
}

[xml]$config = Get-Content $configPath
$updated = $false

foreach ($assemblyName in $assembliesToUpdate) {
	# Search in the MSBuild bin dir and its subdirectories
	$sourceDll = Get-ChildItem $MSBuildBinDir -Recurse -Filter "$assemblyName.dll" -ErrorAction SilentlyContinue |
				 Select-Object -First 1 -ExpandProperty FullName

	if (-not $sourceDll) {
		Write-Host "Skipping $assemblyName (not found in $MSBuildBinDir)"
		continue
	}

	# Get the actual version from the VS MSBuild DLL
	$newVersionStr = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($sourceDll).FileVersion
	$newVersionParsed = [System.Version]::Parse($newVersionStr)
	$newVersionStr = $newVersionParsed.ToString(4)

	# Copy DLL to BuildHost directory
	$destDll = Join-Path $BuildHostDir "$assemblyName.dll"
	Copy-Item $sourceDll $destDll -Force
	Write-Host "Copied $assemblyName.dll ($newVersionStr) to BuildHost-net472"

	# Update binding redirect in config using XPath with namespace manager
	$ns = New-Object System.Xml.XmlNamespaceManager($config.NameTable)
	$ns.AddNamespace("b", "urn:schemas-microsoft-com:asm.v1")

	$identityNodes = $config.SelectNodes("//b:assemblyIdentity[@name='$assemblyName']", $ns)
	foreach ($identityNode in $identityNodes) {
		$dependentAssembly = $identityNode.ParentNode
		$bindingRedirect = $dependentAssembly.SelectSingleNode("b:bindingRedirect", $ns)
		if ($bindingRedirect) {
			$bindingRedirect.SetAttribute("oldVersion", "0.0.0.0-$newVersionStr")
			$bindingRedirect.SetAttribute("newVersion", $newVersionStr)
			$updated = $true
			Write-Host "Updated binding redirect for $assemblyName to $newVersionStr"
		}
	}
}

if ($updated) {
	$config.Save($configPath)
	Write-Host "BuildHost binding redirects saved successfully."
}
