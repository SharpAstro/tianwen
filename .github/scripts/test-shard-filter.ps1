# Prints the --filter of one shard of a test project, from .github/test-shards.json (#2054).
#
# Shard N (1-based) of M takes the classes whose name contains one of its prefixes and none of an earlier
# shard's; shard M, the last, takes whatever no other shard did. So the shards partition the project: no
# test runs twice and a new class is never left out, whatever it is called. The project's own filter
# (Category!=Heavy for the unit suite) is ANDed onto every shard.
#
# -Of is the shard count the workflow's matrix lists, and it picks the project's layout of that many shards (a
# runner that starts later can take more, shorter shards). A count with no layout fails the step, and so does a
# layout whose own count disagrees with its key: a shard the matrix does not list would go unrun.
param(
    [Parameter(Mandatory)][string] $Project,
    [Parameter(Mandatory)][int] $Shard,
    [Parameter(Mandatory)][int] $Of
)
$ErrorActionPreference = 'Stop'

$config = Get-Content -Raw (Join-Path $PSScriptRoot '..' 'test-shards.json') | ConvertFrom-Json
$entry = $config.$Project
if ($null -eq $entry) { throw "test-shards.json has no entry for $Project" }

$layout = $entry.layouts."$Of"
if ($null -eq $layout) {
    throw "$Project has no $Of-shard layout in test-shards.json (it has $(@($entry.layouts.PSObject.Properties.Name) -join ', ')); the matrix lists $Of"
}
$prefixShards = @($layout | ForEach-Object { , @($_) })
$count = $prefixShards.Count + 1
if ($Of -ne $count) { throw "$Project's $Of-shard layout in test-shards.json lists $count shards" }
if ($Shard -lt 1 -or $Shard -gt $count) { throw "shard $Shard is outside 1..$count" }

$terms = [System.Collections.Generic.List[string]]::new()
if ($entry.filter) { $terms.Add("($($entry.filter))") }
# Everything an earlier shard took (for the last shard: every other shard).
for ($i = 0; $i -lt $Shard - 1; $i++) {
    foreach ($p in $prefixShards[$i]) { $terms.Add("FullyQualifiedName!~$p") }
}
if ($Shard -lt $count) {
    $own = $prefixShards[$Shard - 1] | ForEach-Object { "FullyQualifiedName~$_" }
    $terms.Add("($($own -join '|'))")
}
$terms -join '&'
