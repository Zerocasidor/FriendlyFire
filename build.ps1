# Builds FriendlyFire.exe with the C# compiler that comes with Windows (.NET Framework 4).
$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
& "$fw\csc.exe" /nologo /target:winexe /optimize /codepage:65001 "/out:$PSScriptRoot\FriendlyFire.exe" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll "/r:$fw\System.Web.Extensions.dll" `
    "$PSScriptRoot\FriendlyFire.cs"
exit $LASTEXITCODE
