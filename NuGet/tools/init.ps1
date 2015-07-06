param($installPath, $toolsPath, $package)

Push-Location "$installPath\..\.."
$toolsRelativePath = Resolve-Path -relative $toolsPath
Pop-Location

$batchFileContent = "@call ""%~dp0\$toolsRelativePath\FactoryGenerator.exe"" %*"
Set-Content -Path "$installPath\..\..\GenerateFactories.bat" -Value $batchFileContent