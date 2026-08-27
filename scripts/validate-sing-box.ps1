[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SingBoxPath
)

$ErrorActionPreference = 'Stop'
$resolved = (Resolve-Path -LiteralPath $SingBoxPath).Path
$tempDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('proxymanager-sing-box-' + [Guid]::NewGuid().ToString('N'))
$configPath = Join-Path $tempDirectory 'smoke.json'
New-Item -ItemType Directory -Path $tempDirectory | Out-Null

$config = @'
{
  "log": { "level": "error", "timestamp": true },
  "inbounds": [
    {
      "type": "tun",
      "tag": "tun-in",
      "address": ["172.19.0.1/30"],
      "auto_route": true,
      "strict_route": true,
      "stack": "system"
    }
  ],
  "outbounds": [
    { "type": "direct", "tag": "direct" },
    { "type": "socks", "tag": "proxy-local", "server": "127.0.0.1", "server_port": 10808, "version": "5" }
  ],
  "route": {
    "auto_detect_interface": true,
    "rules": [
      { "process_name": ["example.exe"], "network": ["tcp", "udp"], "action": "route", "outbound": "proxy-local" },
      { "process_name": ["blocked.exe"], "network": ["tcp", "udp"], "action": "reject" }
    ],
    "final": "direct"
  }
}
'@

try {
    [System.IO.File]::WriteAllText($configPath, $config, [System.Text.UTF8Encoding]::new($false))
    & $resolved check -c $configPath
    if ($LASTEXITCODE -ne 0) { throw "sing-box check failed with exit code $LASTEXITCODE" }
    & $resolved version
    if ($LASTEXITCODE -ne 0) { throw "sing-box version failed with exit code $LASTEXITCODE" }
    Write-Host 'sing-box schema validation passed.'
}
finally {
    if (Test-Path -LiteralPath $tempDirectory) {
        Remove-Item -LiteralPath $tempDirectory -Recurse -Force
    }
}
