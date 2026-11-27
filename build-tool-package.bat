@echo off
setlocal

pushd "%~dp0"
dotnet pack "FactoryGenerator.csproj" --configuration Release %*
set "exitCode=%ERRORLEVEL%"
popd

exit /b %exitCode%
