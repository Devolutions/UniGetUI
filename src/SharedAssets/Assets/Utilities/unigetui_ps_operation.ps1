#Requires -Version 5
# Controlled launcher for UniGetUI PowerShell package operations.
#
# Invoked as: powershell.exe -NoProfile -ExecutionPolicy Bypass -File <this> <mode> <command> [args...]
#
# This script deliberately declares NO param() block. Without one, PowerShell binds every
# argument positionally into $args and performs no parameter-name binding at all, so a data
# argument that happens to look like "-Mode" or "-Command" cannot be smuggled into a control
# value. The arguments after <command> are splatted, which passes them to the cmdlet as data
# and never re-parses them as script.

$ErrorActionPreference = 'Continue'
$ConfirmPreference = 'None'

if ($args.Count -lt 2)
{
    [Console]::Error.WriteLine('UniGetUI: the operation launcher requires a mode and a command.')
    exit 2
}

$mode = [string]$args[0]
$command = [string]$args[1]

# Windows PowerShell 5.x defaults to TLS 1.0/1.1, which the PowerShell Gallery rejects.
if ($mode -eq 'tls12')
{
    try
    {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    }
    catch
    {
        [Console]::Error.WriteLine('UniGetUI: could not select TLS 1.2.')
    }
}

$named = @{}
$rest = @()

for ($i = 2; $i -lt $args.Count; $i++)
{
    $item = [string]$args[$i]

    # powershell.exe splits "-Switch:$false" into "-Switch" and the literal text "$false" before
    # this script runs, and splatting cannot bind that text to a switch. Such a pair is turned
    # into a real boolean and bound by name through a hashtable, which does accept one.
    if ($item -match '^-([A-Za-z][A-Za-z0-9_]*)$' -and ($i + 1) -lt $args.Count)
    {
        $switchName = $Matches[1]
        $next = [string]$args[$i + 1]

        if ($next -eq '$false' -or $next -eq '$true')
        {
            $named[$switchName] = ($next -eq '$true')
            $i++
            continue
        }
    }

    $rest += $args[$i]
}

# A terminating error, such as a parameter that the cmdlet does not accept, would otherwise be
# written to the error stream and leave this script to exit 0, reporting a failed operation as a
# success. Running under -Command used to fail the process for us, so it is done explicitly here.
try
{
    & $command @named @rest
}
catch
{
    # The whole record, not just the message: the caller matches on the error id to decide
    # whether to retry elevated or without -Scope, and that id is only in the full record.
    Write-Error -ErrorRecord $_
    exit 1
}

# PowerShellGet reports failures as non-terminating errors, so powershell.exe would exit 0
# even though nothing was installed. The caller binds -ErrorVariable to this name.
if ($UniGetUIOperationError)
{
    exit 1
}
